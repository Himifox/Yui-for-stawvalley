using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Inventories;
using StardewValley.Network;
using StardewValley.Objects;
using StardewValley.Pathfinding;

namespace YuiToIssho;

internal readonly record struct CraftActionResult(bool IsSuccess, string Code, string Message)
{
    public static CraftActionResult Success(string code, string message) => new(true, code, message);
    public static CraftActionResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class CraftingCoordinator
{
    private readonly CompanionRegistry registry;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionStorageCoordinator storage;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly CraftingRecipePolicy policy = new();
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, ChestCraftSession> chestSessions = new();
    private readonly HashSet<CompanionIdentity> reconciliationPending = new();
    private readonly Dictionary<CompanionIdentity, PendingBagCraftStart> pendingBagStarts = new();
    private ulong lifecycleGeneration;
    private ulong currentTick;

    public CraftingCoordinator(CompanionRegistry registry, CompanionInventoryStore inventories, CompanionBodyBinder bodies, CompanionStorageCoordinator storage, CompanionAppearanceCoordinator appearance, IMonitor monitor)
    {
        this.registry = registry;
        this.inventories = inventories;
        this.bodies = bodies;
        this.storage = storage;
        this.appearance = appearance;
        this.monitor = monitor;
    }

    public CraftActionResult List(CompanionIdentity identity, Farmer owner)
    {
        if (!this.registry.TryGet(identity, out _))
            return CraftActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        IReadOnlyList<string> keys = this.policy.ListAvailable(owner);
        return CraftActionResult.Success("CRAFT-LIST", keys.Count == 0
            ? "The Owner has no learned recipe in the current safe crafting allowlist."
            : $"Available ({keys.Count}): {string.Join(", ", keys)}");
    }

    public CraftActionResult Preview(CompanionIdentity identity, Farmer owner, string recipeKey, int craftCount)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord? record))
            return CraftActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (craftCount is < 1 or > 25)
            return CraftActionResult.Failure("INVALID-CRAFT-COUNT", "Craft count must be from 1 through 25.");
        CraftRecipeResolution resolution = this.policy.TryResolve(owner, recipeKey);
        if (!resolution.IsSuccess || resolution.Recipe is null)
            return CraftActionResult.Failure(resolution.Code, resolution.Message);

        CraftRecipeDescriptor recipe = resolution.Recipe;
        Inventory bag = this.inventories.Get(identity);
        List<string> requirements = new();
        bool enough = true;
        foreach (CraftIngredientRecord ingredient in recipe.Ingredients)
        {
            int required = checked(ingredient.RequiredPerCraft * craftCount);
            int available = bag.Where(IsUnreservedMaterial).Where(item => CraftingRecipePolicy.Matches(item, ingredient.IngredientId)).Sum(item => item.Stack);
            requirements.Add($"{ingredient.IngredientId} {available}/{required}");
            enough &= available >= required;
        }
        string busy = record.CraftTransaction is null ? "idle" : $"busy:{record.CraftTransaction.Phase}";
        return CraftActionResult.Success("CRAFT-PREVIEW", $"{recipeKey} x{craftCount} => {recipe.OutputQualifiedItemId} x{checked(recipe.OutputPerCraft * craftCount)}; bag materials [{string.Join("; ", requirements)}]; {(enough ? "bag-ready" : "authorized-chest-materials-needed")}; {busy}.");
    }

    public CraftActionResult Status(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord? record))
            return CraftActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        CraftTransactionRecord? craft = record.CraftTransaction;
        return craft is null
            ? CraftActionResult.Success("CRAFT-IDLE", $"{identity} has no crafting responsibility; Craft Escrow={this.inventories.CraftEscrowCount(identity)}.")
            : CraftActionResult.Success("CRAFT-STATUS", $"{craft.RecipeKey} {craft.CompletedCount}/{craft.CraftCount}, phase={craft.Phase}, escrow={this.inventories.CraftEscrowCount(identity)}, output={craft.OutputLocation ?? "none"}, token={craft.OutputToken ?? "none"}, last={craft.LastFailure ?? "none"}.");
    }

    public CraftActionResult Start(CompanionIdentity identity, Farmer owner, string recipeKey, int craftCount, string operationId)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord? record))
            return CraftActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (craftCount is < 1 or > 25)
            return CraftActionResult.Failure("INVALID-CRAFT-COUNT", "Craft count must be from 1 through 25.");
        if (!IsBoundedOperationId(operationId))
            return CraftActionResult.Failure("INVALID-OPERATION-ID", "OperationId must contain 1 to 128 non-control characters.");
        if (TaskReceiptStore.TryGet(record, operationId, out TaskExecutionResult receipt))
            return new CraftActionResult(receipt.IsSuccess, receipt.Code, receipt.Message);
        if (record.CraftTransaction is not null)
            return record.CraftTransaction.OperationId == operationId
                ? CraftActionResult.Success("CRAFT-ALREADY-ACTIVE", $"Craft {record.CraftTransaction.CraftId} is already {record.CraftTransaction.Phase}.")
                : CraftActionResult.Failure("CRAFT-BUSY", $"{identity} already owns craft {record.CraftTransaction.CraftId}.");
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return CraftActionResult.Failure("COMPANION-BUSY", $"{identity} is already executing {record.ActiveTransactionId}.");
        if (this.pendingBagStarts.ContainsKey(identity))
            return CraftActionResult.Failure("CRAFT-START-PENDING", "A crafting request is already waiting for the Yui bag lock.");

        CraftRecipeResolution resolution = this.policy.TryResolve(owner, recipeKey);
        if (!resolution.IsSuccess || resolution.Recipe is null)
            return CraftActionResult.Failure(resolution.Code, resolution.Message);
        CraftRecipeDescriptor recipe = resolution.Recipe;
        if (!HasMaterials(this.inventories.Get(identity), recipe.Ingredients, craftCount))
            return this.StartChestCraft(record, owner, recipe, craftCount, operationId);
        string craftId = Guid.NewGuid().ToString("N");
        var transaction = new CraftTransactionRecord
        {
            CraftId = craftId,
            OperationId = operationId,
            OwnerId = identity.OwnerId,
            RecipeKey = recipeKey,
            RecipePolicyVersion = CraftingRecipePolicy.Version,
            CraftCount = craftCount,
            RecipeSnapshot = Snapshot(recipe),
            Phase = CraftPhases.Planned,
            CreatedDay = Game1.Date.TotalDays,
        };
        if (this.bodies.TryGetBody(identity, out NPC craftBody))
            this.appearance.Prepare(identity, operationId, AppearanceActionKinds.Crafting, null, craftBody.FacingDirection);

        var pendingStart = new PendingBagCraftStart(record, operationId, this.lifecycleGeneration);
        this.pendingBagStarts.Add(identity, pendingStart);

        CraftActionResult immediate = CraftActionResult.Success("CRAFT-LOCK-QUEUED", $"Craft {craftId} is waiting for the Yui bag lock.");
        this.inventories.RequestTransfer(identity, () =>
        {
            if (!this.IsCurrentPendingStart(identity, pendingStart))
                return InventoryActionResult.Failure("CRAFT-START-CANCELLED", "The queued crafting request was cancelled before the Yui bag lock was acquired.");
            CraftActionResult result = this.CommitSingleBagCraft(record, owner, recipe, transaction);
            return new InventoryActionResult(result.IsSuccess, result.Code, result.Message);
        }, result =>
        {
            this.RemovePendingStartIfCurrent(identity, pendingStart);
            if (!result.IsSuccess && result.Code != "CRAFT-START-CANCELLED")
                this.monitor.Log($"HY-CRAFT-{result.Code}: {identity} {result.Message}", LogLevel.Warn);
        });
        return immediate;
    }

    public void Update(ulong tick)
    {
        this.currentTick = tick;
        foreach (ChestCraftSession session in this.chestSessions.Values.ToArray())
            if (OwnerLifecycleGate.CanAdvance(session.Owner))
                this.UpdateChestSession(session);
        foreach (CompanionRecord record in this.registry.All.Where(record => record.CraftTransaction is not null && !this.chestSessions.ContainsKey(record.Identity)).ToArray())
            if (OwnerLifecycleGate.CanAdvance(record.Identity))
                this.TryReconcilePersistedOutput(record);
    }

    public void RestoreAfterLoad()
    {
        foreach (CompanionRecord record in this.registry.All.Where(record => record.CraftTransaction is not null))
        {
            CraftTransactionRecord craft = record.CraftTransaction!;
            if (CraftPhases.OwnsResponsibility(craft.Phase) && craft.OutputToken is null)
            {
                craft.Phase = CraftPhases.ReturningMaterials;
                craft.LastFailure = "RECOVERED-WITHOUT-RUNTIME-SOURCE-REFERENCES";
                this.inventories.RequestTransfer(record.Identity, () =>
                {
                    this.ReturnEscrowToBag(record, "CRAFT-RECOVERED-CANCELLED");
                    return InventoryActionResult.Success("CRAFT-RECOVERED-CANCELLED", "Recovered pre-output craft materials into a responsible local container.");
                }, _ => { });
            }
        }
    }

    public void SuspendAll(string reason)
    {
        this.lifecycleGeneration++;
        foreach ((CompanionIdentity identity, PendingBagCraftStart pending) in this.pendingBagStarts.ToArray())
        {
            TaskReceiptStore.Add(pending.Record, pending.OperationId, false, "CRAFT-CANCELLED", $"Craft was cancelled before the Yui bag lock was acquired ({reason}).");
            this.appearance.Clear(identity, reason);
        }
        this.pendingBagStarts.Clear();
        foreach (ChestCraftSession session in this.chestSessions.Values.ToArray())
            this.FailChestSession(session, reason);
        this.chestSessions.Clear();
        this.reconciliationPending.Clear();
    }

    public CraftActionResult Cancel(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord? record))
            return CraftActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (this.pendingBagStarts.TryGetValue(identity, out PendingBagCraftStart? pending)
            && ReferenceEquals(pending.Record, record))
        {
            this.pendingBagStarts.Remove(identity);
            this.appearance.Clear(identity, "PLAYER-CANCELLED");
            TaskReceiptStore.Add(record, pending.OperationId, false, "CRAFT-CANCELLED", "Craft was cancelled before the Yui bag lock was acquired.");
            return CraftActionResult.Success("CRAFT-CANCELLED", "The crafting request waiting for the Yui bag lock was cancelled.");
        }
        if (record.CraftTransaction is null)
            return CraftActionResult.Success("CRAFT-IDLE", "There is no active crafting responsibility to cancel.");
        if (!CraftPhases.CanCancel(record.CraftTransaction.Phase))
            return CraftActionResult.Failure("CRAFT-COMMIT-BOUNDARY", "The output already exists; this craft must reconcile instead of being cancelled.");
        if (this.chestSessions.TryGetValue(identity, out ChestCraftSession? session))
        {
            this.FailChestSession(session, "PLAYER-CANCELLED");
            return CraftActionResult.Success("CRAFT-CANCELLED", "Craft acquisition stopped; reserved materials were retained in the Yui bag or Craft Escrow.");
        }
        this.inventories.RequestTransfer(identity, () =>
        {
            this.ReturnEscrowToBag(record, "PLAYER-CANCELLED");
            return InventoryActionResult.Success("CRAFT-CANCELLED", "Craft cancellation reconciled its retained materials.");
        }, _ => { });
        return CraftActionResult.Success("CRAFT-CANCEL-QUEUED", "Craft cancellation is waiting for the Yui bag lock.");
    }

    public InventoryValidationResult Validate()
    {
        foreach (CompanionRecord record in this.registry.All)
        {
            if (record.CraftTransaction is CraftTransactionRecord craft
                && CraftPhases.OwnsResponsibility(craft.Phase)
                && craft.RecipePolicyVersion > CraftingRecipePolicy.Version)
                return InventoryValidationResult.Failure("FUTURE-CRAFT-POLICY", $"{record.Identity} uses unsupported craft policy {craft.RecipePolicyVersion}.");
        }
        return InventoryValidationResult.Success("Crafting responsibilities use the current bounded contract.");
    }

    private static bool IsUnreservedMaterial(Item item) => item.Stack > 0
        && !item.modData.ContainsKey(StorageTags.ResponsibilityId)
        && !item.modData.ContainsKey(CompanionInventoryStore.DeliveryCargoTag)
        && !item.modData.ContainsKey(CompanionInventoryStore.CraftIdTag);

    private CraftActionResult CommitSingleBagCraft(CompanionRecord record, Farmer owner, CraftRecipeDescriptor recipe, CraftTransactionRecord transaction)
    {
        CompanionIdentity identity = record.Identity;
        if (!OwnerLifecycleGate.CanAdvance(owner))
            return CraftActionResult.Failure("OWNER-BUSY", "This Yui's Owner became busy before the bag lock was acquired; materials were unchanged.");
        if (record.CraftTransaction is not null || !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return CraftActionResult.Failure("COMPANION-BUSY", "The companion acquired another transaction before the bag lock.");
        Inventory bag = this.inventories.Get(identity);
        Inventory escrow = this.inventories.GetCraftEscrow(identity);
        if (escrow.Any(item => item is not null))
            return CraftActionResult.Failure("CRAFT-ESCROW-NOT-EMPTY", "Craft Escrow contains an unresolved responsibility.");

        List<BagReservation> reservations = new();
        try
        {
            foreach (CraftIngredientRecord ingredient in recipe.Ingredients)
            {
                int remaining = checked(ingredient.RequiredPerCraft * transaction.CraftCount);
                for (int index = 0; index < bag.Count && remaining > 0; index++)
                {
                    Item? source = bag[index];
                    if (source is null || !IsUnreservedMaterial(source) || !CraftingRecipePolicy.Matches(source, ingredient.IngredientId))
                        continue;
                    int take = Math.Min(remaining, source.Stack);
                    int originalIndex = index;
                    Item reserved;
                    bool whole = take == source.Stack;
                    if (whole)
                    {
                        reserved = source;
                        bag.RemoveAt(index--);
                    }
                    else
                    {
                        reserved = source.getOne();
                        reserved.Stack = take;
                        source.Stack -= take;
                    }
                    string sourceToken = Guid.NewGuid().ToString("N");
                    reserved.modData[CompanionInventoryStore.CraftIdTag] = transaction.CraftId;
                    reserved.modData[CompanionInventoryStore.CraftSourceTag] = sourceToken;
                    escrow.Add(reserved);
                    transaction.SourcePlan.Add(new CraftSourceRecord
                    {
                        SourceKind = CraftSourceKinds.Bag,
                        StorageId = CompanionInventoryStore.GetNamespace(identity),
                        SourceSlot = originalIndex,
                        ItemFingerprint = Fingerprint(reserved),
                        QualifiedItemId = reserved.QualifiedItemId,
                        Quantity = take,
                        Acquired = true,
                    });
                    reservations.Add(new BagReservation(source, reserved, originalIndex, whole));
                    remaining -= take;
                }
                if (remaining > 0)
                {
                    RollBackBagReservations(bag, escrow, reservations);
                    return CraftActionResult.Failure("CRAFT-MATERIALS-MISSING", $"The Yui bag is missing {ingredient.IngredientId} x{remaining}; no material was consumed.");
                }
            }

            record.CraftTransaction = transaction;
            record.ActiveTransactionId = $"craft:{transaction.CraftId}";
            transaction.Phase = CraftPhases.MaterialsEscrowed;
            CraftRecipeResolution current = this.policy.TryResolve(owner, transaction.RecipeKey);
            if (!current.IsSuccess || current.Recipe is null || !CraftingRecipePolicy.SnapshotMatches(current.Recipe, transaction.RecipeSnapshot))
                throw new InvalidOperationException("Recipe changed after materials were reserved.");
            transaction.Phase = CraftPhases.CommitReady;

            string lastLocation = string.Empty;
            if (this.bodies.TryGetBody(identity, out NPC craftBody))
                this.appearance.Prepare(identity, transaction.OperationId, AppearanceActionKinds.Crafting, null, craftBody.FacingDirection);
            for (int child = 0; child < transaction.CraftCount; child++)
            {
                Item output = current.Recipe.Recipe.createItem();
                string outputToken = Guid.NewGuid().ToString("N");
                output.modData[CompanionInventoryStore.CraftIdTag] = transaction.CraftId;
                output.modData[CompanionInventoryStore.CraftOutputTokenTag] = outputToken;
                InventoryActionResult routed = this.inventories.StoreGeneratedOutput(identity, output);
                if (routed.Code == "OUTPUT-RESPONSIBILITY-UNKNOWN")
                {
                    transaction.Phase = CraftPhases.Faulted;
                    transaction.LastFailure = routed.Code;
                    record.ActiveTransactionId = null;
                    return CraftActionResult.Failure("CRAFT-OUTPUT-UNKNOWN", $"Output {child + 1} could not prove a unique responsibility location; crafting is faulted.");
                }
                transaction.OutputToken = outputToken;
                transaction.OutputTokens.Add(outputToken);
                transaction.OutputLocation = lastLocation = routed.Code;
                transaction.CompletedCount = child + 1;
                transaction.Phase = CraftPhases.OutputCreated;
                this.appearance.Commit(identity, transaction.OperationId);
            }

            escrow.Clear();
            transaction.Phase = CraftPhases.MaterialsConsumed;
            ApplyOwnerProgress(owner, transaction);
            if (!this.TryReleaseOutputResponsibility(identity, transaction, out string releaseFailure))
            {
                transaction.Phase = CraftPhases.Reconciling;
                transaction.LastFailure = releaseFailure;
                record.ActiveTransactionId = null;
                return CraftActionResult.Failure("CRAFT-OUTPUT-RECONCILE", "Craft output exists, but its terminal responsibility tags could not be released safely.");
            }
            transaction.Phase = CraftPhases.Completed;
            record.ActiveTransactionId = null;
            TaskReceiptStore.Add(record, transaction.OperationId, true, "CRAFT-COMPLETED", $"Crafted {transaction.RecipeKey} {transaction.CompletedCount}/{transaction.CraftCount}; outputs ended in {lastLocation}.");
            record.CraftTransaction = null;
            return CraftActionResult.Success("CRAFT-COMPLETED", $"Crafted {transaction.RecipeKey} {transaction.CompletedCount}/{transaction.CraftCount}; output={transaction.OutputLocation}.");
        }
        catch (Exception ex)
        {
            if (transaction.OutputToken is null)
            {
                RollBackBagReservations(bag, escrow, reservations);
                record.CraftTransaction = null;
                record.ActiveTransactionId = null;
                TaskReceiptStore.Add(record, transaction.OperationId, false, "CRAFT-ROLLED-BACK", "Craft stopped before output creation and the exact reserved materials were restored.");
                return CraftActionResult.Failure("CRAFT-ROLLED-BACK", $"Craft stopped before output creation and materials were restored: {ex.GetType().Name}.");
            }
            if (transaction.OutputTokens.Count == transaction.CompletedCount && transaction.CompletedCount > 0
                && this.TryCompletePartial(record, owner, transaction, ex.GetType().Name))
                return CraftActionResult.Failure("CRAFT-PARTIAL", $"Crafted {transaction.CompletedCount}/{transaction.CraftCount}; later child creation stopped safely.");
            transaction.Phase = CraftPhases.Faulted;
            transaction.LastFailure = ex.GetType().Name;
            record.ActiveTransactionId = null;
            return CraftActionResult.Failure("CRAFT-FAULTED", "Output exists but final reconciliation stopped; the persisted transaction prevents duplicate output.");
        }
    }

    private static CraftRecipeSnapshot Snapshot(CraftRecipeDescriptor recipe) => new()
    {
        Ingredients = recipe.Ingredients.Select(item => new CraftIngredientRecord { IngredientId = item.IngredientId, RequiredPerCraft = item.RequiredPerCraft }).ToList(),
        OutputQualifiedItemId = recipe.OutputQualifiedItemId,
        OutputPerCraft = recipe.OutputPerCraft,
    };

    private static string Fingerprint(Item item) => $"{item.QualifiedItemId}|{item.Quality}|{string.Join(";", item.modData.Pairs.Where(pair => pair.Key != CompanionInventoryStore.CraftIdTag && pair.Key != CompanionInventoryStore.CraftSourceTag).OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}";

    private static void RollBackBagReservations(Inventory bag, Inventory escrow, IEnumerable<BagReservation> reservations)
    {
        foreach (BagReservation reservation in reservations.Reverse())
        {
            escrow.Remove(reservation.Reserved);
            reservation.Reserved.modData.Remove(CompanionInventoryStore.CraftIdTag);
            reservation.Reserved.modData.Remove(CompanionInventoryStore.CraftSourceTag);
            if (reservation.Whole)
                bag.Insert(Math.Clamp(reservation.OriginalIndex, 0, bag.Count), reservation.Reserved);
            else
                reservation.Original.Stack += reservation.Reserved.Stack;
        }
    }

    private static bool IsBoundedOperationId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(character => !char.IsControl(character));

    private sealed record BagReservation(Item Original, Item Reserved, int OriginalIndex, bool Whole);

    private CraftActionResult StartChestCraft(CompanionRecord record, Farmer owner, CraftRecipeDescriptor recipe, int craftCount, string operationId)
    {
        CompanionIdentity identity = record.Identity;
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return CraftActionResult.Failure("CRAFT-BODY-REQUIRED", "Authorized-chest crafting requires a summoned Yui body.");
        IReadOnlyList<CraftChestAccess> chests = this.storage.GetCraftingChests(identity, body);
        if (!TryPlanSources(this.inventories.Get(identity), chests, recipe.Ingredients, craftCount, out List<PlannedCraftSource> sources, out string missing))
            return CraftActionResult.Failure("CRAFT-MATERIALS-MISSING", $"The complete bag + authorized-chest plan is missing {missing}; nothing moved.");

        string craftId = Guid.NewGuid().ToString("N");
        var transaction = new CraftTransactionRecord
        {
            CraftId = craftId,
            OperationId = operationId,
            OwnerId = identity.OwnerId,
            RecipeKey = recipe.RecipeKey,
            RecipePolicyVersion = CraftingRecipePolicy.Version,
            CraftCount = craftCount,
            RecipeSnapshot = Snapshot(recipe),
            Phase = CraftPhases.Planned,
            CreatedDay = Game1.Date.TotalDays,
            UpdatedTick = this.currentTick,
            SourcePlan = sources.Select(source => source.Record).ToList(),
        };
        record.CraftTransaction = transaction;
        record.ActiveTransactionId = $"craft:{craftId}";
        var session = new ChestCraftSession(record, owner, recipe, transaction, sources);
        this.chestSessions.Add(identity, session);
        this.appearance.Prepare(identity, operationId, AppearanceActionKinds.Crafting, null, body.FacingDirection);

        this.inventories.RequestTransfer(identity, () => this.AcquireBagSources(session), result =>
        {
            if (!result.IsSuccess)
                this.FailChestSession(session, result.Code);
        });
        return CraftActionResult.Success("CRAFT-STARTED", $"Craft {craftId} planned {sources.Count} exact source stack(s); Yui will visit {sources.Where(source => source.Chest.HasValue).Select(source => source.Chest!.Value.Authorization.ChestToken).Distinct().Count()} authorized chest(s).");
    }

    private InventoryActionResult AcquireBagSources(ChestCraftSession session)
    {
        Inventory bag = this.inventories.Get(session.Record.Identity);
        Inventory escrow = this.inventories.GetCraftEscrow(session.Record.Identity);
        foreach (PlannedCraftSource source in session.Sources.Where(source => !source.Chest.HasValue))
        {
            int index = FindExactIndex(bag, source.Item);
            if (index < 0 || source.Item.Stack != source.ExpectedStack || !IsUnreservedMaterial(source.Item))
                return InventoryActionResult.Failure("CRAFT-BAG-SOURCE-CHANGED", "A planned bag material changed before locked acquisition.");
            MoveToEscrow(session.Transaction.CraftId, source, bag, escrow, index);
        }
        session.Transaction.Phase = CraftPhases.AcquiringMaterials;
        session.Transaction.UpdatedTick = this.currentTick;
        return InventoryActionResult.Success("CRAFT-BAG-ACQUIRED", "Planned Yui bag materials moved into Craft Escrow.");
    }

    private void UpdateChestSession(ChestCraftSession session)
    {
        if (!this.chestSessions.ContainsKey(session.Record.Identity) || session.Record.CraftTransaction != session.Transaction)
            return;
        PlannedCraftSource? next = session.Sources.FirstOrDefault(source => !source.Record.Acquired && source.Chest.HasValue);
        if (next is null)
        {
            if (!session.CommitRequested)
            {
                session.CommitRequested = true;
                this.inventories.RequestTransfer(session.Record.Identity, () =>
                {
                    CraftActionResult result = this.CommitEscrowed(session);
                    return new InventoryActionResult(result.IsSuccess, result.Code, result.Message);
                }, result =>
                {
                    if (result.Code == "OWNER-BUSY" && this.chestSessions.ContainsKey(session.Record.Identity))
                    {
                        session.CommitRequested = false;
                        return;
                    }
                    if (!result.IsSuccess)
                        this.monitor.Log($"HY-CRAFT-{result.Code}: {session.Record.Identity} {result.Message}", LogLevel.Warn);
                    this.chestSessions.Remove(session.Record.Identity);
                });
            }
            return;
        }

        CraftChestAccess access = next.Chest!.Value;
        if (!CompanionStorageCoordinator.IsCurrentCraftChest(access, session.Record.OwnerId))
        {
            this.FailChestSession(session, "CRAFT-CHEST-CHANGED");
            return;
        }
        if (!this.bodies.TryGetBody(session.Record.Identity, out NPC body) || !ReferenceEquals(body.currentLocation, access.Location))
        {
            this.FailChestSession(session, "CRAFT-BODY-LOCATION-CHANGED");
            return;
        }
        if (Manhattan(body.Tile, access.Tile) == 1)
        {
            body.controller = null;
            body.Halt();
            body.faceDirection(TaskNavigationService.FacingToward(body.Tile, access.Tile));
            if (!session.LockRequested)
                this.RequestChestLocks(session, access);
            return;
        }
        if (session.LockRequested || body.controller is not null || this.currentTick < session.NextPathTick)
            return;
        if (session.PathAttempts++ >= 4)
        {
            this.FailChestSession(session, "CRAFT-CHEST-PATH-EXHAUSTED");
            return;
        }
        session.NextPathTick = this.currentTick + 60;
        int facing = TaskNavigationService.FacingToward(access.ApproachTile, access.Tile);
        body.controller = CompanionPathing.CreateController(body, access.Location, access.ApproachTile.ToPoint(), facing, 256);
    }

    private void RequestChestLocks(ChestCraftSession session, CraftChestAccess access)
    {
        session.LockRequested = true;
        NetMutex chestMutex = access.Chest.GetMutex();
        chestMutex.RequestLock(() =>
        {
            NetMutex bagMutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(CompanionInventoryStore.GetNamespace(session.Record.Identity));
            bagMutex.RequestLock(() =>
            {
                try
                {
                    if (!OwnerLifecycleGate.CanAdvance(session.Owner))
                    {
                        session.LockRequested = false;
                        return;
                    }
                    if (!this.chestSessions.ContainsKey(session.Record.Identity) || !CompanionStorageCoordinator.IsCurrentCraftChest(access, session.Record.OwnerId))
                        return;
                    Inventory escrow = this.inventories.GetCraftEscrow(session.Record.Identity);
                    PlannedCraftSource[] group = session.Sources.Where(source => !source.Record.Acquired && source.Chest.HasValue && source.Chest.Value.Authorization.ChestToken == access.Authorization.ChestToken).ToArray();
                    foreach (PlannedCraftSource source in group)
                    {
                        int index = FindExactIndex(access.Chest.Items, source.Item);
                        if (index < 0 || source.Item.Stack != source.ExpectedStack || source.Item.modData.ContainsKey(StorageTags.ResponsibilityId))
                            throw new InvalidOperationException("A planned chest stack changed.");
                    }
                    foreach (PlannedCraftSource source in group.OrderByDescending(source => FindExactIndex(access.Chest.Items, source.Item)))
                        MoveToEscrow(session.Transaction.CraftId, source, access.Chest.Items, escrow, FindExactIndex(access.Chest.Items, source.Item));
                    session.LockRequested = false;
                    session.PathAttempts = 0;
                    session.Transaction.UpdatedTick = this.currentTick;
                }
                catch
                {
                    this.FailChestSession(session, "CRAFT-CHEST-SOURCE-CHANGED");
                }
                finally
                {
                    bagMutex.ReleaseLock();
                    chestMutex.ReleaseLock();
                }
            }, () => { chestMutex.ReleaseLock(); this.FailChestSession(session, "CRAFT-BAG-LOCK-FAILED"); });
        }, () => this.FailChestSession(session, "CRAFT-CHEST-LOCK-FAILED"));
    }

    private CraftActionResult CommitEscrowed(ChestCraftSession session)
    {
        if (!OwnerLifecycleGate.CanAdvance(session.Owner))
            return CraftActionResult.Failure("OWNER-BUSY", "This Yui's Owner became busy before output creation; escrowed materials remain unchanged.");
        CraftTransactionRecord transaction = session.Transaction;
        Inventory escrow = this.inventories.GetCraftEscrow(session.Record.Identity);
        Item[] responsible = escrow.Where(item => item is not null && item.modData.GetValueOrDefault(CompanionInventoryStore.CraftIdTag) == transaction.CraftId).ToArray();
        if (!CanAllocateMaterials(responsible, session.Recipe.Ingredients, transaction.CraftCount))
            return this.Fault(session, "CRAFT-ESCROW-INCOMPLETE", "Craft Escrow no longer contains the complete immutable material plan.");
        CraftRecipeResolution current = this.policy.TryResolve(session.Owner, transaction.RecipeKey);
        if (!current.IsSuccess || current.Recipe is null || !CraftingRecipePolicy.SnapshotMatches(current.Recipe, transaction.RecipeSnapshot))
        {
            this.FailChestSession(session, "CRAFT-RECIPE-CHANGED");
            return CraftActionResult.Failure("CRAFT-RECIPE-CHANGED", "Recipe changed before output creation; materials are being returned.");
        }
        transaction.Phase = CraftPhases.CommitReady;
        string lastLocation = string.Empty;
        if (this.bodies.TryGetBody(session.Record.Identity, out NPC craftBody))
            this.appearance.Prepare(session.Record.Identity, transaction.OperationId, AppearanceActionKinds.Crafting, null, craftBody.FacingDirection);
        try
        {
            for (int child = 0; child < transaction.CraftCount; child++)
            {
                Item output = current.Recipe.Recipe.createItem();
                string token = Guid.NewGuid().ToString("N");
                output.modData[CompanionInventoryStore.CraftIdTag] = transaction.CraftId;
                output.modData[CompanionInventoryStore.CraftOutputTokenTag] = token;
                InventoryActionResult routed = this.inventories.StoreGeneratedOutput(session.Record.Identity, output);
                if (routed.Code == "OUTPUT-RESPONSIBILITY-UNKNOWN")
                    return this.Fault(session, "CRAFT-OUTPUT-UNKNOWN", $"Output {child + 1} could not prove a unique responsibility location.");
                transaction.OutputToken = token;
                transaction.OutputTokens.Add(token);
                transaction.OutputLocation = lastLocation = routed.Code;
                transaction.CompletedCount = child + 1;
                transaction.Phase = CraftPhases.OutputCreated;
                this.appearance.Commit(session.Record.Identity, transaction.OperationId);
            }
        }
        catch (Exception ex)
        {
            if (transaction.CompletedCount > 0 && this.TryCompletePartial(session.Record, session.Owner, transaction, ex.GetType().Name))
                return CraftActionResult.Failure("CRAFT-PARTIAL", $"Crafted {transaction.CompletedCount}/{transaction.CraftCount}; later child creation stopped safely.");
            return this.Fault(session, "CRAFT-CREATE-FAILED", $"Output creation stopped before any proven child output: {ex.GetType().Name}.");
        }
        escrow.Clear();
        transaction.Phase = CraftPhases.MaterialsConsumed;
        ApplyOwnerProgress(session.Owner, transaction);
        if (!this.TryReleaseOutputResponsibility(session.Record.Identity, transaction, out string releaseFailure))
        {
            transaction.Phase = CraftPhases.Reconciling;
            transaction.LastFailure = releaseFailure;
            session.Record.ActiveTransactionId = null;
            return CraftActionResult.Failure("CRAFT-OUTPUT-RECONCILE", "Craft output exists, but its terminal responsibility tags could not be released safely.");
        }
        transaction.Phase = CraftPhases.Completed;
        session.Record.ActiveTransactionId = null;
        TaskReceiptStore.Add(session.Record, transaction.OperationId, true, "CRAFT-COMPLETED", $"Crafted {transaction.RecipeKey} {transaction.CompletedCount}/{transaction.CraftCount}; output={lastLocation}.");
        session.Record.CraftTransaction = null;
        return CraftActionResult.Success("CRAFT-COMPLETED", $"Crafted {transaction.RecipeKey} {transaction.CompletedCount}/{transaction.CraftCount}; output={lastLocation}.");
    }

    private void FailChestSession(ChestCraftSession session, string reason)
    {
        if (!this.chestSessions.Remove(session.Record.Identity))
            return;
        session.Transaction.Phase = CraftPhases.ReturningMaterials;
        session.Transaction.LastFailure = reason;
        session.Record.ActiveTransactionId = null;
        NetMutex bagMutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(CompanionInventoryStore.GetNamespace(session.Record.Identity));
        if (bagMutex.IsLockHeld())
            this.ReturnEscrowToBag(session.Record, reason);
        else
            this.inventories.RequestTransfer(session.Record.Identity, () =>
            {
                this.ReturnEscrowToBag(session.Record, reason);
                return InventoryActionResult.Success("CRAFT-RETURNED", "Pre-output craft materials were retained in the Yui bag or Craft Escrow.");
            }, _ => { });
    }

    private void ReturnEscrowToBag(CompanionRecord record, string reason)
    {
        if (!this.MoveEscrowToBag(record.Identity))
        {
            record.CraftTransaction!.Phase = CraftPhases.Faulted;
            record.CraftTransaction.LastFailure = $"{reason}:RETURN-BAG-FULL";
            return;
        }
        string operationId = record.CraftTransaction!.OperationId;
        TaskReceiptStore.Add(record, operationId, false, "CRAFT-CANCELLED", $"Craft stopped before output creation; real materials were retained in the Yui bag ({reason}).");
        record.CraftTransaction = null;
    }

    private bool MoveEscrowToBag(CompanionIdentity identity)
    {
        Inventory bag = this.inventories.Get(identity);
        Inventory escrow = this.inventories.GetCraftEscrow(identity);
        foreach (Item item in escrow.ToArray())
        {
            Item? merge = bag.FirstOrDefault(candidate => candidate is not null && IsUnreservedMaterial(candidate) && CanMergeCraft(candidate, item));
            if (merge is not null)
            {
                merge.Stack += item.Stack;
                escrow.Remove(item);
            }
            else if (this.inventories.Count(identity) < CompanionInventoryStore.Capacity)
            {
                item.modData.Remove(CompanionInventoryStore.CraftIdTag);
                item.modData.Remove(CompanionInventoryStore.CraftSourceTag);
                bag.Add(item);
                escrow.Remove(item);
            }
        }
        return !escrow.Any(item => item is not null);
    }

    private CraftActionResult Fault(ChestCraftSession session, string code, string message)
    {
        session.Transaction.Phase = CraftPhases.Faulted;
        session.Transaction.LastFailure = code;
        session.Record.ActiveTransactionId = null;
        this.chestSessions.Remove(session.Record.Identity);
        return CraftActionResult.Failure(code, message);
    }

    private static bool TryPlanSources(Inventory bag, IReadOnlyList<CraftChestAccess> chests, IReadOnlyList<CraftIngredientRecord> ingredients, int craftCount, out List<PlannedCraftSource> plan, out string missing)
    {
        plan = new List<PlannedCraftSource>();
        var remainingByItem = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        IEnumerable<(Item Item, CraftChestAccess? Chest, int Slot)> candidates = bag.Select((item, slot) => (item, (CraftChestAccess?)null, slot))
            .Where(candidate => candidate.item is not null && IsUnreservedMaterial(candidate.item))
            .Select(candidate => (candidate.item!, candidate.Item2, candidate.slot))
            .Concat(chests.SelectMany(chest => chest.Chest.Items.Select((item, slot) => (item, (CraftChestAccess?)chest, slot)))
                .Where(candidate => candidate.item is not null && candidate.item.Stack > 0 && !candidate.item.modData.ContainsKey(StorageTags.ResponsibilityId))
                .Select(candidate => (candidate.item!, candidate.Item2, candidate.slot)));
        (Item Item, CraftChestAccess? Chest, int Slot)[] ordered = candidates.ToArray();
        foreach (var candidate in ordered)
            remainingByItem[candidate.Item] = candidate.Item.Stack;

        foreach (CraftIngredientRecord ingredient in ingredients)
        {
            int remaining = checked(ingredient.RequiredPerCraft * craftCount);
            foreach (var candidate in ordered.Where(candidate => CraftingRecipePolicy.Matches(candidate.Item, ingredient.IngredientId)))
            {
                int available = remainingByItem[candidate.Item];
                if (available <= 0) continue;
                int take = Math.Min(remaining, available);
                PlannedCraftSource? existing = plan.FirstOrDefault(source => ReferenceEquals(source.Item, candidate.Item));
                if (existing is null)
                {
                    var record = new CraftSourceRecord
                    {
                        SourceKind = candidate.Chest.HasValue ? CraftSourceKinds.AuthorizedChest : CraftSourceKinds.Bag,
                        StorageId = candidate.Chest?.Authorization.ChestToken ?? string.Empty,
                        SourceSlot = candidate.Slot,
                        ItemFingerprint = Fingerprint(candidate.Item),
                        QualifiedItemId = candidate.Item.QualifiedItemId,
                        Quantity = take,
                    };
                    plan.Add(new PlannedCraftSource(candidate.Item, candidate.Item.Stack, candidate.Chest, record));
                }
                else
                    existing.Record.Quantity += take;
                remainingByItem[candidate.Item] -= take;
                remaining -= take;
                if (remaining == 0) break;
            }
            if (remaining > 0)
            {
                missing = $"{ingredient.IngredientId} x{remaining}";
                plan.Clear();
                return false;
            }
        }
        missing = string.Empty;
        return true;
    }

    private static void MoveToEscrow(string craftId, PlannedCraftSource source, IList<Item> origin, Inventory escrow, int sourceIndex)
    {
        Item moved;
        if (source.Record.Quantity == source.Item.Stack)
        {
            moved = source.Item;
            origin.RemoveAt(sourceIndex);
        }
        else
        {
            moved = source.Item.getOne();
            moved.Stack = source.Record.Quantity;
            source.Item.Stack -= source.Record.Quantity;
        }
        moved.modData[CompanionInventoryStore.CraftIdTag] = craftId;
        moved.modData[CompanionInventoryStore.CraftSourceTag] = Guid.NewGuid().ToString("N");
        escrow.Add(moved);
        source.Record.Acquired = true;
    }

    private static bool HasMaterials(Inventory bag, IReadOnlyList<CraftIngredientRecord> ingredients, int count) =>
        CanAllocateMaterials(bag.Where(IsUnreservedMaterial), ingredients, count);

    private static bool CanAllocateMaterials(IEnumerable<Item> sourceItems, IReadOnlyList<CraftIngredientRecord> ingredients, int count)
    {
        Item[] items = sourceItems.ToArray();
        var remainingByItem = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        foreach (Item item in items)
            remainingByItem[item] = item.Stack;
        foreach (CraftIngredientRecord ingredient in ingredients)
        {
            int required = checked(ingredient.RequiredPerCraft * count);
            foreach (Item item in items.Where(item => CraftingRecipePolicy.Matches(item, ingredient.IngredientId)))
            {
                int take = Math.Min(required, remainingByItem[item]);
                remainingByItem[item] -= take;
                required -= take;
                if (required == 0) break;
            }
            if (required != 0) return false;
        }
        return true;
    }

    private static bool CanMergeCraft(Item destination, Item source)
    {
        Item comparison = source.getOne();
        comparison.modData.Remove(CompanionInventoryStore.CraftIdTag);
        comparison.modData.Remove(CompanionInventoryStore.CraftSourceTag);
        return destination.canStackWith(comparison) && destination.Stack + source.Stack <= destination.maximumStackSize();
    }

    private static int FindExactIndex(IList<Item> items, Item target)
    {
        for (int index = 0; index < items.Count; index++) if (ReferenceEquals(items[index], target)) return index;
        return -1;
    }

    private static void ApplyOwnerProgress(Farmer owner, CraftTransactionRecord transaction)
    {
        int before = owner.craftingRecipes.TryGetValue(transaction.RecipeKey, out int count) ? count : 0;
        owner.craftingRecipes[transaction.RecipeKey] = checked(before + transaction.CompletedCount);
        transaction.ProgressApplied = true;
        transaction.Phase = CraftPhases.ProgressApplied;
    }

    private bool TryCompletePartial(CompanionRecord record, Farmer owner, CraftTransactionRecord transaction, string failure)
    {
        Inventory escrow = this.inventories.GetCraftEscrow(record.Identity);
        if (!ConsumeMaterials(escrow, transaction.RecipeSnapshot.Ingredients, transaction.CompletedCount)
            || !this.MoveEscrowToBag(record.Identity))
            return false;
        transaction.Phase = CraftPhases.MaterialsConsumed;
        if (!transaction.ProgressApplied)
            ApplyOwnerProgress(owner, transaction);
        if (!this.TryReleaseOutputResponsibility(record.Identity, transaction, out string releaseFailure))
        {
            transaction.Phase = CraftPhases.Faulted;
            transaction.LastFailure = releaseFailure;
            return false;
        }
        transaction.LastFailure = failure;
        record.ActiveTransactionId = null;
        TaskReceiptStore.Add(record, transaction.OperationId, false, "CRAFT-PARTIAL", $"Crafted {transaction.CompletedCount}/{transaction.CraftCount}; unused real materials returned after {failure}.");
        record.CraftTransaction = null;
        return true;
    }

    private static bool ConsumeMaterials(Inventory escrow, IReadOnlyList<CraftIngredientRecord> ingredients, int count)
    {
        Item[] items = escrow.Where(item => item is not null).ToArray();
        var available = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        var consumeByItem = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        foreach (Item item in items)
        {
            available[item] = item.Stack;
            consumeByItem[item] = 0;
        }
        foreach (CraftIngredientRecord ingredient in ingredients)
        {
            int remaining = checked(ingredient.RequiredPerCraft * count);
            foreach (Item item in items.Where(item => CraftingRecipePolicy.Matches(item, ingredient.IngredientId)))
            {
                int consume = Math.Min(remaining, available[item]);
                available[item] -= consume;
                consumeByItem[item] += consume;
                remaining -= consume;
                if (remaining == 0) break;
            }
            if (remaining != 0)
                return false;
        }
        foreach ((Item item, int consume) in consumeByItem.Where(pair => pair.Value > 0))
        {
            item.Stack -= consume;
            if (item.Stack == 0)
                escrow.Remove(item);
        }
        return true;
    }

    private void TryReconcilePersistedOutput(CompanionRecord record)
    {
        CraftTransactionRecord transaction = record.CraftTransaction!;
        if (transaction.OutputTokens.Count != transaction.CraftCount || transaction.OutputToken is null)
            return;
        if (this.inventories.CountCraftOutputs(record.Identity, transaction.OutputTokens) != transaction.OutputTokens.Count)
        {
            transaction.Phase = CraftPhases.Faulted;
            transaction.LastFailure = "CRAFT-OUTPUT-TOKEN-MISMATCH";
            record.ActiveTransactionId = null;
            return;
        }
        Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
        if (owner is null)
        {
            transaction.Phase = CraftPhases.Reconciling;
            transaction.LastFailure = "OWNER-OFFLINE";
            record.ActiveTransactionId = null;
            return;
        }
        if (!this.reconciliationPending.Add(record.Identity))
            return;
        this.inventories.RequestTransfer(record.Identity, () =>
        {
            if (record.CraftTransaction != transaction || this.inventories.CountCraftOutputs(record.Identity, transaction.OutputTokens) != transaction.OutputTokens.Count)
                return InventoryActionResult.Failure("CRAFT-RECONCILE-CHANGED", "Persisted output responsibility changed before locked reconciliation.");
            this.inventories.GetCraftEscrow(record.Identity).Clear();
            transaction.Phase = CraftPhases.MaterialsConsumed;
            if (!transaction.ProgressApplied)
                ApplyOwnerProgress(owner, transaction);
            if (!this.TryReleaseOutputResponsibility(record.Identity, transaction, out string releaseFailure))
            {
                transaction.Phase = CraftPhases.Reconciling;
                transaction.LastFailure = releaseFailure;
                record.ActiveTransactionId = null;
                return InventoryActionResult.Failure("CRAFT-OUTPUT-RECONCILE", "Persisted craft output responsibility could not be released safely.");
            }
            transaction.Phase = CraftPhases.Completed;
            record.ActiveTransactionId = null;
            TaskReceiptStore.Add(record, transaction.OperationId, true, "CRAFT-RECONCILED", $"Reconciled {transaction.RecipeKey} {transaction.CompletedCount}/{transaction.CraftCount} from persisted OutputTokens.");
            record.CraftTransaction = null;
            return InventoryActionResult.Success("CRAFT-RECONCILED", "Persisted crafting output, materials, and Owner progress now agree.");
        }, _ => this.reconciliationPending.Remove(record.Identity));
    }

    private bool IsCurrentPendingStart(CompanionIdentity identity, PendingBagCraftStart pending) =>
        pending.LifecycleGeneration == this.lifecycleGeneration
        && this.pendingBagStarts.TryGetValue(identity, out PendingBagCraftStart? current)
        && ReferenceEquals(current, pending)
        && this.registry.TryGet(identity, out CompanionRecord? currentRecord)
        && ReferenceEquals(currentRecord, pending.Record);

    private void RemovePendingStartIfCurrent(CompanionIdentity identity, PendingBagCraftStart pending)
    {
        if (this.pendingBagStarts.TryGetValue(identity, out PendingBagCraftStart? current)
            && ReferenceEquals(current, pending))
            this.pendingBagStarts.Remove(identity);
    }

    private bool TryReleaseOutputResponsibility(CompanionIdentity identity, CraftTransactionRecord transaction, out string failure)
    {
        HashSet<string> expectedTokens = transaction.OutputTokens.ToHashSet(StringComparer.Ordinal);
        if (transaction.CompletedCount <= 0 || expectedTokens.Count != transaction.CompletedCount)
        {
            failure = "CRAFT-OUTPUT-TOKEN-LEDGER-MISMATCH";
            return false;
        }

        Inventory[] containers =
        {
            this.inventories.Get(identity),
            Game1.player.team.GetOrCreateGlobalInventory(CompanionInventoryStore.GetPendingOutputNamespace(identity)),
            Game1.player.team.GetOrCreateGlobalInventory(CompanionInventoryStore.GetRecoveryVaultNamespace(identity)),
        };
        Item[] outputs = containers
            .SelectMany(container => container)
            .OfType<Item>()
            .Where(item => item.modData.TryGetValue(CompanionInventoryStore.CraftOutputTokenTag, out string? token)
                && expectedTokens.Contains(token))
            .ToArray();
        bool exact = outputs.Length == expectedTokens.Count
            && outputs.All(item => item.modData.GetValueOrDefault(CompanionInventoryStore.CraftIdTag) == transaction.CraftId)
            && outputs.Select(item => item.modData[CompanionInventoryStore.CraftOutputTokenTag]).Distinct(StringComparer.Ordinal).Count() == expectedTokens.Count;
        if (!exact)
        {
            failure = "CRAFT-OUTPUT-RESPONSIBILITY-MISMATCH";
            return false;
        }

        foreach (Item output in outputs)
        {
            output.modData.Remove(CompanionInventoryStore.CraftIdTag);
            output.modData.Remove(CompanionInventoryStore.CraftOutputTokenTag);
        }
        failure = string.Empty;
        return true;
    }

    private static int Manhattan(Vector2 first, Vector2 second) => (int)(Math.Abs(first.X - second.X) + Math.Abs(first.Y - second.Y));

    private sealed record PendingBagCraftStart(CompanionRecord Record, string OperationId, ulong LifecycleGeneration);

    private sealed class PlannedCraftSource
    {
        public PlannedCraftSource(Item item, int expectedStack, CraftChestAccess? chest, CraftSourceRecord record) { this.Item = item; this.ExpectedStack = expectedStack; this.Chest = chest; this.Record = record; }
        public Item Item { get; }
        public int ExpectedStack { get; }
        public CraftChestAccess? Chest { get; }
        public CraftSourceRecord Record { get; }
    }

    private sealed class ChestCraftSession
    {
        public ChestCraftSession(CompanionRecord record, Farmer owner, CraftRecipeDescriptor recipe, CraftTransactionRecord transaction, List<PlannedCraftSource> sources) { this.Record = record; this.Owner = owner; this.Recipe = recipe; this.Transaction = transaction; this.Sources = sources; }
        public CompanionRecord Record { get; }
        public Farmer Owner { get; }
        public CraftRecipeDescriptor Recipe { get; }
        public CraftTransactionRecord Transaction { get; }
        public List<PlannedCraftSource> Sources { get; }
        public bool LockRequested { get; set; }
        public bool CommitRequested { get; set; }
        public int PathAttempts { get; set; }
        public ulong NextPathTick { get; set; }
    }
}
