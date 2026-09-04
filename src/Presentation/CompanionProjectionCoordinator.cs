using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;

namespace YuiToIssho;

internal readonly record struct ProjectionApplyResult(bool IsSuccess, string Code, string Message)
{
    public static ProjectionApplyResult Success(string code, string message) => new(true, code, message);
    public static ProjectionApplyResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class CompanionProjectionCoordinator
{
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly CompanionNetworkBodyResolver networkBodies;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, ProjectedCompanion> projections = new();
    private readonly Dictionary<CompanionIdentity, VisualFarmer> visuals = new();
    private string epoch = string.Empty;
    private ulong snapshotVersion;
    private ulong estimatedHostTick;
    private Func<CompanionIdentity, AgentRuntimeSnapshot?>? getAgentSnapshot;
    private Func<CompanionIdentity, WorkRuntimeSnapshot?>? getWorkSnapshot;
    private Func<CompanionIdentity, CombatRuntimeSnapshot?>? getCombatSnapshot;
    private Func<CompanionIdentity, CompanionSpeechSnapshot?>? getSpeechSnapshot;

    public CompanionProjectionCoordinator(
        CompanionRegistry registry,
        CompanionBodyBinder bodies,
        CompanionInventoryStore inventories,
        CompanionAppearanceCoordinator appearance,
        CompanionNetworkBodyResolver networkBodies,
        IMonitor monitor)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.appearance = appearance;
        this.networkBodies = networkBodies;
        this.monitor = monitor;
    }

    public ulong SnapshotVersion => this.snapshotVersion;

    public bool TryGetProjectedState(CompanionIdentity identity, out CompanionSnapshotDto state)
    {
        if (this.projections.TryGetValue(identity, out ProjectedCompanion? projection))
        {
            state = projection.State;
            return true;
        }
        state = null!;
        return false;
    }

    public string DescribeNetworkState(CompanionIdentity identity)
    {
        if (!this.projections.TryGetValue(identity, out ProjectedCompanion? projection))
            return $"snapshot={this.snapshotVersion} projection=none";
        return $"snapshot={this.snapshotVersion} bodyGen={projection.State.BodyGeneration} presentationRev={projection.PresentationRevision} bodySource={this.networkBodies.CurrentMode} work={projection.State.WorkPhase} matching={projection.State.WorkMatchingCount} blocked={projection.State.WorkBlockedCount} observation={projection.State.WorkObservationRevision}";
    }

    public IReadOnlyList<CompanionMenuIdentitySnapshot> BuildMenuIdentityView(long viewerId)
    {
        if (Context.IsMainPlayer)
        {
            return this.registry.Active.OrderBy(record => record.OwnerId).Select(record =>
            {
                this.appearance.EnsureProfile(record);
                bool bodyPresent = this.bodies.TryGetBody(record.Identity, out NPC body) && body.currentLocation is not null;
                return new CompanionMenuIdentitySnapshot(
                    record.Identity,
                    Bounded(record.DisplayName, 64),
                    Bounded(Game1.GetPlayer(record.OwnerId, onlyOnline: false)?.Name ?? $"Player {record.OwnerId}", 64),
                    record.OwnerId == viewerId,
                    Game1.GetPlayer(record.OwnerId, onlyOnline: true) is not null,
                    record.WantsBody,
                    bodyPresent,
                    bodyPresent ? Bounded(body.currentLocation!.NameOrUniqueName, 128) : string.Empty,
                    record.Mode,
                    record.Vitals.State,
                    record.Vitals.Health,
                    record.Vitals.MaxHealth,
                    record.Vitals.Stamina,
                    record.Vitals.MaxStamina,
                    ToDto(record.Appearance),
                    this.snapshotVersion);
            }).ToArray();
        }

        return this.projections.Values.OrderBy(projection => projection.State.OwnerId).Select(projection =>
        {
            CompanionSnapshotDto state = projection.State;
            return new CompanionMenuIdentitySnapshot(
                state.Identity,
                state.DisplayName,
                Bounded(Game1.GetPlayer(state.OwnerId, onlyOnline: false)?.Name ?? $"Player {state.OwnerId}", 64),
                state.OwnerId == viewerId,
                state.OwnerOnline,
                state.WantsBody,
                state.BodyPresent,
                state.LocationKey,
                state.Mode,
                state.VitalState,
                state.Health,
                state.MaxHealth,
                state.Stamina,
                state.MaxStamina,
                state.Appearance,
                this.snapshotVersion);
        }).ToArray();
    }

    public void AttachAgentSnapshotProvider(Func<CompanionIdentity, AgentRuntimeSnapshot?> provider)
    {
        this.getAgentSnapshot = provider;
    }

    public void AttachWorkSnapshotProvider(Func<CompanionIdentity, WorkRuntimeSnapshot?> provider)
    {
        this.getWorkSnapshot = provider;
    }

    public void AttachCombatSnapshotProvider(Func<CompanionIdentity, CombatRuntimeSnapshot?> provider) => this.getCombatSnapshot = provider;

    public void AttachSpeechSnapshotProvider(Func<CompanionIdentity, CompanionSpeechSnapshot?> provider) => this.getSpeechSnapshot = provider;

    public IReadOnlyDictionary<CompanionIdentity, CompanionPresentationDto?> BuildHostPresentationView()
    {
        if (!Context.IsMainPlayer)
            throw new InvalidOperationException("Only the authoritative host may build presentation state.");
        var view = new Dictionary<CompanionIdentity, CompanionPresentationDto?>();
        foreach (CompanionRecord record in this.registry.Active)
        {
            AppearanceActionSnapshot? action = this.appearance.GetActionSnapshot(record.Identity);
            view[record.Identity] = action is null ? null : ToDto(action.Value);
        }
        return view;
    }

    public RuntimeSnapshotDto BuildHostSnapshot(
        string sessionEpoch,
        ulong version,
        ulong tick,
        long viewerId,
        IReadOnlyDictionary<CompanionIdentity, CompanionPresentationDto?> presentations,
        IReadOnlyDictionary<CompanionIdentity, ulong> presentationRevisions)
    {
        if (!Context.IsMainPlayer || !Guid.TryParseExact(sessionEpoch, "N", out _))
            throw new InvalidOperationException("Only the authoritative host may build a session snapshot.");
        if (this.registry.Count > MultiplayerProtocol.MaxCompanionsPerSnapshot)
            throw new InvalidOperationException("The authoritative registry exceeds the bounded full-snapshot capacity.");

        var snapshot = new RuntimeSnapshotDto
        {
            SessionEpoch = sessionEpoch,
            SnapshotVersion = version,
            HostPlayerId = Game1.player.UniqueMultiplayerID,
            GeneratedTick = tick,
        };
        foreach (CompanionRecord record in this.registry.Active.OrderBy(candidate => candidate.OwnerId))
        {
            this.appearance.EnsureProfile(record);
            bool privateView = record.OwnerId == viewerId;
            bool bodyPresent = this.bodies.TryGetBody(record.Identity, out NPC body) && body.currentLocation is not null;
            AgentRuntimeSnapshot? agent = this.getAgentSnapshot?.Invoke(record.Identity);
            WorkRuntimeSnapshot? work = this.getWorkSnapshot?.Invoke(record.Identity);
            CombatRuntimeSnapshot? combat = this.getCombatSnapshot?.Invoke(record.Identity);
            CompanionSpeechSnapshot? speech = this.getSpeechSnapshot?.Invoke(record.Identity);
            PlantingTransactionRecord? planting = record.PlantingTransaction;
            presentations.TryGetValue(record.Identity, out CompanionPresentationDto? presentation);
            presentationRevisions.TryGetValue(record.Identity, out ulong presentationRevision);
            snapshot.Companions.Add(new CompanionSnapshotDto
            {
                OwnerId = record.OwnerId,
                Slot = record.Slot,
                DisplayName = Bounded(record.DisplayName, 64),
                OwnerOnline = Game1.GetPlayer(record.OwnerId, onlyOnline: true) is not null,
                WantsBody = record.WantsBody,
                Mode = record.Mode,
                BodyPresent = bodyPresent,
                BodyGeneration = bodyPresent && this.bodies.TryGetBodyGeneration(record.Identity, out ulong bodyGeneration) ? bodyGeneration : 0,
                LocationKey = bodyPresent ? Bounded(body.currentLocation!.NameOrUniqueName, 256) : string.Empty,
                PixelX = bodyPresent ? (int)Math.Clamp(body.Position.X, -1_000_000f, 1_000_000f) : 0,
                PixelY = bodyPresent ? (int)Math.Clamp(body.Position.Y, -1_000_000f, 1_000_000f) : 0,
                Facing = bodyPresent ? NormalizeFacing(body.FacingDirection) : 2,
                BagCount = privateView ? Math.Clamp(this.inventories.Count(record.Identity), 0, CompanionInventoryStore.Capacity) : 0,
                LiabilityCount = privateView ? Math.Clamp(record.StorageLiabilities.Count + record.PendingResponsibilities.Count, 0, 999) : 0,
                ActiveTransactionId = privateView ? Bounded(record.ActiveTransactionId, 160) : string.Empty,
                Health = record.Vitals.Health,
                MaxHealth = record.Vitals.MaxHealth,
                Stamina = record.Vitals.Stamina,
                MaxStamina = record.Vitals.MaxStamina,
                VitalState = record.Vitals.State,
                WorkKind = Bounded(record.WorkDirective?.Kind, 32),
                WorkLocationKey = Bounded(record.WorkDirective?.LocationKey, 256),
                WorkAnchorX = record.WorkDirective?.AnchorX ?? 0,
                WorkAnchorY = record.WorkDirective?.AnchorY ?? 0,
                WorkEndX = record.WorkDirective?.EndX ?? 0,
                WorkEndY = record.WorkDirective?.EndY ?? 0,
                WorkRadius = record.WorkDirective?.Radius ?? 0,
                WorkShape = Bounded(record.WorkDirective?.Shape, 24),
                WorkPolicy = Bounded(record.WorkDirective?.CompletionPolicy, 32),
                WorkState = Bounded(record.WorkDirective?.SuspendedReason ?? (record.WorkDirective is null ? string.Empty : "Active"), 64),
                WorkPhase = record.WorkDirective is null ? string.Empty : work?.Phase ?? WorkRuntimePhases.NotObserved,
                WorkMatchingCount = work?.MatchingCount ?? 0,
                WorkCandidateCount = work?.CandidateCount ?? 0,
                WorkBlockedCount = work?.BlockedCount ?? 0,
                WorkOperationId = Bounded(work?.CurrentOperationId, 160),
                WorkLastReason = Bounded(work?.LastReason, 64),
                WorkObservationRevision = work?.ObservationRevision ?? 0,
                CombatMode = Bounded(combat?.Mode, 24),
                CombatPhase = Bounded(combat?.Phase, 24),
                CombatRemainingSeconds = combat?.RemainingSeconds ?? 0,
                CombatCommittedSwings = combat?.CommittedSwings ?? 0,
                CombatMaximumSwings = combat?.MaximumSwings ?? 0,
                CombatTargetKind = Bounded(combat?.TargetKind, 32),
                CombatTargetDistanceBand = Bounded(combat?.TargetDistanceBand, 12),
                CombatLastOutcome = Bounded(combat?.LastOutcome, 64),
                AgentBehaviorState = agent?.BehaviorState ?? AgentBehaviorStates.Unavailable,
                AgentBrainPhase = agent?.BrainPhase ?? AgentBrainPhases.Dormant,
                AgentPlanGeneration = agent?.PlanGeneration ?? 0,
                AgentSnapshotVersion = agent?.SnapshotVersion ?? 0,
                AgentIntentId = Bounded(agent?.IntentId, 32),
                AgentStepKind = Bounded(agent?.StepKind, 32),
                AgentStepState = Bounded(agent?.StepState, 32),
                CraftRecipeKey = privateView ? Bounded(record.CraftTransaction?.RecipeKey, 128) : string.Empty,
                CraftPhase = privateView ? Bounded(record.CraftTransaction?.Phase, 32) : string.Empty,
                CraftCompletedCount = privateView ? record.CraftTransaction?.CompletedCount ?? 0 : 0,
                CraftCount = privateView ? record.CraftTransaction?.CraftCount ?? 0 : 0,
                CraftEscrowCount = privateView ? Math.Clamp(this.inventories.CraftEscrowCount(record.Identity), 0, 999) : 0,
                PlantingPhase = Bounded(planting?.Phase, 32),
                PlantingRequestedCount = planting?.RequestedCount ?? 0,
                PlantingPlantedCount = planting?.PlantedCount ?? 0,
                PlantingRemainingCount = planting is null ? 0 : Math.Max(0, planting.RequestedCount - planting.PlantedCount),
                PlantingScopeSummary = privateView && planting is not null
                    ? Bounded($"{planting.Shape}:{planting.LocationKey}@{planting.AnchorX},{planting.AnchorY}..{planting.EndX},{planting.EndY}:r{planting.Radius}", 128)
                    : string.Empty,
                PlantingCurrentStepSummary = privateView && planting?.CurrentStep is PlantingStepRecord step
                    ? Bounded($"{step.Phase}:{step.LocationKey}@{step.TileX},{step.TileY}", 128)
                    : string.Empty,
                PlantingLastReason = privateView ? Bounded(planting?.LastFailure, 128) : string.Empty,
                PlantEscrowStackCount = privateView ? Math.Clamp(this.inventories.PlantEscrowCount(record.Identity), 0, 999) : 0,
                Appearance = ToDto(record.Appearance),
                PresentationRevision = presentationRevision,
                Presentation = presentation is null ? null : ClonePresentation(presentation),
                SpeechSequence = speech?.Sequence ?? 0,
                SpeechBodyGeneration = speech?.BodyGeneration ?? 0,
                SpeechId = Bounded(speech?.SpeechId, SpeechEventContracts.MaximumIdCharacters),
                SpeechTopicKey = Bounded(speech?.TopicKey, SpeechEventContracts.MaximumIdCharacters),
                SpeechText = Bounded(speech?.Text, SpeechEventContracts.MaximumTextCharacters),
                SpeechPriority = speech?.Priority ?? 0,
                SpeechRemainingTicks = speech?.RemainingTicks ?? 0,
            });
        }
        return snapshot;
    }

    public ProjectionApplyResult ApplySnapshot(RuntimeSnapshotDto? snapshot)
    {
        ProjectionApplyResult validation = Validate(snapshot);
        if (!validation.IsSuccess || snapshot is null)
            return validation;
        if (snapshot.SessionEpoch == this.epoch && snapshot.SnapshotVersion <= this.snapshotVersion)
            return ProjectionApplyResult.Failure("STALE-SNAPSHOT", "The snapshot version is not newer than the applied projection.");

        if (snapshot.SessionEpoch != this.epoch)
        {
            this.projections.Clear();
            this.visuals.Clear();
            this.networkBodies.Clear();
            this.snapshotVersion = 0;
            this.estimatedHostTick = snapshot.GeneratedTick;
        }
        else
            this.estimatedHostTick = Math.Max(this.estimatedHostTick, snapshot.GeneratedTick);

        var next = new Dictionary<CompanionIdentity, ProjectedCompanion>();
        foreach (CompanionSnapshotDto companion in snapshot.Companions)
        {
            if (this.projections.TryGetValue(companion.Identity, out ProjectedCompanion? existing))
            {
                existing.Apply(companion, this.estimatedHostTick);
                next.Add(companion.Identity, existing);
            }
            else
                next.Add(companion.Identity, new ProjectedCompanion(companion, this.estimatedHostTick));
        }
        foreach (CompanionIdentity stale in this.visuals.Keys.Where(identity => !next.ContainsKey(identity)).ToArray())
            this.visuals.Remove(stale);
        this.projections.Clear();
        foreach ((CompanionIdentity identity, ProjectedCompanion projected) in next)
            this.projections.Add(identity, projected);
        this.epoch = snapshot.SessionEpoch;
        this.snapshotVersion = snapshot.SnapshotVersion;
        return ProjectionApplyResult.Success("SNAPSHOT-APPLIED", $"Applied snapshot {snapshot.SnapshotVersion} with {snapshot.Companions.Count} companion(s).");
    }

    public ProjectionApplyResult ApplyPresentation(PresentationEventDto? presentation)
    {
        if (presentation is null
            || presentation.ProtocolVersion != MultiplayerProtocol.Version
            || presentation.SessionEpoch != this.epoch
            || !Guid.TryParseExact(presentation.EventId, "N", out _)
            || presentation.Sequence == 0
            || presentation.OwnerId == 0
            || !CompanionIdentity.IsValidSlot(presentation.Slot)
            || presentation.BodyGeneration == 0
            || presentation.PresentationRevision == 0
            || presentation.OperationId is null || presentation.OperationId.Length > 160
            || !ValidKind(presentation.Kind)
            || !ValidPhase(presentation.Phase, allowClear: true)
            || presentation.ToolId is null || presentation.ToolId.Length > 128
            || presentation.Facing is < 0 or > 3
            || presentation.Frame is < 0 or > 125
            || presentation.StartedAtHostTick == 0
            || presentation.EndsAtHostTick != 0 && presentation.EndsAtHostTick < presentation.StartedAtHostTick
            || presentation.EndsAtHostTick > presentation.StartedAtHostTick + 600)
            return ProjectionApplyResult.Failure("INVALID-PRESENTATION", "The presentation event failed its bounded value contract.");

        CompanionIdentity identity = new(presentation.OwnerId, presentation.Slot);
        if (!this.projections.TryGetValue(identity, out ProjectedCompanion? projected))
            return ProjectionApplyResult.Failure("PRESENTATION-IDENTITY-UNKNOWN", "The presentation identity is absent from the authoritative snapshot.");
        if (projected.State.BodyGeneration != presentation.BodyGeneration)
            return ProjectionApplyResult.Failure("STALE-BODY-GENERATION", "The presentation belongs to another body generation.");
        if (presentation.PresentationRevision <= projected.PresentationRevision)
            return ProjectionApplyResult.Failure("STALE-PRESENTATION", "The presentation revision is not newer than the applied state.");
        projected.ApplyPresentation(presentation, this.estimatedHostTick);
        return ProjectionApplyResult.Success("PRESENTATION-APPLIED", $"Applied presentation {presentation.EventId} to {identity}.");
    }

    public void Update(int elapsedTicks)
    {
        this.networkBodies.Update();
        this.estimatedHostTick += (ulong)Math.Max(1, elapsedTicks);
        foreach (ProjectedCompanion projection in this.projections.Values)
        {
            projection.UpdatePosition(Math.Max(1, elapsedTicks));
            projection.UpdatePresentation(this.estimatedHostTick);
        }
    }

    public bool TryRenderNetworkBody(NPC body, SpriteBatch spriteBatch, float alpha)
    {
        if (Context.IsMainPlayer
            || !Context.IsWorldReady
            || Game1.currentLocation is null
            || !CompanionBodyBinder.TryReadIdentity(body, out CompanionIdentity identity, out ulong generation)
            || !this.projections.TryGetValue(identity, out ProjectedCompanion? projection)
            || projection.State.BodyGeneration != generation
            || !ReferenceEquals(body.currentLocation, Game1.currentLocation))
            return false;
        return this.RenderProjection(projection, spriteBatch, alpha, body);
    }

    public void Render(RenderedWorldEventArgs e)
    {
        if (Context.IsMainPlayer || !Context.IsWorldReady || Game1.currentLocation is null)
            return;
        string locationKey = Game1.currentLocation.NameOrUniqueName;
        foreach (ProjectedCompanion projection in this.projections.Values)
        {
            CompanionSnapshotDto state = projection.State;
            if (!state.WantsBody || !state.BodyPresent || state.LocationKey != locationKey)
                continue;
            if (this.networkBodies.TryGetBody(state.Identity, state.BodyGeneration, state.LocationKey, out _))
                continue;
            this.RenderProjection(projection, e.SpriteBatch, 1f, null);
        }
    }

    private bool RenderProjection(ProjectedCompanion projection, SpriteBatch spriteBatch, float alpha, NPC? body)
    {
        CompanionSnapshotDto state = projection.State;
        try
        {
            VisualFarmer visual = this.GetVisual(state);
            Vector2 position = body?.Position ?? projection.RenderPosition;
            if (body is not null)
                projection.ObserveNativePosition(position);
            visual.Farmer.Position = position;
            visual.Farmer.currentLocation = Game1.currentLocation;
            if (body is not null)
            {
                position.Y += body.GetBoundingBox().Bottom - visual.Farmer.GetBoundingBox().Bottom;
                visual.Farmer.Position = position;
            }
            int facing = projection.Presentation?.Facing ?? projection.ResolveFacing();
            visual.Farmer.faceDirection(facing);
            bool seated = CompanionSeatedPose.IsSeatedKind(projection.Presentation?.Kind);
            int frame;
            bool flip;
            bool secondaryArm;
            if (seated)
                frame = CompanionSeatedPose.Apply(visual.Farmer, facing, out flip, out secondaryArm);
            else
            {
                CompanionSeatedPose.Reset(visual.Farmer);
                frame = projection.Presentation?.Frame ?? projection.ResolveMovementFrame(facing);
                flip = facing == 3;
                secondaryArm = projection.Presentation is CompanionPresentationDto action
                    && CompanionVisualToolAnimation.UsesSecondaryArm(action.Kind, action.Facing, frame);
            }
            Vector2 screen = Game1.GlobalToLocal(Game1.viewport, position);
            bool idle = !seated && projection.Presentation is null && !projection.IsMoving;
            UpdateIdleEyes(visual.Farmer, state.Identity, idle);
            if (idle)
                screen.Y += IdleBreathingOffset(state.Identity);
            float depth = visual.Farmer.getDrawLayer();
            visual.Farmer.FarmerSprite.setCurrentSingleFrame(frame, 32000, secondaryArm, flip);
            Vector2 origin = new(
                visual.Farmer.xOffset,
                (visual.Farmer.yOffset + 128f - visual.Farmer.GetBoundingBox().Height / 2f) / 4f + 4f
            );
            visual.Farmer.FarmerRenderer.draw(spriteBatch, visual.Farmer.FarmerSprite, visual.Farmer.FarmerSprite.SourceRect, screen, origin, depth, Color.White * alpha, 0f, visual.Farmer);
            if (projection.Presentation is CompanionPresentationDto presentation && HasVisualTool(presentation.ToolId))
            {
                if (!TryDrawNativeTool(visual, presentation))
                {
                    Item? icon = visual.GetOrCreateVisualItem(presentation.OperationId, presentation.ToolId);
                    icon?.drawInMenu(spriteBatch, screen + ToolIconOffset(presentation.Facing), 0.45f, 0.9f, Math.Min(1f, depth + 0.0002f), StackDrawType.Hide, Color.White * alpha, drawShadow: false);
                }
            }
            projection.RenderFaulted = false;
            return true;
        }
        catch (Exception ex)
        {
            if (!projection.RenderFaulted)
                this.monitor.Log($"HY-NET-PROJECTION-FAILED: {state.Identity} could not render: {ex.GetType().Name}.", LogLevel.Warn);
            projection.RenderFaulted = true;
            return false;
        }
    }

    public void Clear()
    {
        this.projections.Clear();
        this.visuals.Clear();
        this.networkBodies.Clear();
        this.epoch = string.Empty;
        this.snapshotVersion = 0;
        this.estimatedHostTick = 0;
    }

    public bool IsProjectedBodySeatedAt(MapSeat seat, NPC body)
    {
        if (!CompanionBodyBinder.TryReadIdentity(body, out CompanionIdentity identity, out ulong generation)
            || !this.projections.TryGetValue(identity, out ProjectedCompanion? projection)
            || projection.State.BodyGeneration != generation
            || !CompanionSeatedPose.IsSeatedKind(projection.Presentation?.Kind))
            return false;
        Vector2 tilePosition = body.Position / Game1.tileSize;
        return seat.GetSeatPositions().Any(position => Vector2.DistanceSquared(position, tilePosition) < 0.01f);
    }

    private VisualFarmer GetVisual(CompanionSnapshotDto state)
    {
        CompanionIdentity identity = state.Identity;
        if (this.visuals.TryGetValue(identity, out VisualFarmer? visual) && visual.ProfileId == state.Appearance.ProfileId && visual.Generation == state.Appearance.Generation)
            return visual;
        var profile = state.Appearance;
        var farmer = new Farmer(new FarmerSprite(null!), Vector2.Zero, 2, $"YuiToIsshoProjection_{identity.OwnerId}_{identity.Slot}", new List<Item>(), isMale: false);
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
        visual = new VisualFarmer(profile.ProfileId, profile.Generation, farmer);
        this.visuals[identity] = visual;
        return visual;
    }

    private static ProjectionApplyResult Validate(RuntimeSnapshotDto? snapshot)
    {
        if (snapshot is null || snapshot.ProtocolVersion != MultiplayerProtocol.Version)
            return ProjectionApplyResult.Failure("INVALID-SNAPSHOT-PROTOCOL", "The snapshot protocol is missing or unsupported.");
        if (!Guid.TryParseExact(snapshot.SessionEpoch, "N", out _) || snapshot.SnapshotVersion == 0 || snapshot.HostPlayerId == 0)
            return ProjectionApplyResult.Failure("INVALID-SNAPSHOT-ENVELOPE", "The snapshot epoch, version, or host identity is invalid.");
        if (snapshot.Companions is null || snapshot.Companions.Count > MultiplayerProtocol.MaxCompanionsPerSnapshot)
            return ProjectionApplyResult.Failure("SNAPSHOT-TOO-LARGE", "The snapshot companion list is missing or oversized.");

        var identities = new HashSet<CompanionIdentity>();
        foreach (CompanionSnapshotDto state in snapshot.Companions)
        {
            if (state is null
                || state.OwnerId == 0
                || !CompanionIdentity.IsValidSlot(state.Slot)
                || !identities.Add(state.Identity)
                || string.IsNullOrWhiteSpace(state.DisplayName)
                || state.DisplayName.Length > 64
                || !CompanionModes.IsValid(state.Mode)
                || state.LocationKey is null || state.LocationKey.Length > 256
                || (state.BodyPresent && string.IsNullOrWhiteSpace(state.LocationKey))
                || (state.BodyPresent && state.BodyGeneration == 0)
                || (!state.BodyPresent && state.BodyGeneration != 0)
                || state.PixelX is < -1_000_000 or > 1_000_000
                || state.PixelY is < -1_000_000 or > 1_000_000
                || state.Facing is < 0 or > 3
                || state.BagCount is < 0 or > CompanionInventoryStore.Capacity
                || state.LiabilityCount is < 0 or > 999
                || state.ActiveTransactionId is null || state.ActiveTransactionId.Length > 160
                || state.MaxHealth <= 0
                || state.Health < 0
                || state.Health > state.MaxHealth
                || !float.IsFinite(state.Stamina)
                || !float.IsFinite(state.MaxStamina)
                || state.MaxStamina <= 0
                || state.Stamina < 0
                || state.Stamina > state.MaxStamina
                || !CompanionVitalStates.IsValid(state.VitalState)
                || !ValidAgentSummary(state)
                || !ValidCraftSummary(state)
                || !ValidPlantingSummary(state)
                || !ValidWorkSummary(state)
                || !ValidCombatSummary(state)
                || !ValidAppearance(state.Appearance)
                || !SpeechEventContracts.IsValidSnapshot(state)
                || (state.Presentation is not null && state.Presentation.Revision != state.PresentationRevision)
                || !ValidPresentation(state.Presentation))
                return ProjectionApplyResult.Failure("INVALID-SNAPSHOT-COMPANION", "A companion projection is invalid or duplicated.");
        }
        return ProjectionApplyResult.Success("SNAPSHOT-VALID", "The full projection is bounded and internally consistent.");
    }

    public bool TryFindInteractionTarget(GameLocation location, Vector2 absolutePixels, Point grabTile, Point playerTile, bool mouseTarget, out CompanionIdentity identity, out Point targetTile)
    {
        identity = default;
        targetTile = default;
        if (Context.IsMainPlayer)
            return false;

        this.networkBodies.Update();
        if (this.networkBodies.TryFindInteractionTarget(location, absolutePixels, grabTile, playerTile, mouseTarget, out identity, out targetTile))
            return true;

        float bestDistance = float.MaxValue;
        foreach (ProjectedCompanion projection in this.projections.Values)
        {
            CompanionSnapshotDto state = projection.State;
            if (!state.WantsBody || !state.BodyPresent || !string.Equals(state.LocationKey, location.NameOrUniqueName, StringComparison.Ordinal))
                continue;
            Vector2 position = projection.RenderPosition;
            Point tile = new((int)MathF.Floor(position.X / Game1.tileSize), (int)MathF.Floor(position.Y / Game1.tileSize));
            if (Math.Max(Math.Abs(tile.X - playerTile.X), Math.Abs(tile.Y - playerTile.Y)) > 1)
                continue;
            Rectangle visualBounds = new((int)position.X - 16, (int)position.Y - 72, Game1.tileSize + 32, Game1.tileSize + 96);
            bool hit = mouseTarget ? visualBounds.Contains(absolutePixels.ToPoint()) : tile == grabTile;
            if (!hit)
                continue;
            float distance = Vector2.DistanceSquared(position, absolutePixels);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            identity = state.Identity;
            targetTile = tile;
        }
        return identity.OwnerId != 0;
    }

    private static bool ValidWorkSummary(CompanionSnapshotDto state)
    {
        if (string.IsNullOrEmpty(state.WorkKind))
            return string.IsNullOrEmpty(state.WorkLocationKey)
                && state.WorkAnchorX == 0
                && state.WorkAnchorY == 0
                && state.WorkEndX == 0
                && state.WorkEndY == 0
                && state.WorkRadius == 0
                && string.IsNullOrEmpty(state.WorkShape)
                && string.IsNullOrEmpty(state.WorkPolicy)
                && string.IsNullOrEmpty(state.WorkState)
                && string.IsNullOrEmpty(state.WorkPhase)
                && state.WorkMatchingCount == 0
                && state.WorkCandidateCount == 0
                && state.WorkBlockedCount == 0
                && string.IsNullOrEmpty(state.WorkOperationId)
                && string.IsNullOrEmpty(state.WorkLastReason)
                && state.WorkObservationRevision == 0;
        return WorkKinds.IsContinuous(state.WorkKind)
            && !string.IsNullOrWhiteSpace(state.WorkLocationKey)
            && state.WorkLocationKey.Length <= 256
            && state.WorkAnchorX is >= 0 and <= 9999
            && state.WorkAnchorY is >= 0 and <= 9999
            && state.WorkEndX is >= 0 and <= 9999
            && state.WorkEndY is >= 0 and <= 9999
            && state.WorkShape is WorkScopeShapes.Radius or WorkScopeShapes.Rectangle
            && (state.WorkShape == WorkScopeShapes.Radius
                ? state.WorkRadius is >= WorkScopeContracts.MinimumRadius and <= WorkScopeContracts.MaximumRadius
                : state.WorkRadius == 0 && WorkScopeContracts.IsRectangleWithinLimit(state.WorkAnchorX, state.WorkAnchorY, state.WorkEndX, state.WorkEndY))
            && state.WorkPolicy is WorkCompletionPolicies.UntilClear or WorkCompletionPolicies.UntilStopped
            && !string.IsNullOrWhiteSpace(state.WorkState)
            && state.WorkState.Length <= 64
            && WorkRuntimePhases.IsValid(state.WorkPhase)
            && state.WorkMatchingCount is >= 0 and <= 9999
            && state.WorkCandidateCount is >= 0 and <= WorkCandidateObserver.MaximumCandidates
            && state.WorkBlockedCount >= 0 && state.WorkBlockedCount <= state.WorkMatchingCount
            && state.WorkOperationId is not null && state.WorkOperationId.Length <= 160
            && state.WorkLastReason is not null && state.WorkLastReason.Length <= 64;
    }

    private static bool ValidAgentSummary(CompanionSnapshotDto state) =>
        AgentBehaviorStates.IsValid(state.AgentBehaviorState)
        && AgentBrainPhases.IsValid(state.AgentBrainPhase)
        && state.AgentPlanGeneration >= 0
        && state.AgentSnapshotVersion >= 0
        && state.AgentIntentId is not null && state.AgentIntentId.Length <= 32
        && (state.AgentIntentId.Length == 0 || AgentIntentIds.IsValid(state.AgentIntentId))
        && state.AgentStepKind is not null && state.AgentStepKind.Length <= 32
        && (state.AgentStepKind.Length == 0 || AgentPlanStepKinds.IsValid(state.AgentStepKind))
        && state.AgentStepState is not null && state.AgentStepState.Length <= 32
        && (state.AgentStepState.Length == 0 || AgentPlanStepStates.IsValid(state.AgentStepState));

    private static bool ValidCombatSummary(CompanionSnapshotDto state)
    {
        if (string.IsNullOrEmpty(state.CombatMode))
            return string.IsNullOrEmpty(state.CombatPhase)
                && state.CombatRemainingSeconds == 0
                && state.CombatCommittedSwings == 0
                && state.CombatMaximumSwings == 0
                && string.IsNullOrEmpty(state.CombatTargetKind)
                && string.IsNullOrEmpty(state.CombatTargetDistanceBand)
                && string.IsNullOrEmpty(state.CombatLastOutcome);
        return state.CombatMode is "SingleStrike" or "GuardArea" or "CounterStrike"
            && !string.IsNullOrWhiteSpace(state.CombatPhase) && state.CombatPhase.Length <= 24
            && state.CombatRemainingSeconds is >= 0 and <= 60
            && state.CombatMaximumSwings is >= 1 and <= 30
            && state.CombatCommittedSwings >= 0 && state.CombatCommittedSwings <= state.CombatMaximumSwings
            && state.CombatTargetKind is not null && state.CombatTargetKind.Length <= 32
            && state.CombatTargetDistanceBand is "" or "near" or "medium" or "far"
            && state.CombatLastOutcome is not null && state.CombatLastOutcome.Length <= 64;
    }

    private static bool ValidCraftSummary(CompanionSnapshotDto state)
    {
        if (string.IsNullOrEmpty(state.CraftRecipeKey))
            return string.IsNullOrEmpty(state.CraftPhase) && state.CraftCompletedCount == 0 && state.CraftCount == 0 && state.CraftEscrowCount == 0;
        return state.CraftRecipeKey.Length <= 128
            && CraftPhases.IsValid(state.CraftPhase)
            && state.CraftCount is >= 1 and <= 25
            && state.CraftCompletedCount >= 0 && state.CraftCompletedCount <= state.CraftCount
            && state.CraftEscrowCount is >= 0 and <= 999;
    }

    private static bool ValidPlantingSummary(CompanionSnapshotDto state)
    {
        if (string.IsNullOrEmpty(state.PlantingPhase))
            return state.PlantingRequestedCount == 0
                && state.PlantingPlantedCount == 0
                && state.PlantingRemainingCount == 0
                && string.IsNullOrEmpty(state.PlantingScopeSummary)
                && string.IsNullOrEmpty(state.PlantingCurrentStepSummary)
                && string.IsNullOrEmpty(state.PlantingLastReason)
                && state.PlantEscrowStackCount == 0;
        return PlantingPhases.IsValid(state.PlantingPhase)
            && state.PlantingRequestedCount is >= 1 and <= PlantingConstants.MaximumCount
            && state.PlantingPlantedCount >= 0 && state.PlantingPlantedCount <= state.PlantingRequestedCount
            && state.PlantingRemainingCount == state.PlantingRequestedCount - state.PlantingPlantedCount
            && state.PlantingScopeSummary.Length <= 128
            && state.PlantingCurrentStepSummary.Length <= 128
            && state.PlantingLastReason.Length <= 128
            && state.PlantEscrowStackCount is >= 0 and <= 999;
    }

    private static bool ValidAppearance(CompanionAppearanceDto? profile) =>
        profile is not null
        && profile.ProfileSchemaVersion == CompanionAppearanceProfile.CurrentProfileSchemaVersion
        && profile.Generation >= 1
        && CompanionBodyTypes.IsValid(profile.BodyType)
        && Guid.TryParseExact(profile.ProfileId, "N", out _)
        && profile.HairStyle is >= 0 and <= 10000
        && profile.Skin is >= 0 and <= 23
        && !string.IsNullOrWhiteSpace(profile.ShirtId) && profile.ShirtId.Length <= 16
        && !string.IsNullOrWhiteSpace(profile.PantsId) && profile.PantsId.Length <= 16
        && !string.IsNullOrWhiteSpace(profile.ShoeColorId) && profile.ShoeColorId.Length <= 16
        && profile.AccessoryId == -1
        && profile.HatQualifiedItemId is not null && profile.HatQualifiedItemId.Length == 0;

    private static bool ValidPresentation(CompanionPresentationDto? presentation) => presentation is null
        || (presentation.Revision > 0
            && presentation.OperationId is not null && presentation.OperationId.Length <= 160
            && ValidKind(presentation.Kind)
            && ValidPhase(presentation.Phase, allowClear: false)
            && presentation.ToolId is not null && presentation.ToolId.Length <= 128
            && presentation.Facing is >= 0 and <= 3
            && presentation.Frame is >= 0 and <= 125
            && presentation.RemainingTicks is >= 0 and <= 600
            && presentation.StartedAtHostTick > 0
            && (presentation.EndsAtHostTick == 0
                || presentation.EndsAtHostTick >= presentation.StartedAtHostTick
                && presentation.EndsAtHostTick <= presentation.StartedAtHostTick + 600));

    private static CompanionAppearanceDto ToDto(CompanionAppearanceProfile profile) => new()
    {
        ProfileSchemaVersion = profile.ProfileSchemaVersion,
        Generation = profile.Generation,
        BodyType = profile.BodyType,
        ProfileId = profile.ProfileId,
        HairStyle = profile.HairStyle,
        Skin = profile.Skin,
        ShirtId = Bounded(profile.ShirtId, 16),
        PantsId = Bounded(profile.PantsId, 16),
        ShoeColorId = Bounded(profile.ShoeColorId, 16),
        HairColor = profile.HairColor,
        EyeColor = profile.EyeColor,
        PantsColor = profile.PantsColor,
        AccessoryId = profile.AccessoryId,
        HatQualifiedItemId = Bounded(profile.HatQualifiedItemId, 128),
    };

    private static CompanionPresentationDto ToDto(AppearanceActionSnapshot action) => new()
    {
        OperationId = Bounded(action.OperationId, 160),
        Kind = Bounded(action.Kind, 32),
        Phase = Bounded(action.Phase, 32),
        ToolId = Bounded(action.ToolId, 128),
        Facing = NormalizeFacing(action.Facing),
        Frame = Math.Clamp(action.Frame, 0, 125),
        RemainingTicks = Math.Clamp(action.RemainingTicks, 0, 600),
    };

    private static CompanionPresentationDto ClonePresentation(CompanionPresentationDto source) => new()
    {
        Revision = source.Revision,
        OperationId = source.OperationId,
        Kind = source.Kind,
        Phase = source.Phase,
        ToolId = source.ToolId,
        Facing = source.Facing,
        Frame = source.Frame,
        RemainingTicks = source.RemainingTicks,
        StartedAtHostTick = source.StartedAtHostTick,
        EndsAtHostTick = source.EndsAtHostTick,
    };

    private static string Bounded(string? value, int maximum) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, maximum)];
    private static bool HasVisualTool(string? toolId) => !string.IsNullOrWhiteSpace(toolId) && toolId != "none";
    private static Vector2 ToolIconOffset(int facing) => facing switch
    {
        0 => new Vector2(24f, -34f),
        1 => new Vector2(42f, 8f),
        2 => new Vector2(18f, 34f),
        _ => new Vector2(-18f, 8f),
    };

    private static bool TryDrawNativeTool(VisualFarmer visual, CompanionPresentationDto presentation)
    {
        Item? item = visual.GetOrCreateVisualItem(presentation.OperationId, presentation.ToolId);
        if (item is not Tool tool
            || presentation.Kind is AppearanceActionKinds.Fishing or AppearanceActionKinds.Shearing or AppearanceActionKinds.Milking
            || !CompanionVisualToolAnimation.TryResolve(presentation.Kind, presentation.Facing, out VisualToolAnimation animation))
            return false;
        int totalTicks = presentation.EndsAtHostTick > presentation.StartedAtHostTick
            ? (int)Math.Min(600UL, presentation.EndsAtHostTick - presentation.StartedAtHostTick)
            : Math.Max(1, presentation.RemainingTicks);
        int elapsed = Math.Max(0, totalTicks - presentation.RemainingTicks);
        try
        {
            visual.Farmer.CurrentTool = tool;
            visual.Farmer.UsingTool = true;
            CompanionVisualToolAnimation.Draw(visual.Farmer, tool, animation, elapsed, totalTicks);
            return true;
        }
        finally
        {
            visual.Farmer.UsingTool = false;
            visual.Farmer.CurrentTool = null;
        }
    }
    private static bool ValidKind(string? kind) => kind is
        AppearanceActionKinds.Watering or AppearanceActionKinds.Chopping or AppearanceActionKinds.Mining
        or AppearanceActionKinds.HarvestGrab or AppearanceActionKinds.HarvestScythe or AppearanceActionKinds.Forage
        or AppearanceActionKinds.Mowing or AppearanceActionKinds.Digging or AppearanceActionKinds.Petting
        or AppearanceActionKinds.Milking or AppearanceActionKinds.Shearing or AppearanceActionKinds.Fishing
        or AppearanceActionKinds.CombatSword or AppearanceActionKinds.CombatDagger or AppearanceActionKinds.CombatClub
        or AppearanceActionKinds.Handoff or AppearanceActionKinds.Crafting or AppearanceActionKinds.Planting
        or AppearanceActionKinds.Sitting or AppearanceActionKinds.Swinging;
    private static bool ValidPhase(string? phase, bool allowClear) => phase is "Prepare" or "Commit" or "Cast" or "Waiting" or "Reel" or "Caught" || (allowClear && phase == "Clear");
    private static int NormalizeFacing(int facing) => facing is >= 0 and <= 3 ? facing : 2;
    private static int IdleFrame(int facing) => facing switch { 0 => 12, 1 => 6, 2 => 0, _ => 6 };
    private static ulong VisualOffset(CompanionIdentity identity) => unchecked((ulong)identity.OwnerId + (ulong)(identity.Slot * 53));
    private static void UpdateIdleEyes(Farmer farmer, CompanionIdentity identity, bool idle)
    {
        ulong phase = (unchecked((ulong)Game1.ticks) + VisualOffset(identity)) % 240UL;
        farmer.currentEyes = !idle ? Farmer.eyesOpen : phase switch
        {
            >= 222UL and < 225UL => Farmer.eyesHalfShut,
            >= 225UL and < 229UL => Farmer.eyesClosed,
            >= 229UL and < 232UL => Farmer.eyesHalfShut,
            _ => Farmer.eyesOpen,
        };
    }
    private static float IdleBreathingOffset(CompanionIdentity identity) => (float)(-0.75d - Math.Sin((unchecked((ulong)Game1.ticks) + VisualOffset(identity)) * Math.PI / 60d) * 0.75d);
    private static Color Unpack(uint value) { Color color = default; color.PackedValue = value; return color; }

    private sealed class ProjectedCompanion
    {
        public ProjectedCompanion(CompanionSnapshotDto state, ulong estimatedHostTick)
        {
            this.State = state;
            this.PresentationRevision = state.PresentationRevision;
            this.Presentation = RemainingPresentation(state.Presentation, estimatedHostTick);
            this.RenderPosition = new Vector2(state.PixelX, state.PixelY);
            this.TargetPosition = this.RenderPosition;
            this.MovementFacing = NormalizeFacing(state.Facing);
        }
        public CompanionSnapshotDto State { get; private set; }
        public CompanionPresentationDto? Presentation { get; set; }
        public ulong PresentationRevision { get; private set; }
        public bool RenderFaulted { get; set; }
        public bool IsMoving => this.MovementHoldTicks > 0;
        public Vector2 RenderPosition { get; private set; }
        private Vector2 TargetPosition { get; set; }
        private int InterpolationTicks { get; set; }
        private int WalkTicks { get; set; }
        private int MovementHoldTicks { get; set; }
        private int MovementFacing { get; set; }

        public void Apply(CompanionSnapshotDto state, ulong estimatedHostTick)
        {
            bool sameLocation = this.State.LocationKey == state.LocationKey
                && this.State.BodyGeneration == state.BodyGeneration
                && this.State.BodyPresent
                && state.BodyPresent;
            this.State = state;
            if (state.PresentationRevision >= this.PresentationRevision)
            {
                this.PresentationRevision = state.PresentationRevision;
                this.Presentation = RemainingPresentation(state.Presentation, estimatedHostTick);
            }
            this.TargetPosition = new Vector2(state.PixelX, state.PixelY);
            this.InterpolationTicks = sameLocation ? 30 : 0;
            if (!sameLocation)
                this.RenderPosition = this.TargetPosition;
        }

        public void UpdatePosition(int elapsedTicks)
        {
            Vector2 before = this.RenderPosition;
            if (this.InterpolationTicks > 0)
            {
                float amount = Math.Min(1f, elapsedTicks / (float)this.InterpolationTicks);
                this.RenderPosition = Vector2.Lerp(this.RenderPosition, this.TargetPosition, amount);
                this.InterpolationTicks = Math.Max(0, this.InterpolationTicks - elapsedTicks);
            }
            Vector2 delta = this.RenderPosition - before;
            if (delta.LengthSquared() > 0.01f)
            {
                this.MovementFacing = FacingFromDelta(delta, this.MovementFacing);
                this.WalkTicks += elapsedTicks;
                this.MovementHoldTicks = 6;
            }
            else
            {
                this.MovementHoldTicks = Math.Max(0, this.MovementHoldTicks - elapsedTicks);
                if (this.MovementHoldTicks == 0)
                    this.WalkTicks = 0;
            }
        }

        public int ResolveFacing() => this.IsMoving ? this.MovementFacing : NormalizeFacing(this.State.Facing);

        public int ResolveMovementFrame(int facing)
        {
            if (!this.IsMoving)
                return IdleFrame(facing);
            int phase = this.WalkTicks / 12 % 4;
            int[] down = { 1, 0, 2, 0 };
            int[] side = { 7, 6, 8, 6 };
            int[] up = { 13, 12, 14, 12 };
            return facing switch { 0 => up[phase], 1 => side[phase], 2 => down[phase], _ => side[phase] };
        }

        public void ObserveNativePosition(Vector2 position)
        {
            Vector2 delta = position - this.RenderPosition;
            this.RenderPosition = position;
            this.TargetPosition = position;
            this.InterpolationTicks = 0;
            if (delta.LengthSquared() <= 0.01f)
                return;
            this.MovementFacing = FacingFromDelta(delta, this.MovementFacing);
            this.WalkTicks++;
            this.MovementHoldTicks = 6;
        }

        public void ApplyPresentation(PresentationEventDto presentation, ulong estimatedHostTick)
        {
            this.PresentationRevision = presentation.PresentationRevision;
            this.Presentation = presentation.Phase == "Clear"
                ? null
                : RemainingPresentation(new CompanionPresentationDto
                {
                    Revision = presentation.PresentationRevision,
                    OperationId = presentation.OperationId,
                    Kind = presentation.Kind,
                    Phase = presentation.Phase,
                    ToolId = presentation.ToolId,
                    Facing = presentation.Facing,
                    Frame = presentation.Frame,
                    StartedAtHostTick = presentation.StartedAtHostTick,
                    EndsAtHostTick = presentation.EndsAtHostTick,
                }, estimatedHostTick);
        }

        public void UpdatePresentation(ulong estimatedHostTick)
        {
            if (this.Presentation is null || this.Presentation.EndsAtHostTick == 0)
                return;
            this.Presentation.RemainingTicks = RemainingTicks(this.Presentation.EndsAtHostTick, estimatedHostTick);
            if (this.Presentation.RemainingTicks == 0)
                this.Presentation = null;
        }

        private static CompanionPresentationDto? RemainingPresentation(CompanionPresentationDto? source, ulong estimatedHostTick)
        {
            if (source is null)
                return null;
            CompanionPresentationDto result = ClonePresentation(source);
            result.RemainingTicks = source.EndsAtHostTick == 0 ? Math.Clamp(source.RemainingTicks, 0, 600) : RemainingTicks(source.EndsAtHostTick, estimatedHostTick);
            return result.RemainingTicks == 0 && source.EndsAtHostTick != 0 ? null : result;
        }

        private static int RemainingTicks(ulong endsAtHostTick, ulong estimatedHostTick) =>
            endsAtHostTick <= estimatedHostTick ? 0 : (int)Math.Min(600UL, endsAtHostTick - estimatedHostTick);

        private static int FacingFromDelta(Vector2 delta, int fallback)
        {
            if (Math.Abs(delta.X) > Math.Abs(delta.Y))
                return delta.X > 0f ? 1 : 3;
            if (Math.Abs(delta.Y) > 0.01f)
                return delta.Y > 0f ? 2 : 0;
            return fallback;
        }
    }

    private sealed class VisualFarmer
    {
        private string visualOperationId = string.Empty;
        private string visualToolId = string.Empty;
        private Item? visualItem;

        public VisualFarmer(string profileId, int generation, Farmer farmer)
        {
            this.ProfileId = profileId;
            this.Generation = generation;
            this.Farmer = farmer;
        }

        public string ProfileId { get; }
        public int Generation { get; }
        public Farmer Farmer { get; }

        public Item? GetOrCreateVisualItem(string operationId, string toolId)
        {
            if (this.visualItem is not null && this.visualOperationId == operationId && this.visualToolId == toolId)
                return this.visualItem;
            this.visualOperationId = operationId;
            this.visualToolId = toolId;
            this.visualItem = null;
            try
            {
                this.visualItem = ItemRegistry.Create(toolId);
            }
            catch
            {
                // The caller degrades to a body-only clip and records the outer bounded render warning.
            }
            return this.visualItem;
        }
    }
}
