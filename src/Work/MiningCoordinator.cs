using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal readonly record struct MineCommandResult(bool IsSuccess, string Code, string Message)
{
    public static MineCommandResult Success(string code, string message) => new(true, code, message);

    public static MineCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class MiningCoordinator
{
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 30;
    private const int HitDelayTicks = 30;
    private const int StuckTimeoutTicks = 300;
    private const int MaximumPathAttempts = 5;
    private const int MaximumHits = 64;

    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, MineTask> tasks = new();

    public MiningCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public MineCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return MineCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before mining.");

        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return MineCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when the task starts.");

        Pickaxe? tool = this.inventories.FindFirst<Pickaxe>(identity);
        if (tool is null)
            return MineCommandResult.Failure("PICKAXE-UNAVAILABLE", "This Yui's bag has no real pickaxe.");

        Vector2 requestedTile = new(tileX, tileY);
        if (!MineTarget.TryResolve(owner.currentLocation, requestedTile, out MineTarget targetInstance))
            return MineCommandResult.Failure("TARGET-NOT-MINEABLE", $"Tile {tileX},{tileY} is not a breakable stone, ore node, meteorite, or boulder.");

        Vector2 targetTile = targetInstance.Tile;
        Vector2? approachTile = this.navigation.FindReachableCardinalApproach(body, owner.currentLocation, targetTile, PathSearchLimit);
        if (approachTile is null)
            return MineCommandResult.Failure("TARGET-UNREACHABLE", "No open cardinal standing tile exists beside the mining target.");
        int facing = FacingToward(approachTile.Value, targetTile);
        TaskTargetKey target = new(owner.currentLocation.NameOrUniqueName, targetInstance.Kind, targetInstance.ReservationId);
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Mining", target);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new MineTask(
            begin.Session,
            targetTile,
            approachTile.Value,
            facing,
            targetInstance,
            owner.currentLocation,
            owner,
            tool,
            body.Position
        ));
        this.monitor.Log($"HY-MINE-STARTED: {identity} reserved {target} for operation {operationId}.", LogLevel.Info);
        return MineCommandResult.Success("STARTED", $"Mining operation {operationId} started for tile {tileX},{tileY}.");
    }

    public void Update(ulong tick)
    {
        foreach (MineTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public MineCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out MineTask? task))
            return MineCommandResult.Success("ALREADY-IDLE", $"{identity} has no mining task.");
        return this.Complete(task, code, $"Operation {task.OperationId} was cancelled before completion.", success: false);
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out MineTask? task) ? task.Tool : null;

    public void CancelAll(string code)
    {
        foreach (CompanionIdentity identity in this.tasks.Keys.ToArray())
            this.Cancel(identity, code);
    }

    private void UpdateOne(MineTask task, ulong tick)
    {
        if (!OwnerLifecycleGate.CanAdvance(task.Owner))
            return;
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
            this.Complete(task, "TOOL-RESPONSIBILITY-LOST", "The exact pickaxe instance is no longer in this Yui's bag.", success: false);
            return;
        }

        if (!task.TargetInstance.IsPresent(task.Location))
        {
            this.Complete(task, "TARGET-DISAPPEARED", "The reserved mining target disappeared outside this task's settlement.", success: false);
            return;
        }

        if (body.TilePoint == task.ApproachTile.ToPoint())
        {
            this.bodies.Halt(task.Identity);
            body.faceDirection(task.Facing);
            if (tick >= task.NextHitTick)
                this.SettleOneHit(task, tick);
            return;
        }

        TaskNavigationResult progress = this.navigation.Observe(task.Identity, body, task.Navigation, tick, StuckTimeoutTicks, MaximumPathAttempts, RepathDelayTicks);
        if (progress.BudgetExhausted)
        {
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The mining target stayed unreachable through the bounded retry budget.", success: false);
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

    private void SettleOneHit(MineTask task, ulong tick)
    {
        if (!OwnerContextLease.CanProject(task.Owner))
            return;
        if (task.HitCount >= MaximumHits)
        {
            this.Complete(task, "HIT-BUDGET-EXHAUSTED", "The mining target remained after the bounded hit budget.", success: false);
            return;
        }

        string stepId = $"{task.OperationId}:hit:{task.HitCount}";
        if (!task.Session.TryEnterSettlement(stepId))
            return;

        this.inventories.RequestTransfer(
            task.Identity,
            () => this.SettleOneHitLocked(task, tick),
            result =>
            {
                if (!this.tasks.TryGetValue(task.Identity, out MineTask? current) || !ReferenceEquals(current, task))
                    return;
                if (!result.IsSuccess || result.Code != "STEP-COMMITTED")
                    this.Complete(task, result.Code, result.Message, result.IsSuccess);
                else
                    task.Session.FinishSettlementStep(stepId);
            });
    }

    private InventoryActionResult SettleOneHitLocked(MineTask task, ulong tick)
    {
        if (!this.execution.IsCurrent(task.Session)
            || !task.TargetInstance.IsPresent(task.Location)
            || !this.inventories.ContainsExact(task.Identity, task.Tool))
            return InventoryActionResult.Failure("TARGET-CHANGED", "The exact mining target or reserved pickaxe changed while the bag lock was pending.");
        if (!this.bodies.TryGetBody(task.Identity, out NPC actionBody)
            || !ReferenceEquals(actionBody.currentLocation, task.Location))
            return InventoryActionResult.Failure("BODY-INVALID", "The companion body disappeared while the bag lock was pending.");
        if (!ReferenceEquals(task.Owner.currentLocation, task.Location) || !OwnerContextLease.CanProject(task.Owner))
            return InventoryActionResult.Failure("OWNER-BUSY", "The owner changed location or became busy while the bag lock was pending.");

        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Mining, $"{task.OperationId}:hit:{task.HitCount}");
        if (!cost.IsSuccess)
            return InventoryActionResult.Failure(cost.Result.Code, cost.Result.Message);

        int facing = task.Facing;
        bool vanillaInvoked = false;
        Exception? settlementError = null;
        this.appearance.Prepare(task.Identity, task.OperationId, AppearanceActionKinds.Mining, task.Tool, facing);
        WorldDebrisCapture worldDrops = WorldDebrisCapture.Begin(task.Location, Game1.currentLocation);
        try
        {
            using OwnerContextLease context = OwnerContextLease.Project(task.Owner, actionBody.Position, facing, task.Location);
            vanillaInvoked = true;
            task.Tool.DoFunction(
                task.Location,
                (int)task.TargetTile.X * Game1.tileSize,
                (int)task.TargetTile.Y * Game1.tileSize,
                0,
                task.Owner);
        }
        catch (Exception ex)
        {
            settlementError = ex;
        }

        if (vanillaInvoked)
            cost.Commit();
        WorldDebrisRouteResult worldResult = worldDrops.RouteNewLocked(task.Identity, this.inventories);
        task.RoutedDropStacks += worldResult.StackCount;
        if (!worldResult.Result.IsSuccess)
            return worldResult.Result;
        if (settlementError is not null)
            return InventoryActionResult.Failure("SETTLEMENT-ERROR", $"Pickaxe settlement stopped without retry after an error: {settlementError.Message}");

        this.appearance.Commit(task.Identity, task.OperationId);
        task.HitCount++;
        task.NextHitTick = tick + HitDelayTicks;
        return !task.TargetInstance.IsPresent(task.Location)
            ? InventoryActionResult.Success("COMMITTED", $"Removed {task.Target} after {task.HitCount} single-settlement hit(s); routed {task.RoutedDropStacks} drop stack(s) to Yui responsibility.")
            : InventoryActionResult.Success("STEP-COMMITTED", $"Committed mining hit {task.HitCount} and routed {worldResult.StackCount} immediate drop stack(s).");
    }

    private MineCommandResult Complete(MineTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-MINE-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
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

    private static MineCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? MineCommandResult.Success(result.Code, result.Message)
        : MineCommandResult.Failure(result.Code, result.Message);

    private sealed class MineTarget
    {
        private MineTarget(Vector2 tile, SObject? stone, ResourceClump? clump)
        {
            this.Tile = tile;
            this.Stone = stone;
            this.Clump = clump;
        }

        public Vector2 Tile { get; }

        public SObject? Stone { get; }

        public ResourceClump? Clump { get; }

        public object Reference => (object?)this.Stone ?? this.Clump!;

        public string Kind => this.Stone is not null ? "BreakableStone" : "ResourceClump";

        public string ReservationId => this.Stone is not null
            ? $"{(int)this.Tile.X},{(int)this.Tile.Y}"
            : $"runtime-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this.Clump!)}";

        public bool IsPresent(GameLocation location)
        {
            if (this.Stone is not null)
                return location.Objects.TryGetValue(this.Tile, out SObject? current) && ReferenceEquals(current, this.Stone);
            return this.Clump is not null
                && location.resourceClumps.Contains(this.Clump)
                && this.Clump.occupiesTile((int)this.Tile.X, (int)this.Tile.Y);
        }

        public static bool TryResolve(GameLocation location, Vector2 tile, out MineTarget target)
        {
            if (location.Objects.TryGetValue(tile, out SObject? stone) && stone.IsBreakableStone())
            {
                target = new MineTarget(tile, stone, null);
                return true;
            }

            ResourceClump? clump = location.resourceClumps.FirstOrDefault(candidate =>
                candidate.parentSheetIndex.Value is ResourceClump.meteoriteIndex or ResourceClump.boulderIndex
                && candidate.occupiesTile((int)tile.X, (int)tile.Y));
            if (clump is not null)
            {
                Vector2? interactionTile = FindClumpInteractionTile(location, clump, tile);
                if (interactionTile is null)
                {
                    target = null!;
                    return false;
                }
                target = new MineTarget(interactionTile.Value, null, clump);
                return true;
            }

            target = null!;
            return false;
        }

        private static Vector2? FindClumpInteractionTile(GameLocation location, ResourceClump clump, Vector2 requestedTile)
        {
            var occupied = new List<Vector2>();
            for (int x = (int)requestedTile.X - 4; x <= (int)requestedTile.X + 4; x++)
            {
                for (int y = (int)requestedTile.Y - 4; y <= (int)requestedTile.Y + 4; y++)
                {
                    if (clump.occupiesTile(x, y))
                        occupied.Add(new Vector2(x, y));
                }
            }

            Vector2[] directions = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
            return occupied
                .Where(tile => directions.Any(direction => location.isTileOnMap(tile + direction) && !location.IsTileBlockedBy(tile + direction)))
                .OrderBy(tile => ManhattanDistance(tile.ToPoint(), requestedTile.ToPoint()))
                .Cast<Vector2?>()
                .FirstOrDefault();
        }
    }

    private sealed class MineTask
    {
        public MineTask(
            TaskSession session,
            Vector2 targetTile,
            Vector2 approachTile,
            int facing,
            MineTarget targetInstance,
            GameLocation location,
            Farmer owner,
            Pickaxe tool,
            Vector2 initialPosition)
        {
            this.Session = session;
            this.TargetTile = targetTile;
            this.ApproachTile = approachTile;
            this.Facing = facing;
            this.TargetInstance = targetInstance;
            this.Location = location;
            this.Owner = owner;
            this.Tool = tool;
            this.Navigation = new TaskNavigationState(initialPosition, 0);
        }

        public TaskSession Session { get; }
        public CompanionIdentity Identity => this.Session.Identity;
        public string OperationId => this.Session.OperationId;
        public TaskTargetKey Target => this.Session.Target;
        public Vector2 TargetTile { get; }
        public Vector2 ApproachTile { get; }
        public int Facing { get; }
        public MineTarget TargetInstance { get; }
        public GameLocation Location { get; }
        public Farmer Owner { get; }
        public Pickaxe Tool { get; }
        public TaskNavigationState Navigation { get; }
        public int RoutedDropStacks { get; set; }
        public int HitCount { get; set; }
        public ulong NextHitTick { get; set; }
    }
}
