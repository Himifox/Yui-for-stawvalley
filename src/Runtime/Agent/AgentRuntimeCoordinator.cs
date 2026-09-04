using StardewModdingAPI;

namespace YuiToIssho;

internal sealed class AgentRuntimeCoordinator
{
    public const int MaximumRuntimes = 64;
    public const int RuntimeBudgetPerTick = 8;

    private readonly CompanionRegistry registry;
    private readonly IMonitor monitor;
    private readonly AgentPerceptionService perception;
    private readonly DeterministicAgentBrain brain = new();
    private readonly TaskExecutionService tasks;
    private readonly Dictionary<CompanionIdentity, AgentRuntime> runtimes = new();
    private string authorityGeneration = string.Empty;
    private int roundRobinCursor;
    private bool suspended = true;

    public AgentRuntimeCoordinator(CompanionRegistry registry, CompanionBodyBinder bodies, TaskExecutionService tasks, IMonitor monitor)
    {
        this.registry = registry;
        this.monitor = monitor;
        this.perception = new AgentPerceptionService(bodies);
        this.tasks = tasks;
    }

    public int Count => this.runtimes.Count;

    public void BeginHostSession()
    {
        this.Reset();
        if (!Context.IsMainPlayer)
            return;
        this.authorityGeneration = Guid.NewGuid().ToString("N");
        this.suspended = false;
        this.Synchronize();
        this.monitor.Log($"HY-AGENT-SESSION: Created {this.runtimes.Count} authoritative Runtime(s) in a new generation.", LogLevel.Trace);
    }

    public void Synchronize()
    {
        if (!Context.IsMainPlayer || this.suspended || string.IsNullOrEmpty(this.authorityGeneration))
        {
            if (!Context.IsMainPlayer)
                this.Reset();
            return;
        }

        CompanionIdentity[] active = this.registry.Active.Select(record => record.Identity).OrderBy(identity => identity.OwnerId).ToArray();
        if (active.Length > MaximumRuntimes)
            throw new InvalidOperationException($"Active companion count {active.Length} exceeds Runtime limit {MaximumRuntimes}.");
        HashSet<CompanionIdentity> activeSet = active.ToHashSet();
        foreach (CompanionIdentity stale in this.runtimes.Keys.Where(identity => !activeSet.Contains(identity)).ToArray())
            this.runtimes.Remove(stale);
        foreach (CompanionIdentity identity in active)
            if (!this.runtimes.ContainsKey(identity))
                this.runtimes.Add(identity, new AgentRuntime(identity, this.authorityGeneration));
        this.roundRobinCursor = this.runtimes.Count == 0 ? 0 : this.roundRobinCursor % this.runtimes.Count;
    }

    public void Interrupt(CompanionIdentity identity, string reason)
    {
        if (this.runtimes.TryGetValue(identity, out AgentRuntime? runtime))
            runtime.Interrupt(reason);
    }

    public void SuspendAll(string reason)
    {
        this.suspended = true;
        foreach (AgentRuntime runtime in this.runtimes.Values)
            runtime.Interrupt(reason);
    }

    public void InterruptAll(string reason)
    {
        foreach (AgentRuntime runtime in this.runtimes.Values)
            runtime.Interrupt(reason);
    }

    public void ResumeAll()
    {
        if (!Context.IsMainPlayer || string.IsNullOrEmpty(this.authorityGeneration))
            return;
        this.suspended = false;
        this.Synchronize();
        foreach (AgentRuntime runtime in this.runtimes.Values)
            runtime.Resume();
    }

    public void RemoveOwner(long ownerId, string reason)
    {
        foreach (AgentRuntime runtime in this.runtimes.Values.Where(runtime => runtime.Identity.OwnerId == ownerId))
            runtime.Interrupt(reason);
    }

    public void Reset()
    {
        this.runtimes.Clear();
        this.authorityGeneration = string.Empty;
        this.roundRobinCursor = 0;
        this.suspended = true;
    }

    public AgentRuntimeSnapshot? GetSnapshot(CompanionIdentity identity)
    {
        if (!this.runtimes.TryGetValue(identity, out AgentRuntime? runtime))
            return null;
        AgentPlanStep? step = runtime.CurrentPlan?.CurrentStep;
        return new AgentRuntimeSnapshot(runtime.Identity, runtime.AuthorityGeneration, runtime.SnapshotVersion, runtime.PlanGeneration, runtime.BehaviorState, runtime.BrainPhase, runtime.CurrentPlan?.IntentId ?? runtime.LastIntentId, step?.Kind ?? runtime.LastStepKind, step?.State ?? runtime.LastStepState, runtime.FailureCount, runtime.LastCancellationReason, runtime.LastFailure);
    }

