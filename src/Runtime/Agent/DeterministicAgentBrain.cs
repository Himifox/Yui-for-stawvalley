namespace YuiToIssho;

internal readonly record struct AgentPlanValidationResult(bool IsSuccess, string Code, string Message)
{
    public static AgentPlanValidationResult Success() => new(true, "PLAN-VALID", "The plan is bound to the current authoritative snapshot.");
    public static AgentPlanValidationResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class DeterministicAgentBrain
{
    public const ulong PlanLifetimeTicks = 120;
    private static readonly string[] Priority =
    {
        AgentIntentIds.Recover,
        AgentIntentIds.HoldForLifecycle,
        AgentIntentIds.HonorWait,
        AgentIntentIds.ObserveManualTask,
        AgentIntentIds.MaintainContinuousWork,
        AgentIntentIds.FollowOwner,
        AgentIntentIds.IdleNearby,
    };

    public AgentPlan CreatePlan(AgentRuntime runtime, ulong tick)
    {
        AgentPerceptionSnapshot snapshot = runtime.CurrentSnapshot
            ?? throw new InvalidOperationException("A Brain cannot plan without an authoritative perception snapshot.");
        string intent = Priority.FirstOrDefault(snapshot.AllowedIntentIds.Contains)
            ?? throw new InvalidOperationException("The snapshot exposes no supported deterministic intent.");
        string step = intent switch
        {
            AgentIntentIds.Recover or AgentIntentIds.HoldForLifecycle or AgentIntentIds.HonorWait => AgentPlanStepKinds.Hold,
            AgentIntentIds.ObserveManualTask => AgentPlanStepKinds.ObserveTask,
            AgentIntentIds.MaintainContinuousWork => AgentPlanStepKinds.AdvanceWorkExecutor,
            AgentIntentIds.FollowOwner => AgentPlanStepKinds.AdvanceFollowExecutor,
            AgentIntentIds.IdleNearby => AgentPlanStepKinds.Idle,
            _ => throw new InvalidOperationException("The selected intent has no deterministic step mapping."),
        };
        return new AgentPlan(Guid.NewGuid().ToString("N"), intent, snapshot.SnapshotVersion, runtime.PlanGeneration + 1, tick + PlanLifetimeTicks, new[] { new AgentPlanStep(step) });
    }

    public AgentPlanValidationResult Validate(AgentRuntime runtime, AgentPlan? plan, ulong tick)
    {
        AgentPerceptionSnapshot? snapshot = runtime.CurrentSnapshot;
        if (snapshot is null || plan is null)
            return AgentPlanValidationResult.Failure("PLAN-MISSING", "A current snapshot and plan are required.");
        if (snapshot.Identity != runtime.Identity || snapshot.AuthorityGeneration != runtime.AuthorityGeneration)
            return AgentPlanValidationResult.Failure("PLAN-AUTHORITY-MISMATCH", "The snapshot belongs to another identity or authority generation.");
        if (plan.Generation != runtime.PlanGeneration || plan.SnapshotVersion != snapshot.SnapshotVersion)
            return AgentPlanValidationResult.Failure("PLAN-SNAPSHOT-STALE", "The plan is not bound to the current snapshot and generation.");
        if (tick > plan.ExpiresAtTick)
            return AgentPlanValidationResult.Failure("PLAN-EXPIRED", "The plan exceeded its bounded lifetime.");
        if (!snapshot.AllowedIntentIds.Contains(plan.IntentId, StringComparer.Ordinal))
            return AgentPlanValidationResult.Failure("PLAN-INTENT-NOT-ALLOWED", "The intent is absent from AllowedIntentIds.");
        if (plan.Steps.Count is < 1 or > 4 || plan.Steps.Any(step => !AgentPlanStepKinds.IsValid(step.Kind)))
            return AgentPlanValidationResult.Failure("PLAN-STEPS-INVALID", "The plan has an unknown step or exceeds four steps.");
        HashSet<string> targets = snapshot.NearbyTargets.Select(target => target.TargetId).ToHashSet(StringComparer.Ordinal);
        if (plan.Steps.Any(step => step.TargetId is not null && !targets.Contains(step.TargetId)))
            return AgentPlanValidationResult.Failure("PLAN-TARGET-NOT-ALLOWED", "A step target is absent from the bound snapshot.");
        return AgentPlanValidationResult.Success();
    }
}
