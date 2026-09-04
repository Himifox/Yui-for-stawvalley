using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace YuiToIssho;

internal readonly record struct WaterCommandResult(bool IsSuccess, string Code, string Message)
{
    public static WaterCommandResult Success(string code, string message) => new(true, code, message);

    public static WaterCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class WateringCoordinator
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
    private readonly Dictionary<CompanionIdentity, WaterTask> tasks = new();

    public WateringCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public WaterCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return WaterCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before watering.");

        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return WaterCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when the task starts.");

        WateringCan? tool = this.inventories.FindFirst<WateringCan>(identity, can => can.IsBottomless || can.WaterLeft > 0);
        if (tool is null)
            return WaterCommandResult.Failure("WATERING-CAN-UNAVAILABLE", "This Yui's bag has no real watering can with water.");

        Vector2 targetTile = new(tileX, tileY);
        if (!TryGetDryDirt(owner.currentLocation, targetTile, out HoeDirt targetDirt))
            return WaterCommandResult.Failure("TARGET-NOT-DRY-DIRT", $"Tile {tileX},{tileY} is not dry hoe dirt.");
        Vector2? approachTile = this.navigation.FindReachableCardinalApproach(body, owner.currentLocation, targetTile, PathSearchLimit);
        if (approachTile is null)
            return WaterCommandResult.Failure("TARGET-UNREACHABLE", "No open cardinal standing tile exists beside the dry dirt.");
        int facing = FacingToward(approachTile.Value, targetTile);

        TaskTargetKey target = new(owner.currentLocation.NameOrUniqueName, "HoeDirt", $"{tileX},{tileY}");
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Watering", target);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new WaterTask(
            begin.Session,
            targetTile,
            approachTile.Value,
            facing,
            owner.currentLocation,
            owner,
            tool,
            targetDirt,
            body.Position
        ));
        this.monitor.Log($"HY-WATER-STARTED: {identity} reserved {target} for operation {operationId}.", LogLevel.Info);
        return WaterCommandResult.Success("STARTED", $"Watering operation {operationId} started for tile {tileX},{tileY}.");
    }

    public void Update(ulong tick)
    {
        foreach (WaterTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public WaterCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out WaterTask? task))
            return WaterCommandResult.Success("ALREADY-IDLE", $"{identity} has no watering task.");

        return this.Complete(task, code, $"Operation {task.OperationId} was cancelled before settlement.", success: false);
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out WaterTask? task) ? task.Tool : null;

    public bool TryGetReservedGeometry(CompanionIdentity identity, out WorkStepGeometry geometry)
    {
        if (this.tasks.TryGetValue(identity, out WaterTask? task))
        {
            geometry = new WorkStepGeometry(task.TargetTile, task.ApproachTile, task.Facing);
            return true;
        }
        geometry = default;
        return false;
    }

    public void CancelAll(string code)
    {
        foreach (CompanionIdentity identity in this.tasks.Keys.ToArray())
            this.Cancel(identity, code);
    }

    private void UpdateOne(WaterTask task, ulong tick)
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
            this.Complete(task, "BODY-INVALID", "The companion body became unavailable or changed location.", success: false);
            return;
        }

        if (task.Owner.currentLocation is null || !ReferenceEquals(task.Owner.currentLocation, task.Location))
        {
            this.Complete(task, "OWNER-LEFT-LOCATION", "The owner left the task location.", success: false);
            return;
        }

        if (!this.inventories.ContainsExact(task.Identity, task.Tool))
        {
            this.Complete(task, "TOOL-RESPONSIBILITY-LOST", "The exact watering can instance is no longer in this Yui's bag.", success: false);
            return;
        }

        if (!task.Tool.IsBottomless && task.Tool.WaterLeft <= 0)
        {
            this.Complete(task, "WATERING-CAN-EMPTY", "The reserved watering can has no water.", success: false);
            return;
        }

        if (!TryGetDryDirt(task.Location, task.TargetTile, out HoeDirt dirt)
            || !ReferenceEquals(dirt, task.TargetDirt))
        {
            this.Complete(task, "TARGET-CHANGED", "The reserved target is no longer the same dry hoe dirt.", success: false);
            return;
        }

        if (body.TilePoint == task.ApproachTile.ToPoint())
        {
            this.bodies.Halt(task.Identity);
            body.faceDirection(task.Facing);
            this.Settle(task, dirt);
            return;
        }

        TaskNavigationResult progress = this.navigation.Observe(
            task.Identity,
            body,
            task.Navigation,
            tick,
            StuckTimeoutTicks,
            MaximumPathAttempts,
            RepathDelayTicks
        );
        if (progress.BudgetExhausted)
        {
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The watering target stayed unreachable through the bounded retry budget.", success: false);
            return;
        }
        if (!progress.CanIssuePath)
            return;

        body.controller = CompanionPathing.CreateController(
            body,
            task.Location,
            task.ApproachTile.ToPoint(),
            task.Facing,
            PathSearchLimit
        );
        task.Session.MarkTraveling();
        this.navigation.MarkPathIssued(task.Navigation, body.Position, tick, RepathDelayTicks);
    }

    private void Settle(WaterTask task, HoeDirt dirt)
    {
        if (!task.Session.TryEnterSettlement())
            return;

        int waterBefore = task.Tool.WaterLeft;
        bool consumesWater = !task.Tool.IsBottomless;
        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Watering, $"{task.OperationId}:water");
        if (!cost.IsSuccess)
        {
            this.Complete(task, cost.Result.Code, cost.Result.Message, success: false);
            return;
        }

        int facing = task.Facing;
        this.appearance.Prepare(task.Identity, task.OperationId, AppearanceActionKinds.Watering, task.Tool, facing);
        try
        {
            dirt.performToolAction(task.Tool, 0, task.TargetTile);
            if (dirt.state.Value != HoeDirt.watered)
            {
                this.Complete(task, "VANILLA-REJECTED", "Vanilla hoe-dirt rules did not water the target.", success: false);
                return;
            }

            cost.Commit();

            if (consumesWater && task.Tool.WaterLeft == waterBefore)
                task.Tool.WaterLeft = waterBefore - 1;

            int expectedWater = consumesWater ? waterBefore - 1 : waterBefore;
            if (task.Tool.WaterLeft != expectedWater)
            {
                this.Complete(task, "WATER-DELTA-UNEXPECTED", "The target changed, but the real tool water delta did not match its vanilla bottomless/resource rule.", success: false);
                return;
            }

            this.appearance.Commit(task.Identity, task.OperationId);
            this.Complete(task, "COMMITTED", $"Watered {task.Target} exactly once with operation {task.OperationId}.", success: true);
        }
        catch (Exception ex)
        {
            // Settlement is never retried after entering this block because the vanilla side effect may already have occurred.
            cost.Commit();
            this.Complete(task, "SETTLEMENT-ERROR", $"Settlement stopped without retry after an error: {ex.Message}", success: false);
        }
    }

    private WaterCommandResult Complete(WaterTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-WATER-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
    }

    private static bool TryGetDryDirt(GameLocation location, Vector2 tile, out HoeDirt dirt)
    {
        if (location.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature)
            && feature is HoeDirt found
            && found.state.Value == HoeDirt.dry)
        {
            dirt = found;
            return true;
        }

        dirt = null!;
        return false;
    }

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

    private static WaterCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? WaterCommandResult.Success(result.Code, result.Message)
        : WaterCommandResult.Failure(result.Code, result.Message);

    private sealed class WaterTask
    {
        public WaterTask(
            TaskSession session,
            Vector2 targetTile,
            Vector2 approachTile,
            int facing,
            GameLocation location,
            Farmer owner,
            WateringCan tool,
            HoeDirt targetDirt,
            Vector2 initialPosition)
        {
            this.Session = session;
            this.TargetTile = targetTile;
            this.ApproachTile = approachTile;
            this.Facing = facing;
            this.Location = location;
            this.Owner = owner;
            this.Tool = tool;
            this.TargetDirt = targetDirt;
            this.Navigation = new TaskNavigationState(initialPosition, 0);
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

        public WateringCan Tool { get; }

        public HoeDirt TargetDirt { get; }

        public TaskNavigationState Navigation { get; }
    }
}
