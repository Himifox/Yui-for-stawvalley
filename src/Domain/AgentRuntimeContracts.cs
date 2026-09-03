namespace YuiToIssho;

internal static class AgentBehaviorStates
{
    public const string Unavailable = "Unavailable";
    public const string Available = "Available";
    public const string Following = "Following";
    public const string Idle = "Idle";
    public const string Working = "Working";
    public const string Combat = "Combat";
    public const string Waiting = "Waiting";
    public const string Resting = "Resting";
    public const string Recovering = "Recovering";

    public static bool IsValid(string? value) => value is Unavailable or Available or Following or Idle or Working or Combat or Waiting or Resting or Recovering;
}

internal static class AgentBrainPhases
{
    public const string Dormant = "Dormant";
    public const string Observing = "Observing";
    public const string Thinking = "Thinking";
    public const string Executing = "Executing";
    public const string Cooldown = "Cooldown";
    public const string Interrupted = "Interrupted";

    public static bool IsValid(string? value) => value is Dormant or Observing or Thinking or Executing or Cooldown or Interrupted;
}

internal static class AgentIntentIds
{
    public const string Recover = "Recover";
    public const string HoldForLifecycle = "HoldForLifecycle";
    public const string HonorWait = "HonorWait";
    public const string ObserveManualTask = "ObserveManualTask";
    public const string MaintainContinuousWork = "MaintainContinuousWork";
    public const string FollowOwner = "FollowOwner";
    public const string IdleNearby = "IdleNearby";

    public static bool IsValid(string? value) => value is Recover or HoldForLifecycle or HonorWait or ObserveManualTask or MaintainContinuousWork or FollowOwner or IdleNearby;
}

internal static class AgentPlanStepKinds
{
    public const string Hold = "Hold";
    public const string ObserveTask = "ObserveTask";
    public const string AdvanceWorkExecutor = "AdvanceWorkExecutor";
    public const string AdvanceFollowExecutor = "AdvanceFollowExecutor";
    public const string Idle = "Idle";

    public static bool IsValid(string? value) => value is Hold or ObserveTask or AdvanceWorkExecutor or AdvanceFollowExecutor or Idle;
}

internal static class AgentPlanStepStates
{
    public const string Pending = "Pending";
    public const string Starting = "Starting";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Interrupted = "Interrupted";
    public const string TimedOut = "TimedOut";

    public static bool IsValid(string? value) => value is Pending or Starting or Running or Completed or Failed or Interrupted or TimedOut;
}

