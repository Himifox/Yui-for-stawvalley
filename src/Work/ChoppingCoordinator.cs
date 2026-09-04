using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal readonly record struct ChopCommandResult(bool IsSuccess, string Code, string Message)
{
    public static ChopCommandResult Success(string code, string message) => new(true, code, message);

    public static ChopCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class ChoppingCoordinator
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
    private readonly Dictionary<CompanionIdentity, ChopTask> tasks = new();

    public ChoppingCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public ChopCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return ChopCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before chopping.");

        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return ChopCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when the task starts.");

        Axe? tool = this.inventories.FindFirst<Axe>(identity);
        if (tool is null)
            return ChopCommandResult.Failure("AXE-UNAVAILABLE", "This Yui's bag has no real axe.");

        Vector2 requestedTile = new(tileX, tileY);
        if (!ChopTarget.TryResolve(owner.currentLocation, requestedTile, out ChopTarget targetInstance))
            return ChopCommandResult.Failure("TARGET-NOT-CHOPPABLE", $"Tile {tileX},{tileY} is not a wild tree, fruit tree, twig, hardwood stump, or hollow log.");

        Vector2 targetTile = targetInstance.Tile;
        Vector2? approachTile = this.navigation.FindReachableCardinalApproach(body, owner.currentLocation, targetTile, PathSearchLimit);
        if (approachTile is null)
            return ChopCommandResult.Failure("TARGET-UNREACHABLE", "No open cardinal standing tile exists beside the tree.");
        int facing = FacingToward(approachTile.Value, targetTile);

        TaskTargetKey target = new(owner.currentLocation.NameOrUniqueName, targetInstance.Kind, targetInstance.ReservationId);
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Chopping", target);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new ChopTask(
            begin.Session,
            targetTile,
            approachTile.Value,
            facing,
            owner.currentLocation,
            owner,
            tool,
            targetInstance,
            body.Position
        ));
        this.monitor.Log($"HY-CHOP-STARTED: {identity} reserved {target} for operation {operationId}.", LogLevel.Info);
        return ChopCommandResult.Success("STARTED", $"Chopping operation {operationId} started for tile {tileX},{tileY}.");
    }

    public void Update(ulong tick)
    {
        foreach (ChopTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public ChopCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out ChopTask? task))
            return ChopCommandResult.Success("ALREADY-IDLE", $"{identity} has no chopping task.");

        return this.Complete(task, code, $"Operation {task.OperationId} was cancelled before completion.", success: false);
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out ChopTask? task) ? task.Tool : null;

    public void CancelAll(string code)
    {
        foreach (CompanionIdentity identity in this.tasks.Keys.ToArray())
            this.Cancel(identity, code);
    }

    private void UpdateOne(ChopTask task, ulong tick)
    {
        if (!this.execution.IsCurrent(task.Session))
        {
            this.execution.AbandonRuntime(task.Session);
            this.tasks.Remove(task.Identity);
            return;
        }

        if (this.TryRouteDelayedDrops(task))
            return;

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
            this.Complete(task, "TOOL-RESPONSIBILITY-LOST", "The exact axe instance is no longer in this Yui's bag.", success: false);
            return;
        }

        if (!task.TargetInstance.IsPresent(task.Location))
        {
            if (task.AwaitingVanillaRemoval)
                this.Complete(task, "COMMITTED", $"Removed {task.Target} after {task.HitCount} single-settlement hit(s).", success: true);
            else
                this.Complete(task, "TARGET-DISAPPEARED", "The reserved tree disappeared outside this task's settlement.", success: false);
            return;
        }

        bool isFalling = task.TargetInstance.IsFalling;
        if (isFalling)
        {
            task.AwaitingVanillaRemoval = true;
            this.bodies.Halt(task.Identity);
            return;
        }
        task.AwaitingVanillaRemoval = false;

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
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The tree stayed unreachable through the bounded retry budget.", success: false);
            return;
        }
        if (!progress.CanIssuePath)
            return;

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

    private void SettleOneHit(ChopTask task, ulong tick)
    {
        if (!OwnerContextLease.CanProject(task.Owner))
            return;
        if (task.HitCount >= MaximumHits)
        {
            this.Complete(task, "HIT-BUDGET-EXHAUSTED", "The tree remained after the bounded hit budget.", success: false);
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
                if (!this.tasks.TryGetValue(task.Identity, out ChopTask? current) || !ReferenceEquals(current, task))
                    return;
                if (!result.IsSuccess || result.Code != "STEP-COMMITTED")
                    this.Complete(task, result.Code, result.Message, result.IsSuccess);
                else
                    task.Session.FinishSettlementStep(stepId);
            });
    }

    private InventoryActionResult SettleOneHitLocked(ChopTask task, ulong tick)
    {
        if (!this.execution.IsCurrent(task.Session)
            || !task.TargetInstance.IsPresent(task.Location)
            || !this.inventories.ContainsExact(task.Identity, task.Tool))
            return InventoryActionResult.Failure("TARGET-CHANGED", "The exact chopping target or reserved axe changed while the bag lock was pending.");
        if (!this.bodies.TryGetBody(task.Identity, out NPC actionBody)
            || !ReferenceEquals(actionBody.currentLocation, task.Location))
            return InventoryActionResult.Failure("BODY-INVALID", "The companion body disappeared while the bag lock was pending.");
        if (!ReferenceEquals(task.Owner.currentLocation, task.Location) || !OwnerContextLease.CanProject(task.Owner))
            return InventoryActionResult.Failure("OWNER-BUSY", "The owner changed location or became busy while the bag lock was pending.");

        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Chopping, $"{task.OperationId}:hit:{task.HitCount}");
        if (!cost.IsSuccess)
            return InventoryActionResult.Failure(cost.Result.Code, cost.Result.Message);

        int facing = task.Facing;
        bool vanillaInvoked = false;
        Exception? settlementError = null;
        this.appearance.Prepare(task.Identity, task.OperationId, AppearanceActionKinds.Chopping, task.Tool, facing);
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
            return InventoryActionResult.Failure("SETTLEMENT-ERROR", $"Axe settlement stopped without retry after an error: {settlementError.Message}");

        this.appearance.Commit(task.Identity, task.OperationId);
        task.HitCount++;
        task.NextHitTick = tick + HitDelayTicks;
        if (!task.TargetInstance.IsPresent(task.Location))
            return InventoryActionResult.Success("COMMITTED", $"Removed {task.Target} after {task.HitCount} single-settlement hit(s); routed {task.RoutedDropStacks} drop stack(s) to Yui responsibility.");

        task.AwaitingVanillaRemoval = task.TargetInstance.IsFalling;
        return InventoryActionResult.Success("STEP-COMMITTED", $"Committed chopping hit {task.HitCount} and routed {worldResult.StackCount} immediate drop stack(s).");
    }

    private bool TryRouteDelayedDrops(ChopTask task)
    {
        if (task.DelayedRoutingPending)
            return true;

        List<Debris> candidates = new();
        foreach (Debris? debris in task.Location.debris)
        {
            if (debris is null || task.ObservedDebris.Contains(debris))
                continue;
            if (IsDelayedDropForTask(task, debris))
                candidates.Add(debris);
            else
                task.ObservedDebris.Add(debris);
        }
        if (candidates.Count == 0)
            return false;

        string stepId = $"{task.OperationId}:delayed-drops:{task.DelayedDropSequence++}";
        if (!task.Session.TryEnterSettlement(stepId))
            return true;
        task.ObservedDebris.UnionWith(candidates);
        task.DelayedRoutingPending = true;
        int routedStacks = 0;
        this.inventories.RequestTransfer(
            task.Identity,
            () =>
            {
                WorldDebrisRouteResult routed = WorldDebrisCapture.RouteSpecificLocked(task.Identity, this.inventories, task.Location, candidates);
                routedStacks = routed.StackCount;
                return routed.Result;
            },
            result =>
            {
                if (!this.tasks.TryGetValue(task.Identity, out ChopTask? current) || !ReferenceEquals(current, task))
                    return;
                task.DelayedRoutingPending = false;
                if (!result.IsSuccess)
                {
                    this.Complete(task, result.Code, result.Message, false);
                    return;
                }
                task.RoutedDropStacks += routedStacks;
                task.Session.FinishSettlementStep(stepId);
            });
        return true;
    }

    private static bool IsDelayedDropForTask(ChopTask task, Debris debris)
    {
        if (!WorldDebrisCapture.IsItemDrop(debris))
            return false;
        long droppedBy = debris.DroppedByPlayerID.Value;
        if (droppedBy != 0 && droppedBy != task.Owner.UniqueMultiplayerID)
            return false;

        Vector2 targetPixel = (task.TargetTile + new Vector2(0.5f, 0.5f)) * Game1.tileSize;
        const float radiusInTiles = 7f;
        float radiusSquared = radiusInTiles * Game1.tileSize * radiusInTiles * Game1.tileSize;
        return debris.Chunks.Any(chunk => Vector2.DistanceSquared(chunk.position.Value + new Vector2(32f, 32f), targetPixel) <= radiusSquared);
    }

    private ChopCommandResult Complete(ChopTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-CHOP-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
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

    private static ChopCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? ChopCommandResult.Success(result.Code, result.Message)
        : ChopCommandResult.Failure(result.Code, result.Message);

    private sealed class ChopTarget
    {
        private ChopTarget(Vector2 tile, object instance, string kind, string reservationId)
        {
            this.Tile = tile;
            this.Instance = instance;
            this.Kind = kind;
            this.ReservationId = reservationId;
        }

        public Vector2 Tile { get; }
        public object Instance { get; }
        public string Kind { get; }
        public string ReservationId { get; }
        public bool IsFalling => (this.Instance is Tree tree && tree.falling.Value)
            || (this.Instance is FruitTree fruitTree && fruitTree.falling.Value);

        public bool IsPresent(GameLocation location) => this.Instance switch
        {
            ResourceClump clump => location.resourceClumps.Contains(clump),
            TerrainFeature feature => location.terrainFeatures.TryGetValue(this.Tile, out TerrainFeature? current) && ReferenceEquals(current, feature),
            SObject twig => location.Objects.TryGetValue(this.Tile, out SObject? current) && ReferenceEquals(current, twig) && current.IsTwig(),
            _ => false,
        };

        public static bool TryResolve(GameLocation location, Vector2 requestedTile, out ChopTarget target)
        {
            if (location.terrainFeatures.TryGetValue(requestedTile, out TerrainFeature? feature)
                && feature is Tree or FruitTree)
            {
                string kind = feature is FruitTree ? WorldTargetCategories.FruitTree : WorldTargetCategories.WildTree;
                target = new ChopTarget(requestedTile, feature, kind, $"{(int)requestedTile.X},{(int)requestedTile.Y}");
                return true;
            }
            if (location.Objects.TryGetValue(requestedTile, out SObject? twig) && twig.IsTwig())
            {
                target = new ChopTarget(requestedTile, twig, WorldTargetCategories.WoodDebris, $"{(int)requestedTile.X},{(int)requestedTile.Y}");
                return true;
            }
            ResourceClump? clump = location.resourceClumps.FirstOrDefault(candidate =>
                candidate.parentSheetIndex.Value is ResourceClump.stumpIndex or ResourceClump.hollowLogIndex
                && candidate.occupiesTile((int)requestedTile.X, (int)requestedTile.Y));
            if (clump is not null && FindClumpInteractionTile(location, clump, requestedTile) is Vector2 interactionTile)
            {
                target = new ChopTarget(
                    interactionTile,
                    clump,
                    WorldTargetCategories.HardwoodClump,
                    $"{clump.parentSheetIndex.Value}:{(int)clump.Tile.X},{(int)clump.Tile.Y}");
                return true;
            }
            target = null!;
            return false;
        }

        private static Vector2? FindClumpInteractionTile(GameLocation location, ResourceClump clump, Vector2 requestedTile)
        {
            Vector2[] directions = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
            var occupied = new List<Vector2>();
            for (int x = (int)requestedTile.X - 4; x <= (int)requestedTile.X + 4; x++)
                for (int y = (int)requestedTile.Y - 4; y <= (int)requestedTile.Y + 4; y++)
                    if (clump.occupiesTile(x, y))
                        occupied.Add(new Vector2(x, y));
            return occupied
                .Where(tile => directions.Any(direction => location.isTileOnMap(tile + direction) && !location.IsTileBlockedBy(tile + direction)))
                .OrderBy(tile => Math.Abs(tile.X - requestedTile.X) + Math.Abs(tile.Y - requestedTile.Y))
                .Cast<Vector2?>()
                .FirstOrDefault();
        }
    }

    private sealed class ChopTask
    {
        public ChopTask(
            TaskSession session,
            Vector2 targetTile,
            Vector2 approachTile,
            int facing,
            GameLocation location,
            Farmer owner,
            Axe tool,
            ChopTarget targetInstance,
            Vector2 initialPosition)
        {
            this.Session = session;
            this.TargetTile = targetTile;
            this.ApproachTile = approachTile;
            this.Facing = facing;
            this.Location = location;
            this.Owner = owner;
            this.Tool = tool;
            this.TargetInstance = targetInstance;
            this.Navigation = new TaskNavigationState(initialPosition, 0);
            this.ObservedDebris = WorldDebrisCapture.Snapshot(location);
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

        public Axe Tool { get; }

        public ChopTarget TargetInstance { get; }

        public TaskNavigationState Navigation { get; }

        public int HitCount { get; set; }

        public ulong NextHitTick { get; set; }

        public bool AwaitingVanillaRemoval { get; set; }

        public HashSet<Debris> ObservedDebris { get; }

        public bool DelayedRoutingPending { get; set; }

        public int DelayedDropSequence { get; set; }

        public int RoutedDropStacks { get; set; }
    }
}
