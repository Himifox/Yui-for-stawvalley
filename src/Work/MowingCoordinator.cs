using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal readonly record struct MowCommandResult(bool IsSuccess, string Code, string Message)
{
    public static MowCommandResult Success(string code, string message) => new(true, code, message);

    public static MowCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class MowingCoordinator
{
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 30;
    private const int StuckTimeoutTicks = 300;
    private const int MaximumPathAttempts = 5;
    private const int VanillaSwingFrameCount = 6;
    private const int VanillaSwingPower = 1;
    private const int VanillaToolReachPixels = 48;

    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, MowingTask> tasks = new();

    public MowingCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public MowCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId, Func<Vector2, bool>? workScope = null)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return MowCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before mowing.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return MowCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when mowing starts.");

        MeleeWeapon? scythe = this.inventories.FindFirst<MeleeWeapon>(identity, item => item.isScythe());
        if (scythe is null)
            return MowCommandResult.Failure("SCYTHE-MISSING", "This Yui's bag has no real vanilla-recognized scythe.");

        Vector2 targetTile = new(tileX, tileY);
        if (workScope is not null && !workScope(targetTile))
            return MowCommandResult.Failure("TARGET-OUTSIDE-WORK-SCOPE", $"Seed grass {tileX},{tileY} is outside the authoritative work scope.");
        if (!TryGetMowTarget(owner.currentLocation, targetTile, null, out MowTarget seedTarget))
            return MowCommandResult.Failure("TARGET-NOT-MOWABLE", $"Tile {tileX},{tileY} has no Grass terrain feature or weed litter object.");

        if (!TryFindSwingApproach(owner.currentLocation, targetTile, body, scythe, owner, seedTarget, out Vector2 approachTile, out Vector2 approachPosition, out int facing, out int swingFrame, out SwingRegion region))
            return MowCommandResult.Failure("TARGET-UNREACHABLE", "No reachable standing position and facing can include the seed vegetation in the vanilla swing area.");

        TaskTargetKey[] targets = region.Targets
            .Select(target => new TaskTargetKey(owner.currentLocation.NameOrUniqueName, target.Kind, $"{(int)target.Tile.X},{(int)target.Tile.Y}"))
            .ToArray();
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Mowing", targets);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        int outsideScopeCount = workScope is null ? 0 : region.Targets.Count(target => !workScope(target.Tile));
        this.tasks.Add(identity, new MowingTask(
            begin.Session,
            targetTile,
            approachTile,
            approachPosition,
            facing,
            swingFrame,
            owner.currentLocation,
            owner,
            scythe,
            region.Targets,
            outsideScopeCount,
            body.Position
        ));
        string seedScope = workScope is null ? "not-applicable" : workScope(targetTile).ToString().ToLowerInvariant();
        bool stayedInPlace = approachTile.ToPoint() == body.TilePoint;
        this.monitor.Log($"HY-MOW-STARTED: {identity} reserved {region.Targets.Count} mowable target(s) around {begin.Session.Target} for {operationId}; stand={(int)approachTile.X},{(int)approachTile.Y} facing={facing} frame={swingFrame} stayed={stayedInPlace} seedInScope={seedScope} affectedOutside={outsideScopeCount}.", LogLevel.Info);
        return MowCommandResult.Success("STARTED", $"Mowing {operationId} reserved {region.Targets.Count} mowable target(s).");
    }

    public void Update(ulong tick)
    {
        foreach (MowingTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public MowCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out MowingTask? task))
            return MowCommandResult.Success("ALREADY-IDLE", $"{identity} has no mowing task.");

        return this.Complete(task, code, $"Operation {task.OperationId} was cancelled before its swing.", false);
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out MowingTask? task) ? task.Scythe : null;

    public void CancelAll(string code)
    {
        foreach (CompanionIdentity identity in this.tasks.Keys.ToArray())
            this.Cancel(identity, code);
    }

    private void UpdateOne(MowingTask task, ulong tick)
    {
        if (!this.execution.IsCurrent(task.Session))
        {
            this.execution.AbandonRuntime(task.Session);
            this.tasks.Remove(task.Identity);
            return;
        }

        if (!this.bodies.TryGetBody(task.Identity, out NPC body)
            || body.currentLocation is null
            || !ReferenceEquals(body.currentLocation, task.Location))
        {
            this.Complete(task, "BODY-INVALID", "The companion body became unavailable or changed location.", false);
            return;
        }

        if (task.Owner.currentLocation is null || !ReferenceEquals(task.Owner.currentLocation, task.Location))
        {
            this.Complete(task, "OWNER-LEFT-LOCATION", "The owner left the mowing location.", false);
            return;
        }

        if (!this.inventories.ContainsExact(task.Identity, task.Scythe) || !task.Scythe.isScythe())
        {
            this.Complete(task, "SCYTHE-CHANGED", "The exact reserved scythe left this Yui's bag or is no longer a scythe.", false);
            return;
        }

        if (!this.ValidateReservedGrass(task, task.ApproachPosition, out string validationFailure))
        {
            this.Complete(task, "AREA-CHANGED", validationFailure, false);
            return;
        }

        if (body.TilePoint == task.ApproachTile.ToPoint())
        {
            this.bodies.Halt(task.Identity);
            body.Position = task.ApproachPosition;
            if (!this.ValidateReservedGrass(task, body.Position, out string finalValidationFailure))
            {
                this.Complete(task, "AREA-SHIFTED", finalValidationFailure, false);
                return;
            }
            body.faceDirection(task.Facing);
            this.Settle(task, body);
            return;
        }

        TaskNavigationResult progress = this.navigation.Observe(task.Identity, body, task.Navigation, tick, StuckTimeoutTicks, MaximumPathAttempts, RepathDelayTicks);
        if (progress.BudgetExhausted)
        {
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The standing tile stayed unreachable through the bounded retry budget.", false);
            return;
        }
        if (!progress.CanIssuePath)
            return;

        if (!CompanionPathing.IsStandable(body, task.Location, task.ApproachTile))
        {
            this.Complete(task, "APPROACH-BLOCKED", "The reserved standing tile became blocked.", false);
            return;
        }

        body.controller = new PathFindController(
            body,
            task.Location,
            task.ApproachTile.ToPoint(),
            task.Facing,
            null,
            PathSearchLimit
        );
        task.Session.MarkTraveling();
        this.navigation.MarkPathIssued(task.Navigation, body.Position, tick, RepathDelayTicks);
    }

    private bool ValidateReservedGrass(MowingTask task, Vector2 projectedOwnerPosition, out string failure)
    {
        SwingRegion currentRegion = GetSwingRegion(
            task.Location,
            task.Scythe,
            task.Owner,
            projectedOwnerPosition,
            task.Facing,
            task.SwingFrame
        );

        if (currentRegion.Targets.Count != task.Targets.Count)
        {
            failure = "The vanilla swing region gained or lost a mowable target before settlement.";
            return false;
        }

        foreach (MowTarget target in task.Targets)
        {
            MowTarget? current = currentRegion.Targets.FirstOrDefault(candidate => candidate.Kind == target.Kind && candidate.Tile == target.Tile);
            if (current is null
                || !ReferenceEquals(current.Instance, target.Instance)
                || current.State != target.State)
            {
                failure = $"{target.Kind} at {target.Tile.X},{target.Tile.Y} changed before settlement.";
                return false;
            }

        }

        failure = string.Empty;
        return true;
    }

    private void Settle(MowingTask task, NPC body)
    {
        // Vanilla melee settlement requires a real local Farmer for authority. Never project that
        // Farmer while their own tool animation or movement lock is active; retry on a later tick.
        if (!OwnerContextLease.CanProject(task.Owner))
            return;
        if (!task.Session.TryEnterSettlement())
            return;
        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Mowing, $"{task.OperationId}:mow");
        if (!cost.IsSuccess)
        {
            this.Complete(task, cost.Result.Code, cost.Result.Message, false);
            return;
        }

        this.appearance.Prepare(task.Identity, task.OperationId, AppearanceActionKinds.Mowing, task.Scythe, task.Facing);
        try
        {
            using OwnerContextLease context = OwnerContextLease.Project(task.Owner, body.Position, task.Facing);
            task.Owner.FarmerSprite.currentAnimationIndex = task.SwingFrame;
            Vector2 toolLocation = task.Owner.GetToolLocation(ignoreClick: true);
            this.inventories.RunWithMeleeWeaponSelected(
                task.Identity,
                task.Owner,
                task.Scythe,
                () => task.Scythe.DoDamage(task.Location, (int)toolLocation.X, (int)toolLocation.Y, task.Facing, VanillaSwingPower, task.Owner)
            );
            cost.Commit();
        }
        catch (Exception ex)
        {
            this.Complete(task, "SETTLEMENT-ERROR", $"The vanilla swing stopped without retry after an error: {ex.Message}", false);
            return;
        }

        bool changed = false;
        foreach (MowTarget target in task.Targets)
        {
            if (!TryGetMowTarget(task.Location, target.Tile, target.Kind, out MowTarget current))
            {
                changed = true;
                continue;
            }

            if (!ReferenceEquals(current.Instance, target.Instance))
            {
                this.Complete(task, "TARGET-REPLACED-AFTER-SWING", $"{target.Kind} at {target.Tile.X},{target.Tile.Y} was replaced during the vanilla swing; no retry will occur.", false);
                return;
            }

            if (current.State < target.State)
                changed = true;
        }

        if (changed)
        {
            this.appearance.Commit(task.Identity, task.OperationId);
            this.Complete(task, "COMMITTED", $"Vanilla settled one multi-target scythe swing across {task.Targets.Count} reserved grass/weed target(s); affectedOutside={task.OutsideScopeCount}.", true);
            return;
        }

        this.Complete(task, "VANILLA-NO-CHANGE", "The one permitted vanilla swing changed no reserved grass; no retry was attempted.", false);
    }

    private MowCommandResult Complete(MowingTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-MOW-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
    }

    private static SwingRegion GetSwingRegion(
        GameLocation location,
        MeleeWeapon scythe,
        Farmer owner,
        Vector2 projectedOwnerPosition,
        int facing,
        int swingFrame)
    {
        Rectangle projectedBounds = owner.GetBoundingBox();
        projectedBounds.Offset(
            (int)(projectedOwnerPosition.X - owner.Position.X),
            (int)(projectedOwnerPosition.Y - owner.Position.Y)
        );
        Vector2 toolLocation = ProjectedToolLocation(projectedBounds, facing);
        Vector2 tileLocation1 = Vector2.Zero;
        Vector2 tileLocation2 = Vector2.Zero;
        Rectangle area = scythe.getAreaOfEffect(
            (int)toolLocation.X,
            (int)toolLocation.Y,
            facing,
            ref tileLocation1,
            ref tileLocation2,
            projectedBounds,
            swingFrame
        );

        List<MowTarget> targets = new();
        foreach (Vector2 tile in Utility.getListOfTileLocationsForBordersOfNonTileRectangle(area).Distinct())
        {
            if (TryGetGrass(location, tile, out Grass grass))
                targets.Add(new MowTarget(tile, WorldTargetCategories.Grass, grass, grass.numberOfWeeds.Value));
            if (location.Objects.TryGetValue(tile, out SObject? weed) && weed.IsWeeds())
                targets.Add(new MowTarget(tile, WorldTargetCategories.Weed, weed, 1));
        }

        targets.Sort((left, right) =>
        {
            int yComparison = left.Tile.Y.CompareTo(right.Tile.Y);
            if (yComparison != 0)
                return yComparison;
            int xComparison = left.Tile.X.CompareTo(right.Tile.X);
            return xComparison != 0 ? xComparison : string.CompareOrdinal(left.Kind, right.Kind);
        });
        return new SwingRegion(area, targets);
    }

    private static Vector2 ProjectedToolLocation(Rectangle bounds, int facing) => facing switch
    {
        0 => new Vector2(bounds.Center.X, bounds.Y - VanillaToolReachPixels),
        1 => new Vector2(bounds.Right + VanillaToolReachPixels, bounds.Center.Y),
        2 => new Vector2(bounds.Center.X, bounds.Bottom + VanillaToolReachPixels),
        _ => new Vector2(bounds.X - VanillaToolReachPixels, bounds.Center.Y),
    };

    private static bool TryGetGrass(GameLocation location, Vector2 tile, out Grass grass)
    {
        if (location.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature) && feature is Grass found)
        {
            grass = found;
            return true;
        }

        grass = null!;
        return false;
    }

    private static bool TryGetMowTarget(GameLocation location, Vector2 tile, string? requiredKind, out MowTarget target)
    {
        if ((requiredKind is null or WorldTargetCategories.Grass) && TryGetGrass(location, tile, out Grass grass))
        {
            target = new MowTarget(tile, WorldTargetCategories.Grass, grass, grass.numberOfWeeds.Value);
            return true;
        }
        if ((requiredKind is null or WorldTargetCategories.Weed)
            && location.Objects.TryGetValue(tile, out SObject? weed)
            && weed.IsWeeds())
        {
            target = new MowTarget(tile, WorldTargetCategories.Weed, weed, 1);
            return true;
        }
        target = null!;
        return false;
    }

    private bool TryFindSwingApproach(GameLocation location, Vector2 target, NPC body, MeleeWeapon scythe, Farmer owner, MowTarget seedTarget, out Vector2 approach, out Vector2 approachPosition, out int facing, out int swingFrame, out SwingRegion region)
    {
        var options = new List<SwingOption>();
        Point currentTile = body.TilePoint;
        Vector2[] candidates =
        {
            target + new Vector2(1f, 0f),
            target + new Vector2(-1f, 0f),
            target + new Vector2(0f, 1f),
            target + new Vector2(0f, -1f),
        };
        foreach (Vector2 candidate in candidates)
        {
            bool alreadyStanding = candidate.ToPoint() == currentTile;
            if (!alreadyStanding && !CompanionPathing.IsStandable(body, location, candidate))
                continue;

            Vector2 projectedPosition = ProjectPositionToTile(body, candidate);
            int candidateFacing = TaskNavigationService.FacingToward(candidate, target);
            for (int candidateFrame = 0; candidateFrame < VanillaSwingFrameCount; candidateFrame++)
            {
                SwingRegion candidateRegion = GetSwingRegion(location, scythe, owner, projectedPosition, candidateFacing, candidateFrame);
                if (!candidateRegion.Targets.Any(candidateTarget => ReferenceEquals(candidateTarget.Instance, seedTarget.Instance)))
                    continue;
                options.Add(new SwingOption(
                    candidate,
                    projectedPosition,
                    candidateFacing,
                    candidateFrame,
                    candidateRegion,
                    Vector2.DistanceSquared(projectedPosition, body.Position),
                    TurnDistance(body.FacingDirection, candidateFacing)
                ));
            }
        }

        foreach (SwingOption option in options
            .OrderBy(candidate => candidate.MovementCost)
            .ThenBy(candidate => candidate.TurnCost)
            .ThenByDescending(candidate => candidate.Region.Targets.Count)
            .ThenBy(candidate => candidate.Tile.Y)
            .ThenBy(candidate => candidate.Tile.X)
            .ThenBy(candidate => candidate.Facing)
            .ThenBy(candidate => candidate.Frame))
        {
            bool alreadyStanding = option.Tile.ToPoint() == currentTile;
            if (!alreadyStanding && !this.navigation.CanReach(body, location, option.Tile, option.Facing, PathSearchLimit))
                continue;
            approach = option.Tile;
            approachPosition = option.Position;
            facing = option.Facing;
            swingFrame = option.Frame;
            region = option.Region;
            return true;
        }

        approach = default;
        approachPosition = default;
        facing = 2;
        swingFrame = 0;
        region = new SwingRegion(Rectangle.Empty, new List<MowTarget>());
        return false;
    }

    private static Vector2 ProjectPositionToTile(NPC body, Vector2 tile)
    {
        Vector2 centeredStandingPixel = tile * Game1.tileSize + new Vector2(Game1.tileSize / 2f, Game1.tileSize / 2f);
        return body.Position + centeredStandingPixel - body.StandingPixel.ToVector2();
    }

    private static int TurnDistance(int from, int to)
    {
        int delta = Math.Abs((from & 3) - (to & 3));
        return Math.Min(delta, 4 - delta);
    }

    private static MowCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? MowCommandResult.Success(result.Code, result.Message)
        : MowCommandResult.Failure(result.Code, result.Message);

    private sealed record MowTarget(Vector2 Tile, string Kind, object Instance, int State);

    private sealed record SwingRegion(Rectangle Area, List<MowTarget> Targets);

    private sealed record SwingOption(
        Vector2 Tile,
        Vector2 Position,
        int Facing,
        int Frame,
        SwingRegion Region,
        float MovementCost,
        int TurnCost);

    private sealed class MowingTask
    {
        public MowingTask(
            TaskSession session,
            Vector2 targetTile,
            Vector2 approachTile,
            Vector2 approachPosition,
            int facing,
            int swingFrame,
            GameLocation location,
            Farmer owner,
            MeleeWeapon scythe,
            List<MowTarget> targets,
            int outsideScopeCount,
            Vector2 initialPosition)
        {
            this.Session = session;
            this.TargetTile = targetTile;
            this.ApproachTile = approachTile;
            this.ApproachPosition = approachPosition;
            this.Facing = facing;
            this.SwingFrame = swingFrame;
            this.Location = location;
            this.Owner = owner;
            this.Scythe = scythe;
            this.Targets = targets;
            this.OutsideScopeCount = outsideScopeCount;
            this.Navigation = new TaskNavigationState(initialPosition, 0);
        }

        public TaskSession Session { get; }
        public CompanionIdentity Identity => this.Session.Identity;
        public string OperationId => this.Session.OperationId;
        public TaskTargetKey Target => this.Session.Target;
        public Vector2 TargetTile { get; }
        public Vector2 ApproachTile { get; }
        public Vector2 ApproachPosition { get; }
        public int Facing { get; }
        public int SwingFrame { get; }
        public GameLocation Location { get; }
        public Farmer Owner { get; }
        public MeleeWeapon Scythe { get; }
        public List<MowTarget> Targets { get; }
        public int OutsideScopeCount { get; }
        public TaskNavigationState Navigation { get; }
    }
}
