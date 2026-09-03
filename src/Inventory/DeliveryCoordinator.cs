using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace YuiToIssho;

internal sealed class DeliveryCoordinator
{
    private const int OfferDistance = 12;
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 30;
    private const int StuckTimeoutTicks = 300;
    private const int MaximumPathAttempts = 5;
    private const ulong RetryDelayTicks = 600;

    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, DeliveryTask> tasks = new();
    private readonly Dictionary<CompanionIdentity, string> pendingReturns = new();
    private ulong currentTick;

    public DeliveryCoordinator(CompanionRegistry registry, CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public void Update(ulong tick)
    {
        this.currentTick = tick;
        foreach (DeliveryTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
        if (tick % 60 != 0)
            return;
        foreach (CompanionRecord record in this.registry.Active)
        {
            this.NormalizeRetryClock(record, tick);
            this.TryReturnPending(record, tick);
            this.TryActivate(record, tick);
        }
    }

    public void Cancel(CompanionIdentity identity, string code)
    {
        this.pendingReturns.Remove(identity);
        if (!this.tasks.TryGetValue(identity, out DeliveryTask? task))
            return;
        if (DeliveryPhases.OwnsEscrow(task.Delivery.Phase))
        {
            task.Delivery.Phase = DeliveryPhases.Escrowed;
            task.Delivery.LastFailure = code;
            task.Delivery.NextAttemptTick = RetryAt(this.currentTick);
        }
        this.appearance.Fail(identity, task.Session.OperationId, code);
        this.execution.Complete(task.Session, false, code, "Automatic delivery stopped before handoff; exact cargo remains in Escrow.");
        this.tasks.Remove(identity);
    }

    public void CancelAll(string code)
    {
        this.pendingReturns.Clear();
        foreach (CompanionIdentity identity in this.tasks.Keys.ToArray())
            this.Cancel(identity, code);
    }

    private void NormalizeRetryClock(CompanionRecord record, ulong tick)
    {
        ulong latestReasonableRetry = RetryAt(tick);
        foreach (DeliveryRecord delivery in record.Deliveries.Where(candidate => DeliveryPhases.OwnsEscrow(candidate.Phase)))
        {
            if (delivery.NextAttemptTick > latestReasonableRetry)
                delivery.NextAttemptTick = tick;
        }
    }

    private void TryReturnPending(CompanionRecord record, ulong tick)
    {
        if (this.pendingReturns.ContainsKey(record.Identity)
            || this.tasks.ContainsKey(record.Identity)
            || !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return;

        DeliveryRecord? delivery = record.Deliveries
            .Where(candidate => candidate.Phase == DeliveryPhases.Returning && candidate.NextAttemptTick <= tick)
            .OrderBy(candidate => candidate.CreatedTick)
            .ThenBy(candidate => candidate.DeliveryId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (delivery is null)
            return;

        string deliveryId = delivery.DeliveryId;
        this.pendingReturns.Add(record.Identity, deliveryId);
        this.inventories.RequestTransfer(record.Identity, () =>
        {
            if (!this.pendingReturns.TryGetValue(record.Identity, out string? pendingId)
                || !string.Equals(pendingId, deliveryId, StringComparison.Ordinal)
                || !this.registry.TryGet(record.Identity, out CompanionRecord current)
                || !ReferenceEquals(current, record)
                || !string.IsNullOrWhiteSpace(record.ActiveTransactionId)
                || this.tasks.ContainsKey(record.Identity)
                || delivery.Phase != DeliveryPhases.Returning)
                return InventoryActionResult.Failure("DELIVERY-RETURN-CONTEXT-CHANGED", "Automatic delivery return context changed before the bag lock was acquired.");
            return this.inventories.ReturnDeliveryLocked(record, deliveryId);
        }, result =>
        {
            if (!this.pendingReturns.TryGetValue(record.Identity, out string? pendingId)
                || !string.Equals(pendingId, deliveryId, StringComparison.Ordinal))
                return;
            this.pendingReturns.Remove(record.Identity);
            if (result.IsSuccess)
            {
                this.monitor.Log($"HY-DELIVERY-{result.Code}: {result.Message}", LogLevel.Info);
                return;
            }
            if (delivery.Phase == DeliveryPhases.Returning)
            {
                delivery.LastFailure = result.Message;
                delivery.NextAttemptTick = RetryAt(this.currentTick);
            }
            this.monitor.Log($"HY-DELIVERY-{result.Code}: {result.Message}", LogLevel.Warn);
        });
    }

    private void TryActivate(CompanionRecord record, ulong tick)
    {
        if (Game1.GetPlayer(record.OwnerId, onlyOnline: true) is null
            || this.tasks.ContainsKey(record.Identity)
            || this.pendingReturns.ContainsKey(record.Identity)
            || !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return;
        DeliveryRecord? delivery = record.Deliveries
            .Where(candidate => candidate.Phase is DeliveryPhases.Escrowed or DeliveryPhases.Traveling or DeliveryPhases.Offering)
            .Where(candidate => candidate.NextAttemptTick <= tick)
            .OrderBy(candidate => candidate.CreatedTick)
            .ThenBy(candidate => candidate.DeliveryId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (delivery is null
            || !this.bodies.TryGetBody(record.Identity, out NPC body)
            || body.currentLocation is null)
            return;
        Farmer? recipient = Game1.GetPlayer(delivery.RecipientPlayerId, onlyOnline: true);
        if (recipient?.currentLocation is null || !ReferenceEquals(recipient.currentLocation, body.currentLocation))
            return;

        int attempt = delivery.Attempt + 1;
        string operationId = $"delivery-run:{delivery.DeliveryId}:{attempt}";
        TaskTargetKey target = new(body.currentLocation.NameOrUniqueName, "DeliveryRecipient", recipient.UniqueMultiplayerID.ToString());
        TaskBeginResult begin = this.execution.TryBegin(record.Identity, operationId, "Delivery", target);
        if (!begin.Started || begin.Session is null)
            return;
        delivery.Attempt = attempt;
        delivery.Phase = DeliveryPhases.Traveling;
        delivery.LastFailure = null;
        this.tasks.Add(record.Identity, new DeliveryTask(begin.Session, delivery, recipient, body.currentLocation, body.Position, recipient.Tile));
        this.monitor.Log($"HY-DELIVERY-TRAVELING: {record.Identity} is approaching recipient {recipient.UniqueMultiplayerID} for {delivery.DeliveryId}.", LogLevel.Info);
    }

    private void UpdateOne(DeliveryTask task, ulong tick)
    {
        if (!this.execution.IsCurrent(task.Session))
        {
            this.execution.AbandonRuntime(task.Session);
            this.tasks.Remove(task.Session.Identity);
            return;
        }
        if (!this.bodies.TryGetBody(task.Session.Identity, out NPC body)
            || body.currentLocation is null
            || !ReferenceEquals(body.currentLocation, task.Location)
            || task.Recipient.currentLocation is null
            || !ReferenceEquals(task.Recipient.currentLocation, task.Location))
        {
            this.Fail(task, "RECIPIENT-LOCATION-CHANGED", "Recipient or Yui changed map; cargo remains in Escrow.", tick);
            return;
        }

        int distance = ManhattanDistance(body.TilePoint, task.Recipient.TilePoint);
        if (distance <= OfferDistance)
        {
            this.bodies.Halt(task.Session.Identity);
            body.faceDirection(FacingToward(body.Tile, task.Recipient.Tile));
            this.Settle(task, body.FacingDirection, tick);
            return;
        }

        if (task.LastRecipientTile != task.Recipient.Tile)
        {
            task.LastRecipientTile = task.Recipient.Tile;
            this.bodies.Halt(task.Session.Identity);
            task.Navigation.PathIssued = false;
            task.Navigation.LastPosition = body.Position;
            task.Navigation.LastProgressTick = tick;
            task.Navigation.NextPathTick = tick;
        }
        TaskNavigationResult progress = this.navigation.Observe(task.Session.Identity, body, task.Navigation, tick, StuckTimeoutTicks, MaximumPathAttempts, RepathDelayTicks);
        if (progress.BudgetExhausted)
        {
            this.Fail(task, "DELIVERY-PATH-EXHAUSTED", "Yui could not reach the recipient; cargo remains in Escrow.", tick);
            return;
        }
        if (!progress.CanIssuePath)
            return;
        Vector2? approach = FindApproachTile(task.Location, task.Recipient.Tile, body);
        if (approach is null)
        {
            this.Fail(task, "DELIVERY-NO-APPROACH", "No open handoff tile exists near the recipient; cargo remains in Escrow.", tick);
            return;
        }
        body.controller = new PathFindController(body, task.Location, approach.Value.ToPoint(), FacingToward(approach.Value, task.Recipient.Tile), null, PathSearchLimit);
        task.Session.MarkTraveling();
        this.navigation.MarkPathIssued(task.Navigation, body.Position, tick, RepathDelayTicks);
    }

    private void Settle(DeliveryTask task, int facing, ulong tick)
    {
        if (!task.Session.TryEnterSettlement())
            return;
        Item? cargo = this.inventories.GetEscrow(task.Session.Identity).OfType<Item>().FirstOrDefault(item =>
            item.modData.TryGetValue(CompanionInventoryStore.DeliveryCargoTag, out string? deliveryId)
            && string.Equals(deliveryId, task.Delivery.DeliveryId, StringComparison.Ordinal));
        if (cargo is null)
        {
            this.Fail(task, "DELIVERY-CARGO-MISSING", "The delivery record lost its exact Escrow cargo.", tick);
            return;
        }
        task.Delivery.Phase = DeliveryPhases.Offering;
        this.appearance.Prepare(task.Session.Identity, task.Session.OperationId, AppearanceActionKinds.Handoff, cargo, facing);
        this.inventories.RequestTransfer(task.Session.Identity, () => this.SettleLocked(task), result =>
        {
            if (!this.tasks.TryGetValue(task.Session.Identity, out DeliveryTask? current) || !ReferenceEquals(current, task))
                return;
            if (result.IsSuccess)
            {
                this.appearance.Commit(task.Session.Identity, task.Session.OperationId);
                this.execution.Complete(task.Session, true, result.Code, result.Message);
                this.tasks.Remove(task.Session.Identity);
            }
            else
                this.Fail(task, result.Code, result.Message, tick);
        });
    }

    private InventoryActionResult SettleLocked(DeliveryTask task)
    {
        if (!this.execution.IsCurrent(task.Session)
            || !this.bodies.TryGetBody(task.Session.Identity, out NPC body)
            || body.currentLocation is null
            || !ReferenceEquals(body.currentLocation, task.Recipient.currentLocation)
            || ManhattanDistance(body.TilePoint, task.Recipient.TilePoint) > OfferDistance)
            return InventoryActionResult.Failure("DELIVERY-CONTEXT-CHANGED", "Handoff context changed while the bag lock was pending; cargo remains in Escrow.");
        return this.registry.TryGet(task.Session.Identity, out CompanionRecord record)
            ? this.inventories.CompleteDeliveryLocked(record, task.Delivery.DeliveryId, task.Recipient)
            : InventoryActionResult.Failure("IDENTITY-NOT-FOUND", "The delivery owner disappeared while the bag lock was pending.");
    }

    private void Fail(DeliveryTask task, string code, string message, ulong tick)
    {
        if (DeliveryPhases.OwnsEscrow(task.Delivery.Phase))
        {
            task.Delivery.Phase = DeliveryPhases.Escrowed;
            task.Delivery.LastFailure = message;
            task.Delivery.NextAttemptTick = RetryAt(tick);
        }
        this.appearance.Fail(task.Session.Identity, task.Session.OperationId, code);
        this.execution.Complete(task.Session, false, code, message);
        this.tasks.Remove(task.Session.Identity);
    }

    private static Vector2? FindApproachTile(GameLocation location, Vector2 target, NPC body)
    {
        Vector2[] candidates = { target + new Vector2(1, 0), target + new Vector2(-1, 0), target + new Vector2(0, 1), target + new Vector2(0, -1) };
        return candidates.Where(tile => location.isTileLocationOpen(tile)
                && location.characters.All(character => ReferenceEquals(character, body) || character.Tile != tile))
            .OrderBy(tile => ManhattanDistance(tile.ToPoint(), body.TilePoint)).Cast<Vector2?>().FirstOrDefault();
    }

    private static int FacingToward(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y)) return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }

    private static int ManhattanDistance(Point left, Point right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static ulong RetryAt(ulong tick) => tick > ulong.MaxValue - RetryDelayTicks ? ulong.MaxValue : tick + RetryDelayTicks;

    private sealed class DeliveryTask
    {
        public DeliveryTask(TaskSession session, DeliveryRecord delivery, Farmer recipient, GameLocation location, Vector2 position, Vector2 recipientTile)
        { this.Session = session; this.Delivery = delivery; this.Recipient = recipient; this.Location = location; this.Navigation = new TaskNavigationState(position, 0); this.LastRecipientTile = recipientTile; }
        public TaskSession Session { get; }
        public DeliveryRecord Delivery { get; }
        public Farmer Recipient { get; }
        public GameLocation Location { get; }
        public TaskNavigationState Navigation { get; }
        public Vector2 LastRecipientTile { get; set; }
    }
}
