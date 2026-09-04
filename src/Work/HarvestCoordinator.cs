using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace YuiToIssho;

internal readonly record struct HarvestCommandResult(bool IsSuccess, string Code, string Message)
{
    public static HarvestCommandResult Success(string code, string message) => new(true, code, message);

    public static HarvestCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class HarvestCoordinator
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
    private readonly Dictionary<CompanionIdentity, HarvestTask> tasks = new();

    public HarvestCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public HarvestCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return HarvestCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before harvesting.");

        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return HarvestCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when the task starts.");

        Vector2 targetTile = new(tileX, tileY);
        if (!TryGetCrop(owner.currentLocation, targetTile, out HoeDirt dirt, out Crop crop))
            return HarvestCommandResult.Failure("TARGET-NOT-HARVESTABLE", $"Tile {tileX},{tileY} has no mature living crop.");
        string? protectionReason = CropProtectionPolicy.GetReason(crop);
        if (protectionReason is not null)
            return HarvestCommandResult.Failure("TARGET-PROTECTED", $"Tile {tileX},{tileY} is protected from Yui harvest ({protectionReason}).");
        if (!IsHarvestReady(crop))
            return HarvestCommandResult.Failure("TARGET-NOT-HARVESTABLE", $"Tile {tileX},{tileY} has no mature living crop.");

        HarvestMethod method = crop.GetHarvestMethod();
        MeleeWeapon? scythe = null;
        if (method == HarvestMethod.Scythe)
        {
            scythe = this.inventories.FindFirst<MeleeWeapon>(identity, tool => tool.isScythe());
            if (scythe is null)
                return HarvestCommandResult.Failure("SCYTHE-UNAVAILABLE", "This crop requires a real scythe in this Yui's bag.");
        }

        Vector2? approachTile = this.navigation.FindReachableCardinalApproach(body, owner.currentLocation, targetTile, PathSearchLimit);
        if (approachTile is null)
            return HarvestCommandResult.Failure("TARGET-UNREACHABLE", "No open cardinal standing tile exists beside the crop.");
        int facing = FacingToward(approachTile.Value, targetTile);

        TaskTargetKey target = new(owner.currentLocation.NameOrUniqueName, "Crop", $"{tileX},{tileY}");
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Harvesting", target);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new HarvestTask(
            begin.Session,
            targetTile,
            approachTile.Value,
            facing,
            owner.currentLocation,
            owner,
            dirt,
            crop,
            method,
            scythe,
            body.Position
        ));
        this.monitor.Log($"HY-HARVEST-STARTED: {identity} reserved {target} using {method} for operation {operationId}.", LogLevel.Info);
        return HarvestCommandResult.Success("STARTED", $"Harvest operation {operationId} started for tile {tileX},{tileY}.");
    }

    public void Update(ulong tick)
    {
        foreach (HarvestTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public HarvestCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out HarvestTask? task))
            return HarvestCommandResult.Success("ALREADY-IDLE", $"{identity} has no harvest task.");
        return this.Complete(task, code, $"Operation {task.OperationId} was cancelled before settlement.", success: false);
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out HarvestTask? task) ? task.Scythe : null;

    public bool TryGetReservedGeometry(CompanionIdentity identity, out WorkStepGeometry geometry)
    {
        if (this.tasks.TryGetValue(identity, out HarvestTask? task))
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

    private void UpdateOne(HarvestTask task, ulong tick)
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

        if (!TryGetExactCrop(task, requireReady: true))
        {
            this.Complete(task, "TARGET-CHANGED", "The exact reserved crop or dirt changed before settlement.", success: false);
            return;
        }

        if (task.Method == HarvestMethod.Scythe
            && (task.Scythe is null || !this.inventories.ContainsExact(task.Identity, task.Scythe)))
        {
            this.Complete(task, "TOOL-RESPONSIBILITY-LOST", "The exact scythe instance is no longer in this Yui's bag.", success: false);
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
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The crop stayed unreachable through the bounded retry budget.", success: false);
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

    private void Settle(HarvestTask task)
    {
        if (!OwnerContextLease.CanProject(Game1.player))
            return;
        if (!task.Session.TryEnterSettlement())
            return;

        int facing = task.Facing;
        this.inventories.RequestTransfer(
            task.Identity,
            () => this.SettleLocked(task, facing),
            result =>
            {
                if (!this.tasks.TryGetValue(task.Identity, out HarvestTask? current) || !ReferenceEquals(current, task))
                    return;
                if (result.IsSuccess)
                    this.appearance.Commit(task.Identity, task.OperationId);
                this.Complete(task, result.Code, result.Message, result.IsSuccess);
            }
        );
    }

    private InventoryActionResult SettleLocked(HarvestTask task, int facing)
    {
        if (!this.execution.IsCurrent(task.Session) || !TryGetExactCrop(task, requireReady: true))
            return InventoryActionResult.Failure("TARGET-CHANGED", "The exact mature crop changed while the bag lock was pending.");
        if (!this.bodies.TryGetBody(task.Identity, out NPC body))
            return InventoryActionResult.Failure("BODY-INVALID", "The companion body disappeared while the bag lock was pending.");
        if (!OwnerContextLease.CanProject(Game1.player))
            return InventoryActionResult.Failure("OWNER-BUSY", "The local engine recipient started another action while harvest was waiting for the bag lock; the crop remains unchanged.");
        if (task.Method == HarvestMethod.Scythe && (task.Scythe is null || !this.inventories.ContainsExact(task.Identity, task.Scythe)))
            return InventoryActionResult.Failure("TOOL-RESPONSIBILITY-LOST", "The exact scythe changed while the bag lock was pending.");

        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Harvesting, $"{task.OperationId}:harvest");
        if (!cost.IsSuccess)
            return InventoryActionResult.Failure(cost.Result.Code, cost.Result.Message);

        string visualKind = task.Method == HarvestMethod.Scythe ? AppearanceActionKinds.HarvestScythe : AppearanceActionKinds.HarvestGrab;
        this.appearance.Prepare(task.Identity, task.OperationId, visualKind, task.Scythe, facing);
        Farmer engineRecipient = Game1.player;
        IReadOnlyList<Item> outputs;
        bool removeCrop = false;
        Exception? settlementError = null;
        using (OwnerContextLease context = OwnerContextLease.Project(engineRecipient, body.Position, facing, task.Location))
        using (FarmerInventoryIsolationLease inventory = FarmerInventoryIsolationLease.Begin(engineRecipient))
        {
            try
            {
                removeCrop = task.TargetCrop.harvest(
                    (int)task.TargetTile.X,
                    (int)task.TargetTile.Y,
                    task.TargetDirt,
                    null,
                    task.Method == HarvestMethod.Scythe
                );
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
            return InventoryActionResult.Failure("SETTLEMENT-ERROR", $"Harvest stopped without retry after {settlementError.GetType().Name}.");

        if (removeCrop)
        {
            if (ReferenceEquals(task.TargetDirt.crop, task.TargetCrop))
                task.TargetDirt.crop = null;
            if (task.Method == HarvestMethod.Grab && outputs.Count == 0)
                return InventoryActionResult.Failure("OUTPUT-MISSING", "Vanilla removed the hand-harvested crop without a traceable inventory output.");
            return InventoryActionResult.Success("COMMITTED", $"Harvested {task.Target} once using {task.Method}; routed {outputs.Count} exact stack(s) to Yui responsibility.");
        }
        if (ReferenceEquals(task.TargetDirt.crop, task.TargetCrop) && !IsHarvestReady(task.TargetCrop))
        {
            if (task.Method == HarvestMethod.Grab && outputs.Count == 0)
                return InventoryActionResult.Failure("OUTPUT-MISSING", "Vanilla reset the crop without a traceable hand-harvest output.");
            return InventoryActionResult.Success("COMMITTED-REGROWTH", $"Harvested {task.Target} once; vanilla retained the regrowing crop and routed {outputs.Count} stack(s). ");
        }
        if (ReferenceEquals(task.TargetDirt.crop, task.TargetCrop) && IsHarvestReady(task.TargetCrop))
            return InventoryActionResult.Failure("VANILLA-REJECTED", "Vanilla rejected the harvest and the mature crop remains unchanged.");
        return InventoryActionResult.Failure("TARGET-CHANGED-AFTER-SETTLEMENT", "The crop reference changed during settlement; no retry will occur.");
    }

    private HarvestCommandResult Complete(HarvestTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-HARVEST-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
    }

    private static bool TryGetCrop(GameLocation location, Vector2 tile, out HoeDirt dirt, out Crop crop)
    {
        if (location.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature)
            && feature is HoeDirt foundDirt
            && foundDirt.crop is Crop foundCrop)
        {
            dirt = foundDirt;
            crop = foundCrop;
            return true;
        }

        dirt = null!;
        crop = null!;
        return false;
    }

    private static bool TryGetExactCrop(HarvestTask task, bool requireReady)
    {
        return task.Location.terrainFeatures.TryGetValue(task.TargetTile, out TerrainFeature? feature)
            && ReferenceEquals(feature, task.TargetDirt)
            && ReferenceEquals(task.TargetDirt.crop, task.TargetCrop)
            && CropProtectionPolicy.GetReason(task.TargetCrop) is null
            && (!requireReady || IsHarvestReady(task.TargetCrop));
    }

    private static bool IsHarvestReady(Crop crop)
    {
        return !crop.dead.Value
            && crop.currentPhase.Value >= crop.phaseDays.Count - 1
            && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);
    }

    private static Vector2? FindApproachTile(GameLocation location, Vector2 target, NPC body)
    {
        Vector2[] candidates =
        {
            target + new Vector2(1, 0),
            target + new Vector2(-1, 0),
            target + new Vector2(0, 1),
            target + new Vector2(0, -1),
        };
        return candidates
            .Where(candidate => location.isTileLocationOpen(candidate)
                && location.characters.All(character => ReferenceEquals(character, body) || character.Tile != candidate))
            .OrderBy(candidate => ManhattanDistance(candidate.ToPoint(), body.TilePoint))
            .Cast<Vector2?>()
            .FirstOrDefault();
    }

    private static int FacingToward(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }

    private static int ManhattanDistance(Point left, Point right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static HarvestCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? HarvestCommandResult.Success(result.Code, result.Message)
        : HarvestCommandResult.Failure(result.Code, result.Message);

    private sealed class HarvestTask
    {
        public HarvestTask(
            TaskSession session,
            Vector2 targetTile,
            Vector2 approachTile,
            int facing,
            GameLocation location,
            Farmer owner,
            HoeDirt targetDirt,
            Crop targetCrop,
            HarvestMethod method,
            MeleeWeapon? scythe,
            Vector2 initialPosition)
        {
            this.Session = session;
            this.TargetTile = targetTile;
            this.ApproachTile = approachTile;
            this.Facing = facing;
            this.Location = location;
            this.Owner = owner;
            this.TargetDirt = targetDirt;
            this.TargetCrop = targetCrop;
            this.Method = method;
            this.Scythe = scythe;
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
        public HoeDirt TargetDirt { get; }
        public Crop TargetCrop { get; }
        public HarvestMethod Method { get; }
        public MeleeWeapon? Scythe { get; }
        public TaskNavigationState Navigation { get; }
    }
}
