using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;

namespace YuiToIssho;

internal static class AppearanceActionKinds
{
    public const string Watering = "Watering";
    public const string Chopping = "Chopping";
    public const string Mining = "Mining";
    public const string HarvestGrab = "HarvestGrab";
    public const string HarvestScythe = "HarvestScythe";
    public const string Forage = "Forage";
    public const string Mowing = "Mowing";
    public const string Digging = "Digging";
    public const string Petting = "Petting";
    public const string Milking = "Milking";
    public const string Shearing = "Shearing";
    public const string Fishing = "Fishing";
    public const string CombatSword = "CombatSword";
    public const string CombatDagger = "CombatDagger";
    public const string CombatClub = "CombatClub";
    public const string Handoff = "Handoff";
    public const string Crafting = "Crafting";
    public const string Planting = "Planting";
    public const string Sitting = "Sitting";
    public const string Swinging = "Swinging";
}

internal readonly record struct AppearanceActionSnapshot(
    string OperationId,
    string Kind,
    string Phase,
    string ToolId,
    int Facing,
    int Frame,
    int RemainingTicks,
    bool CommitQueued,
    string? LastFailure);

internal sealed class CompanionAppearanceCoordinator
{
    private const int PrepareTicks = 6;
    private const int DefaultCommitTicks = 30;
    private const int IdleAwarenessDistance = 5;
    private const ulong MovementFacingHoldTicks = 6;
    private const ulong MinimumIdleTurnTicks = 240;
    private const ulong IdleTurnVarianceTicks = 180;
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, VisualRuntime> visuals = new();
    private readonly Dictionary<CompanionIdentity, ActionPulse> actions = new();
    private readonly Dictionary<CompanionIdentity, string> lastFailures = new();
    private ulong lastUpdateTick;

    private static readonly uint[] HairColors =
    {
        Packed(43, 38, 52), Packed(81, 50, 42), Packed(132, 78, 48), Packed(45, 72, 105), Packed(116, 58, 85), Packed(202, 153, 87),
    };

    private static readonly uint[] PantsColors =
    {
        Packed(39, 55, 89), Packed(59, 74, 69), Packed(81, 52, 69), Packed(58, 58, 64),
    };

