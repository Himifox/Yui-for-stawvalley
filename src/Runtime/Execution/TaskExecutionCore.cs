using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace YuiToIssho;

internal readonly record struct TaskExecutionResult(bool IsSuccess, string Code, string Message)
{
    public static TaskExecutionResult Success(string code, string message) => new(true, code, message);

    public static TaskExecutionResult Failure(string code, string message) => new(false, code, message);
}

internal readonly record struct TaskTargetKey(string LocationKey, string TargetKind, string StableId)
{
    public override string ToString() => $"{this.LocationKey}:{this.TargetKind}:{this.StableId}";
}

internal enum TaskSessionPhase
{
    Reserved,
    Traveling,
    Settling,
    Released,
}

internal sealed class TaskSession
{
    private string? activeSettlementStep;

    public TaskSession(CompanionIdentity identity, string operationId, string taskKind, IReadOnlyList<TaskTargetKey> targets, string resumeMode, string? parentTransactionId = null)
    {
        if (targets.Count == 0)
            throw new ArgumentException("A task session must reserve at least one target.", nameof(targets));

        this.Identity = identity;
        this.OperationId = operationId;
        this.TaskKind = taskKind;
        this.Targets = targets;
        this.ResumeMode = resumeMode;
        this.ParentTransactionId = parentTransactionId;
    }

    public CompanionIdentity Identity { get; }

    public string OperationId { get; }

    public string TaskKind { get; }

    public TaskTargetKey Target => this.Targets[0];

    public IReadOnlyList<TaskTargetKey> Targets { get; private set; }

    public string ResumeMode { get; }

    public string? ParentTransactionId { get; }

    public TaskSessionPhase Phase { get; private set; } = TaskSessionPhase.Reserved;

    public bool TryEnterSettlement() => this.TryEnterSettlement(this.OperationId);

