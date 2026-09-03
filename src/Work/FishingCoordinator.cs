using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal readonly record struct FishingCommandResult(bool IsSuccess, string Code, string Message)
{
    public static FishingCommandResult Success(string code, string message) => new(true, code, message);
    public static FishingCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class FishingCoordinator
{
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 30;
    private const int StuckSampleLimit = 10;
    private const int MaximumPathAttempts = 5;
    private const int MaximumCastDistance = 5;
    private const int CastTicks = 30;
    private const int WaitingTicks = 90;
    private const int ReelTicks = 30;
    private const int CaughtTicks = 30;

    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, FishingTask> tasks = new();

    public FishingCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.monitor = monitor;
    }

    public FishingCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return FishingCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before fishing.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return FishingCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when fishing starts.");

        FishingRod? rod = this.inventories.FindFirst<FishingRod>(identity);
        if (rod is null)
            return FishingCommandResult.Failure("FISHING-ROD-REQUIRED", "A real FishingRod in this Yui's bag is required.");
        if (rod.inUse())
            return FishingCommandResult.Failure("FISHING-ROD-IN-USE", "The real FishingRod is already in a vanilla use sequence.");
        GameLocation location = owner.currentLocation;
        Vector2 waterTile = new(tileX, tileY);
        if (!location.canFishHere() || !location.isWaterTile(tileX, tileY))
            return FishingCommandResult.Failure("INVALID-WATER-TARGET", $"Tile {tileX},{tileY} is not fishable water in this location.");

        Vector2? approachTile = FindFishingApproach(location, waterTile, body);
        if (approachTile is null)
            return FishingCommandResult.Failure("NO-LEGAL-CAST-POSITION", "No open land tile with a continuous one-to-five-tile water cast line exists.");

        SObject? bait = rod.GetBait();
        IReadOnlyList<SObject> tackle = rod.GetTackle().ToArray();
        TaskTargetKey target = new(location.NameOrUniqueName, "FishingWater", $"{tileX},{tileY}");
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Fishing", target);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new FishingTask(begin.Session, waterTile, approachTile.Value, location, owner, rod, bait, tackle, body.Position));
        this.monitor.Log($"HY-FISH-STARTED: {identity} reserved {target} for session {operationId}.", LogLevel.Info);
        return FishingCommandResult.Success("STARTED", $"Fishing session {operationId} is approaching {tileX},{tileY}.");
    }

    public void Update(ulong tick)
    {
        foreach (FishingTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public FishingCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out FishingTask? task))
            return FishingCommandResult.Success("ALREADY-IDLE", $"{identity} has no fishing session.");
        if (task.CatchRouted)
            return this.Complete(task, "COMMITTED", task.CatchMessage, true);
        return this.Complete(task, code, $"Fishing session {task.OperationId} was cancelled during {task.Phase} before fish generation.", false);
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out FishingTask? task) ? task.Rod : null;

    public void CancelAll(string code)
    {
        foreach (CompanionIdentity identity in this.tasks.Keys.ToArray())
            this.Cancel(identity, code);
    }

    private void UpdateOne(FishingTask task, ulong tick)
    {
        if (!this.execution.IsCurrent(task.Session))
        {
            this.execution.AbandonRuntime(task.Session);
            this.tasks.Remove(task.Identity);
            return;
        }
        if (task.CatchRouted)
        {
            if (tick >= task.PhaseEndsAt)
                this.Complete(task, "COMMITTED", task.CatchMessage, true);
            return;
        }
        if (!this.bodies.TryGetBody(task.Identity, out NPC body) || body.currentLocation is null || !ReferenceEquals(body.currentLocation, task.Location))
        {
            this.Complete(task, "BODY-INVALID", "The companion body became unavailable or changed location.", false);
            return;
        }
        if (task.Owner.currentLocation is null || !ReferenceEquals(task.Owner.currentLocation, task.Location))
        {
            this.Complete(task, "OWNER-LEFT-LOCATION", "The owner left the fixed fishing location.", false);
            return;
        }
        if (!this.inventories.ContainsExact(task.Identity, task.Rod) || task.Rod.inUse() || !AttachmentsUnchanged(task))
        {
            this.Complete(task, "FISHING-ROD-CHANGED", "The exact FishingRod or its bait/tackle attachments changed; the session ended safely.", false);
            return;
        }
        if (!task.Location.canFishHere() || !task.Location.isWaterTile((int)task.WaterTile.X, (int)task.WaterTile.Y)
            || !IsStableCastLine(task.Location, task.ApproachTile, task.WaterTile))
        {
            this.Complete(task, "WATER-TARGET-CHANGED", "The fixed water target or cast line is no longer legal.", false);
            return;
        }
        if (task.Phase != FishingPhase.Approach && body.Tile != task.ApproachTile)
        {
            this.Complete(task, "CAST-POSITION-CHANGED", "The companion left the fixed legal cast position before settlement.", false);
            return;
        }

        if (task.Phase == FishingPhase.Approach)
        {
            this.UpdateApproach(task, body, tick);
            return;
        }
        if (tick < task.PhaseEndsAt)
            return;

        switch (task.Phase)
        {
            case FishingPhase.Cast:
                this.EnterPhase(task, FishingPhase.Waiting, tick + WaitingTicks);
                break;
            case FishingPhase.Waiting:
                this.EnterPhase(task, FishingPhase.Reel, tick + ReelTicks);
                break;
            case FishingPhase.Reel:
                this.Settle(task, body, tick);
                break;
            case FishingPhase.Caught:
                break;
        }
    }

    private void UpdateApproach(FishingTask task, NPC body, ulong tick)
    {
        if (body.Tile == task.ApproachTile)
        {
            this.bodies.Halt(task.Identity);
            body.faceDirection(FacingToward(task.ApproachTile, task.WaterTile));
            this.EnterPhase(task, FishingPhase.Cast, tick + CastTicks);
            return;
        }

        this.TrackProgress(task, body, tick);
        if (!this.tasks.ContainsKey(task.Identity) || tick < task.NextPathTick || body.controller is not null)
            return;
        if (!task.Location.isTileLocationOpen(task.ApproachTile)
            || task.Location.characters.Any(character => !ReferenceEquals(character, body) && character.Tile == task.ApproachTile))
        {
            this.Complete(task, "CAST-POSITION-BLOCKED", "The fixed land tile for this cast became blocked.", false);
            return;
        }

        body.controller = new PathFindController(body, task.Location, task.ApproachTile.ToPoint(), FacingToward(task.ApproachTile, task.WaterTile), null, PathSearchLimit);
        task.NextPathTick = tick + RepathDelayTicks;
    }

    private void TrackProgress(FishingTask task, NPC body, ulong tick)
    {
        if (body.Position != task.LastPosition)
        {
            task.LastPosition = body.Position;
            task.StuckSamples = 0;
            task.PathAttempts = 0;
            return;
        }
        if (++task.StuckSamples < StuckSampleLimit)
            return;
        task.StuckSamples = 0;
        task.PathAttempts++;
        this.bodies.Halt(task.Identity);
        if (task.PathAttempts >= MaximumPathAttempts)
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The cast position stayed unreachable through the bounded retry budget.", false);
        else
            task.NextPathTick = tick + (ulong)(RepathDelayTicks * task.PathAttempts);
    }

    private void Settle(FishingTask task, NPC body, ulong tick)
    {
        if (!task.Session.TryEnterSettlement())
            return;
        this.inventories.RequestTransfer(
            task.Identity,
            () => this.GenerateCatchLocked(task),
            result =>
            {
                if (!this.tasks.TryGetValue(task.Identity, out FishingTask? current) || !ReferenceEquals(current, task))
                    return;
                if (!result.IsSuccess)
                {
                    this.Complete(task, result.Code, result.Message, false);
                    return;
                }
                task.CatchRouted = true;
                task.CatchMessage = result.Message;
                this.EnterPhase(task, FishingPhase.Caught, tick + CaughtTicks);
            }
        );
    }

    private InventoryActionResult GenerateCatchLocked(FishingTask task)
    {
        if (!this.execution.IsCurrent(task.Session) || !AttachmentsUnchanged(task))
            return InventoryActionResult.Failure("FISHING-SESSION-CHANGED", "The fishing session changed while the bag lock was pending.");

        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Fishing, $"{task.OperationId}:catch");
        if (!cost.IsSuccess)
            return InventoryActionResult.Failure(cost.Result.Code, cost.Result.Message);
        try
        {
            int waterDepth = Math.Max(0, FishingRod.distanceToLand((int)task.WaterTile.X, (int)task.WaterTile.Y, task.Location, false));
            Item? catchItem = task.Location.getFish(0f, task.Bait?.QualifiedItemId, waterDepth, task.Owner, 0d, task.WaterTile, null);
            if (catchItem is null)
                return InventoryActionResult.Failure("NO-VANILLA-CATCH", "Vanilla returned no catch; generation was not retried.");

            cost.Commit();
            InventoryActionResult routed = this.inventories.StoreGeneratedOutput(task.Identity, catchItem);
            return routed.IsSuccess
                ? InventoryActionResult.Success("CATCH-ROUTED", $"Caught {catchItem.DisplayName}; the exact vanilla catch now belongs to this Yui. Bait and tackle remain unchanged.")
                : routed;
        }
        catch (Exception ex)
        {
            cost.Commit();
            return InventoryActionResult.Failure("SETTLEMENT-ERROR", $"Fishing stopped without generation retry after {ex.GetType().Name}.");
        }
    }

    private void EnterPhase(FishingTask task, FishingPhase phase, ulong phaseEndsAt)
    {
        task.Phase = phase;
        task.PhaseEndsAt = phaseEndsAt;
        if (phase != FishingPhase.Approach)
        {
            int facing = this.bodies.TryGetBody(task.Identity, out NPC visualBody) ? visualBody.FacingDirection : 2;
            int duration = phase switch { FishingPhase.Cast => CastTicks, FishingPhase.Waiting => WaitingTicks, FishingPhase.Reel => ReelTicks, _ => CaughtTicks };
            this.appearance.SetPhase(task.Identity, task.OperationId, AppearanceActionKinds.Fishing, phase.ToString(), task.Rod, facing, duration);
        }
        this.monitor.Log($"HY-FISH-PHASE: {task.Identity} session {task.OperationId} entered {phase}.", LogLevel.Trace);
    }

    private FishingCommandResult Complete(FishingTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-FISH-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
    }

    private static bool AttachmentsUnchanged(FishingTask task)
    {
        if (!ReferenceEquals(task.Rod.GetBait(), task.Bait))
            return false;
        List<SObject> current = task.Rod.GetTackle();
        return current.Count == task.Tackle.Count && current.Zip(task.Tackle).All(pair => ReferenceEquals(pair.First, pair.Second));
    }

    private static Vector2? FindFishingApproach(GameLocation location, Vector2 waterTile, NPC body)
    {
        Vector2[] directions = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        List<Vector2> candidates = new();
        foreach (Vector2 direction in directions)
        {
            for (int distance = 1; distance <= MaximumCastDistance; distance++)
            {
                Vector2 candidate = waterTile + (direction * distance);
                if (location.isWaterTile((int)candidate.X, (int)candidate.Y))
                    continue;
                if (IsStableCastLine(location, candidate, waterTile) && location.isTileLocationOpen(candidate)
                    && location.characters.All(character => ReferenceEquals(character, body) || character.Tile != candidate))
                    candidates.Add(candidate);
                break;
            }
        }
        return candidates.OrderBy(candidate => ManhattanDistance(candidate.ToPoint(), body.TilePoint)).Cast<Vector2?>().FirstOrDefault();
    }

    private static bool IsStableCastLine(GameLocation location, Vector2 approach, Vector2 water)
    {
        Vector2 delta = water - approach;
        if ((delta.X != 0 && delta.Y != 0) || delta == Vector2.Zero)
            return false;
        int distance = (int)(Math.Abs(delta.X) + Math.Abs(delta.Y));
        if (distance < 1 || distance > MaximumCastDistance || location.isWaterTile((int)approach.X, (int)approach.Y))
            return false;
        Vector2 step = new(Math.Sign(delta.X), Math.Sign(delta.Y));
        for (int index = 1; index <= distance; index++)
        {
            Vector2 tile = approach + (step * index);
            if (!location.isWaterTile((int)tile.X, (int)tile.Y))
                return false;
        }
        return true;
    }

    private static int FacingToward(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }

    private static int ManhattanDistance(Point left, Point right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static FishingCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? FishingCommandResult.Success(result.Code, result.Message)
        : FishingCommandResult.Failure(result.Code, result.Message);

    private enum FishingPhase
    {
        Approach,
        Cast,
        Waiting,
        Reel,
        Caught,
    }

    private sealed class FishingTask
    {
        public FishingTask(TaskSession session, Vector2 waterTile, Vector2 approachTile, GameLocation location, Farmer owner, FishingRod rod, SObject? bait, IReadOnlyList<SObject> tackle, Vector2 position)
        {
            this.Session = session; this.WaterTile = waterTile; this.ApproachTile = approachTile;
            this.Location = location; this.Owner = owner; this.Rod = rod; this.Bait = bait; this.Tackle = tackle; this.LastPosition = position;
        }

        public TaskSession Session { get; }
        public CompanionIdentity Identity => this.Session.Identity;
        public string OperationId => this.Session.OperationId;
        public TaskTargetKey Target => this.Session.Target;
        public Vector2 WaterTile { get; }
        public Vector2 ApproachTile { get; }
        public GameLocation Location { get; }
        public Farmer Owner { get; }
        public FishingRod Rod { get; }
        public SObject? Bait { get; }
        public IReadOnlyList<SObject> Tackle { get; }
        public Vector2 LastPosition { get; set; }
        public int StuckSamples { get; set; }
        public int PathAttempts { get; set; }
        public ulong NextPathTick { get; set; }
        public FishingPhase Phase { get; set; } = FishingPhase.Approach;
        public ulong PhaseEndsAt { get; set; }
        public bool CatchRouted { get; set; }
        public string CatchMessage { get; set; } = string.Empty;
    }
}
