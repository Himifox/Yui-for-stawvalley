using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using xTile.Dimensions;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal readonly record struct ForageCommandResult(bool IsSuccess, string Code, string Message)
{
    public static ForageCommandResult Success(string code, string message) => new(true, code, message);
    public static ForageCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class ForageCoordinator
{
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 30;
    private const int StuckTimeoutTicks = 300;
    private const int MaximumPathAttempts = 5;

    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, ForageTask> tasks = new();

    public ForageCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public ForageCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return ForageCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before collecting forage.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return ForageCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when the task starts.");
        Vector2 targetTile = new(tileX, tileY);
        if (!TryGetSpawnedObject(owner.currentLocation, targetTile, out SObject targetObject))
            return ForageCommandResult.Failure("TARGET-NOT-SPAWNED-FORAGE", $"Tile {tileX},{tileY} has no spawned ground object.");
        Vector2? approachTile = this.navigation.FindReachableCardinalApproach(body, owner.currentLocation, targetTile, PathSearchLimit);
        if (approachTile is null)
            return ForageCommandResult.Failure("TARGET-UNREACHABLE", "No open cardinal standing tile exists beside the ground object.");
        int facing = FacingToward(approachTile.Value, targetTile);
        TaskTargetKey target = new(owner.currentLocation.NameOrUniqueName, "SpawnedForage", $"{tileX},{tileY}");
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Foraging", target);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new ForageTask(begin.Session, targetTile, approachTile.Value, facing, owner.currentLocation, owner, targetObject, body.Position));
        this.monitor.Log($"HY-FORAGE-STARTED: {identity} reserved {target} for operation {operationId}.", LogLevel.Info);
        return ForageCommandResult.Success("STARTED", $"Ground collection {operationId} started for tile {tileX},{tileY}.");
    }

    public void Update(ulong tick)
    {
        foreach (ForageTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public ForageCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out ForageTask? task))
            return ForageCommandResult.Success("ALREADY-IDLE", $"{identity} has no ground-collection task.");
        return this.Complete(task, code, $"Operation {task.OperationId} was cancelled before settlement.", false);
    }

    public void CancelAll(string code)
    {
        foreach (CompanionIdentity identity in this.tasks.Keys.ToArray())
            this.Cancel(identity, code);
    }

    private void UpdateOne(ForageTask task, ulong tick)
    {
        if (!this.execution.IsCurrent(task.Session))
        {
            this.execution.AbandonRuntime(task.Session);
            this.tasks.Remove(task.Identity);
            return;
        }
        if (!this.bodies.TryGetBody(task.Identity, out NPC body) || body.currentLocation is null || !ReferenceEquals(body.currentLocation, task.Location))
        {
            this.Complete(task, "BODY-INVALID", "The companion body became unavailable or changed location.", false);
            return;
        }
        if (task.Owner.currentLocation is null || !ReferenceEquals(task.Owner.currentLocation, task.Location))
        {
            this.Complete(task, "OWNER-LEFT-LOCATION", "The owner left the task location.", false);
            return;
        }
        if (!TryGetExactObject(task))
        {
            this.Complete(task, "TARGET-CHANGED", "The exact ground object changed or disappeared before settlement.", false);
            return;
        }
        if (body.TilePoint == task.ApproachTile.ToPoint())
        {
            this.bodies.Halt(task.Identity);
            body.faceDirection(task.Facing);
            this.Settle(task);
            return;
        }

        TaskNavigationResult progress = this.navigation.Observe(task.Identity, body, task.Navigation, tick, StuckTimeoutTicks, MaximumPathAttempts, RepathDelayTicks);
        if (progress.BudgetExhausted)
        {
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The ground object stayed unreachable through the bounded retry budget.", false);
            return;
        }
        if (!progress.CanIssuePath)
            return;
        body.controller = new PathFindController(body, task.Location, task.ApproachTile.ToPoint(), task.Facing, null, PathSearchLimit);
        task.Session.MarkTraveling();
        this.navigation.MarkPathIssued(task.Navigation, body.Position, tick, RepathDelayTicks);
    }

    private void Settle(ForageTask task)
    {
        if (!OwnerContextLease.CanProject(task.Owner))
            return;
        if (!task.Session.TryEnterSettlement())
            return;

        int facing = task.Facing;
        this.inventories.RequestTransfer(
            task.Identity,
            () => this.SettleLocked(task, facing),
            result =>
            {
                if (!this.tasks.TryGetValue(task.Identity, out ForageTask? current) || !ReferenceEquals(current, task))
                    return;
                if (result.IsSuccess)
                    this.appearance.Commit(task.Identity, task.OperationId);
                this.Complete(task, result.Code, result.Message, result.IsSuccess);
            }
        );
    }

    private InventoryActionResult SettleLocked(ForageTask task, int facing)
    {
        if (!this.execution.IsCurrent(task.Session) || !TryGetExactObject(task))
            return InventoryActionResult.Failure("TARGET-CHANGED", "The exact ground object changed while the bag lock was pending.");
        if (!this.bodies.TryGetBody(task.Identity, out NPC body))
            return InventoryActionResult.Failure("BODY-INVALID", "The companion body disappeared while the bag lock was pending.");
        if (!OwnerContextLease.CanProject(task.Owner))
            return InventoryActionResult.Failure("OWNER-BUSY", "The owner started another action while collection was waiting for the bag lock; the forage remains unchanged.");

        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Foraging, $"{task.OperationId}:forage");
        if (!cost.IsSuccess)
            return InventoryActionResult.Failure(cost.Result.Code, cost.Result.Message);

        this.appearance.Prepare(task.Identity, task.OperationId, AppearanceActionKinds.Forage, null, facing);
        IReadOnlyList<Item> outputs;
        Exception? settlementError = null;
        using (OwnerContextLease context = OwnerContextLease.Project(task.Owner, body.Position, facing))
        using (FarmerInventoryIsolationLease inventory = FarmerInventoryIsolationLease.Begin(task.Owner))
        {
            try
            {
                task.Location.checkAction(new Location((int)task.TargetTile.X, (int)task.TargetTile.Y), Game1.viewport, task.Owner);
            }
            catch (Exception ex)
            {
                settlementError = ex;
            }
            outputs = inventory.ExtractOutputs();
        }

        cost.Commit();
        foreach (Item output in outputs)
        {
            InventoryActionResult routed = this.inventories.StoreGeneratedOutput(task.Identity, output);
            if (!routed.IsSuccess)
                return routed;
        }

        if (settlementError is not null)
            return InventoryActionResult.Failure("SETTLEMENT-ERROR", $"Ground collection stopped without retry after {settlementError.GetType().Name}.");
        if (task.Location.Objects.TryGetValue(task.TargetTile, out SObject? current))
            return ReferenceEquals(current, task.TargetObject)
                ? InventoryActionResult.Failure("VANILLA-REJECTED", "Vanilla left the exact ground object in the world; no retry will occur.")
                : InventoryActionResult.Failure("TARGET-CHANGED-AFTER-SETTLEMENT", "A replacement object appeared; no second collection was attempted.");
        if (outputs.Count == 0)
            return InventoryActionResult.Failure("OUTPUT-MISSING", "Vanilla removed the ground object without producing a traceable inventory output.");
        return InventoryActionResult.Success("COMMITTED", $"Vanilla collected {task.Target}; {outputs.Count} exact output stack(s) now belong to this Yui.");
    }

    private ForageCommandResult Complete(ForageTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-FORAGE-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
    }

    private static bool TryGetSpawnedObject(GameLocation location, Vector2 tile, out SObject target)
    {
        if (location.Objects.TryGetValue(tile, out SObject? found) && found.IsSpawnedObject)
        {
            target = found;
            return true;
        }
        target = null!;
        return false;
    }

    private static bool TryGetExactObject(ForageTask task) =>
        task.Location.Objects.TryGetValue(task.TargetTile, out SObject? current)
        && ReferenceEquals(current, task.TargetObject)
        && current.IsSpawnedObject;

    private Vector2? FindApproachTile(GameLocation location, Vector2 target, NPC body) =>
        this.navigation.FindReachableCardinalApproach(body, location, target, PathSearchLimit);

    private static int FacingToward(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }
    private static int ManhattanDistance(Point left, Point right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static ForageCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? ForageCommandResult.Success(result.Code, result.Message)
        : ForageCommandResult.Failure(result.Code, result.Message);

    private sealed class ForageTask
    {
        public ForageTask(TaskSession session, Vector2 targetTile, Vector2 approachTile, int facing, GameLocation location, Farmer owner, SObject targetObject, Vector2 initialPosition)
        {
            this.Session = session; this.TargetTile = targetTile; this.ApproachTile = approachTile; this.Facing = facing; this.Location = location; this.Owner = owner;
            this.TargetObject = targetObject; this.Navigation = new TaskNavigationState(initialPosition, 0);
        }
        public TaskSession Session { get; }
        public CompanionIdentity Identity => this.Session.Identity;
        public string OperationId => this.Session.OperationId;
        public TaskTargetKey Target => this.Session.Target;
        public Vector2 TargetTile { get; }
        public Vector2 ApproachTile { get; }
        public int Facing { get; }
        public GameLocation Location { get; }
        public Farmer Owner { get; }
        public SObject TargetObject { get; }
        public TaskNavigationState Navigation { get; }
    }
}
