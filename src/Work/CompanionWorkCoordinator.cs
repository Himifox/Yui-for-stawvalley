using Microsoft.Xna.Framework;
using StardewValley;
using StardewModdingAPI;

namespace YuiToIssho;

internal readonly record struct WorkDirectiveResult(bool IsSuccess, string Code, string Message)
{
    public static WorkDirectiveResult Success(string code, string message) => new(true, code, message);
    public static WorkDirectiveResult Failure(string code, string message) => new(false, code, message);
}

internal enum WorkFailureClass
{
    ImmediateRescan,
    ShortCooldown,
    TopologyBlocked,
    ResourcePaused,
    ExecutionFault,
}

internal readonly record struct WorkRuntimeSnapshot(
    string Phase,
    int MatchingCount,
    int CandidateCount,
    int BlockedCount,
    string? CurrentOperationId,
    string? LastReason,
    ulong ObservationRevision
);

internal sealed class CompanionWorkCoordinator
{
    private const ulong SuccessfulInterStepTicks = 30;
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly TaskNavigationService navigation;
    private readonly CompanionWorkTaskRouter taskRouter;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, WorkRuntimeState> runtimes = new();
    private int roundRobinCursor;

    public CompanionWorkCoordinator(CompanionRegistry registry, CompanionBodyBinder bodies, CompanionInventoryStore inventories, TaskNavigationService navigation, CompanionWorkTaskRouter taskRouter, IMonitor monitor)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.navigation = navigation;
        this.taskRouter = taskRouter;
        this.monitor = monitor;
    }

    public WorkDirectiveResult Start(CompanionIdentity identity, Farmer owner, string directiveId, WorkScopeRequest request)
    {
        WorkDirectiveResult gate = this.CanStart(identity, owner, request);
        if (!gate.IsSuccess)
            return gate;
        this.registry.TryGet(identity, out CompanionRecord record);
        WorkScopeValidationResult validation = request.Shape == WorkScopeShapes.Rectangle
            ? WorkScopeNormalizer.NormalizeRectangle(owner, request)
            : WorkScopeNormalizer.NormalizeRadius(owner, request);
        NormalizedWorkScope scope = validation.Scope;

        string returnMode = record.WorkDirective?.ReturnMode is CompanionModes.Follow or CompanionModes.Wait
            ? record.WorkDirective.ReturnMode
            : record.Mode is CompanionModes.Follow or CompanionModes.Wait ? record.Mode : CompanionModes.Follow;
        int day = Game1.Date.TotalDays;
        record.WorkDirective = new WorkDirectiveRecord
        {
            DirectiveId = directiveId,
            Kind = scope.Kind,
            LocationKey = scope.LocationKey,
            AnchorX = scope.AnchorX,
            AnchorY = scope.AnchorY,
            EndX = scope.EndX,
            EndY = scope.EndY,
            Shape = scope.Shape,
            Radius = scope.Radius,
            CompletionPolicy = scope.CompletionPolicy,
            ReturnMode = returnMode,
            NextStepSequence = 0,
            CreatedDay = day,
            LastConfirmedDay = day,
        };
        record.Mode = CompanionModes.Work;
        this.runtimes.Remove(identity);
        this.bodies.Halt(identity);
        return WorkDirectiveResult.Success(
            "WORK-STARTED",
            $"{identity} accepted {scope.Kind} at {scope.LocationKey} ({scope.AnchorX},{scope.AnchorY})..({scope.EndX},{scope.EndY}) shape={scope.Shape} radius={scope.Radius} policy={scope.CompletionPolicy} directive={directiveId}."
        );
    }

    public WorkDirectiveResult CanStart(CompanionIdentity identity, Farmer owner, WorkScopeRequest request)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return WorkDirectiveResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (!identity.IsCanonical)
            return WorkDirectiveResult.Failure("SINGLE-COMPANION-PER-OWNER", "Only the Owner's current Yui identity can start continuous work.");
        if (!record.WantsBody || !this.bodies.TryGetBody(identity, out var body) || body.currentLocation is null)
            return WorkDirectiveResult.Failure("WORK-BODY-NOT-READY", $"{identity} must have a valid summoned body.");
        if (!ReferenceEquals(body.currentLocation, owner.currentLocation))
            return WorkDirectiveResult.Failure("WORK-BODY-LOCATION-MISMATCH", "The Owner and Yui must be in the same location.");
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return WorkDirectiveResult.Failure("COMPANION-BUSY", $"{identity} must finish or stop transaction {record.ActiveTransactionId} first.");

        WorkScopeValidationResult validation = request.Shape == WorkScopeShapes.Rectangle
            ? WorkScopeNormalizer.NormalizeRectangle(owner, request)
            : WorkScopeNormalizer.NormalizeRadius(owner, request);
        if (!validation.IsSuccess)
            return WorkDirectiveResult.Failure(validation.Code, validation.Message);
        return WorkDirectiveResult.Success("WORK-START-READY", $"{identity} may accept the normalized work scope.");
    }

    public WorkDirectiveResult Status(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return WorkDirectiveResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        WorkDirectiveRecord? directive = record.WorkDirective;
        if (directive is null)
            return WorkDirectiveResult.Success("WORK-INACTIVE", $"{identity} has no continuous work directive; mode={record.Mode}.");
        string runtime = this.runtimes.TryGetValue(identity, out WorkRuntimeState? state)
            ? $" phase={state.Phase} operation={state.CurrentOperationId ?? "none"} matching={state.MatchingCount} candidates={state.CandidateCount} hasMore={state.HasMoreCandidates} probed={state.ProbedCount} cooldowns={state.Cooldowns.Count} blocked[path={state.PathBlockedCount},cooldown={state.CooldownBlockedCount}] selected={state.SelectedTarget ?? "none"} emptyScans={state.EmptyScans} observation={state.ObservationRevision} faults={state.ConsecutiveExecutionFaults} sweep={state.DescribeSweep()} reason={state.LastReason ?? "none"}"
            : " phase=NotObserved";
        return WorkDirectiveResult.Success(
            "WORK-STATUS",
            $"{identity} directive={directive.DirectiveId} kind={directive.Kind} location={directive.LocationKey} bounds=({directive.AnchorX},{directive.AnchorY})..({directive.EndX},{directive.EndY}) shape={directive.Shape} radius={directive.Radius} policy={directive.CompletionPolicy} next={directive.NextStepSequence} suspended={directive.SuspendedReason ?? "none"}.{runtime}"
        );
    }

    public WorkDirectiveResult Resume(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return WorkDirectiveResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (record.WorkDirective is null)
            return WorkDirectiveResult.Failure("WORK-NOT-FOUND", $"{identity} has no work directive to resume.");
        if (!record.WantsBody || !this.bodies.TryGetBody(identity, out _))
            return WorkDirectiveResult.Failure("WORK-BODY-NOT-READY", $"{identity} must have a valid summoned body.");
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return WorkDirectiveResult.Failure("COMPANION-BUSY", $"{identity} must finish or stop transaction {record.ActiveTransactionId} first.");
        record.WorkDirective.LastConfirmedDay = Game1.Date.TotalDays;
        record.WorkDirective.SuspendedReason = null;
        record.Mode = CompanionModes.Work;
        this.runtimes.Remove(identity);
        return WorkDirectiveResult.Success("WORK-RESUMED", $"{identity} resumed directive {record.WorkDirective.DirectiveId} from a fresh observation boundary.");
    }

    public WorkDirectiveResult Stop(CompanionIdentity identity, string reason, bool useReturnMode)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return WorkDirectiveResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        WorkDirectiveRecord? directive = record.WorkDirective;
        if (directive is null)
            return WorkDirectiveResult.Success("WORK-ALREADY-STOPPED", $"{identity} has no work directive.");
        record.WorkDirective = null;
        this.runtimes.Remove(identity);
        record.Mode = useReturnMode && directive.ReturnMode == CompanionModes.Follow ? CompanionModes.Follow : CompanionModes.Wait;
        this.bodies.Halt(identity);
        return WorkDirectiveResult.Success("WORK-STOPPED", $"{identity} stopped directive {directive.DirectiveId} ({reason}); mode={record.Mode}.");
    }

    public void Suspend(CompanionIdentity identity, string reason)
    {
        if (this.registry.TryGet(identity, out CompanionRecord record) && record.WorkDirective is not null)
        {
            record.WorkDirective.SuspendedReason = reason;
            this.runtimes.Remove(identity);
        }
    }

    public void SuspendAll(string reason)
    {
        foreach (CompanionRecord record in this.registry.Active)
            if (record.WorkDirective is not null)
                record.WorkDirective.SuspendedReason = reason;
        this.runtimes.Clear();
    }

    public void RestoreAfterLoad()
    {
        int day = Game1.Date.TotalDays;
        foreach (CompanionRecord record in this.registry.Active)
        {
            WorkDirectiveRecord? directive = record.WorkDirective;
            if (directive is null)
                continue;
            if (directive.LastConfirmedDay != day)
                directive.SuspendedReason = "DAY-CONFIRMATION-REQUIRED";
            else if (directive.SuspendedReason == "SAVING")
                directive.SuspendedReason = null;
        }
    }

    public void ResumeAfterSave()
    {
        foreach (CompanionRecord record in this.registry.Active)
            if (record.WorkDirective?.SuspendedReason == "SAVING")
                record.WorkDirective.SuspendedReason = null;
    }

    public void RequireDayConfirmation()
    {
        int day = Game1.Date.TotalDays;
        foreach (CompanionRecord record in this.registry.Active)
            if (record.WorkDirective is not null && record.WorkDirective.LastConfirmedDay != day)
                record.WorkDirective.SuspendedReason = "DAY-CONFIRMATION-REQUIRED";
        this.runtimes.Clear();
    }

    public string DescribeRuntime(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record) || record.WorkDirective is null)
            return "none";
        WorkDirectiveRecord directive = record.WorkDirective;
        this.runtimes.TryGetValue(identity, out WorkRuntimeState? state);
        return $"{directive.Kind} {directive.LocationKey} ({directive.AnchorX},{directive.AnchorY})..({directive.EndX},{directive.EndY}) {directive.Shape} r={directive.Radius} {directive.CompletionPolicy} phase={state?.Phase ?? "NotObserved"} matching={state?.MatchingCount ?? 0} batch={state?.CandidateCount ?? 0} blocked[path={state?.PathBlockedCount ?? 0},cooldown={state?.CooldownBlockedCount ?? 0}] step={directive.NextStepSequence} op={state?.CurrentOperationId ?? "none"} sweep={state?.DescribeSweep() ?? "NotObserved"} reason={directive.SuspendedReason ?? state?.LastReason ?? "none"}";
    }

    public WorkRuntimeSnapshot? GetSnapshot(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record) || record.WorkDirective is null)
            return null;
        if (!this.runtimes.TryGetValue(identity, out WorkRuntimeState? state))
            return new WorkRuntimeSnapshot(WorkRuntimePhases.NotObserved, 0, 0, 0, null, record.WorkDirective.SuspendedReason, 0);
        return new WorkRuntimeSnapshot(
            state.Phase,
            state.MatchingCount,
            state.CandidateCount,
            Math.Clamp(state.PathBlockedCount + state.CooldownBlockedCount, 0, state.MatchingCount),
            state.CurrentOperationId,
            record.WorkDirective.SuspendedReason ?? state.LastReason,
            state.ObservationRevision);
    }

    public void TrackManualRequest(CompanionIdentity identity, string operationId)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record) || record.WorkDirective is null)
            return;
        var state = new WorkRuntimeState(record.WorkDirective.DirectiveId, record.WorkDirective.Kind)
        {
            Phase = "Executing",
            CurrentOperationId = operationId,
            IsManualRequest = true,
            LastReason = "MANUAL-REQUEST",
            PreviousSuspension = record.WorkDirective.SuspendedReason,
        };
        record.WorkDirective.SuspendedReason = "MANUAL-REQUEST";
        this.runtimes[identity] = state;
    }

    public void ClearRuntime()
    {
        this.runtimes.Clear();
        this.roundRobinCursor = 0;
    }

    public void NotifyLocationChanged(string locationKey, ulong tick)
    {
        foreach ((CompanionIdentity identity, WorkRuntimeState state) in this.runtimes)
        {
            if (state.CurrentOperationId is not null
                || !this.registry.TryGet(identity, out CompanionRecord record)
                || record.WorkDirective?.LocationKey != locationKey)
                continue;
            state.Candidates = null;
            state.NextObservationTick = Math.Min(state.NextObservationTick, tick + 1);
            if (state.Phase == "Blocked")
            {
                state.Phase = "Observing";
                state.LastReason = "WORLD-CHANGED";
            }
        }
    }

    public void Update(ulong tick)
    {
        CompanionRecord[] eligible = this.registry.Active
            .Where(record => record.Mode == CompanionModes.Work && record.WorkDirective is not null)
            .OrderBy(record => record.OwnerId)
            .ThenBy(record => record.Slot)
            .ToArray();
        if (eligible.Length == 0)
        {
            this.ClearRuntime();
            return;
        }

        this.roundRobinCursor %= eligible.Length;
        CompanionRecord record = eligible[this.roundRobinCursor];
        this.roundRobinCursor = (this.roundRobinCursor + 1) % eligible.Length;
        try
        {
            this.UpdateOne(record, tick);
        }
        catch (Exception ex)
        {
            if (record.WorkDirective is not null)
                record.WorkDirective.SuspendedReason = "RUNTIME-FAULT";
            this.runtimes.Remove(record.Identity);
            this.monitor.Log($"HY-WORK-RUNTIME-FAULT: {record.Identity} observation was isolated after {ex.GetType().Name}.", LogLevel.Error);
        }
    }

    private void UpdateOne(CompanionRecord record, ulong tick)
    {
        WorkDirectiveRecord directive = record.WorkDirective!;
        WorkRuntimeState state = this.runtimes.TryGetValue(record.Identity, out WorkRuntimeState? existing)
            ? existing
            : this.runtimes[record.Identity] = new WorkRuntimeState(directive.DirectiveId, directive.Kind);
        if (state.DirectiveId != directive.DirectiveId)
        {
            state = new WorkRuntimeState(directive.DirectiveId, directive.Kind);
            this.runtimes[record.Identity] = state;
        }
        if (state.CurrentOperationId is not null)
        {
            if (string.Equals(record.ActiveTransactionId, state.CurrentOperationId, StringComparison.Ordinal))
            {
                state.Phase = "Executing";
                state.LastReason = "TASK-SESSION-ACTIVE";
                return;
            }
            if (!TaskReceiptStore.TryGet(record, state.CurrentOperationId, out TaskExecutionResult receipt))
            {
                directive.SuspendedReason = "EXECUTION-FAULT:TASK-RECEIPT-MISSING";
                state.Phase = "Faulted";
                state.LastReason = directive.SuspendedReason;
                state.ClearProposal();
                return;
            }

            string completedOperation = state.CurrentOperationId;
            string? completedTarget = state.SelectedTarget;
            bool wasManual = state.IsManualRequest;
            WorkStepGeometry? completedGeometry = state.ProposedGeometry;
            WorkSweepProposal? completedSweep = state.ProposedSweep;
            state.CurrentOperationId = null;
            state.IsManualRequest = false;
            state.SelectedTarget = null;
            state.Phase = "InterStep";
            state.LastReason = receipt.Code;
            state.NextObservationTick = tick + SuccessfulInterStepTicks;
            if (wasManual && directive.SuspendedReason == "MANUAL-REQUEST")
                directive.SuspendedReason = state.PreviousSuspension;
            state.PreviousSuspension = null;
            if (receipt.IsSuccess)
            {
                state.ConsecutiveExecutionFaults = 0;
                state.ResetEmptyScans();
                if (!wasManual)
                    state.CommitProposal(completedGeometry, completedSweep);
                else
                    state.ClearProposal();
            }
            else
            {
                state.ClearProposal();
                this.ApplyStepFailure(directive, state, completedTarget, receipt.Code, tick);
            }
            this.monitor.Log($"HY-WORK-STEP-{receipt.Code}: {record.Identity} {completedOperation} reached its terminal Receipt; success={receipt.IsSuccess}.", receipt.IsSuccess ? LogLevel.Info : LogLevel.Warn);
            return;
        }
        if (directive.SuspendedReason is not null)
        {
            state.Phase = directive.SuspendedReason.StartsWith("EXECUTION-FAULT:", StringComparison.Ordinal)
                || directive.SuspendedReason == "RUNTIME-FAULT"
                ? "Faulted"
                : "Paused";
            state.LastReason = directive.SuspendedReason;
            return;
        }
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
        {
            state.Phase = "Executing";
            state.LastReason = "TASK-SESSION-ACTIVE";
            return;
        }
        if (tick < state.NextObservationTick)
            return;
        if (!this.bodies.TryGetBody(record.Identity, out var body) || body.currentLocation is null)
        {
            directive.SuspendedReason = "BODY-UNAVAILABLE";
            state.Phase = "Paused";
            state.LastReason = directive.SuspendedReason;
            return;
        }
        Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null)
        {
            directive.SuspendedReason = "OWNER-OFFLINE";
            state.Phase = "Paused";
            state.LastReason = directive.SuspendedReason;
            return;
        }
        if (!OwnerLifecycleGate.CanAdvance(owner))
        {
            state.Phase = "Paused";
            state.LastReason = "OWNER-BUSY";
            return;
        }
        if (!ReferenceEquals(owner.currentLocation, body.currentLocation))
        {
            this.Stop(record.Identity, "OWNER-LEFT-WORK-LOCATION", useReturnMode: true);
            this.monitor.Log($"HY-WORK-LOCATION-ENDED: {record.Identity} returned to {directive.ReturnMode} because the Owner left the work location.", LogLevel.Info);
            return;
        }
        if (body.currentLocation.NameOrUniqueName != directive.LocationKey)
        {
            this.Stop(record.Identity, "WORK-LOCATION-CHANGED", useReturnMode: true);
            this.monitor.Log($"HY-WORK-LOCATION-ENDED: {record.Identity} returned to {directive.ReturnMode} because its work location changed.", LogLevel.Info);
            return;
        }
        if (directive.Kind is WorkKinds.Harvest or WorkKinds.Forage or WorkKinds.Milk or WorkKinds.Shear
            && this.inventories.Count(record.Identity) >= CompanionInventoryStore.Capacity)
        {
            directive.SuspendedReason = this.inventories.PendingOutputCount(record.Identity) > 0 ? "PENDING-OUTPUT-BLOCKED" : "BAG-FULL";
            state.Phase = "Paused";
            state.LastReason = directive.SuspendedReason;
            return;
        }

        int preferredFacing = state.LastCommittedFacing ?? body.FacingDirection;
        if (state.Candidates is null || state.ProbeCursor >= state.Candidates.Count)
        {
            foreach (string expired in state.Cooldowns.Where(pair => pair.Value <= tick).Select(pair => pair.Key).ToArray())
                state.Cooldowns.Remove(expired);
            WorkObservation observation = WorkCandidateObserver.Observe(
                directive,
                body.currentLocation,
                body.Tile,
                state.Cooldowns.Keys.ToHashSet(StringComparer.Ordinal),
                preferredFacing,
                state.GetSweepHint());
            state.ObservationRevision++;
            state.Candidates = observation.Candidates;
            state.ObservationSweepPlan = observation.SweepPlan;
            state.MatchingCount = observation.MatchingCount;
            state.CandidateCount = state.Candidates.Count;
            state.HasMoreCandidates = observation.HasMore;
            state.ProbeCursor = 0;
            state.ProbedCount = 0;
            state.PathBlockedCount = 0;
            state.CooldownBlockedCount = observation.ExcludedCount;
            state.SelectedTarget = null;
            state.Phase = "Observing";
            if (state.MatchingCount == 0)
            {
                state.RecordEmptyScan();
                state.LastReason = "NO-CANDIDATES";
                if (directive.CompletionPolicy == WorkCompletionPolicies.UntilClear && state.EmptyScans >= 3)
                {
                    string revisions = string.Join(",", state.EmptyScanRevisions);
                    this.Stop(record.Identity, "AREA-CLEAR", useReturnMode: true);
                    this.monitor.Log($"HY-WORK-AREA-CLEAR: {record.Identity} completed directive {directive.DirectiveId} after bounded empty observations [{revisions}].", LogLevel.Info);
                    return;
                }
                state.NextObservationTick = tick + (directive.CompletionPolicy == WorkCompletionPolicies.UntilStopped ? 300UL : 60UL);
                return;
            }
            state.ResetEmptyScans();
            if (state.CandidateCount == 0)
            {
                state.Phase = "Blocked";
                state.LastReason = "NO-EXECUTABLE-CANDIDATE";
                state.NextObservationTick = tick + 300;
                return;
            }
        }

        WorkCandidate? best = null;
        WorkSweepCandidateRank bestRank = new(int.MaxValue, int.MaxValue, int.MaxValue);
        int bestCost = int.MaxValue;
        int bestTurnCost = int.MaxValue;
        int probes = 0;
        while (state.ProbeCursor < state.Candidates.Count && probes < 8)
        {
            WorkCandidate candidate = state.Candidates[state.ProbeCursor++];
            probes++;
            state.ProbedCount++;
            if (state.Cooldowns.TryGetValue(candidate.StableId, out ulong cooldownUntil) && cooldownUntil > tick)
            {
                state.CooldownBlockedCount++;
                continue;
            }
            var approach = this.navigation.FindReachableCardinalApproach(body, body.currentLocation, candidate.Tile, 256);
            if (approach is null)
            {
                state.Cooldowns[candidate.StableId] = tick + 300;
                state.PathBlockedCount++;
                continue;
            }
            int cost = Math.Abs(approach.Value.ToPoint().X - body.TilePoint.X) + Math.Abs(approach.Value.ToPoint().Y - body.TilePoint.Y);
            int actionFacing = TaskNavigationService.FacingToward(approach.Value, candidate.Tile);
            int turnCost = TaskNavigationService.TurnDistance(preferredFacing, actionFacing);
            WorkSweepCandidateRank rank = state.ObservationSweepPlan is WorkSweepPlan sweepPlan
                ? WorkCandidateObserver.GetSweepRank(sweepPlan, candidate)
                : new WorkSweepCandidateRank(0, 0, 0);
            if (IsBetterCandidate(rank, cost, turnCost, candidate.KindPriority, bestRank, bestCost, bestTurnCost, best?.KindPriority ?? int.MaxValue))
            {
                best = candidate;
                bestRank = rank;
                bestCost = cost;
                bestTurnCost = turnCost;
            }
        }

        if (best is WorkCandidate selected)
        {
            state.SelectedTarget = selected.StableId;
            state.Cooldowns.Remove(selected.StableId);
            state.Candidates = null;
            state.ResetEmptyScans();
            this.StartStep(record, directive, state, selected, tick);
            return;
        }
        if (state.ProbeCursor >= state.Candidates.Count)
        {
            state.Phase = "Blocked";
            state.LastReason = "NO-EXECUTABLE-CANDIDATE";
            state.NextObservationTick = tick + 300;
            state.Candidates = null;
        }
        else
        {
            state.LastReason = "PROBE-BUDGET-YIELDED";
            state.NextObservationTick = tick + 1;
        }
    }

    private void StartStep(CompanionRecord record, WorkDirectiveRecord directive, WorkRuntimeState state, WorkCandidate selected, ulong tick)
    {
        string operationId = $"{directive.DirectiveId}:{directive.NextStepSequence}";
        WorkStepStartResult result = this.taskRouter.TryStart(record, directive, selected, operationId);
        if (result.IsSuccess)
        {
            directive.NextStepSequence++;
            state.CurrentOperationId = operationId;
            state.Phase = "Starting";
            state.LastReason = result.Code;
            state.ConsecutiveExecutionFaults = 0;
            state.ProposedGeometry = result.Geometry;
            state.ProposedSweep = WorkCandidateObserver.CreateSweepProposal(state.ObservationSweepPlan, selected);
            if (state.ProposedSweep is WorkSweepProposal proposal)
                state.LastSweepReason = proposal.Reason;
            else if (directive.Kind is WorkKinds.Water or WorkKinds.Harvest or WorkKinds.Till)
                state.LastSweepReason = result.Geometry is null ? "SWEEP-FALLBACK-GEOMETRY-CHANGED" : "SWEEP-FALLBACK-SPARSE";
            this.monitor.Log($"HY-WORK-STEP-STARTED: {record.Identity} started {operationId} at {selected.StableId}.", LogLevel.Info);
            return;
        }

        state.LastReason = result.Code;
        state.SelectedTarget = null;
        state.ProposedGeometry = null;
        state.ProposedSweep = null;
        this.ApplyStepFailure(directive, state, selected.StableId, result.Code, tick);
    }

    private static bool IsBetterCandidate(
        WorkSweepCandidateRank rank,
        int cost,
        int turnCost,
        int kindPriority,
        WorkSweepCandidateRank bestRank,
        int bestCost,
        int bestTurnCost,
        int bestKindPriority)
    {
        if (rank.Bucket != bestRank.Bucket)
            return rank.Bucket < bestRank.Bucket;
        if (rank.LaneDistance != bestRank.LaneDistance)
            return rank.LaneDistance < bestRank.LaneDistance;
        if (rank.AxisDistance != bestRank.AxisDistance)
            return rank.AxisDistance < bestRank.AxisDistance;
        if (cost != bestCost)
            return cost < bestCost;
        if (turnCost != bestTurnCost)
            return turnCost < bestTurnCost;
        return kindPriority < bestKindPriority;
    }

    private void ApplyStepFailure(WorkDirectiveRecord directive, WorkRuntimeState state, string? targetId, string code, ulong tick)
    {
        WorkFailureClass failureClass = ClassifyFailure(code);
        state.LastReason = code;
        switch (failureClass)
        {
            case WorkFailureClass.ResourcePaused:
                state.ConsecutiveExecutionFaults = 0;
                directive.SuspendedReason = code;
                state.Phase = "Paused";
                return;
            case WorkFailureClass.ExecutionFault:
                state.ConsecutiveExecutionFaults++;
                directive.SuspendedReason = $"EXECUTION-FAULT:{code}";
                state.Phase = "Faulted";
                return;
            case WorkFailureClass.TopologyBlocked:
                state.ConsecutiveExecutionFaults = 0;
                if (targetId is not null)
                    state.Cooldowns[targetId] = tick + 300;
                state.Phase = "InterStep";
                state.NextObservationTick = tick + 12;
                return;
            case WorkFailureClass.ShortCooldown:
                state.ConsecutiveExecutionFaults = 0;
                if (targetId is not null)
                    state.Cooldowns[targetId] = tick + 30;
                state.Phase = "InterStep";
                state.NextObservationTick = tick + 12;
                return;
            default:
                state.ConsecutiveExecutionFaults = 0;
                state.Phase = "InterStep";
                state.NextObservationTick = tick + 1;
                return;
        }
    }

    private static WorkFailureClass ClassifyFailure(string code)
    {
        if (ShouldSuspendForResource(code))
            return WorkFailureClass.ResourcePaused;
        if (code is "SETTLEMENT-ERROR" or "SESSION-NOT-CURRENT" or "TASK-RECEIPT-MISSING")
            return WorkFailureClass.ExecutionFault;
        if (code is "TARGET-UNREACHABLE" or "PATH-BUDGET-EXHAUSTED")
            return WorkFailureClass.TopologyBlocked;
        if (code is "TARGET-RESERVED" or "APPROACH-BLOCKED" or "VANILLA-NO-CHANGE")
            return WorkFailureClass.ShortCooldown;
        if (code.StartsWith("TARGET-NOT-", StringComparison.Ordinal)
            || code is "AREA-CHANGED" or "AREA-SHIFTED" or "TARGET-REPLACED-AFTER-SWING")
            return WorkFailureClass.ImmediateRescan;
        return WorkFailureClass.ShortCooldown;
    }

    private static bool ShouldSuspendForResource(string code) => code is
        "SCYTHE-MISSING"
        or "SCYTHE-UNAVAILABLE"
        or "WATERING-CAN-UNAVAILABLE"
        or "AXE-UNAVAILABLE"
        or "PICKAXE-UNAVAILABLE"
        or "HOE-MISSING"
        or "BAG-FULL"
        or "ENERGY-INSUFFICIENT"
        or "LOW-HEALTH-RETREAT"
        or "VITALS-ACTION-BLOCKED"
        or "VITALS-WRITE-GATE"
        or "OUTPUT-CAPACITY-BLOCKED";

    private sealed class WorkRuntimeState
    {
        public WorkRuntimeState(string directiveId, string kind)
        {
            this.DirectiveId = directiveId;
            this.SweepPolicy = WorkCandidateObserver.SupportsSweep(kind) ? "Eligible" : "Disabled";
        }

        public string DirectiveId { get; }
        public string Phase { get; set; } = "Observing";
        public IReadOnlyList<WorkCandidate>? Candidates { get; set; }
        public int ProbeCursor { get; set; }
        public int CandidateCount { get; set; }
        public int MatchingCount { get; set; }
        public bool HasMoreCandidates { get; set; }
        public int ProbedCount { get; set; }
        public int EmptyScans { get; set; }
        public ulong ObservationRevision { get; set; }
        public Queue<ulong> EmptyScanRevisions { get; } = new();
        public int PathBlockedCount { get; set; }
        public int CooldownBlockedCount { get; set; }
        public ulong NextObservationTick { get; set; }
        public string? SelectedTarget { get; set; }
        public string? LastReason { get; set; }
        public string? CurrentOperationId { get; set; }
        public int ConsecutiveExecutionFaults { get; set; }
        public bool IsManualRequest { get; set; }
        public string? PreviousSuspension { get; set; }
        public WorkSweepPlan? ObservationSweepPlan { get; set; }
        public WorkSweepProposal? ProposedSweep { get; set; }
        public WorkStepGeometry? ProposedGeometry { get; set; }
        public Vector2? LastCommittedTargetTile { get; set; }
        public int? LastCommittedFacing { get; set; }
        public string SweepPolicy { get; set; }
        public string SweepAxis { get; set; } = "None";
        public int SweepDirection { get; set; }
        public int LaneCoordinate { get; set; }
        public int LaneStep { get; set; }
        public string LastSweepReason { get; set; } = "SWEEP-NOT-INITIALIZED";
        public Dictionary<string, ulong> Cooldowns { get; } = new(StringComparer.Ordinal);

        public WorkSweepHint? GetSweepHint()
        {
            if (this.SweepPolicy != "Active"
                || this.LastCommittedTargetTile is not Vector2 lastTarget
                || this.LastCommittedFacing is not int lastFacing
                || !Enum.TryParse(this.SweepAxis, out WorkSweepAxis axis)
                || this.SweepDirection is not (-1 or 1)
                || this.LaneStep is not (-1 or 1))
                return null;
            return new WorkSweepHint(axis, this.SweepDirection, this.LaneCoordinate, this.LaneStep, lastTarget, lastFacing);
        }

        public void CommitProposal(WorkStepGeometry? geometry, WorkSweepProposal? proposal)
        {
            if (geometry is not WorkStepGeometry committed)
            {
                this.ClearProposal();
                return;
            }

            this.LastCommittedTargetTile = committed.TargetTile;
            this.LastCommittedFacing = committed.Facing;
            if (proposal is not WorkSweepProposal sweep || sweep.IsFallback)
            {
                this.SweepPolicy = "Fallback";
                this.SweepAxis = "None";
                this.SweepDirection = 0;
                this.LaneCoordinate = 0;
                this.LaneStep = 0;
                this.LastSweepReason = proposal?.Reason ?? "SWEEP-FALLBACK-SPARSE";
                this.ClearProposal();
                return;
            }

            this.SweepPolicy = "Active";
            this.SweepAxis = sweep.Axis.ToString();
            this.SweepDirection = sweep.Direction;
            this.LaneCoordinate = sweep.LaneCoordinate;
            this.LaneStep = sweep.LaneStep;
            this.LastSweepReason = sweep.Reason == "SWEEP-LANE-TURN-PROPOSED"
                ? "SWEEP-LANE-TURN-COMMITTED"
                : sweep.Reason;
            this.ClearProposal();
        }

        public void ClearProposal()
        {
            this.ProposedGeometry = null;
            this.ProposedSweep = null;
            this.ObservationSweepPlan = null;
        }

        public string DescribeSweep() => $"{this.SweepPolicy},{this.SweepAxis},direction={this.SweepDirection},lane={this.LaneCoordinate},laneStep={this.LaneStep},reason={this.LastSweepReason}";

        public void RecordEmptyScan()
        {
            this.EmptyScans++;
            this.EmptyScanRevisions.Enqueue(this.ObservationRevision);
            while (this.EmptyScanRevisions.Count > 3)
                this.EmptyScanRevisions.Dequeue();
        }

        public void ResetEmptyScans()
        {
            this.EmptyScans = 0;
            this.EmptyScanRevisions.Clear();
        }
    }
}
