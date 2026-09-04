using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal readonly record struct DigCommandResult(bool IsSuccess, string Code, string Message)
{
    public static DigCommandResult Success(string code, string message) => new(true, code, message);

    public static DigCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class DiggingCoordinator
{
    private const string ArtifactSpotId = "(O)590";
    private const string SeedSpotId = "(O)SeedSpot";
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 30;
    private const int StuckTimeoutTicks = 300;
    private const int MaximumPathAttempts = 5;
    private const int UnchargedToolPower = 0;
    private const int VanillaUsePower = 1;

    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, DiggingTask> tasks = new();

    public DiggingCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public DigCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return DigCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before digging.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return DigCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when digging starts.");

        Hoe? hoe = this.inventories.FindFirst<Hoe>(identity);
        if (hoe is null)
            return DigCommandResult.Failure("HOE-MISSING", "This Yui's bag has no real Hoe.");

        Vector2 targetTile = new(tileX, tileY);
        if (!TryClassifyTarget(owner.currentLocation, targetTile, out DigTargetKind kind, out SObject? digSpot, out string classificationFailure))
            return DigCommandResult.Failure("TARGET-NOT-DIGGABLE", classificationFailure);

        TaskTargetKey target = new(owner.currentLocation.NameOrUniqueName, kind.ToString(), $"{tileX},{tileY}");

        Vector2? approachTile = this.navigation.FindReachableCardinalApproach(body, owner.currentLocation, targetTile, PathSearchLimit);
        if (approachTile is null)
            return DigCommandResult.Failure("TARGET-UNREACHABLE", "No open adjacent standing tile exists for the requested dig target.");
        int facing = FacingToward(approachTile.Value, targetTile);

        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Digging", target);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new DiggingTask(
            begin.Session,
            targetTile,
            approachTile.Value,
            facing,
            kind,
            digSpot,
            owner.currentLocation,
            owner,
            hoe,
            body.Position
        ));
        this.monitor.Log($"HY-DIG-STARTED: {identity} reserved {kind} target {target} for {operationId}.", LogLevel.Info);
        return DigCommandResult.Success("STARTED", $"Digging {operationId} reserved {kind} target {tileX},{tileY}.");
    }

    public void Update(ulong tick)
    {
        foreach (DiggingTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public DigCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out DiggingTask? task))
            return DigCommandResult.Success("ALREADY-IDLE", $"{identity} has no digging task.");

        return this.Complete(task, code, $"Operation {task.OperationId} was cancelled before settlement.", false);
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out DiggingTask? task) ? task.Hoe : null;

    public bool TryGetReservedGeometry(CompanionIdentity identity, out WorkStepGeometry geometry)
    {
        if (this.tasks.TryGetValue(identity, out DiggingTask? task))
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

    private void UpdateOne(DiggingTask task, ulong tick)
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
            this.Complete(task, "BODY-INVALID", "The companion body became unavailable or changed location.", false);
            return;
        }

        if (task.Owner.currentLocation is null || !ReferenceEquals(task.Owner.currentLocation, task.Location))
        {
            this.Complete(task, "OWNER-LEFT-LOCATION", "The owner left the digging location.", false);
            return;
        }

        if (!this.inventories.ContainsExact(task.Identity, task.Hoe))
        {
            this.Complete(task, "HOE-CHANGED", "The exact reserved Hoe left this Yui's bag.", false);
            return;
        }

        if (!this.ValidateTarget(task, out string targetFailure))
        {
            this.Complete(task, "TARGET-CHANGED", targetFailure, false);
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
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The dig target stayed unreachable through the bounded retry budget.", false);
            return;
        }
        if (!progress.CanIssuePath)
            return;

        if (!CompanionPathing.IsStandable(body, task.Location, task.ApproachTile))
        {
            this.Complete(task, "APPROACH-BLOCKED", "The reserved standing tile became blocked.", false);
            return;
        }

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

    private bool ValidateTarget(DiggingTask task, out string failure)
    {
        if (task.Kind == DigTargetKind.OrdinaryGround)
        {
            if (IsOrdinaryGround(task.Location, task.TargetTile, out failure))
                return true;
            return false;
        }

        if (!task.Location.Objects.TryGetValue(task.TargetTile, out SObject? current)
            || !ReferenceEquals(current, task.DigSpot)
            || current.QualifiedItemId != ExpectedQualifiedId(task.Kind))
        {
            failure = $"The exact {task.Kind} object changed or disappeared before settlement.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private void Settle(DiggingTask task)
    {
        if (!OwnerContextLease.CanProject(task.Owner))
            return;
        if (!task.Session.TryEnterSettlement())
            return;

        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Digging, $"{task.OperationId}:dig");
        if (!cost.IsSuccess)
        {
            this.Complete(task, cost.Result.Code, cost.Result.Message, false);
            return;
        }
        bool vanillaInvoked = false;
        this.appearance.Prepare(task.Identity, task.OperationId, AppearanceActionKinds.Digging, task.Hoe, task.Facing);
        try
        {
            if (!this.bodies.TryGetBody(task.Identity, out NPC actionBody))
            {
                this.Complete(task, "BODY-INVALID", "The companion body disappeared before the Hoe commit.", false);
                return;
            }

            using OwnerContextLease context = OwnerContextLease.Project(task.Owner, actionBody.Position, task.Facing);
            task.Owner.toolPower.Value = UnchargedToolPower;
            int targetPixelX = (int)(task.TargetTile.X * Game1.tileSize) + (Game1.tileSize / 2);
            int targetPixelY = (int)(task.TargetTile.Y * Game1.tileSize) + (Game1.tileSize / 2);
            vanillaInvoked = true;
            task.Hoe.DoFunction(task.Location, targetPixelX, targetPixelY, VanillaUsePower, task.Owner);
        }
        catch (Exception ex)
        {
            if (vanillaInvoked)
                cost.Commit();
            this.Complete(task, "SETTLEMENT-ERROR", $"The vanilla Hoe action stopped without retry after an error: {ex.Message}", false);
            return;
        }

        if (task.Kind == DigTargetKind.OrdinaryGround)
        {
            if (task.Location.GetHoeDirtAtTile(task.TargetTile) is not null)
            {
                cost.Commit();
                this.appearance.Commit(task.Identity, task.OperationId);
                this.Complete(task, "COMMITTED", $"Vanilla tilled ordinary ground at {task.Target} once.", true);
                return;
            }

            this.Complete(task, "VANILLA-REJECTED", "Vanilla added no HoeDirt; no retry or adjacent target was attempted.", false);
            return;
        }

        if (!task.Location.Objects.TryGetValue(task.TargetTile, out SObject? current))
        {
            cost.Commit();
            this.appearance.Commit(task.Identity, task.OperationId);
            this.Complete(task, "COMMITTED", $"Vanilla settled exact {task.Kind} {task.Target}; its products remain unrestricted world Debris.", true);
            return;
        }

        if (ReferenceEquals(current, task.DigSpot))
        {
            this.Complete(task, "VANILLA-REJECTED", $"Vanilla left the exact {task.Kind} in place; no retry was attempted.", false);
            return;
        }

        cost.Commit();
        this.Complete(task, "TARGET-REPLACED-AFTER-SETTLEMENT", "A replacement object appeared during settlement; the original world change kept its one stamina charge and no second Hoe action was attempted.", false);
    }

    private DigCommandResult Complete(DiggingTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-DIG-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
    }

    private static bool TryClassifyTarget(
        GameLocation location,
        Vector2 tile,
        out DigTargetKind kind,
        out SObject? digSpot,
        out string failure)
    {
        if (location.Objects.TryGetValue(tile, out SObject? objectAtTile))
        {
            if (objectAtTile.QualifiedItemId == ArtifactSpotId)
                kind = DigTargetKind.ArtifactSpot;
            else if (objectAtTile.QualifiedItemId == SeedSpotId)
                kind = DigTargetKind.SeedSpot;
            else
            {
                kind = default;
                digSpot = null;
                failure = $"Tile {tile.X},{tile.Y} is occupied by a non-DigSpot object.";
                return false;
            }

            digSpot = objectAtTile;
            failure = string.Empty;
            return true;
        }

        if (IsOrdinaryGround(location, tile, out failure))
        {
            kind = DigTargetKind.OrdinaryGround;
            digSpot = null;
            return true;
        }

        kind = default;
        digSpot = null;
        return false;
    }

    private static bool IsOrdinaryGround(GameLocation location, Vector2 tile, out string failure)
    {
        if (location.Objects.ContainsKey(tile))
        {
            failure = "The ordinary-ground tile is occupied by an object.";
            return false;
        }
        if (location.terrainFeatures.ContainsKey(tile) || location.GetHoeDirtAtTile(tile) is not null)
        {
            failure = "The ordinary-ground tile already has a terrain feature or HoeDirt.";
            return false;
        }
        if (location.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back") is null)
        {
            failure = "The ordinary-ground tile has no vanilla Diggable Back-layer property.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static string ExpectedQualifiedId(DigTargetKind kind) =>
        kind == DigTargetKind.ArtifactSpot ? ArtifactSpotId : SeedSpotId;

    private Vector2? FindApproachTile(GameLocation location, Vector2 target, NPC body) =>
        this.navigation.FindReachableCardinalApproach(body, location, target, PathSearchLimit);

    private static int FacingToward(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }

    private static int ManhattanDistance(Point left, Point right) =>
        Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private enum DigTargetKind
    {
        OrdinaryGround,
        ArtifactSpot,
        SeedSpot,
    }

    private static DigCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? DigCommandResult.Success(result.Code, result.Message)
        : DigCommandResult.Failure(result.Code, result.Message);

    private sealed class DiggingTask
    {
        public DiggingTask(
            TaskSession session,
            Vector2 targetTile,
            Vector2 approachTile,
            int facing,
            DigTargetKind kind,
            SObject? digSpot,
            GameLocation location,
            Farmer owner,
            Hoe hoe,
            Vector2 initialPosition)
        {
            this.Session = session;
            this.TargetTile = targetTile;
            this.ApproachTile = approachTile;
            this.Facing = facing;
            this.Kind = kind;
            this.DigSpot = digSpot;
            this.Location = location;
            this.Owner = owner;
            this.Hoe = hoe;
            this.Navigation = new TaskNavigationState(initialPosition, 0);
        }

        public TaskSession Session { get; }
        public CompanionIdentity Identity => this.Session.Identity;
        public string OperationId => this.Session.OperationId;
        public TaskTargetKey Target => this.Session.Target;
        public Vector2 TargetTile { get; }
        public Vector2 ApproachTile { get; }
        public int Facing { get; }
        public DigTargetKind Kind { get; }
        public SObject? DigSpot { get; }
        public GameLocation Location { get; }
        public Farmer Owner { get; }
        public Hoe Hoe { get; }
        public TaskNavigationState Navigation { get; }
    }
}