internal sealed class AgentPlanStep
{
    public AgentPlanStep(string kind, string? targetId = null)
    {
        if (!AgentPlanStepKinds.IsValid(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        this.Kind = kind;
        this.TargetId = targetId;
    }

    public string Kind { get; }
    public string? TargetId { get; }
    public string State { get; set; } = AgentPlanStepStates.Pending;
    public ulong StartedTick { get; set; }
    public string? ResultCode { get; set; }
}

internal sealed class AgentPlan
{
    public AgentPlan(string planId, string intentId, long snapshotVersion, long generation, ulong expiresAtTick, IReadOnlyList<AgentPlanStep> steps)
    {
        if (!Guid.TryParseExact(planId, "N", out _) || !AgentIntentIds.IsValid(intentId) || steps.Count is < 1 or > 4)
            throw new ArgumentException("The plan contract is invalid.");
        this.PlanId = planId;
        this.IntentId = intentId;
        this.SnapshotVersion = snapshotVersion;
        this.Generation = generation;
        this.ExpiresAtTick = expiresAtTick;
        this.Steps = steps;
    }

    public string PlanId { get; }
    public string IntentId { get; }
    public long SnapshotVersion { get; }
    public long Generation { get; }
    public ulong ExpiresAtTick { get; }
    public IReadOnlyList<AgentPlanStep> Steps { get; }
    public int CurrentStepIndex { get; set; }
    public AgentPlanStep? CurrentStep => this.CurrentStepIndex < this.Steps.Count ? this.Steps[this.CurrentStepIndex] : null;
}

internal sealed record AgentSelfPerception(
    bool BodyPresent,
    string LocationKey,
    int TileX,
    int TileY,
    int Facing,
    bool Moving,
    string Mode,
    string VitalState,
    int Health,
    float Stamina,
    string? ActiveTransactionId,
    string? WorkDirectiveId);

internal sealed record AgentOwnerPerception(
    bool Online,
    bool SameLocation,
    string LocationKey,
    int TileX,
    int TileY,
    int TileDistance);

internal sealed record AgentWorldPerception(string LocationKey, int Day, int TimeOfDay, int NearbyDangerCount);

internal sealed record AgentTargetPerception(
    string TargetId,
    string Kind,
    string Subtype,
    string StableId,
    int TileX,
    int TileY,
    int Distance,
    string? SuggestedWorkKind,
    string Disposition,
    string ReasonCode);

internal sealed record AgentPerceptionSnapshot(
    CompanionIdentity Identity,
    string AuthorityGeneration,
    long SnapshotVersion,
    ulong CreatedTick,
    AgentSelfPerception Self,
    AgentOwnerPerception Owner,
    AgentWorldPerception World,
    IReadOnlyList<AgentTargetPerception> NearbyTargets,
    IReadOnlyList<string> RecentChanges,
    IReadOnlyList<string> AllowedIntentIds);

internal sealed class AgentRuntime
{
    public AgentRuntime(CompanionIdentity identity, string authorityGeneration)
    {
        if (!identity.IsCanonical || !Guid.TryParseExact(authorityGeneration, "N", out _))
            throw new ArgumentException("Only a canonical identity in a valid authority generation may own a Runtime.");
        this.Identity = identity;
        this.AuthorityGeneration = authorityGeneration;
    }

    public CompanionIdentity Identity { get; }
    public string AuthorityGeneration { get; }
    public long SnapshotVersion { get; set; }
    public long PlanGeneration { get; private set; }
    public string BehaviorState { get; set; } = AgentBehaviorStates.Unavailable;
    public string BrainPhase { get; set; } = AgentBrainPhases.Dormant;
    public AgentPerceptionSnapshot? CurrentSnapshot { get; set; }
    public AgentPlan? CurrentPlan { get; set; }
    public int FailureCount { get; set; }
    public ulong NextEligibleTick { get; set; }
    public string? LastCancellationReason { get; private set; }
    public string? LastFailure { get; set; }
    public string? LastIntentId { get; set; }
    public string? LastStepKind { get; set; }
    public string? LastStepState { get; set; }

    public void Interrupt(string reason)
    {
        string boundedReason = Bound(reason, 96);
        if (this.CurrentPlan is null && this.BrainPhase == AgentBrainPhases.Interrupted && this.LastCancellationReason == boundedReason)
            return;
        if (this.CurrentPlan is not null)
            foreach (AgentPlanStep step in this.CurrentPlan.Steps.Where(step => step.State is AgentPlanStepStates.Pending or AgentPlanStepStates.Starting or AgentPlanStepStates.Running))
                step.State = AgentPlanStepStates.Interrupted;
        this.CurrentPlan = null;
        this.PlanGeneration++;
        this.BrainPhase = AgentBrainPhases.Interrupted;
        this.LastCancellationReason = boundedReason;
    }

    public void AcceptPlan(AgentPlan plan)
    {
        if (plan.Generation != this.PlanGeneration + 1 || plan.SnapshotVersion != this.SnapshotVersion)
            throw new InvalidOperationException("The plan does not belong to the next generation of the current snapshot.");
        this.PlanGeneration = plan.Generation;
        this.CurrentPlan = plan;
        this.LastIntentId = plan.IntentId;
        this.LastStepKind = plan.CurrentStep?.Kind;
        this.LastStepState = plan.CurrentStep?.State;
        this.BrainPhase = AgentBrainPhases.Executing;
    }

    public void Resume() => this.BrainPhase = AgentBrainPhases.Dormant;

    private static string Bound(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? "UNSPECIFIED" : value.Length <= maximum ? value : value[..maximum];
}

internal readonly record struct AgentRuntimeSnapshot(
    CompanionIdentity Identity,
    string AuthorityGeneration,
    long SnapshotVersion,
    long PlanGeneration,
    string BehaviorState,
    string BrainPhase,
    string? IntentId,
    string? StepKind,
    string? StepState,
    int FailureCount,
    string? LastCancellationReason,
    string? LastFailure);

internal readonly record struct AgentScheduleDecision(bool AdvanceWork, bool AdvanceFollow);
