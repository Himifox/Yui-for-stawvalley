using System.Globalization;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Tools;

namespace YuiToIssho;

internal readonly record struct InventoryActionResult(bool IsSuccess, string Code, string Message)
{
    public static InventoryActionResult Success(string code, string message) => new(true, code, message);
    public static InventoryActionResult Failure(string code, string message) => new(false, code, message);
}

internal readonly record struct InventoryValidationResult(bool IsSuccess, string Code, string Message)
{
    public static InventoryValidationResult Success(string message) => new(true, "VALID", message);
    public static InventoryValidationResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class CompanionInventoryStore
{
    public const int Capacity = 24;
    private const string NamespacePrefix = "Himifox.YuiToIssho/Bag/v2";
    private const string PendingOutputPrefix = "Himifox.YuiToIssho/PendingOutput/v2";
    private const string RecoveryVaultPrefix = "Himifox.YuiToIssho/RecoveryVault/v2";
    private const string EscrowPrefix = "Himifox.YuiToIssho/Escrow/v2";
    private const string CraftEscrowPrefix = "Himifox.YuiToIssho/CraftEscrow/v1";
    private const string PlantEscrowPrefix = "Himifox.YuiToIssho/PlantEscrow/v1";
    internal const string CraftIdTag = "Himifox.YuiToIssho/CraftId/v1";
    internal const string CraftSourceTag = "Himifox.YuiToIssho/CraftSource/v1";
    internal const string CraftOutputTokenTag = "Himifox.YuiToIssho/CraftOutputToken/v1";
    internal const string PlantingIdTag = "Himifox.YuiToIssho/PlantingId/v1";
    internal const string PlantSourceTag = "Himifox.YuiToIssho/PlantSource/v1";
    internal const string DeliveryCargoTag = "Himifox.YuiToIssho/DeliveryCargo/v2";
    private const string DeliveryCargoTokenTag = "Himifox.YuiToIssho/DeliveryCargoToken/v2";
    private const string PendingOutputResponsibilityTag = "Himifox.YuiToIssho/PendingOutputResponsibility/v2";
    private const string StarterToolTag = "Himifox.YuiToIssho/StarterTool/v2";
    private static readonly string[] StarterToolKinds = { "Scythe", "Axe", "Pickaxe", "WateringCan", "Hoe" };

    public Inventory Get(CompanionIdentity identity) => Game1.player.team.GetOrCreateGlobalInventory(GetNamespace(identity));

    public int Count(CompanionIdentity identity) => this.Get(identity).Count(item => item is not null && !IsStarterTool(item));

    public bool HasItems(CompanionIdentity identity) => this.Count(identity) > 0;

    public int PendingOutputCount(CompanionIdentity identity) => this.GetPendingOutputs(identity).Count(item => item is not null);

    public int RecoveryVaultCount(CompanionIdentity identity) => this.GetRecoveryVault(identity).Count(item => item is not null);

    public int EscrowCount(CompanionIdentity identity) => this.GetEscrow(identity).Count(item => item is not null);

    public int CraftEscrowCount(CompanionIdentity identity) => this.GetCraftEscrow(identity).Count(item => item is not null);

    public int PlantEscrowCount(CompanionIdentity identity) => this.GetPlantEscrow(identity).Count(item => item is not null);

    public int CountCraftOutputs(CompanionIdentity identity, IReadOnlyCollection<string> outputTokens)
    {
        if (outputTokens.Count == 0)
            return 0;
        HashSet<string> expected = outputTokens.ToHashSet(StringComparer.Ordinal);
        return this.Get(identity).Concat(this.GetPendingOutputs(identity)).Concat(this.GetRecoveryVault(identity))
            .Count(item => item is not null
                && item.modData.TryGetValue(CraftOutputTokenTag, out string? token)
                && expected.Contains(token));
    }

    public bool HasOutstandingOutputs(CompanionIdentity identity) => this.PendingOutputCount(identity) > 0 || this.RecoveryVaultCount(identity) > 0 || this.EscrowCount(identity) > 0 || this.CraftEscrowCount(identity) > 0 || this.PlantEscrowCount(identity) > 0;

    public bool ContainsExact(CompanionIdentity identity, Item item) => this.Get(identity).Any(candidate => ReferenceEquals(candidate, item));

    public bool IsBagLocked(CompanionIdentity identity) => Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLocked();

    public T? FindFirst<T>(CompanionIdentity identity, Func<T, bool>? predicate = null) where T : Item
    {
        return this.Get(identity)
            .OfType<T>()
            .Where(item => !item.modData.ContainsKey(StorageTags.ReturnPending) && (predicate is null || predicate(item)))
            .OrderBy(item => IsStarterTool(item) ? 1 : 0)
            .ThenByDescending(item => item is Tool tool ? tool.UpgradeLevel : 0)
            .FirstOrDefault();
    }

    public InventoryValidationResult EnsureStarterTools(IEnumerable<CompanionRecord> records)
    {
        foreach (CompanionRecord record in records)
        {
            InventoryValidationResult result = this.EnsureStarterTools(record.Identity);
            if (!result.IsSuccess)
                return result;
        }
        return InventoryValidationResult.Success("Every Yui has one protected starter tool set.");
    }

    public InventoryValidationResult EnsureStarterTools(CompanionIdentity identity)
    {
        Inventory bag = this.Get(identity);
        Dictionary<string, Item> existing = new(StringComparer.Ordinal);
        foreach (Item item in bag.OfType<Item>().Where(IsStarterTool))
        {
            string kind = item.modData[StarterToolTag];
            if (!StarterToolKinds.Contains(kind, StringComparer.Ordinal) || !MatchesStarterKind(item, kind) || !existing.TryAdd(kind, item))
                return InventoryValidationResult.Failure("INVALID-STARTER-TOOLS", $"{identity} has an unknown, mismatched, or duplicated protected starter tool.");
        }

        foreach (string kind in StarterToolKinds.Where(kind => !existing.ContainsKey(kind)))
        {
            Tool tool = CreateStarterTool(kind);
            tool.modData[StarterToolTag] = kind;
            bag.Add(tool);
        }
        return InventoryValidationResult.Success($"{identity} has one protected starter tool set.");
    }

    public InventoryValidationResult Validate(IEnumerable<CompanionRecord> records)
    {
        HashSet<Item> seen = new(ReferenceEqualityComparer.Instance);
        foreach (CompanionRecord record in records)
        {
            if (record.Inventory.Count > 0)
                return InventoryValidationResult.Failure("LEGACY-INVENTORY-RECORDS", $"{record.Identity} has {record.Inventory.Count} lossy inventory record(s); automatic reconstruction is forbidden.");

            Inventory bag = this.Get(record.Identity);
            int occupied = bag.Count(item => item is not null && !IsStarterTool(item));
            if (occupied > Capacity)
                return InventoryValidationResult.Failure("BAG-OVER-CAPACITY", $"{record.Identity} has {occupied} real stacks, exceeding capacity {Capacity}; no item was removed.");

            foreach (Item? item in bag)
            {
                if (item is not null && !seen.Add(item))
                    return InventoryValidationResult.Failure("DUPLICATE-ITEM-REFERENCE", "One real Item reference appears in more than one Yui bag slot; writes are disabled without deleting either reference.");
            }

            foreach (Item? item in this.GetPendingOutputs(record.Identity).Concat(this.GetRecoveryVault(record.Identity)))
            {
                if (item is null || item.Stack <= 0 || !seen.Add(item))
                    return InventoryValidationResult.Failure("INVALID-OUTPUT-RESPONSIBILITY", $"{record.Identity} has an invalid or duplicated Pending Output/Recovery Vault item.");
            }

            Inventory escrow = this.GetEscrow(record.Identity);
            Dictionary<string, Item> escrowByDelivery = new(StringComparer.Ordinal);
            foreach (Item? item in escrow)
            {
                if (item is null || item.Stack <= 0 || !seen.Add(item)
                    || !item.modData.TryGetValue(DeliveryCargoTag, out string? deliveryId)
                    || string.IsNullOrWhiteSpace(deliveryId)
                    || !escrowByDelivery.TryAdd(deliveryId, item))
                    return InventoryValidationResult.Failure("INVALID-ESCROW-CARGO", $"{record.Identity} has invalid, duplicated, or untagged Escrow cargo.");
            }
            foreach (DeliveryRecord delivery in record.Deliveries)
            {
                bool hasCargo = escrowByDelivery.TryGetValue(delivery.DeliveryId, out Item? cargo)
                    && cargo.QualifiedItemId == delivery.QualifiedItemId
                    && cargo.Stack == delivery.Quantity
                    && cargo.modData.TryGetValue(DeliveryCargoTokenTag, out string? cargoToken)
                    && string.Equals(cargoToken, delivery.CargoToken, StringComparison.Ordinal);
                if (DeliveryPhases.OwnsEscrow(delivery.Phase) != hasCargo)
                    return InventoryValidationResult.Failure("DELIVERY-CARGO-MISMATCH", $"{record.Identity} delivery {delivery.DeliveryId} does not match its Escrow responsibility.");
            }
            if (escrowByDelivery.Keys.Any(deliveryId => record.Deliveries.All(delivery => !string.Equals(delivery.DeliveryId, deliveryId, StringComparison.Ordinal))))
                return InventoryValidationResult.Failure("ORPHANED-ESCROW-CARGO", $"{record.Identity} has Escrow cargo without a delivery record.");

            Inventory craftEscrow = this.GetCraftEscrow(record.Identity);
            foreach (Item? item in craftEscrow)
            {
                if (item is null || item.Stack <= 0 || !seen.Add(item)
                    || !item.modData.TryGetValue(CraftIdTag, out string? craftId)
                    || record.CraftTransaction is null
                    || !string.Equals(craftId, record.CraftTransaction.CraftId, StringComparison.Ordinal))
                    return InventoryValidationResult.Failure("INVALID-CRAFT-ESCROW", $"{record.Identity} has invalid, duplicated, or orphaned Craft Escrow material.");
            }
            if (record.CraftTransaction is not null
                && record.CraftTransaction.SourcePlan.Count(source => source.Acquired) != craftEscrow.Count(item => item is not null))
                return InventoryValidationResult.Failure("CRAFT-ESCROW-MISMATCH", $"{record.Identity} Craft Escrow does not match its acquired source ledger.");

            Inventory plantEscrow = this.GetPlantEscrow(record.Identity);
            Dictionary<string, Item> plantItemsBySource = new(StringComparer.Ordinal);
            foreach (Item? item in plantEscrow)
            {
                if (item is null || item.Stack <= 0 || !seen.Add(item)
                    || !item.modData.TryGetValue(PlantingIdTag, out string? plantingId)
                    || !item.modData.TryGetValue(PlantSourceTag, out string? sourceId)
                    || record.PlantingTransaction is not PlantingTransactionRecord planting
                    || planting.PlantingId != plantingId
                    || !plantItemsBySource.TryAdd(sourceId, item))
                    return InventoryValidationResult.Failure("INVALID-PLANT-ESCROW", $"{record.Identity} has invalid, duplicated, or orphaned Plant Escrow seed responsibility.");
            }
            if (record.PlantingTransaction is PlantingTransactionRecord transaction)
            {
                int consumed = 0;
                foreach (PlantingSourceRecord source in transaction.SourcePlan)
                {
                    int stillEscrowed = plantItemsBySource.TryGetValue(source.SourceId, out Item? item) ? item.Stack : 0;
                    if (item is not null && item.QualifiedItemId != source.QualifiedItemId)
                        return InventoryValidationResult.Failure("PLANT-ESCROW-ITEM-MISMATCH", $"{record.Identity} source {source.SourceId} changed seed identity in Plant Escrow.");
                    int ledgerStill = source.AcquiredQuantity - source.ConsumedQuantity - source.ReturnedQuantity;
                    bool frozenOneSeedDelta = transaction.CurrentStep is PlantingStepRecord step
                        && step.SeedSourceId == source.SourceId
                        && step.Phase is PlantingStepPhases.WorldCommitted or PlantingStepPhases.ReconcilingStep
                        && ledgerStill == stillEscrowed + 1;
                    if (ledgerStill != stillEscrowed && !frozenOneSeedDelta)
                        return InventoryValidationResult.Failure("PLANT-ESCROW-CONSERVATION", $"{record.Identity} source {source.SourceId} violates Acquired = Consumed + Returned + StillInPlantEscrow.");
                    consumed += source.ConsumedQuantity;
                }
                if (plantItemsBySource.Keys.Any(sourceId => transaction.SourcePlan.All(source => source.SourceId != sourceId))
                    || consumed != transaction.PlantedCount)
                    return InventoryValidationResult.Failure("PLANT-ESCROW-LEDGER-MISMATCH", $"{record.Identity} Plant Escrow does not match its source ledger and planted count.");
                if (transaction.Phase is PlantingPhases.Completed or PlantingPhases.Cancelled && plantEscrow.Any(item => item is not null))
                    return InventoryValidationResult.Failure("PLANT-TERMINAL-ESCROW", $"{record.Identity} terminal planting transaction still owns Plant Escrow seeds.");
            }
            else if (plantEscrow.Any(item => item is not null))
                return InventoryValidationResult.Failure("ORPHANED-PLANT-ESCROW", $"{record.Identity} has Plant Escrow seeds without a planting transaction.");
        }
        return InventoryValidationResult.Success($"Validated {records.Count()} isolated Yui bag(s).");
    }

    public InventoryActionResult TryGive(CompanionIdentity identity, Farmer owner, int oneBasedOwnerSlot)
    {
        if (!Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLockHeld())
            return InventoryActionResult.Failure("BAG-LOCK-REQUIRED", "The Yui bag transfer lock is not held; no transfer occurred.");

        int sourceIndex = oneBasedOwnerSlot - 1;
        if (sourceIndex < 0 || sourceIndex >= owner.Items.Count)
            return InventoryActionResult.Failure("INVALID-PLAYER-SLOT", $"Player slot must be between 1 and {owner.Items.Count}.");
        Item? item = owner.Items[sourceIndex];
        if (item is null)
            return InventoryActionResult.Failure("PLAYER-SLOT-EMPTY", $"Player slot {oneBasedOwnerSlot} is empty.");
        if (item.modData.ContainsKey(StorageTags.ResponsibilityId))
            return InventoryActionResult.Failure("STORAGE-RESPONSIBILITY", "A storage-responsible item cannot enter through the ordinary bag transfer path.");

        Inventory bag = this.Get(identity);
        if (this.Count(identity) >= Capacity)
            return InventoryActionResult.Failure("BAG-FULL", $"{identity} already owns {Capacity} real stacks; the player item was not moved.");
        bool added = false;
        try
        {
            bag.Add(item);
            added = true;
            owner.Items[sourceIndex] = null;
            return InventoryActionResult.Success("ITEM-GRANTED", $"Moved exact stack {item.QualifiedItemId} x{item.Stack} from player slot {oneBasedOwnerSlot} to {identity}.");
        }
        catch (Exception ex)
        {
            if (added)
                RemoveExact(bag, item);
            owner.Items[sourceIndex] = item;
            return InventoryActionResult.Failure("GRANT-ROLLED-BACK", $"Grant failed and the exact stack was restored to its player slot: {ex.Message}");
        }
    }

    public InventoryActionResult TryTake(CompanionIdentity identity, Farmer owner, int oneBasedBagSlot)
    {
        if (!Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLockHeld())
            return InventoryActionResult.Failure("BAG-LOCK-REQUIRED", "The Yui bag transfer lock is not held; no transfer occurred.");

        Inventory bag = this.Get(identity);
        int sourceIndex = oneBasedBagSlot - 1;
        if (sourceIndex < 0 || sourceIndex >= bag.Count)
            return InventoryActionResult.Failure("INVALID-BAG-SLOT", $"Bag slot must be between 1 and {bag.Count}.");
        Item? item = bag[sourceIndex];
        if (item is null)
            return InventoryActionResult.Failure("BAG-SLOT-EMPTY", $"Bag slot {oneBasedBagSlot} is empty.");
        if (IsStarterTool(item))
            return InventoryActionResult.Failure("STARTER-TOOL-PROTECTED", "Yui's built-in starter tools cannot be transferred or removed.");
        if (item.modData.TryGetValue(StorageTags.ResponsibilityId, out string? responsibilityId))
            return InventoryActionResult.Failure("STORAGE-RESPONSIBILITY", $"Return storage responsibility {responsibilityId} through the storage command before taking this item.");
        int destinationIndex = FindEmptyOwnerSlot(owner);
        if (destinationIndex < 0)
            return InventoryActionResult.Failure("PLAYER-INVENTORY-FULL", "Taking requires one literal empty player slot; the Yui stack remains untouched.");

        bool placed = false;
        try
        {
            owner.Items[destinationIndex] = item;
            placed = true;
            bag.RemoveAt(sourceIndex);
            return InventoryActionResult.Success("ITEM-TAKEN", $"Moved exact stack {item.QualifiedItemId} x{item.Stack} from {identity} bag slot {oneBasedBagSlot} to player slot {destinationIndex + 1}.");
        }
        catch (Exception ex)
        {
            if (placed)
                owner.Items[destinationIndex] = null;
            if (!bag.Any(candidate => ReferenceEquals(candidate, item)))
                bag.Insert(Math.Min(sourceIndex, bag.Count), item);
            return InventoryActionResult.Failure("TAKE-ROLLED-BACK", $"Take failed and the exact stack was restored to the Yui bag: {ex.Message}");
        }
    }

    public IReadOnlyList<string> Describe(CompanionIdentity identity)
    {
        Inventory bag = this.Get(identity);
        List<string> lines = new();
        for (int index = 0; index < bag.Count; index++)
        {
            Item? item = bag[index];
            if (item is not null)
                lines.Add($"slot={index + 1}, item={item.QualifiedItemId}, stack={item.Stack}, name={item.DisplayName}");
        }
        return lines;
    }

    public InventoryActionResult StoreGeneratedOutput(CompanionIdentity identity, Item item)
    {
        if (!Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLockHeld())
            return InventoryActionResult.Failure("BAG-LOCK-REQUIRED", "Generated output routing requires the Yui bag lock.");
        if (item.Stack <= 0)
            return InventoryActionResult.Failure("INVALID-OUTPUT", "Generated output has no positive quantity.");

        Inventory bag = this.Get(identity);
        Inventory pendingOutputs = this.GetPendingOutputs(identity);
        Inventory destination = this.Count(identity) < Capacity ? bag : pendingOutputs;
        string code = ReferenceEquals(destination, bag) ? "OUTPUT-IN-BAG" : "OUTPUT-PENDING";
        bool pending = ReferenceEquals(destination, pendingOutputs);
        if (pending && !item.modData.ContainsKey(PendingOutputResponsibilityTag))
            item.modData[PendingOutputResponsibilityTag] = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            destination.Add(item);
            return InventoryActionResult.Success(code, $"Stored exact generated stack {item.QualifiedItemId} x{item.Stack}.");
        }
        catch (Exception ex)
        {
            if (destination.Any(candidate => ReferenceEquals(candidate, item)))
                return InventoryActionResult.Success(code, $"Stored exact generated stack {item.QualifiedItemId} x{item.Stack} before the container reported an error.");

            try
            {
                if (pending)
                    item.modData.Remove(PendingOutputResponsibilityTag);
                this.GetRecoveryVault(identity).Add(item);
                return InventoryActionResult.Failure("OUTPUT-IN-RECOVERY", $"Normal output routing failed; the exact stack was retained in Recovery Vault: {ex.Message}");
            }
            catch (Exception recoveryError)
            {
                return InventoryActionResult.Failure("OUTPUT-RESPONSIBILITY-UNKNOWN", $"Both output routing and Recovery Vault failed: {recoveryError.Message}");
            }
        }
    }

    public InventoryActionResult DrainPendingOutputsLocked(CompanionRecord record)
    {
        CompanionIdentity identity = record.Identity;
        if (!Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLockHeld())
            return InventoryActionResult.Failure("BAG-LOCK-REQUIRED", "Pending Output draining requires the Yui bag lock.");

        Inventory bag = this.Get(identity);
        Inventory pending = this.GetPendingOutputs(identity);
        int moved = 0;
        while (this.Count(identity) < Capacity)
        {
            int sourceIndex = pending.ToList().FindIndex(item => item is not null);
            if (sourceIndex < 0)
                break;
            Item item = pending[sourceIndex]!;
            if (!item.modData.TryGetValue(PendingOutputResponsibilityTag, out string? responsibilityId)
                || string.IsNullOrWhiteSpace(responsibilityId))
            {
                responsibilityId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                item.modData[PendingOutputResponsibilityTag] = responsibilityId;
            }

            pending.RemoveAt(sourceIndex);
            try
            {
                bag.Add(item);
            }
            catch (Exception ex)
            {
                if (bag.Any(candidate => ReferenceEquals(candidate, item)))
                {
                    item.modData.Remove(PendingOutputResponsibilityTag);
                    TaskReceiptStore.Add(record, $"pending-drain:{responsibilityId}", true, "PENDING-DRAINED", $"Moved exact stack {item.QualifiedItemId} x{item.Stack} into the Yui bag.");
                    moved++;
                    continue;
                }

                pending.Insert(Math.Min(sourceIndex, pending.Count), item);
                return InventoryActionResult.Failure("PENDING-DRAIN-ROLLED-BACK", $"Pending Output remained responsible after bag insertion failed: {ex.Message}");
            }

            item.modData.Remove(PendingOutputResponsibilityTag);
            TaskReceiptStore.Add(record, $"pending-drain:{responsibilityId}", true, "PENDING-DRAINED", $"Moved exact stack {item.QualifiedItemId} x{item.Stack} into the Yui bag.");
            moved++;
        }

        if (moved == 0)
            return InventoryActionResult.Success("PENDING-UNCHANGED", "No Pending Output could move into the Yui bag.");
        return InventoryActionResult.Success("PENDING-DRAINED", $"Moved {moved} exact Pending Output stack(s) into the Yui bag.");
    }

    public InventoryActionResult CreateDeliveryLocked(CompanionRecord record, string deliveryId, long recipientPlayerId, int oneBasedBagSlot, int quantity, ulong tick)
    {
        CompanionIdentity identity = record.Identity;
        if (!Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLockHeld())
            return InventoryActionResult.Failure("BAG-LOCK-REQUIRED", "Creating delivery Escrow requires the Yui bag lock.");
        if (string.IsNullOrWhiteSpace(deliveryId) || deliveryId.Length > 128 || deliveryId.Any(char.IsControl) || recipientPlayerId == 0 || quantity <= 0)
            return InventoryActionResult.Failure("INVALID-DELIVERY", "DeliveryId, recipient, or quantity is invalid.");

        DeliveryRecord? existing = record.Deliveries.FirstOrDefault(delivery => string.Equals(delivery.DeliveryId, deliveryId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (existing.RecipientPlayerId != recipientPlayerId || existing.Quantity != quantity)
                return InventoryActionResult.Failure("DELIVERY-ID-CONFLICT", "DeliveryId already identifies different immutable cargo details.");
            return existing.Phase == DeliveryPhases.Completed
                ? InventoryActionResult.Success("ALREADY-COMPLETED", $"Delivery {deliveryId} was already completed.")
                : existing.Phase == DeliveryPhases.Returned
                    ? InventoryActionResult.Success("ALREADY-RETURNED", $"Delivery {deliveryId} cargo was already returned.")
                    : InventoryActionResult.Success("ALREADY-ESCROWED", $"Delivery {deliveryId} already owns its cargo in phase {existing.Phase}.");
        }

        Inventory bag = this.Get(identity);
        int sourceIndex = oneBasedBagSlot - 1;
        if (sourceIndex < 0 || sourceIndex >= bag.Count || bag[sourceIndex] is not Item cargo)
            return InventoryActionResult.Failure("INVALID-BAG-SLOT", "The selected Yui bag slot is empty or outside the bag.");
        if (IsStarterTool(cargo) || cargo.modData.ContainsKey(StorageTags.ResponsibilityId) || cargo.modData.ContainsKey(DeliveryCargoTag))
            return InventoryActionResult.Failure("INELIGIBLE-DELIVERY-CARGO", "Protected, borrowed, material-responsible, or already escrowed items cannot start a delivery.");
        if (quantity > cargo.Stack)
            return InventoryActionResult.Failure("INSUFFICIENT-DELIVERY-QUANTITY", $"The selected stack only contains {cargo.Stack} item(s).");

        int originalStack = cargo.Stack;
        Item? remainder = null;
        string cargoToken = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        Inventory escrow = this.GetEscrow(identity);
        try
        {
            if (quantity == originalStack)
                bag.RemoveAt(sourceIndex);
            else
            {
                remainder = cargo.getOne();
                remainder.Stack = originalStack - quantity;
                cargo.Stack = quantity;
                bag[sourceIndex] = remainder;
            }
            cargo.modData[DeliveryCargoTag] = deliveryId;
            cargo.modData[DeliveryCargoTokenTag] = cargoToken;
            escrow.Add(cargo);
            record.Deliveries.Add(new DeliveryRecord
            {
                DeliveryId = deliveryId,
                RecipientPlayerId = recipientPlayerId,
                CargoToken = cargoToken,
                QualifiedItemId = cargo.QualifiedItemId,
                Quantity = cargo.Stack,
                Phase = DeliveryPhases.Escrowed,
                CreatedTick = tick,
            });
            return InventoryActionResult.Success("DELIVERY-ESCROWED", $"Moved exact cargo {cargo.QualifiedItemId} x{cargo.Stack} into delivery {deliveryId} Escrow.");
        }
        catch (Exception ex)
        {
            RemoveExact(escrow, cargo);
            record.Deliveries.RemoveAll(delivery => string.Equals(delivery.DeliveryId, deliveryId, StringComparison.Ordinal));
            cargo.modData.Remove(DeliveryCargoTag);
            cargo.modData.Remove(DeliveryCargoTokenTag);
            cargo.Stack = originalStack;
            if (remainder is not null && sourceIndex < bag.Count)
                bag[sourceIndex] = cargo;
            else
                bag.Insert(Math.Min(sourceIndex, bag.Count), cargo);
            return InventoryActionResult.Failure("DELIVERY-ESCROW-ROLLED-BACK", $"Delivery creation failed and the exact cargo returned to its Yui bag slot: {ex.Message}");
        }
    }

    public InventoryActionResult CompleteDeliveryLocked(CompanionRecord record, string deliveryId, Farmer recipient)
    {
        CompanionIdentity identity = record.Identity;
        if (!Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLockHeld())
            return InventoryActionResult.Failure("BAG-LOCK-REQUIRED", "Completing a delivery requires the Yui bag lock.");
        DeliveryRecord? delivery = record.Deliveries.FirstOrDefault(candidate => string.Equals(candidate.DeliveryId, deliveryId, StringComparison.Ordinal));
        if (delivery is null)
            return InventoryActionResult.Failure("DELIVERY-NOT-FOUND", $"Delivery {deliveryId} does not exist.");
        if (delivery.Phase == DeliveryPhases.Completed)
            return InventoryActionResult.Success("ALREADY-COMPLETED", $"Delivery {deliveryId} was already completed.");
        if (recipient.UniqueMultiplayerID != delivery.RecipientPlayerId)
            return InventoryActionResult.Failure("RECIPIENT-MISMATCH", "The offered Farmer is not the immutable delivery recipient.");

        Inventory escrow = this.GetEscrow(identity);
        int sourceIndex = FindDeliveryCargoIndex(escrow, deliveryId);
        if (sourceIndex < 0 || escrow[sourceIndex] is not Item cargo || cargo.QualifiedItemId != delivery.QualifiedItemId || cargo.Stack != delivery.Quantity)
            return InventoryActionResult.Failure("DELIVERY-CARGO-MISMATCH", "The exact Escrow cargo no longer matches its delivery record.");
        int destinationIndex = FindEmptyOwnerSlot(recipient);
        if (destinationIndex < 0)
            return InventoryActionResult.Failure("RECIPIENT-INVENTORY-FULL", "The recipient has no literal empty slot; cargo remains in Escrow.");

        recipient.Items[destinationIndex] = cargo;
        try
        {
            escrow.RemoveAt(sourceIndex);
        }
        catch (Exception ex)
        {
            recipient.Items[destinationIndex] = null;
            return InventoryActionResult.Failure("DELIVERY-COMMIT-ROLLED-BACK", $"The recipient placement was rolled back and cargo remains in Escrow: {ex.Message}");
        }
        cargo.modData.Remove(DeliveryCargoTag);
        cargo.modData.Remove(DeliveryCargoTokenTag);
        delivery.Phase = DeliveryPhases.Completed;
        delivery.LastFailure = null;
        TaskReceiptStore.Add(record, $"delivery:{deliveryId}", true, "DELIVERY-COMPLETED", $"Recipient {recipient.UniqueMultiplayerID} received exact cargo {cargo.QualifiedItemId} x{cargo.Stack}.");
        return InventoryActionResult.Success("DELIVERY-COMPLETED", $"Recipient received exact cargo {cargo.QualifiedItemId} x{cargo.Stack}.");
    }

    public InventoryActionResult ReturnDeliveryLocked(CompanionRecord record, string deliveryId)
    {
        CompanionIdentity identity = record.Identity;
        if (!Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLockHeld())
            return InventoryActionResult.Failure("BAG-LOCK-REQUIRED", "Returning delivery cargo requires the Yui bag lock.");
        DeliveryRecord? delivery = record.Deliveries.FirstOrDefault(candidate => string.Equals(candidate.DeliveryId, deliveryId, StringComparison.Ordinal));
        if (delivery is null)
            return InventoryActionResult.Failure("DELIVERY-NOT-FOUND", $"Delivery {deliveryId} does not exist.");
        if (delivery.Phase == DeliveryPhases.Returned)
            return InventoryActionResult.Success("ALREADY-RETURNED", $"Delivery {deliveryId} cargo was already returned.");
        if (delivery.Phase == DeliveryPhases.Completed)
            return InventoryActionResult.Failure("DELIVERY-ALREADY-COMPLETED", "Completed cargo cannot be returned from the recipient.");

        Inventory escrow = this.GetEscrow(identity);
        int sourceIndex = FindDeliveryCargoIndex(escrow, deliveryId);
        if (sourceIndex < 0 || escrow[sourceIndex] is not Item cargo)
            return InventoryActionResult.Failure("DELIVERY-CARGO-MISSING", "Delivery cargo is not present in Escrow.");
        if (this.Count(identity) >= Capacity)
        {
            delivery.Phase = DeliveryPhases.Returning;
            delivery.LastFailure = "Yui bag is full.";
            return InventoryActionResult.Failure("DELIVERY-RETURN-PENDING", "Yui's bag is full; cargo remains in Escrow with Returning responsibility.");
        }

        escrow.RemoveAt(sourceIndex);
        try
        {
            this.Get(identity).Add(cargo);
        }
        catch (Exception ex)
        {
            if (this.Get(identity).Any(candidate => ReferenceEquals(candidate, cargo)))
            {
                cargo.modData.Remove(DeliveryCargoTag);
                cargo.modData.Remove(DeliveryCargoTokenTag);
                delivery.Phase = DeliveryPhases.Returned;
                delivery.LastFailure = null;
                TaskReceiptStore.Add(record, $"delivery-return:{deliveryId}", true, "DELIVERY-RETURNED", $"Returned exact cargo {cargo.QualifiedItemId} x{cargo.Stack} to the Yui bag.");
                return InventoryActionResult.Success("DELIVERY-RETURNED", $"Returned exact cargo {cargo.QualifiedItemId} x{cargo.Stack} before the bag reported an error.");
            }
            escrow.Insert(Math.Min(sourceIndex, escrow.Count), cargo);
            delivery.Phase = DeliveryPhases.Returning;
            delivery.LastFailure = ex.Message;
            return InventoryActionResult.Failure("DELIVERY-RETURN-ROLLED-BACK", "Cargo remains in Escrow after the bag return failed.");
        }
        cargo.modData.Remove(DeliveryCargoTag);
        cargo.modData.Remove(DeliveryCargoTokenTag);
        delivery.Phase = DeliveryPhases.Returned;
        delivery.LastFailure = null;
        TaskReceiptStore.Add(record, $"delivery-return:{deliveryId}", true, "DELIVERY-RETURNED", $"Returned exact cargo {cargo.QualifiedItemId} x{cargo.Stack} to the Yui bag.");
        return InventoryActionResult.Success("DELIVERY-RETURNED", $"Returned exact cargo {cargo.QualifiedItemId} x{cargo.Stack} to the Yui bag.");
    }

    public void RequestTransfer(
        CompanionIdentity identity,
        Func<InventoryActionResult> transfer,
        Action<InventoryActionResult> completed)
    {
        var mutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity));
        mutex.RequestLock(
            acquired: () =>
            {
                InventoryActionResult result;
                try
                {
                    result = transfer();
                }
                catch (Exception ex)
                {
                    result = InventoryActionResult.Failure("TRANSFER-FAILED", $"The locked transfer failed without completing: {ex.Message}");
                }
                finally
                {
                    mutex.ReleaseLock();
                }
                completed(result);
            },
            failed: () => completed(InventoryActionResult.Failure("BAG-LOCK-FAILED", "The Yui bag transfer lock could not be acquired; no transfer occurred."))
        );
    }

    public void RunWithMeleeWeaponSelected(CompanionIdentity identity, Farmer owner, MeleeWeapon weapon, Action action)
    {
        Inventory bag = this.Get(identity);
        int bagIndex = FindExactIndex(bag, weapon);
        if (bagIndex < 0)
            throw new InvalidOperationException("The exact melee weapon is no longer in the Yui bag.");
        if (Game1.player.team.GetOrCreateGlobalInventoryMutex(GetNamespace(identity)).IsLocked())
            throw new InvalidOperationException("The Yui bag mutex became locked before the vanilla swing.");
        if (owner.Items.Count == 0)
            throw new InvalidOperationException("The acting owner has no inventory slot for the synchronous weapon lease.");

        int originalToolIndex = owner.CurrentToolIndex;
        int ownerIndex = originalToolIndex >= 0 && originalToolIndex < owner.Items.Count ? originalToolIndex : 0;
        Item? displaced = owner.Items[ownerIndex];
        bool swapped = false;
        try
        {
            owner.Items[ownerIndex] = weapon;
            bag[bagIndex] = displaced;
            owner.CurrentToolIndex = ownerIndex;
            swapped = true;
            action();
        }
        finally
        {
            if (swapped)
            {
                bag[bagIndex] = weapon;
                owner.Items[ownerIndex] = displaced;
            }
            owner.CurrentToolIndex = originalToolIndex;
        }
    }

    public static string GetNamespace(CompanionIdentity identity) => string.Create(
        CultureInfo.InvariantCulture,
        $"{NamespacePrefix}/{identity.OwnerId}/{identity.Slot}"
    );

    public static string GetPendingOutputNamespace(CompanionIdentity identity) => string.Create(
        CultureInfo.InvariantCulture,
        $"{PendingOutputPrefix}/{identity.OwnerId}/{identity.Slot}"
    );

    public static string GetRecoveryVaultNamespace(CompanionIdentity identity) => string.Create(
        CultureInfo.InvariantCulture,
        $"{RecoveryVaultPrefix}/{identity.OwnerId}/{identity.Slot}"
    );

    public static string GetEscrowNamespace(CompanionIdentity identity) => string.Create(
        CultureInfo.InvariantCulture,
        $"{EscrowPrefix}/{identity.OwnerId}/{identity.Slot}"
    );

    public static string GetCraftEscrowNamespace(CompanionIdentity identity) => string.Create(
        CultureInfo.InvariantCulture,
        $"{CraftEscrowPrefix}/{identity.OwnerId}/{identity.Slot}"
    );

    public static string GetPlantEscrowNamespace(CompanionIdentity identity) => string.Create(
        CultureInfo.InvariantCulture,
        $"{PlantEscrowPrefix}/{identity.OwnerId}/{identity.Slot}"
    );

    public void RemoveStarterTools(CompanionIdentity identity)
    {
        Inventory bag = this.Get(identity);
        for (int index = bag.Count - 1; index >= 0; index--)
        {
            if (bag[index] is Item item && IsStarterTool(item))
                bag.RemoveAt(index);
        }
    }

    private Inventory GetPendingOutputs(CompanionIdentity identity) => Game1.player.team.GetOrCreateGlobalInventory(GetPendingOutputNamespace(identity));

    private Inventory GetRecoveryVault(CompanionIdentity identity) => Game1.player.team.GetOrCreateGlobalInventory(GetRecoveryVaultNamespace(identity));

    internal Inventory GetEscrow(CompanionIdentity identity) => Game1.player.team.GetOrCreateGlobalInventory(GetEscrowNamespace(identity));

    internal Inventory GetCraftEscrow(CompanionIdentity identity) => Game1.player.team.GetOrCreateGlobalInventory(GetCraftEscrowNamespace(identity));

    internal Inventory GetPlantEscrow(CompanionIdentity identity) => Game1.player.team.GetOrCreateGlobalInventory(GetPlantEscrowNamespace(identity));

    private static bool IsStarterTool(Item item) => item.modData.ContainsKey(StarterToolTag);

    private static bool MatchesStarterKind(Item item, string kind) => kind switch
    {
        "Scythe" => item is MeleeWeapon weapon && weapon.isScythe(),
        "Axe" => item is Axe,
        "Pickaxe" => item is Pickaxe,
        "WateringCan" => item is WateringCan,
        "Hoe" => item is Hoe,
        _ => false,
    };

    private static Tool CreateStarterTool(string kind) => kind switch
    {
        "Scythe" => new MeleeWeapon("47"),
        "Axe" => new Axe(),
        "Pickaxe" => new Pickaxe(),
        "WateringCan" => new WateringCan(),
        "Hoe" => new Hoe(),
        _ => throw new InvalidOperationException($"Unknown starter tool kind {kind}."),
    };

    private static int FindEmptyOwnerSlot(Farmer owner)
    {
        for (int index = 0; index < owner.Items.Count; index++)
        {
            if (owner.Items[index] is null)
                return index;
        }
        return -1;
    }

    private static int FindExactIndex(IList<Item> items, Item target)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], target))
                return index;
        }
        return -1;
    }

    private static void RemoveExact(IList<Item> items, Item target)
    {
        int index = FindExactIndex(items, target);
        if (index >= 0)
            items.RemoveAt(index);
    }

    private static int FindDeliveryCargoIndex(IList<Item> items, string deliveryId)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index] is Item item
                && item.modData.TryGetValue(DeliveryCargoTag, out string? taggedId)
                && string.Equals(taggedId, deliveryId, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

}