    public CompanionAppearanceCoordinator(CompanionRegistry registry, CompanionBodyBinder bodies, IMonitor monitor)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.monitor = monitor;
    }

    public InventoryValidationResult ValidateAndInitialize()
    {
        List<int> hairs = Farmer.GetAllHairstyleIndices();
        if (hairs.Count == 0)
            return InventoryValidationResult.Failure("HAIR-CATALOG-EMPTY", "Vanilla returned no Farmer hairstyle indices.");

        HashSet<string> profileIds = new(StringComparer.Ordinal);
        foreach (CompanionRecord record in this.registry.Active)
        {
            CompanionAppearanceProfile profile = record.Appearance;
            if (!profile.IsInitialized)
                Generate(profile, hairs);
            if (!Guid.TryParseExact(profile.ProfileId, "N", out _)
                || !profileIds.Add(profile.ProfileId)
                || profile.ProfileSchemaVersion != CompanionAppearanceProfile.CurrentProfileSchemaVersion
                || profile.Generation < 1
                || !CompanionBodyTypes.IsValid(profile.BodyType)
                || !hairs.Contains(profile.HairStyle)
                || profile.Skin is < 0 or > 23
                || string.IsNullOrWhiteSpace(profile.ShirtId)
                || string.IsNullOrWhiteSpace(profile.PantsId)
                || string.IsNullOrWhiteSpace(profile.ShoeColorId)
                || profile.AccessoryId != -1
                || profile.HatQualifiedItemId.Length != 0)
                return InventoryValidationResult.Failure("INVALID-APPEARANCE", $"{record.Identity} contains an invalid or duplicated appearance profile.");
        }
        return InventoryValidationResult.Success($"Validated {this.registry.Count} persistent vanilla appearance profile(s).");
    }

    public void EnsureProfile(CompanionRecord record)
    {
        if (!record.Appearance.IsInitialized)
            Generate(record.Appearance, Farmer.GetAllHairstyleIndices());
    }

    public void Prepare(CompanionIdentity identity, string operationId, string kind, Item? tool, int facing)
    {
        this.actions[identity] = new ActionPulse(operationId, kind, "Prepare", tool, NormalizeFacing(facing), PrepareTicks);
        this.lastFailures.Remove(identity);
    }

    public void Commit(CompanionIdentity identity, string operationId)
    {
        if (!this.actions.TryGetValue(identity, out ActionPulse? pulse) || pulse.OperationId != operationId)
            return;
        if (pulse.Phase == "Prepare")
            pulse.CommitQueued = true;
        else
        {
            pulse.Phase = "Commit";
            pulse.SetDuration(CommitDuration(pulse.Kind));
        }
    }

    public void SetPhase(CompanionIdentity identity, string operationId, string kind, string phase, Item? tool, int facing, int durationTicks)
    {
        this.actions[identity] = new ActionPulse(operationId, kind, phase, tool, NormalizeFacing(facing), Math.Max(6, durationTicks));
        this.lastFailures.Remove(identity);
    }

    public void SetPersistentPhase(CompanionIdentity identity, string operationId, string kind, string phase, int facing)
    {
        this.actions[identity] = new ActionPulse(operationId, kind, phase, null, NormalizeFacing(facing), 0);
        this.lastFailures.Remove(identity);
    }

    public void Fail(CompanionIdentity identity, string operationId, string code)
    {
        if (this.actions.TryGetValue(identity, out ActionPulse? pulse) && pulse.OperationId == operationId)
            this.actions.Remove(identity);
        this.lastFailures[identity] = code;
    }

    public void Clear(CompanionIdentity identity, string reason)
    {
        this.actions.Remove(identity);
        this.lastFailures[identity] = reason;
    }

    public void Update(ulong tick)
    {
        int elapsed = this.lastUpdateTick == 0 ? Math.Max(1, (int)Math.Min(6UL, tick)) : Math.Max(1, (int)Math.Min(60UL, tick - this.lastUpdateTick));
        this.lastUpdateTick = tick;
        foreach ((CompanionIdentity identity, ActionPulse pulse) in this.actions.ToArray())
        {
            pulse.RemainingTicks = Math.Max(0, pulse.RemainingTicks - elapsed);
            if (pulse.RemainingTicks > 0)
                continue;
            if (pulse.CommitQueued)
            {
                pulse.CommitQueued = false;
                pulse.Phase = "Commit";
                pulse.SetDuration(CommitDuration(pulse.Kind));
            }
            else if (pulse.Phase is not ("Waiting" or "Cast" or "Reel"))
            {
                this.actions.Remove(identity);
            }
        }
    }

    public bool TryRenderNetworkBody(NPC body, SpriteBatch spriteBatch, float alpha)
    {
        if (!Context.IsWorldReady || Game1.currentLocation is null)
            return false;
        if (!this.bodies.TryGetIdentity(body, out CompanionIdentity identity)
            || !this.registry.TryGet(identity, out CompanionRecord record)
            || !ReferenceEquals(body.currentLocation, Game1.currentLocation))
            return false;
        try
        {
            this.EnsureProfile(record);
            VisualRuntime visual = this.GetOrCreateVisual(record);
            string locationName = body.currentLocation?.NameOrUniqueName ?? string.Empty;
            if (!string.IsNullOrEmpty(visual.LastLocationName) && visual.LastLocationName != locationName)
                this.Clear(record.Identity, "LOCATION-CHANGED");
            visual.LastLocationName = locationName;
            visual.Farmer.Position = body.Position;
            visual.Farmer.currentLocation = body.currentLocation;
            Vector2 visualPosition = body.Position;
            visualPosition.Y += body.GetBoundingBox().Bottom - visual.Farmer.GetBoundingBox().Bottom;
            visual.Farmer.Position = visualPosition;
            if (!visual.HasBodyPosition)
            {
                visual.LastBodyPosition = body.Position;
                visual.HasBodyPosition = true;
            }
            bool moved = body.Position != visual.LastBodyPosition;
            if (moved)
            {
                Vector2 delta = body.Position - visual.LastBodyPosition;
                visual.MovementFacing = FacingFromDelta(delta, visual.MovementFacing);
                visual.IdleFacing = visual.MovementFacing;
                visual.HasIdleFacing = true;
                visual.NextIdleTurnTick = this.lastUpdateTick + IdleTurnDelay(record.Identity);
                visual.LastBodyPosition = body.Position;
                visual.LastMovementTick = this.lastUpdateTick;
            }
            bool visuallyMoving = moved || this.lastUpdateTick - visual.LastMovementTick <= MovementFacingHoldTicks;
            bool taskOwnsFacing = !string.IsNullOrWhiteSpace(record.ActiveTransactionId);
            bool waitingAtTaskTarget = taskOwnsFacing && body.controller is null;
            int facing = this.actions.TryGetValue(record.Identity, out ActionPulse? pulse)
                ? pulse.Facing
                : waitingAtTaskTarget ? NormalizeFacing(body.FacingDirection)
                : visuallyMoving ? visual.MovementFacing
                : this.ResolveIdleFacing(record, body, visual);
            if (pulse is null && !visuallyMoving && !taskOwnsFacing)
                body.faceDirection(facing);
            visual.Farmer.faceDirection(facing);
            bool seated = pulse is not null && CompanionSeatedPose.IsSeatedKind(pulse.Kind);
            int frame;
            bool flip;
            bool secondaryArm;
            if (seated)
                frame = CompanionSeatedPose.Apply(visual.Farmer, facing, out flip, out secondaryArm);
            else
            {
                CompanionSeatedPose.Reset(visual.Farmer);
                frame = this.ResolveFrame(record.Identity, body, visual, this.lastUpdateTick, facing, out flip);
                secondaryArm = pulse is not null && CompanionVisualToolAnimation.UsesSecondaryArm(pulse.Kind, pulse.Facing, frame);
            }
            Vector2 screen = Game1.GlobalToLocal(Game1.viewport, visualPosition);
            bool idle = !seated && pulse is null && !visuallyMoving && !taskOwnsFacing;
            UpdateIdleEyes(visual.Farmer, record.Identity, this.lastUpdateTick, idle);
            if (idle)
                screen.Y += IdleBreathingOffset(record.Identity, this.lastUpdateTick);
            float depth = visual.Farmer.getDrawLayer();
            visual.Farmer.FarmerSprite.setCurrentSingleFrame(frame, 32000, secondaryArm, flip);
            Vector2 origin = new(
                visual.Farmer.xOffset,
                (visual.Farmer.yOffset + 128f - visual.Farmer.GetBoundingBox().Height / 2f) / 4f + 4f
            );
            visual.Farmer.FarmerRenderer.draw(
                spriteBatch,
                visual.Farmer.FarmerSprite,
                visual.Farmer.FarmerSprite.SourceRect,
                screen,
                origin,
                depth,
                Color.White * alpha,
                0f,
                visual.Farmer
            );
            if (pulse?.Tool is not null)
            {
                if (!TryDrawNativeTool(visual, pulse))
                {
                    Vector2 icon = screen + ToolIconOffset(pulse.Facing);
                    pulse.Tool.drawInMenu(spriteBatch, icon, 0.45f, 0.9f, Math.Min(1f, depth + 0.0002f), StackDrawType.Hide, Color.White, drawShadow: false);
                }
            }
            visual.LastFailure = null;
            return true;
        }
        catch (Exception ex)
        {
            string message = $"{ex.GetType().Name}: {ex.Message}";
            if (!this.lastFailures.TryGetValue(record.Identity, out string? previous) || previous != message)
                this.monitor.Log($"HY-APPEARANCE-RENDER-FAILED: {record.Identity} fell back to its placeholder body. {message}", LogLevel.Warn);
            this.lastFailures[record.Identity] = message;
            body.IsInvisible = false;
            body.HideShadow = false;
            return false;
        }
    }

    public AppearanceActionSnapshot? GetActionSnapshot(CompanionIdentity identity)
    {
        if (!this.actions.TryGetValue(identity, out ActionPulse? pulse))
            return null;
        return new AppearanceActionSnapshot(pulse.OperationId, pulse.Kind, pulse.Phase, pulse.Tool?.QualifiedItemId ?? "none", pulse.Facing, ResolveActionFrame(pulse, out _), pulse.RemainingTicks, pulse.CommitQueued, this.lastFailures.GetValueOrDefault(identity));
    }

    public bool IsPresenting(CompanionIdentity identity) => this.actions.ContainsKey(identity);

    public string DescribeProfile(CompanionRecord record) =>
        $"profile={record.Appearance.ProfileId}, generation={record.Appearance.Generation}, hair={record.Appearance.HairStyle}, skin={record.Appearance.Skin}, shirt={record.Appearance.ShirtId}, pants={record.Appearance.PantsId}";

    public string? GetLastFailure(CompanionIdentity identity) => this.lastFailures.GetValueOrDefault(identity);

    public void ClearRuntime()
    {
        foreach ((_, NPC body) in this.bodies.BoundBodies)
        {
            body.IsInvisible = false;
            body.HideShadow = false;
        }
        this.actions.Clear();
        this.visuals.Clear();
        this.lastFailures.Clear();
        this.lastUpdateTick = 0;
    }

    private VisualRuntime GetOrCreateVisual(CompanionRecord record)
    {
        if (this.visuals.TryGetValue(record.Identity, out VisualRuntime? visual) && visual.ProfileId == record.Appearance.ProfileId)
            return visual;
        Farmer farmer = CreateVisualFarmer(record.Appearance, $"YuiToIsshoVisual_{record.OwnerId}_{record.Slot}");
        visual = new VisualRuntime(record.Appearance.ProfileId, farmer);
        this.visuals[record.Identity] = visual;
        return visual;
    }

    private int ResolveFrame(CompanionIdentity identity, NPC body, VisualRuntime visual, ulong tick, int facing, out bool flip)
    {
        if (this.actions.TryGetValue(identity, out ActionPulse? pulse))
            return ResolveActionFrame(pulse, out flip);
        flip = facing == 3;
        bool walking = tick - visual.LastMovementTick <= MovementFacingHoldTicks;
        if (!walking)
            return facing switch { 0 => 12, 1 => 6, 2 => 0, _ => 6 };
        ulong frameTicks = body.Speed >= Farmer.runningSpeed ? 6UL : 12UL;
        int phase = (int)(tick / frameTicks) % 4;
        int[] down = { 1, 0, 2, 0 };
        int[] side = { 7, 6, 8, 6 };
        int[] up = { 13, 12, 14, 12 };
        return facing switch { 0 => up[phase], 1 => side[phase], 2 => down[phase], _ => side[phase] };
    }

    private static int ResolveActionFrame(ActionPulse pulse, out bool flip)
    {
        int facing = pulse.Facing;
        VisualClip clip = pulse.Kind switch
        {
            AppearanceActionKinds.Watering => FacingClip(facing, WaterUp, WaterSide, WaterDown, WaterSideLeft),
            AppearanceActionKinds.Chopping or AppearanceActionKinds.Mining or AppearanceActionKinds.Digging => FacingClip(facing, ToolUp, ToolSide, ToolDown, ToolSideLeft),
            AppearanceActionKinds.HarvestGrab or AppearanceActionKinds.Forage or AppearanceActionKinds.Handoff or AppearanceActionKinds.Crafting or AppearanceActionKinds.Planting => FacingClip(facing, HarvestUp, HarvestSide, HarvestDown, HarvestSideLeft),
            AppearanceActionKinds.HarvestScythe or AppearanceActionKinds.Mowing or AppearanceActionKinds.CombatSword or AppearanceActionKinds.CombatClub => FacingClip(facing, SwordUp, SwordSide, SwordDown, SwordSideLeft),
            AppearanceActionKinds.Petting => FacingClip(facing, IdleUp, IdleSide, IdleDown, IdleSideLeft),
            AppearanceActionKinds.Shearing => FacingClip(facing, ShearUp, ShearSide, ShearDown, ShearSideLeft),
            AppearanceActionKinds.Milking => FacingClip(facing, MilkUp, MilkSide, MilkDown, MilkSideLeft),
            AppearanceActionKinds.Fishing when pulse.Phase == "Cast" => FacingClip(facing, FishCastUp, FishCastSide, FishCastDown, FishCastSideLeft),
            AppearanceActionKinds.Fishing when pulse.Phase == "Waiting" => FacingClip(facing, FishWaitUp, FishWaitSide, FishWaitDown, FishWaitSideLeft),
            AppearanceActionKinds.Fishing when pulse.Phase is "Reel" or "Caught" => FacingClip(facing, FishDoneUp, FishDoneSide, FishDoneDown, FishDoneSideLeft),
            AppearanceActionKinds.CombatDagger => FacingClip(facing, DaggerUp, DaggerSide, DaggerDown, DaggerSideLeft),
            AppearanceActionKinds.Sitting or AppearanceActionKinds.Swinging => FacingClip(facing, SitUp, SitSide, SitDown, SitSideLeft),
            _ => FacingClip(facing, IdleUp, IdleSide, IdleDown, IdleSideLeft),
        };
        flip = clip.Flip;
        if (pulse.Phase == "Prepare" || clip.Frames.Length == 1)
            return clip.Frames[0];
        int elapsed = Math.Max(0, pulse.TotalTicks - pulse.RemainingTicks);
        int frameIndex = Math.Min(clip.Frames.Length - 1, elapsed * clip.Frames.Length / Math.Max(1, pulse.TotalTicks));
        return clip.Frames[frameIndex];
    }

    private int ResolveIdleFacing(CompanionRecord record, NPC body, VisualRuntime visual)
    {
        int bodyFacing = NormalizeFacing(body.FacingDirection);
        if (body.controller is not null || !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return bodyFacing;

        Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is not null && ReferenceEquals(owner.currentLocation, body.currentLocation))
        {
            Vector2 delta = owner.Position - body.Position;
            if (Math.Abs(owner.TilePoint.X - body.TilePoint.X) + Math.Abs(owner.TilePoint.Y - body.TilePoint.Y) <= IdleAwarenessDistance)
            {
                visual.IdleFacing = FacingFromDelta(delta, bodyFacing);
                visual.HasIdleFacing = true;
                visual.NextIdleTurnTick = this.lastUpdateTick + IdleTurnDelay(record.Identity);
                return visual.IdleFacing;
            }
        }

        if (!visual.HasIdleFacing)
        {
            visual.IdleFacing = bodyFacing;
            visual.HasIdleFacing = true;
            visual.NextIdleTurnTick = this.lastUpdateTick + IdleTurnDelay(record.Identity);
        }
        else if (this.lastUpdateTick >= visual.NextIdleTurnTick)
        {
            visual.IdleFacing = (visual.IdleFacing + 1) % 4;
            visual.NextIdleTurnTick = this.lastUpdateTick + IdleTurnDelay(record.Identity);
        }
        return visual.IdleFacing;
    }

    private static bool TryDrawNativeTool(VisualRuntime visual, ActionPulse pulse)
    {
        if (pulse.Tool is not Tool || pulse.Kind is AppearanceActionKinds.Fishing or AppearanceActionKinds.Shearing or AppearanceActionKinds.Milking)
            return false;
        if (!CompanionVisualToolAnimation.TryResolve(pulse.Kind, pulse.Facing, out VisualToolAnimation animation))
            return false;
        if (visual.ToolOperationId != pulse.OperationId)
        {
            visual.VisualTool = pulse.Tool.getOne() as Tool;
            visual.ToolOperationId = pulse.OperationId;
        }
        if (visual.VisualTool is null)
            return false;

        int elapsed = Math.Max(0, pulse.TotalTicks - pulse.RemainingTicks);
        try
        {
            visual.Farmer.CurrentTool = visual.VisualTool;
            visual.Farmer.UsingTool = true;
            CompanionVisualToolAnimation.Draw(visual.Farmer, visual.VisualTool, animation, elapsed, pulse.TotalTicks);
            return true;
        }
        finally
        {
            visual.Farmer.UsingTool = false;
            visual.Farmer.CurrentTool = null;
        }
    }

    private static int CommitDuration(string kind) => kind switch
    {
        AppearanceActionKinds.Shearing or AppearanceActionKinds.Milking => 96,
        AppearanceActionKinds.HarvestGrab or AppearanceActionKinds.Forage or AppearanceActionKinds.Handoff or AppearanceActionKinds.Crafting or AppearanceActionKinds.Planting => 30,
        AppearanceActionKinds.CombatDagger => 18,
        _ => DefaultCommitTicks,
    };

    private static void UpdateIdleEyes(Farmer farmer, CompanionIdentity identity, ulong tick, bool idle)
    {
        if (!idle)
        {
            farmer.currentEyes = Farmer.eyesOpen;
            return;
        }
        ulong phase = (tick + IdentityVisualOffset(identity)) % 240UL;
        farmer.currentEyes = phase switch
        {
            >= 222UL and < 225UL => Farmer.eyesHalfShut,
            >= 225UL and < 229UL => Farmer.eyesClosed,
            >= 229UL and < 232UL => Farmer.eyesHalfShut,
            _ => Farmer.eyesOpen,
        };
    }

    private static float IdleBreathingOffset(CompanionIdentity identity, ulong tick)
    {
        double phase = (tick + IdentityVisualOffset(identity)) * Math.PI / 60d;
        return (float)(-0.75d - Math.Sin(phase) * 0.75d);
    }

    private static ulong IdentityVisualOffset(CompanionIdentity identity) => unchecked((ulong)identity.OwnerId + (ulong)(identity.Slot * 53));

    private static VisualClip FacingClip(int facing, VisualClip up, VisualClip side, VisualClip down, VisualClip left) => facing switch { 0 => up, 1 => side, 2 => down, _ => left };

    private static readonly VisualClip ToolDown = new(new[] { 66, 67, 68, 69, 70 });
    private static readonly VisualClip ToolSide = new(new[] { 48, 49, 50, 51, 52 });
    private static readonly VisualClip ToolUp = new(new[] { 36, 37, 38, 63, 62 });
    private static readonly VisualClip ToolSideLeft = new(ToolSide.Frames, true);
    private static readonly VisualClip WaterDown = new(new[] { 54, 54, 55, 25 });
    private static readonly VisualClip WaterSide = new(new[] { 58, 58, 59, 45 });
    private static readonly VisualClip WaterUp = new(new[] { 62, 62, 63, 46 });
    private static readonly VisualClip WaterSideLeft = new(WaterSide.Frames, true);
    private static readonly VisualClip SwordDown = new(new[] { 24, 25, 26, 27, 28, 29 });
    private static readonly VisualClip SwordSide = new(new[] { 30, 31, 32, 33, 34, 35 });
    private static readonly VisualClip SwordUp = new(new[] { 36, 37, 38, 39, 40, 41 });
    private static readonly VisualClip SwordSideLeft = new(SwordSide.Frames, true);
    private static readonly VisualClip DaggerDown = new(new[] { 25, 27 });
    private static readonly VisualClip DaggerSide = new(new[] { 34, 33 });
    private static readonly VisualClip DaggerUp = new(new[] { 40, 38 });
    private static readonly VisualClip DaggerSideLeft = new(DaggerSide.Frames, true);
    private static readonly VisualClip HarvestDown = new(new[] { 54, 55, 56, 57 });
    private static readonly VisualClip HarvestSide = new(new[] { 58, 59, 60, 61 });
    private static readonly VisualClip HarvestUp = new(new[] { 62, 63, 64, 65 });
    private static readonly VisualClip HarvestSideLeft = new(HarvestSide.Frames, true);
    private static readonly VisualClip ShearDown = new(new[] { 78, 79, 78, 79 });
    private static readonly VisualClip ShearSide = new(new[] { 80, 81, 80, 81 });
    private static readonly VisualClip ShearUp = new(new[] { 82, 83, 82, 83 });
    private static readonly VisualClip ShearSideLeft = new(ShearSide.Frames, true);
    private static readonly VisualClip MilkDown = new(new[] { 54, 55, 54, 55 });
    private static readonly VisualClip MilkSide = new(new[] { 58, 59, 58, 59 });
    private static readonly VisualClip MilkUp = new(new[] { 62, 63, 62, 63 });
    private static readonly VisualClip MilkSideLeft = new(MilkSide.Frames, true);
    private static readonly VisualClip FishCastDown = ToolDown;
    private static readonly VisualClip FishCastSide = ToolSide;
    private static readonly VisualClip FishCastUp = new(new[] { 76, 38, 63, 62, 63 });
    private static readonly VisualClip FishCastSideLeft = new(FishCastSide.Frames, true);
    private static readonly VisualClip FishWaitDown = new(new[] { 70 });
    private static readonly VisualClip FishWaitSide = new(new[] { 89 });
    private static readonly VisualClip FishWaitUp = new(new[] { 44 });
    private static readonly VisualClip FishWaitSideLeft = new(FishWaitSide.Frames, true);
    private static readonly VisualClip FishDoneDown = new(new[] { 74 });
    private static readonly VisualClip FishDoneSide = new(new[] { 72 });
    private static readonly VisualClip FishDoneUp = new(new[] { 76 });
    private static readonly VisualClip FishDoneSideLeft = new(FishDoneSide.Frames, true);
    private static readonly VisualClip IdleDown = new(new[] { 0 });
    private static readonly VisualClip IdleSide = new(new[] { 6 });
    private static readonly VisualClip IdleUp = new(new[] { 12 });
    private static readonly VisualClip IdleSideLeft = new(IdleSide.Frames, true);
    private static readonly VisualClip SitDown = new(new[] { 107 });
    private static readonly VisualClip SitSide = new(new[] { 117 });
    private static readonly VisualClip SitUp = new(new[] { 113 });
    private static readonly VisualClip SitSideLeft = new(SitSide.Frames, true);

    internal static Farmer CreateVisualFarmer(CompanionAppearanceProfile profile, string runtimeName)
    {
        var farmer = new Farmer(new FarmerSprite(null!), Vector2.Zero, 2, runtimeName, new List<Item>(), isMale: false);
        farmer.changeHairStyle(profile.HairStyle);
        farmer.changeSkinColor(profile.Skin, force: true);
        farmer.changeShirt(profile.ShirtId);
        farmer.changePantStyle(profile.PantsId);
        farmer.changeShoeColor(profile.ShoeColorId);
        farmer.changeHairColor(Unpack(profile.HairColor));
        farmer.changeEyeColor(Unpack(profile.EyeColor));
        farmer.changePantsColor(Unpack(profile.PantsColor));
        farmer.hat.Value = null;
        farmer.accessory.Value = profile.AccessoryId;
        return farmer;
    }

    private static void Generate(CompanionAppearanceProfile profile, IReadOnlyList<int> hairs)
    {
        profile.ProfileId = Guid.NewGuid().ToString("N");
        profile.ProfileSchemaVersion = CompanionAppearanceProfile.CurrentProfileSchemaVersion;
        profile.Generation = Math.Max(1, profile.Generation);
        profile.BodyType = CompanionBodyTypes.Feminine;
        profile.HairStyle = hairs.Count == 0 ? 0 : hairs[RandomNumberGenerator.GetInt32(hairs.Count)];
        profile.Skin = RandomNumberGenerator.GetInt32(4, 18);
        profile.ShirtId = "1000";
        profile.PantsId = "0";
        profile.ShoeColorId = "2";
        profile.HairColor = HairColors[RandomNumberGenerator.GetInt32(HairColors.Length)];
        profile.EyeColor = Packed(59, 105, 142);
        profile.PantsColor = PantsColors[RandomNumberGenerator.GetInt32(PantsColors.Length)];
        profile.AccessoryId = -1;
        profile.HatQualifiedItemId = string.Empty;
        profile.IsInitialized = true;
    }

    private static int FacingFromDelta(Vector2 delta, int fallback)
    {
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            return delta.X > 0f ? 1 : 3;
        if (Math.Abs(delta.Y) > 0.01f)
            return delta.Y > 0f ? 2 : 0;
        return fallback;
    }
    private static ulong IdleTurnDelay(CompanionIdentity identity) => MinimumIdleTurnTicks
        + (unchecked((ulong)identity.OwnerId) % IdleTurnVarianceTicks + (ulong)(identity.Slot * 37)) % IdleTurnVarianceTicks;
    private static int NormalizeFacing(int facing) => facing is >= 0 and <= 3 ? facing : 2;
    private static Vector2 ToolIconOffset(int facing) => facing switch { 0 => new Vector2(35f, -26f), 1 => new Vector2(48f, 30f), 2 => new Vector2(34f, 58f), _ => new Vector2(-12f, 30f) };
    private static uint Packed(byte r, byte g, byte b) => new Color(r, g, b).PackedValue;
    private static Color Unpack(uint value) { Color color = default; color.PackedValue = value; return color; }

    private sealed class VisualRuntime
    {
        public VisualRuntime(string profileId, Farmer farmer) { this.ProfileId = profileId; this.Farmer = farmer; this.MovementFacing = 2; }
        public string ProfileId { get; }
        public Farmer Farmer { get; }
        public string? LastFailure { get; set; }
        public string? LastLocationName { get; set; }
        public Vector2 LastBodyPosition { get; set; }
        public bool HasBodyPosition { get; set; }
        public ulong LastMovementTick { get; set; }
        public int MovementFacing { get; set; }
        public int IdleFacing { get; set; }
        public bool HasIdleFacing { get; set; }
        public ulong NextIdleTurnTick { get; set; }
        public string? ToolOperationId { get; set; }
        public Tool? VisualTool { get; set; }
    }

    private sealed record VisualClip(int[] Frames, bool Flip = false);

    private sealed class ActionPulse
    {
        public ActionPulse(string operationId, string kind, string phase, Item? tool, int facing, int remainingTicks)
        { this.OperationId = operationId; this.Kind = kind; this.Phase = phase; this.Tool = tool; this.Facing = facing; this.SetDuration(remainingTicks); }
        public string OperationId { get; }
        public string Kind { get; }
        public string Phase { get; set; }
        public Item? Tool { get; }
        public int Facing { get; }
        public int RemainingTicks { get; set; }
        public int TotalTicks { get; private set; }
        public bool CommitQueued { get; set; }
        public void SetDuration(int ticks) { this.TotalTicks = ticks; this.RemainingTicks = ticks; }
    }
}