    public IReadOnlyList<AgentRuntime> ObserveBudget(ulong tick)
    {
        IReadOnlyList<AgentRuntime> selected = this.SelectRuntimeBudget();
        foreach (AgentRuntime runtime in selected)
        {
            if (tick < runtime.NextEligibleTick
                || !OwnerLifecycleGate.CanAdvance(runtime.Identity)
                || !this.registry.TryGet(runtime.Identity, out CompanionRecord record))
                continue;
            try
            {
                runtime.BrainPhase = AgentBrainPhases.Observing;
                AgentPerceptionSnapshot snapshot = this.perception.Capture(runtime, record, tick);
                runtime.CurrentSnapshot = snapshot;
                runtime.SnapshotVersion = snapshot.SnapshotVersion;
                runtime.BrainPhase = AgentBrainPhases.Thinking;
                runtime.FailureCount = 0;
                runtime.LastFailure = null;
            }
            catch (Exception ex)
            {
                runtime.FailureCount++;
                runtime.LastFailure = ex.GetType().Name;
                runtime.BrainPhase = AgentBrainPhases.Cooldown;
                runtime.NextEligibleTick = tick + (ulong)Math.Min(600, 30 * runtime.FailureCount);
                this.monitor.Log($"HY-AGENT-PERCEPTION-FAULT: {runtime.Identity} entered bounded retry after {ex.GetType().Name}.", LogLevel.Error);
            }
        }
        return selected;
    }

    public IReadOnlyList<AgentRuntime> ThinkBudget(ulong tick)
    {
        IReadOnlyList<AgentRuntime> selected = this.ObserveBudget(tick);
        foreach (AgentRuntime runtime in selected.Where(runtime => runtime.BrainPhase == AgentBrainPhases.Thinking))
        {
            try
            {
                runtime.BehaviorState = this.ResolveBehavior(runtime);
                if (runtime.CurrentPlan is not null)
                {
                    bool factsChanged = runtime.CurrentSnapshot!.RecentChanges.Any(change => change is
                        "SELF-LOCATION-CHANGED" or "OWNER-AVAILABILITY-CHANGED" or "MODE-CHANGED" or "VITAL-STATE-CHANGED" or "TRANSACTION-CHANGED" or "WORK-DIRECTIVE-CHANGED");
                    AgentPlanValidationResult existing = this.brain.Validate(runtime, runtime.CurrentPlan, tick);
                    if (factsChanged || !existing.IsSuccess)
                        runtime.Interrupt(factsChanged ? "AUTHORITATIVE-FACTS-CHANGED" : existing.Code);
                }
                if (runtime.CurrentPlan is null)
                    runtime.AcceptPlan(this.brain.CreatePlan(runtime, tick));
            }
            catch (Exception ex)
            {
                runtime.Interrupt("BRAIN-FAULT");
                runtime.FailureCount++;
                runtime.LastFailure = ex.GetType().Name;
                runtime.BrainPhase = AgentBrainPhases.Cooldown;
                runtime.NextEligibleTick = tick + (ulong)Math.Min(600, 30 * runtime.FailureCount);
                this.monitor.Log($"HY-AGENT-BRAIN-FAULT: {runtime.Identity} entered bounded retry after {ex.GetType().Name}.", LogLevel.Error);
            }
        }
        return selected;
    }