    public bool TryEnterSettlement(string stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId)
            || this.Phase == TaskSessionPhase.Released
            || this.activeSettlementStep is not null)
            return false;

        this.activeSettlementStep = stepId;
        this.Phase = TaskSessionPhase.Settling;
        return true;
    }

    public void FinishSettlementStep(string stepId)
    {
        if (!string.Equals(this.activeSettlementStep, stepId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Settlement step {stepId} does not own the active commit gate.");

        this.activeSettlementStep = null;
        this.Phase = TaskSessionPhase.Traveling;
    }

    public void MarkTraveling()
    {
        if (this.Phase == TaskSessionPhase.Reserved)
            this.Phase = TaskSessionPhase.Traveling;
    }

    public void MarkReleased() => this.Phase = TaskSessionPhase.Released;

    internal void ReplaceTargets(IReadOnlyList<TaskTargetKey> targets)
    {
        if (this.Phase == TaskSessionPhase.Released || targets.Count == 0)
            throw new InvalidOperationException("A released task cannot replace its reservation set.");
        this.Targets = targets.ToArray();
    }
}

internal readonly record struct TaskBeginResult(bool Started, TaskSession? Session, TaskExecutionResult Result);

internal readonly record struct TaskDirectiveBeginResult(bool Started, string ResumeMode, TaskExecutionResult Result);

internal readonly record struct TaskSessionSnapshot(string OperationId, string TaskKind, string Phase, int TargetCount);

internal readonly record struct TaskCompletionObservation(
    CompanionIdentity Identity,
    string OperationId,
    string TaskKind,
    TaskExecutionResult Result);

internal static class TaskReceiptStore
{
    public static bool TryGet(CompanionRecord record, string operationId, out TaskExecutionResult result)
    {
        OperationReceiptRecord? receipt = record.RecentOperations.FirstOrDefault(item =>
            string.Equals(item.OperationId, operationId, StringComparison.Ordinal));
        if (receipt is null)
        {
            result = default;
            return false;
        }

        result = receipt.IsSuccess
            ? TaskExecutionResult.Success(receipt.Code, receipt.Message)
            : TaskExecutionResult.Failure(receipt.Code, receipt.Message);
        return true;
    }

    public static void Add(CompanionRecord record, string operationId, bool isSuccess, string code, string message)
    {
        if (record.RecentOperations.Any(receipt => string.Equals(receipt.OperationId, operationId, StringComparison.Ordinal)))
            return;

        // Operation IDs are permanent idempotency keys. Never discard one merely because it is old;
        // any future compaction must first introduce a persisted generation boundary.
        record.RecentOperations.Add(new OperationReceiptRecord
        {
            OperationId = operationId,
            IsSuccess = isSuccess,
            Code = code,
            Message = message,
        });
    }
}

internal sealed class TaskExecutionService
{
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, TaskSession> sessions = new();
    private readonly Dictionary<TaskTargetKey, CompanionIdentity> reservations = new();
    private Action<TaskCompletionObservation>? completionObserver;

    public TaskExecutionService(CompanionRegistry registry, CompanionBodyBinder bodies, IMonitor monitor)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.monitor = monitor;
    }

    public void AttachCompletionObserver(Action<TaskCompletionObservation> observer) => this.completionObserver = observer;

    public bool TryResolveExisting(CompanionIdentity identity, string operationId, out TaskExecutionResult result)
    {
        if (!IsValidOperationId(operationId))
        {
            result = TaskExecutionResult.Failure("INVALID-OPERATION-ID", "OperationId must contain 1 to 128 non-control characters.");
            return true;
        }

        if (!this.registry.TryGet(identity, out CompanionRecord record))
        {
            result = TaskExecutionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
            return true;
        }
        if (!identity.IsCanonical)
        {
            result = TaskExecutionResult.Failure("SINGLE-COMPANION-PER-OWNER", "Only the Owner's current Yui identity can enter task execution.");
            return true;
        }

        if (TaskReceiptStore.TryGet(record, operationId, out result))
            return true;

        if (this.sessions.TryGetValue(identity, out TaskSession? current))
        {
            result = string.Equals(current.OperationId, operationId, StringComparison.Ordinal)
                ? TaskExecutionResult.Success("ALREADY-ACTIVE", $"Operation {operationId} is already active.")
                : TaskExecutionResult.Failure("COMPANION-BUSY", $"{identity} is already executing {current.OperationId}.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
        {
            result = TaskExecutionResult.Failure("COMPANION-BUSY", $"{identity} is already executing {record.ActiveTransactionId}.");
            return true;
        }

        result = default;
        return false;
    }

    public TaskExecutionResult GetOperationStatus(CompanionIdentity identity, string operationId)
    {
        if (!IsValidOperationId(operationId))
            return TaskExecutionResult.Failure("INVALID-OPERATION-ID", "OperationId must contain 1 to 128 non-control characters.");
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return TaskExecutionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (TaskReceiptStore.TryGet(record, operationId, out TaskExecutionResult receipt))
            return receipt;
        if (this.sessions.TryGetValue(identity, out TaskSession? session)
            && string.Equals(session.OperationId, operationId, StringComparison.Ordinal))
            return TaskExecutionResult.Success("OPERATION-ACTIVE", $"Operation {operationId} is active in {session.Phase}.");
        if (record.CraftTransaction is CraftTransactionRecord craft
            && string.Equals(craft.OperationId, operationId, StringComparison.Ordinal))
            return TaskExecutionResult.Success(
                craft.Phase == CraftPhases.Reconciling ? "OPERATION-RECONCILING" : "OPERATION-ACTIVE",
                $"Craft operation {operationId} is {craft.Phase}; output={craft.OutputLocation ?? "none"}.");
        if (record.PlantingTransaction is PlantingTransactionRecord planting
            && string.Equals(planting.RequestOperationId, operationId, StringComparison.Ordinal))
            return TaskExecutionResult.Success(
                planting.Phase == PlantingPhases.Reconciling ? "OPERATION-RECONCILING" : "OPERATION-ACTIVE",
                $"Planting operation {operationId} is {planting.Phase}; planted={planting.PlantedCount}/{planting.RequestedCount}.");
        if (string.Equals(record.ActiveTransactionId, operationId, StringComparison.Ordinal))
            return TaskExecutionResult.Success("OPERATION-ACTIVE", $"Operation {operationId} is the active companion transaction.");
        return TaskExecutionResult.Failure("OPERATION-NOT-FOUND", $"Operation {operationId} has no active state or persistent receipt.");
    }

    public TaskBeginResult TryBegin(CompanionIdentity identity, string operationId, string taskKind, TaskTargetKey target)
        => this.TryBegin(identity, operationId, taskKind, new[] { target });

    public TaskBeginResult TryBegin(CompanionIdentity identity, string operationId, string taskKind, IReadOnlyList<TaskTargetKey> targets)
    {
        if (this.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return new TaskBeginResult(false, null, existing);

        if (targets.Count == 0 || targets.Distinct().Count() != targets.Count)
        {
            return new TaskBeginResult(false, null,
                TaskExecutionResult.Failure("INVALID-TARGET-SET", "A task must provide at least one unique reservation target."));
        }

        foreach (TaskTargetKey target in targets)
        {
            if (this.reservations.TryGetValue(target, out CompanionIdentity reservedBy))
            {
                return new TaskBeginResult(false, null,
                    TaskExecutionResult.Failure("TARGET-RESERVED", $"Target {target} is reserved by {reservedBy}."));
            }
        }

        CompanionRecord record = this.registry.TryGet(identity, out CompanionRecord found)
            ? found
            : throw new InvalidOperationException("The identity disappeared during an authoritative task start.");
        var session = new TaskSession(identity, operationId, taskKind, targets.ToArray(), record.Mode);
        foreach (TaskTargetKey target in session.Targets)
            this.reservations.Add(target, identity);
        this.sessions.Add(identity, session);
        record.ActiveTransactionId = operationId;
        this.bodies.Halt(identity);
        this.monitor.Log($"HY-TASK-STARTED: {identity} started {taskKind} operation {operationId} with {session.Targets.Count} reservation(s).", LogLevel.Trace);
        return new TaskBeginResult(true, session, TaskExecutionResult.Success("STARTED", $"Operation {operationId} started."));
    }

    public TaskBeginResult TryBeginChild(
        CompanionIdentity identity,
        string operationId,
        string parentTransactionId,
        string taskKind,
        TaskTargetKey target) => this.TryBeginChild(identity, operationId, parentTransactionId, taskKind, new[] { target });

    public TaskBeginResult TryBeginChild(
        CompanionIdentity identity,
        string operationId,
        string parentTransactionId,
        string taskKind,
        IReadOnlyList<TaskTargetKey> targets)
    {
        if (!IsValidOperationId(operationId) || !IsValidOperationId(parentTransactionId))
            return new TaskBeginResult(false, null, TaskExecutionResult.Failure("INVALID-OPERATION-ID", "Child and parent operation IDs must be bounded non-control text."));
        if (!this.registry.TryGet(identity, out CompanionRecord record) || !identity.IsCanonical)
            return new TaskBeginResult(false, null, TaskExecutionResult.Failure("IDENTITY-NOT-FOUND", "The canonical companion identity is unavailable."));
        if (!string.Equals(record.ActiveTransactionId, parentTransactionId, StringComparison.Ordinal))
            return new TaskBeginResult(false, null, TaskExecutionResult.Failure("PARENT-TRANSACTION-STALE", "The persistent parent transaction no longer owns the companion gate."));
        if (TaskReceiptStore.TryGet(record, operationId, out TaskExecutionResult receipt))
            return new TaskBeginResult(false, null, receipt);
        if (this.sessions.TryGetValue(identity, out TaskSession? current))
            return new TaskBeginResult(false, null, TaskExecutionResult.Failure("COMPANION-BUSY", $"{identity} is already executing child step {current.OperationId}."));
        if (targets.Count == 0 || targets.Distinct().Count() != targets.Count)
            return new TaskBeginResult(false, null, TaskExecutionResult.Failure("INVALID-TARGET-SET", "A child task must reserve one or more unique targets."));
        foreach (TaskTargetKey target in targets)
        {
            if (this.reservations.TryGetValue(target, out CompanionIdentity reservedBy))
                return new TaskBeginResult(false, null, TaskExecutionResult.Failure("TARGET-RESERVED", $"Target {target} is reserved by {reservedBy}."));
        }

        var session = new TaskSession(identity, operationId, taskKind, targets.ToArray(), record.Mode, parentTransactionId);
        foreach (TaskTargetKey target in targets)
            this.reservations.Add(target, identity);
        this.sessions.Add(identity, session);
        this.bodies.Halt(identity);
        this.monitor.Log($"HY-TASK-CHILD-STARTED: {identity} started {taskKind} child {operationId} under {parentTransactionId}.", LogLevel.Trace);
        return new TaskBeginResult(true, session, TaskExecutionResult.Success("STARTED", $"Child operation {operationId} started."));
    }

    public TaskDirectiveBeginResult TryBeginDirective(CompanionIdentity identity, string operationId, string taskKind)
    {
        if (this.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return new TaskDirectiveBeginResult(false, string.Empty, existing);
        CompanionRecord record = this.registry.TryGet(identity, out CompanionRecord found)
            ? found
            : throw new InvalidOperationException("The identity disappeared during directive start.");
        record.ActiveTransactionId = operationId;
        this.bodies.Halt(identity);
        this.monitor.Log($"HY-TASK-DIRECTIVE-STARTED: {identity} started {taskKind} directive {operationId}.", LogLevel.Trace);
        return new TaskDirectiveBeginResult(true, record.Mode, TaskExecutionResult.Success("STARTED", $"Directive {operationId} started."));
    }

    public TaskExecutionResult CompleteDirective(CompanionIdentity identity, string operationId, string resumeMode, bool success, string code, string message)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record)
            || !string.Equals(record.ActiveTransactionId, operationId, StringComparison.Ordinal))
            return TaskExecutionResult.Failure("PARENT-TRANSACTION-STALE", $"Directive {operationId} no longer owns the companion gate.");
        if (this.sessions.TryGetValue(identity, out TaskSession? child))
            this.ReleaseRuntime(child);
        record.ActiveTransactionId = null;
        record.Mode = resumeMode;
        TaskReceiptStore.Add(record, operationId, success, code, message);
        this.bodies.Halt(identity);
        this.monitor.Log($"HY-TASK-DIRECTIVE-{code}: {identity} {message}", success ? LogLevel.Info : LogLevel.Warn);
        TaskExecutionResult result = success ? TaskExecutionResult.Success(code, message) : TaskExecutionResult.Failure(code, message);
        this.ObserveCompletion(new TaskCompletionObservation(identity, operationId, "Directive", result));
        return result;
    }

    public bool IsCurrent(TaskSession session)
    {
        return this.sessions.TryGetValue(session.Identity, out TaskSession? current)
            && ReferenceEquals(current, session)
            && this.registry.TryGet(session.Identity, out CompanionRecord record)
            && string.Equals(record.ActiveTransactionId, session.ParentTransactionId ?? session.OperationId, StringComparison.Ordinal);
    }

    public TaskExecutionResult TryReplaceTargets(TaskSession session, IReadOnlyList<TaskTargetKey> targets)
    {
        if (!this.IsCurrent(session))
            return TaskExecutionResult.Failure("SESSION-NOT-CURRENT", "Only the current task session can replace reservations.");
        TaskTargetKey[] replacement = targets.Distinct().ToArray();
        if (replacement.Length == 0 || replacement.Length != targets.Count)
            return TaskExecutionResult.Failure("INVALID-TARGET-SET", "Replacement reservations must contain one or more unique targets.");
        foreach (TaskTargetKey target in replacement)
        {
            if (this.reservations.TryGetValue(target, out CompanionIdentity reservedBy)
                && reservedBy != session.Identity)
                return TaskExecutionResult.Failure("TARGET-RESERVED", $"Target {target} is reserved by {reservedBy}.");
        }

        foreach (TaskTargetKey target in session.Targets.Where(target => !replacement.Contains(target)))
            if (this.reservations.TryGetValue(target, out CompanionIdentity reservedBy) && reservedBy == session.Identity)
                this.reservations.Remove(target);
        foreach (TaskTargetKey target in replacement)
            this.reservations[target] = session.Identity;
        session.ReplaceTargets(replacement);
        return TaskExecutionResult.Success("TARGET-SET-RESERVED", $"Reserved the complete {replacement.Length}-target settlement set.");
    }

    public TaskSessionSnapshot? GetSnapshot(CompanionIdentity identity) => this.sessions.TryGetValue(identity, out TaskSession? session)
        ? new TaskSessionSnapshot(session.OperationId, session.TaskKind, session.Phase.ToString(), session.Targets.Count)
        : null;

    public TaskExecutionResult Complete(TaskSession session, bool success, string code, string message)
    {
        if (!this.sessions.TryGetValue(session.Identity, out TaskSession? current) || !ReferenceEquals(current, session))
            return TaskExecutionResult.Failure("SESSION-NOT-CURRENT", $"Operation {session.OperationId} is no longer the active task session.");

        if (this.registry.TryGet(session.Identity, out CompanionRecord record))
        {
            if (session.ParentTransactionId is null && string.Equals(record.ActiveTransactionId, session.OperationId, StringComparison.Ordinal))
                record.ActiveTransactionId = null;
            if (session.ParentTransactionId is null)
                record.Mode = session.ResumeMode;
            TaskReceiptStore.Add(record, session.OperationId, success, code, message);
        }

        this.ReleaseRuntime(session);
        this.monitor.Log($"HY-TASK-{code}: {session.Identity} {session.TaskKind} operation {session.OperationId}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        TaskExecutionResult result = success ? TaskExecutionResult.Success(code, message) : TaskExecutionResult.Failure(code, message);
        this.ObserveCompletion(new TaskCompletionObservation(session.Identity, session.OperationId, session.TaskKind, result));
        return result;
    }

    public void AbandonRuntime(TaskSession session)
    {
        if (this.sessions.TryGetValue(session.Identity, out TaskSession? current) && ReferenceEquals(current, session))
            this.ReleaseRuntime(session);
    }

    public void ClearRuntime()
    {
        foreach (TaskSession session in this.sessions.Values.ToArray())
            this.ReleaseRuntime(session);
    }

    private void ReleaseRuntime(TaskSession session)
    {
        this.bodies.Halt(session.Identity);
        this.sessions.Remove(session.Identity);
        foreach (TaskTargetKey target in session.Targets)
        {
            if (this.reservations.TryGetValue(target, out CompanionIdentity reservedBy) && reservedBy == session.Identity)
                this.reservations.Remove(target);
        }
        session.MarkReleased();
    }

    private void ObserveCompletion(TaskCompletionObservation observation)
    {
        try
        {
            this.completionObserver?.Invoke(observation);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"HY-TASK-OBSERVER-FAILED: {observation.Identity} {ex.GetType().Name}.", LogLevel.Warn);
        }
    }

    private static bool IsValidOperationId(string? operationId)
    {
        return !string.IsNullOrWhiteSpace(operationId)
            && operationId.Length <= 128
            && !operationId.Any(char.IsControl);
    }
}

internal sealed class TaskNavigationState
{
    public TaskNavigationState(Vector2 initialPosition, ulong tick)
    {
        this.LastPosition = initialPosition;
        this.LastProgressTick = tick;
    }

    public Vector2 LastPosition { get; set; }

    public ulong LastProgressTick { get; set; }

    public int FailedAttempts { get; set; }

    public ulong NextPathTick { get; set; }

    public bool PathIssued { get; set; }
}

internal readonly record struct TaskNavigationResult(bool BudgetExhausted, bool CanIssuePath);

internal sealed class TaskNavigationService
{
    private readonly CompanionBodyBinder bodies;

    public TaskNavigationService(CompanionBodyBinder bodies)
    {
        this.bodies = bodies;
    }

    public bool CanReach(NPC body, GameLocation location, Vector2 destination, int facing, int pathSearchLimit)
    {
        if (body.TilePoint == destination.ToPoint())
            return true;

        var probe = new PathFindController(body, location, destination.ToPoint(), facing, null, pathSearchLimit);
        return probe.pathToEndPoint is { Count: > 0 };
    }

    public Vector2? FindReachableCardinalApproach(NPC body, GameLocation location, Vector2 target, int pathSearchLimit)
    {
        Vector2[] candidates =
        {
            target + new Vector2(1, 0),
            target + new Vector2(-1, 0),
            target + new Vector2(0, 1),
            target + new Vector2(0, -1),
        };
        foreach (Vector2 candidate in candidates
            .Where(candidate => location.isTileLocationOpen(candidate)
                && location.characters.All(character => ReferenceEquals(character, body) || character.Tile != candidate))
            .OrderBy(candidate => Vector2.DistanceSquared(
                candidate * Game1.tileSize + new Vector2(Game1.tileSize / 2f),
                body.StandingPixel.ToVector2()))
            .ThenBy(candidate => TurnDistance(body.FacingDirection, FacingToward(candidate, target))))
        {
            if (this.CanReach(body, location, candidate, FacingToward(candidate, target), pathSearchLimit))
                return candidate;
        }
        return null;
    }

    internal static int FacingToward(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }

    internal static int TurnDistance(int from, int to)
    {
        int delta = Math.Abs((from & 3) - (to & 3));
        return Math.Min(delta, 4 - delta);
    }

    public TaskNavigationResult Observe(CompanionIdentity identity, NPC body, TaskNavigationState state, ulong tick, ulong stuckTimeoutTicks, int maximumAttempts, int repathDelayTicks)
    {
        if (body.Position != state.LastPosition)
        {
            state.LastPosition = body.Position;
            state.LastProgressTick = tick;
            state.FailedAttempts = 0;
            return new TaskNavigationResult(false, false);
        }

        if (body.controller is not null)
        {
            if (tick - state.LastProgressTick < stuckTimeoutTicks)
                return new TaskNavigationResult(false, false);

            this.RegisterFailure(identity, state, tick, repathDelayTicks);
            return new TaskNavigationResult(state.FailedAttempts >= maximumAttempts, false);
        }

        if (state.PathIssued)
        {
            this.RegisterFailure(identity, state, tick, repathDelayTicks);
            if (state.FailedAttempts >= maximumAttempts)
                return new TaskNavigationResult(true, false);
        }

        return new TaskNavigationResult(false, tick >= state.NextPathTick);
    }

    public void MarkPathIssued(TaskNavigationState state, Vector2 position, ulong tick, int repathDelayTicks)
    {
        state.PathIssued = true;
        state.LastPosition = position;
        state.LastProgressTick = tick;
        state.NextPathTick = tick + (ulong)repathDelayTicks;
    }

    private void RegisterFailure(CompanionIdentity identity, TaskNavigationState state, ulong tick, int repathDelayTicks)
    {
        this.bodies.Halt(identity);
        state.PathIssued = false;
        state.FailedAttempts++;
        state.LastProgressTick = tick;
        state.NextPathTick = tick + (ulong)(repathDelayTicks * state.FailedAttempts);
    }
}