    public AgentScheduleDecision Update(ulong tick)
    {
        bool advanceWork = false;
        bool advanceFollow = false;
        foreach (AgentRuntime runtime in this.ThinkBudget(tick))
        {
            if (runtime.BrainPhase != AgentBrainPhases.Executing || runtime.CurrentPlan?.CurrentStep is not AgentPlanStep step)
                continue;
            try
            {
                if (tick > runtime.CurrentPlan.ExpiresAtTick)
                {
                    step.State = AgentPlanStepStates.TimedOut;
                    runtime.Interrupt("PLAN-EXPIRED");
                    continue;
                }
                if (step.State == AgentPlanStepStates.Pending)
                {
                    step.State = AgentPlanStepStates.Starting;
                    step.StartedTick = tick;
                }
                switch (step.Kind)
                {
                    case AgentPlanStepKinds.AdvanceWorkExecutor:
                        step.State = AgentPlanStepStates.Completed;
                        step.ResultCode = "WORK-EXECUTOR-SCHEDULED";
                        advanceWork = true;
                        break;
                    case AgentPlanStepKinds.AdvanceFollowExecutor:
                        step.State = AgentPlanStepStates.Completed;
                        step.ResultCode = "FOLLOW-EXECUTOR-SCHEDULED";
                        advanceFollow = true;
                        break;
                    case AgentPlanStepKinds.ObserveTask:
                        step.State = AgentPlanStepStates.Completed;
                        step.ResultCode = string.IsNullOrWhiteSpace(runtime.CurrentSnapshot!.Self.ActiveTransactionId) ? "TASK-TERMINAL" : "TASK-OBSERVED";
                        break;
                    case AgentPlanStepKinds.Hold:
                    case AgentPlanStepKinds.Idle:
                        step.State = AgentPlanStepStates.Completed;
                        step.ResultCode = step.Kind == AgentPlanStepKinds.Hold ? "HELD" : "IDLE";
                        advanceFollow = true;
                        break;
                    default:
                        throw new InvalidOperationException("The validated step has no Executor route.");
                }
                runtime.LastIntentId = runtime.CurrentPlan.IntentId;
                runtime.LastStepKind = step.Kind;
                runtime.LastStepState = step.State;
                if (step.State == AgentPlanStepStates.Completed)
                {
                    runtime.CurrentPlan.CurrentStepIndex++;
                    if (runtime.CurrentPlan.CurrentStep is null)
                    {
                        runtime.CurrentPlan = null;
                        runtime.BrainPhase = AgentBrainPhases.Cooldown;
                        runtime.NextEligibleTick = tick + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                step.State = AgentPlanStepStates.Failed;
                step.ResultCode = "EXECUTOR-FAULT";
                runtime.Interrupt("EXECUTOR-FAULT");
                runtime.FailureCount++;
                runtime.LastFailure = ex.GetType().Name;
                runtime.BrainPhase = AgentBrainPhases.Cooldown;
                runtime.NextEligibleTick = tick + (ulong)Math.Min(600, 30 * runtime.FailureCount);
                this.monitor.Log($"HY-AGENT-EXECUTOR-FAULT: {runtime.Identity} entered bounded retry after {ex.GetType().Name}.", LogLevel.Error);
            }
        }
        return new AgentScheduleDecision(advanceWork, advanceFollow);
    }

    private string ResolveBehavior(AgentRuntime runtime)
    {
        AgentPerceptionSnapshot snapshot = runtime.CurrentSnapshot!;
        if (!snapshot.Self.BodyPresent || !snapshot.Owner.Online)
            return AgentBehaviorStates.Unavailable;
        if (snapshot.Self.VitalState is CompanionVitalStates.Downed or CompanionVitalStates.Recovering or CompanionVitalStates.Retreating)
            return AgentBehaviorStates.Recovering;
        if (snapshot.Self.Mode == CompanionModes.Wait)
            return AgentBehaviorStates.Waiting;
        if (!string.IsNullOrWhiteSpace(snapshot.Self.ActiveTransactionId))
            return this.tasks.GetSnapshot(runtime.Identity)?.TaskKind == "Combat" ? AgentBehaviorStates.Combat : AgentBehaviorStates.Working;
        if (snapshot.Self.Mode == CompanionModes.Work && snapshot.Self.WorkDirectiveId is not null)
            return AgentBehaviorStates.Working;
        if (snapshot.Self.VitalState == CompanionVitalStates.Resting)
            return AgentBehaviorStates.Resting;
        if (snapshot.Self.Mode == CompanionModes.Follow)
            return snapshot.Owner.SameLocation && snapshot.Owner.TileDistance <= 3 ? AgentBehaviorStates.Idle : AgentBehaviorStates.Following;
        return AgentBehaviorStates.Available;
    }

    internal IReadOnlyList<AgentRuntime> SelectRuntimeBudget()
    {
        this.Synchronize();
        if (this.suspended || this.runtimes.Count == 0)
            return Array.Empty<AgentRuntime>();
        AgentRuntime[] ordered = this.runtimes.Values
            .Where(runtime => OwnerLifecycleGate.CanAdvance(runtime.Identity))
            .OrderBy(runtime => runtime.Identity.OwnerId)
            .ToArray();
        if (ordered.Length == 0)
            return Array.Empty<AgentRuntime>();
        int count = Math.Min(RuntimeBudgetPerTick, ordered.Length);
        var selected = new List<AgentRuntime>(count);
        for (int offset = 0; offset < count; offset++)
            selected.Add(ordered[(this.roundRobinCursor + offset) % ordered.Length]);
        this.roundRobinCursor = (this.roundRobinCursor + count) % ordered.Length;
        return selected;
    }
}
