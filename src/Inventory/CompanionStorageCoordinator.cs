using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Network;
using StardewValley.Objects;
using StardewValley.Pathfinding;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal static class StorageTags
{
    public const string ChestToken = "Himifox.YuiToIssho/ChestToken";
    public const string ResponsibilityId = "Himifox.YuiToIssho/StorageResponsibility";
    public const string ReturnPending = "Himifox.YuiToIssho/StorageReturnPending";
}

internal readonly record struct StorageActionResult(bool IsSuccess, string Code, string Message)
{
    public static StorageActionResult Success(string code, string message) => new(true, code, message);
    public static StorageActionResult Failure(string code, string message) => new(false, code, message);
}

internal readonly record struct CraftChestAccess(AuthorizedChestRecord Authorization, GameLocation Location, Chest Chest, Vector2 Tile, Vector2 ApproachTile);

internal sealed class CompanionStorageCoordinator
{
    private const int SearchLimit = 256;
    private const int MaximumPathAttempts = 4;
    private const ulong TaskStartGraceTicks = 600;
    private const ulong ReturnRetryTicks = 600;
    private const string StorageOperationPrefix = "storage-";

    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Func<LifecycleState> getLifecycleState;
    private readonly Func<bool> canMutateSave;
    private readonly Dictionary<CompanionIdentity, StorageTransfer> transfers = new();
    private readonly Dictionary<string, ulong> nextReturnAttempt = new(StringComparer.Ordinal);
    private ulong currentTick;

    public CompanionStorageCoordinator(
        CompanionRegistry registry,
        CompanionBodyBinder bodies,
        CompanionInventoryStore inventories,
        TaskNavigationService navigation,
        IMonitor monitor,
        Func<LifecycleState> getLifecycleState,
        Func<bool> canMutateSave)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.navigation = navigation;
        this.monitor = monitor;
        this.getLifecycleState = getLifecycleState;
        this.canMutateSave = canMutateSave;
    }

    public InventoryValidationResult Validate()
    {
        HashSet<string> liabilities = new(StringComparer.Ordinal);
        foreach (CompanionRecord record in this.registry.Active)
        {
            if (record.StorageWriteBlocked && string.IsNullOrWhiteSpace(record.LastStorageFailure))
                return InventoryValidationResult.Failure("STORAGE-FAULT-MISSING-DIAGNOSIS", $"{record.Identity} blocks storage writes without a persisted failure explanation.");

            Inventory bag = this.inventories.Get(record.Identity);
            foreach (StorageLiabilityRecord liability in record.StorageLiabilities)
            {
                if (!liabilities.Add(liability.ResponsibilityId))
                    return InventoryValidationResult.Failure("DUPLICATE-STORAGE-RESPONSIBILITY", $"Responsibility {liability.ResponsibilityId} is duplicated.");

                Item[] matches = bag.Where(item => item is not null
                    && item.modData.TryGetValue(StorageTags.ResponsibilityId, out string? value)
                    && value == liability.ResponsibilityId).ToArray();
                if (matches.Length != 1)
                    return InventoryValidationResult.Failure("RESPONSIBLE-ITEM-MISSING", $"{record.Identity} responsibility {liability.ResponsibilityId} maps to {matches.Length} real bag items.");

                Item item = matches[0];
                if (item.QualifiedItemId != liability.QualifiedItemId
                    || item.Stack <= 0
                    || item.Stack > liability.MaximumResponsibleStack
                    || (liability.Kind == StorageLiabilityKinds.BorrowedTool && item is not Tool))
                    return InventoryValidationResult.Failure("RESPONSIBLE-ITEM-CHANGED", $"{record.Identity} responsibility {liability.ResponsibilityId} no longer matches its recorded real item.");

                bool hasReturnTag = item.modData.TryGetValue(StorageTags.ReturnPending, out string? returnId);
                if (hasReturnTag != liability.ReturnRequested || (hasReturnTag && returnId != liability.ResponsibilityId))
                    return InventoryValidationResult.Failure("RETURN-RESPONSIBILITY-CHANGED", $"{record.Identity} responsibility {liability.ResponsibilityId} has an inconsistent pending-return marker.");
            }

            foreach (Item item in bag.Where(item => item is not null))
            {
                if (item.modData.TryGetValue(StorageTags.ResponsibilityId, out string? responsibilityId)
                    && !record.StorageLiabilities.Any(liability => liability.ResponsibilityId == responsibilityId))
                    return InventoryValidationResult.Failure("ORPHANED-STORAGE-TAG", $"{record.Identity} contains a tagged item with no liability record: {responsibilityId}.");
                if (item.modData.TryGetValue(StorageTags.ReturnPending, out string? returnId)
                    && (!item.modData.TryGetValue(StorageTags.ResponsibilityId, out responsibilityId) || responsibilityId != returnId))
                    return InventoryValidationResult.Failure("ORPHANED-RETURN-TAG", $"{record.Identity} contains a pending-return marker without the matching storage responsibility: {returnId}.");
            }
        }
        return InventoryValidationResult.Success($"Validated {liabilities.Count} storage item responsibility record(s).");
    }

    public IReadOnlyList<CraftChestAccess> GetCraftingChests(CompanionIdentity identity, NPC body)
    {
        return this.registry.AuthorizedChests
            .Where(record => record.OwnerId == identity.OwnerId)
            .Select(record =>
            {
                if (!TryResolveChest(record, out GameLocation location, out Chest chest) || !ReferenceEquals(location, body.currentLocation))
                    return (CraftChestAccess?)null;
                Vector2 tile = new(record.TileX, record.TileY);
                Vector2? approach = FindApproach(location, tile, body);
                return approach is null ? null : new CraftChestAccess(record, location, chest, tile, approach.Value);
            })
            .Where(access => access.HasValue)
            .Select(access => access!.Value)
            .OrderBy(access => Manhattan(body.Tile, access.Tile))
            .ThenBy(access => access.Authorization.ChestToken, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool IsCurrentCraftChest(CraftChestAccess access, long ownerId) =>
        access.Authorization.OwnerId == ownerId
        && TryResolveChest(access.Authorization, out GameLocation location, out Chest chest)
        && ReferenceEquals(location, access.Location)
        && ReferenceEquals(chest, access.Chest);

    public void SetAuthorization(CompanionIdentity identity, Farmer owner, int tileX, int tileY, bool allow, Action<StorageActionResult> completed)
    {
        if (!this.TryGetWritableRecord(identity, allowNewWrites: true, out CompanionRecord record, out StorageActionResult gate))
        {
            completed(gate);
            return;
        }
        if (this.OwnerHasActiveTransaction(identity.OwnerId))
        {
            completed(StorageActionResult.Failure("OWNER-STORAGE-BUSY", "Finish every active task or storage transfer for this owner before changing Chest authorization."));
            return;
        }
        if (owner.UniqueMultiplayerID != identity.OwnerId || tileX < 0 || tileY < 0 || owner.currentLocation is null || owner.currentLocation.IsTemporary)
        {
            completed(StorageActionResult.Failure("INVALID-CHEST-TILE", "A non-negative tile in a persistent current location is required."));
            return;
        }

        GameLocation location = owner.currentLocation;
        AuthorizedChestIdentity key = new(identity.OwnerId, location.NameOrUniqueName, location.isStructure.Value, tileX, tileY);
        AuthorizedChestRecord? existing = this.registry.AuthorizedChests.FirstOrDefault(candidate => candidate.Identity == key);
        if (!allow && existing is null)
        {
            completed(StorageActionResult.Success("ALREADY-UNAUTHORIZED", $"Chest {key.LocationKey}@{tileX},{tileY} is already unauthorized."));
            return;
        }
        if (!allow && this.registry.All.Any(candidate => candidate.OwnerId == identity.OwnerId
            && candidate.StorageLiabilities.Any(liability => IsSource(liability, existing!))))
        {
            completed(StorageActionResult.Failure("CHEST-HAS-RESPONSIBILITIES", "Return every borrowed tool and material responsibility before removing this authorization."));
            return;
        }

        if (!TryGetChestAt(location, tileX, tileY, out Chest chest) || !IsEligibleChest(chest, identity.OwnerId))
        {
            if (!allow && existing is not null)
            {
                this.registry.RemoveAuthorization(key);
                completed(StorageActionResult.Success("STALE-AUTHORIZATION-REMOVED", $"Removed stale authorization {key.LocationKey}@{tileX},{tileY} without touching any replacement object."));
            }
            else
            {
                completed(StorageActionResult.Failure("CHEST-NOT-ELIGIBLE", "The tile must contain an owned normal or Big player Chest, not a special/global container."));
            }
            return;
        }
        if (Manhattan(owner.Tile, new Vector2(tileX, tileY)) > 1)
        {
            completed(StorageActionResult.Failure("PLAYER-NOT-ADJACENT", "The host player must stand next to the Chest when changing authorization."));
            return;
        }
        if (existing is not null && ChestToken(chest) == existing.ChestToken)
        {
            if (allow)
                completed(StorageActionResult.Success("ALREADY-AUTHORIZED", $"Chest {key.LocationKey}@{tileX},{tileY} is already authorized."));
            else
                this.RequestAuthorizationRemoval(record, existing, location, chest, completed);
            return;
        }
        if (existing is not null)
        {
            if (!allow && this.registry.RemoveAuthorization(existing.Identity))
                completed(StorageActionResult.Success("STALE-AUTHORIZATION-REMOVED", $"Removed stale authorization {key.LocationKey}@{tileX},{tileY} without touching the replacement Chest."));
            else
                completed(StorageActionResult.Failure("AUTHORIZATION-TOKEN-MISMATCH", "A different Chest now occupies the authorized tile; unauthorize the stale record first."));
            return;
        }

        NetMutex mutex = chest.GetMutex();
        mutex.RequestLock(
            acquired: () =>
            {
                StorageActionResult result;
                try
                {
                    if (!this.CanCommit(identity, record)
                        || this.OwnerHasActiveTransaction(identity.OwnerId)
                        || !ReferenceEquals(owner.currentLocation, location)
                        || Manhattan(owner.Tile, new Vector2(tileX, tileY)) > 1
                        || !TryGetChestAt(location, tileX, tileY, out Chest current)
                        || !ReferenceEquals(current, chest)
                        || !IsEligibleChest(current, identity.OwnerId))
                    {
                        result = StorageActionResult.Failure("CHEST-CHANGED", "The exact Chest or write authority changed before the mutex was acquired.");
                    }
                    else
                    {
                        string? token = ChestToken(current);
                        if (token is not null && !Guid.TryParseExact(token, "N", out _))
                            result = StorageActionResult.Failure("CHEST-TOKEN-INVALID", "The Chest has an invalid Yui to Issho! token; it was not overwritten.");
                        else
                        {
                            token ??= Guid.NewGuid().ToString("N");
                            current.modData[StorageTags.ChestToken] = token;
                            bool added = this.registry.TryAddAuthorization(new AuthorizedChestRecord
                            {
                                OwnerId = identity.OwnerId,
                                LocationKey = location.NameOrUniqueName,
                                IsStructure = location.isStructure.Value,
                                TileX = tileX,
                                TileY = tileY,
                                ChestToken = token,
                            });
                            result = added
                                ? StorageActionResult.Success("CHEST-AUTHORIZED", $"Authorized {location.NameOrUniqueName}@{tileX},{tileY} for owner {identity.OwnerId}.")
                                : StorageActionResult.Failure("AUTHORIZATION-RACE", "The authorization was added by another request first.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result = StorageActionResult.Failure("AUTHORIZATION-FAILED", $"Authorization failed without moving items: {ex.Message}");
                }
                finally
                {
                    mutex.ReleaseLock();
                }
                completed(result);
            },
            failed: () => completed(StorageActionResult.Failure("CHEST-LOCK-FAILED", "The Chest mutex could not be acquired; authorization was unchanged."))
        );
    }

    public StorageActionResult TryBorrowTool(CompanionIdentity identity, string qualifiedItemId)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return StorageActionResult.Failure("INVALID-ITEM-ID", "A qualified tool item ID is required.");
        if (!this.TryGetWritableRecord(identity, allowNewWrites: true, out CompanionRecord record, out StorageActionResult gate))
            return gate;
        if (record.StorageLiabilities.Any(liability => liability.Kind == StorageLiabilityKinds.BorrowedTool))
            return StorageActionResult.Failure("TOOL-ALREADY-BORROWED", "Return the existing borrowed tool before borrowing another one.");
        if (!this.TryFindSource(identity, qualifiedItemId, requireTool: true, requestedCount: 1, out SourceSelection selection, out string failure))
            return StorageActionResult.Failure("TOOL-SOURCE-NOT-FOUND", failure);
        return this.StartTransfer(record, selection, TransferKind.BorrowTool, requestedCount: 1, liability: null);
    }

    public StorageActionResult TryTakeMaterial(CompanionIdentity identity, string qualifiedItemId, int count)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId) || count <= 0)
            return StorageActionResult.Failure("INVALID-MATERIAL-REQUEST", "A qualified item ID and positive exact count are required.");
        if (!this.TryGetWritableRecord(identity, allowNewWrites: true, out CompanionRecord record, out StorageActionResult gate))
            return gate;
        if (!this.TryFindSource(identity, qualifiedItemId, requireTool: false, requestedCount: count, out SourceSelection selection, out string failure))
            return StorageActionResult.Failure("MATERIAL-SOURCE-NOT-FOUND", failure);
        if (selection.Sources[0].Item is Tool || selection.Sources[0].Item.maximumStackSize() <= 1 || count > selection.Sources[0].Item.maximumStackSize())
            return StorageActionResult.Failure("MATERIAL-NOT-STACKABLE", "This slice takes one legal stack of non-tool material at a time.");
        return this.StartTransfer(record, selection, TransferKind.TakeMaterial, count, liability: null);
    }

    public StorageActionResult RequestReturn(CompanionIdentity identity, string responsibilityId)
    {
        if (!this.TryGetWritableRecord(identity, allowNewWrites: false, out CompanionRecord record, out StorageActionResult gate))
            return gate;
        StorageLiabilityRecord? liability = record.StorageLiabilities.FirstOrDefault(candidate => candidate.ResponsibilityId == responsibilityId);
        if (liability is null)
            return StorageActionResult.Failure("RESPONSIBILITY-NOT-FOUND", $"{identity} has no storage responsibility {responsibilityId}.");
        this.MarkReturnPending(identity, liability);
        return this.TryBeginReturn(record, liability, explicitRequest: true);
    }

    public void BindTask(CompanionIdentity identity, string operationId, Item? exactTool)
    {
        if (exactTool is null
            || !exactTool.modData.TryGetValue(StorageTags.ResponsibilityId, out string? responsibilityId)
            || !this.registry.TryGet(identity, out CompanionRecord record))
            return;
        StorageLiabilityRecord? liability = record.StorageLiabilities.FirstOrDefault(candidate =>
            candidate.ResponsibilityId == responsibilityId
            && candidate.Kind == StorageLiabilityKinds.BorrowedTool);
        if (liability is not null
            && !liability.ReturnRequested
            && ReferenceEquals(this.FindResponsibleItem(identity, responsibilityId), exactTool))
            liability.TaskOperationId = operationId;
    }

    public IReadOnlyList<string> Describe(CompanionIdentity identity)
    {
        List<string> lines = new();
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return new[] { $"{identity} does not exist." };

        AuthorizedChestRecord[] authorizations = this.registry.AuthorizedChests.Where(candidate => candidate.OwnerId == identity.OwnerId).ToArray();
        lines.Add($"storage blocked={record.StorageWriteBlocked}, authorizations={authorizations.Length}, liabilities={record.StorageLiabilities.Count}, runtime={(this.transfers.TryGetValue(identity, out StorageTransfer? transfer) ? transfer.Phase : "none")}, failure={record.LastStorageFailure ?? "none"}");
        foreach (AuthorizedChestRecord authorization in authorizations)
            lines.Add($"chest {authorization.LocationKey}[structure={authorization.IsStructure}]@{authorization.TileX},{authorization.TileY}, token={authorization.ChestToken}");
        foreach (StorageLiabilityRecord liability in record.StorageLiabilities)
        {
            Item? item = this.FindResponsibleItem(identity, liability.ResponsibilityId);
            lines.Add($"responsibility={liability.ResponsibilityId}, kind={liability.Kind}, item={liability.QualifiedItemId}, stack={item?.Stack.ToString() ?? "missing"}, source={liability.SourceLocationKey}@{liability.SourceTileX},{liability.SourceTileY}, task={liability.TaskOperationId ?? "awaiting"}, return={liability.ReturnRequested}");
        }
        return lines;
    }

    public void Update(ulong tick)
    {
        this.currentTick = tick;
        this.ObserveBorrowedTools();
        foreach (StorageTransfer transfer in this.transfers.Values.ToArray())
            if (OwnerLifecycleGate.CanAdvance(transfer.Identity))
                this.UpdateTransfer(transfer);
    }

    public void CancelRuntime(string code)
    {
        foreach (CompanionRecord record in this.registry.Active)
        {
            foreach (StorageLiabilityRecord liability in record.StorageLiabilities.Where(candidate => candidate.Kind == StorageLiabilityKinds.BorrowedTool))
                this.MarkReturnPending(record.Identity, liability);
        }
        foreach (StorageTransfer transfer in this.transfers.Values.ToArray())
            this.FailTransfer(transfer, code, "The uncommitted storage operation was cancelled by a lifecycle gate.", scheduleReturnRetry: transfer.Kind == TransferKind.ReturnLiability);
        this.transfers.Clear();
    }

    public void Cancel(CompanionIdentity identity, string code)
    {
        if (this.registry.TryGet(identity, out CompanionRecord record))
        {
            foreach (StorageLiabilityRecord liability in record.StorageLiabilities.Where(candidate => candidate.Kind == StorageLiabilityKinds.BorrowedTool))
                this.MarkReturnPending(record.Identity, liability);
        }
        if (this.transfers.TryGetValue(identity, out StorageTransfer? transfer))
            this.FailTransfer(transfer, code, "The uncommitted storage operation was cancelled; committed responsibilities remain explicit.", transfer.Kind == TransferKind.ReturnLiability);
    }

    private StorageActionResult StartTransfer(CompanionRecord record, SourceSelection selection, TransferKind kind, int requestedCount, StorageLiabilityRecord? liability)
    {
        CompanionIdentity identity = record.Identity;
        if (this.transfers.ContainsKey(identity) || !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return StorageActionResult.Failure("COMPANION-BUSY", $"{identity} already has an active world or storage transaction.");
        if (kind != TransferKind.ReturnLiability && this.inventories.Count(identity) >= CompanionInventoryStore.Capacity)
            return StorageActionResult.Failure("BAG-FULL", "The Yui bag has no free stack slot; the Chest remains unchanged.");
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null || !ReferenceEquals(body.currentLocation, selection.Location))
            return StorageActionResult.Failure("BODY-LOCATION-UNAVAILABLE", "The Yui body must be in the same location as the authorized Chest.");
        Vector2? approach = FindApproach(selection.Location, selection.Tile, body);
        if (approach is null)
            return StorageActionResult.Failure("CHEST-UNREACHABLE", "No open adjacent tile exists for the authorized Chest.");

        string operationId = $"{StorageOperationPrefix}{Guid.NewGuid():N}";
        var transfer = new StorageTransfer(identity, operationId, kind, selection.Authorization, selection.Location, selection.Chest, selection.Tile, approach.Value, selection.Sources, requestedCount, liability, record.Mode)
        {
            CreatedTick = this.currentTick,
            LastPosition = body.Position,
            LastProgressTick = this.currentTick,
        };
        this.transfers.Add(identity, transfer);
        record.ActiveTransactionId = operationId;
        record.Mode = CompanionModes.Wait;
        body.controller = null;
        body.Halt();
        return StorageActionResult.Success("STORAGE-SCHEDULED", $"{kind} {operationId} will approach and revalidate authorized Chest {selection.Authorization.LocationKey}@{selection.Tile.X},{selection.Tile.Y}.");
    }

    private StorageActionResult TryBeginReturn(CompanionRecord record, StorageLiabilityRecord liability, bool explicitRequest)
    {
        CompanionIdentity identity = record.Identity;
        if (this.transfers.ContainsKey(identity) || !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return explicitRequest
                ? StorageActionResult.Failure("COMPANION-BUSY", "The companion must finish its current transaction before returning storage responsibility.")
                : StorageActionResult.Failure("RETURN-DEFERRED", "Return is waiting for the current transaction.");
        Item? item = this.FindResponsibleItem(identity, liability.ResponsibilityId);
        if (item is null)
        {
            this.BlockStorage(record, liability.ResponsibilityId, "The responsible real item is missing from the Yui bag; automatic compensation is forbidden.");
            return StorageActionResult.Failure("RESPONSIBLE-ITEM-MISSING", record.LastStorageFailure!);
        }
        AuthorizedChestRecord? authorization = this.registry.AuthorizedChests.FirstOrDefault(candidate => IsSource(liability, candidate));
        if (authorization is null || !TryResolveChest(authorization, out GameLocation location, out Chest chest))
        {
            this.nextReturnAttempt[liability.ResponsibilityId] = this.currentTick + ReturnRetryTicks;
            return StorageActionResult.Success("RETURN-PENDING", "The original authorized Chest is unavailable; the exact item remains in the Yui bag with its persisted responsibility.");
        }
        if (!this.bodies.TryGetBody(identity, out NPC body) || !ReferenceEquals(body.currentLocation, location))
        {
            this.nextReturnAttempt[liability.ResponsibilityId] = this.currentTick + ReturnRetryTicks;
            return StorageActionResult.Success("RETURN-PENDING", "The Yui body is not in the source Chest location; the exact item remains pending in its bag.");
        }
        Vector2 tile = new(authorization.TileX, authorization.TileY);
        Vector2? approach = FindApproach(location, tile, body);
        if (approach is null)
            return StorageActionResult.Success("RETURN-PENDING", "No safe adjacent tile currently exists; the exact item remains pending in its bag.");

        var selection = new SourceSelection(authorization, location, chest, tile, new List<SourceStack> { new(item, item.Stack, FindExactIndex(this.inventories.Get(identity), item)) });
        return this.StartTransfer(record, selection, TransferKind.ReturnLiability, item.Stack, liability);
    }

    private void ObserveBorrowedTools()
    {
        foreach (CompanionRecord record in this.registry.Active)
        {
            foreach (StorageLiabilityRecord liability in record.StorageLiabilities.Where(candidate => candidate.Kind == StorageLiabilityKinds.BorrowedTool).ToArray())
            {
                string? active = record.ActiveTransactionId;
                if (liability.TaskOperationId is null)
                {
                    if (this.currentTick >= liability.CreatedTick + TaskStartGraceTicks)
                        this.MarkReturnPending(record.Identity, liability);
                }
                else if (active != liability.TaskOperationId)
                {
                    this.MarkReturnPending(record.Identity, liability);
                }

                if (liability.ReturnRequested
                    && !this.transfers.ContainsKey(record.Identity)
                    && string.IsNullOrWhiteSpace(record.ActiveTransactionId)
                    && (!this.nextReturnAttempt.TryGetValue(liability.ResponsibilityId, out ulong next) || this.currentTick >= next))
                {
                    StorageActionResult result = this.TryBeginReturn(record, liability, explicitRequest: false);
                    if (!result.IsSuccess)
                        this.monitor.Log($"HY-STORAGE-{result.Code}: {result.Message}", LogLevel.Warn);
                }
            }
        }
    }

    private void UpdateTransfer(StorageTransfer transfer)
    {
        if (!this.IsCurrent(transfer)
            || !this.registry.TryGet(transfer.Identity, out CompanionRecord record)
            || record.ActiveTransactionId != transfer.OperationId)
        {
            this.FailTransfer(transfer, "TRANSFER-STALE", "Storage authority or transaction identity changed before commit.", transfer.Kind == TransferKind.ReturnLiability);
            return;
        }
        if (!this.bodies.TryGetBody(transfer.Identity, out NPC body) || !ReferenceEquals(body.currentLocation, transfer.Location))
        {
            this.FailTransfer(transfer, "BODY-LOCATION-CHANGED", "The Yui body left the source Chest location before commit.", transfer.Kind == TransferKind.ReturnLiability);
            return;
        }
        if (!TryResolveChest(transfer.Authorization, out GameLocation location, out Chest chest)
            || !ReferenceEquals(location, transfer.Location)
            || !ReferenceEquals(chest, transfer.Chest))
        {
            this.FailTransfer(transfer, "CHEST-CHANGED", "The authorized location/tile/token no longer resolves to the exact reserved Chest.", transfer.Kind == TransferKind.ReturnLiability);
            return;
        }
        if (Manhattan(body.Tile, transfer.Tile) == 1)
        {
            body.controller = null;
            body.Halt();
            body.faceDirection(TaskNavigationService.FacingToward(body.Tile, transfer.Tile));
            if (!transfer.LockRequested)
                this.RequestTransferLocks(transfer);
            return;
        }
        if (transfer.LockRequested)
        {
            if (this.currentTick >= transfer.LockRequestedAt + ReturnRetryTicks)
                this.FailTransfer(transfer, "STORAGE-LOCK-TIMEOUT", "The bounded mutex wait expired before commit; nothing moved.", transfer.Kind == TransferKind.ReturnLiability);
            return;
        }
        if (body.controller is not null && body.Position != transfer.LastPosition)
        {
            transfer.LastPosition = body.Position;
            transfer.LastProgressTick = this.currentTick;
        }
        else if (body.controller is not null && this.currentTick - transfer.LastProgressTick >= 300)
        {
            body.controller = null;
            body.Halt();
            transfer.NextPathTick = this.currentTick + 30;
            transfer.LastProgressTick = this.currentTick;
        }
        if (body.controller is not null || this.currentTick < transfer.NextPathTick)
            return;
        if (transfer.PathAttempts >= MaximumPathAttempts)
        {
            this.FailTransfer(transfer, "CHEST-PATH-EXHAUSTED", "The bounded path budget could not reach the authorized Chest.", transfer.Kind == TransferKind.ReturnLiability);
            return;
        }
        transfer.PathAttempts++;
        transfer.NextPathTick = this.currentTick + 60;
        transfer.LastPosition = body.Position;
        transfer.LastProgressTick = this.currentTick;
        int facing = TaskNavigationService.FacingToward(transfer.ApproachTile, transfer.Tile);
        body.controller = CompanionPathing.CreateController(body, transfer.Location, transfer.ApproachTile.ToPoint(), facing, SearchLimit);
    }

    private void RequestTransferLocks(StorageTransfer transfer)
    {
        transfer.LockRequested = true;
        transfer.LockRequestedAt = this.currentTick;
        NetMutex chestMutex = transfer.Chest.GetMutex();
        chestMutex.RequestLock(
            acquired: () =>
            {
                if (!this.IsCurrent(transfer))
                {
                    chestMutex.ReleaseLock();
                    return;
                }
                NetMutex bagMutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(CompanionInventoryStore.GetNamespace(transfer.Identity));
                bagMutex.RequestLock(
                    acquired: () =>
                    {
                        try
                        {
                            if (!this.IsCurrent(transfer))
                                return;
                            this.CommitTransfer(transfer);
                        }
                        finally
                        {
                            bagMutex.ReleaseLock();
                            chestMutex.ReleaseLock();
                        }
                    },
                    failed: () =>
                    {
                        chestMutex.ReleaseLock();
                        this.FailTransfer(transfer, "BAG-LOCK-FAILED", "The Yui bag mutex could not be acquired after the Chest lock; nothing moved.", transfer.Kind == TransferKind.ReturnLiability);
                    }
                );
            },
            failed: () => this.FailTransfer(transfer, "CHEST-LOCK-FAILED", "The Chest mutex could not be acquired; nothing moved.", transfer.Kind == TransferKind.ReturnLiability)
        );
    }

    private void CommitTransfer(StorageTransfer transfer)
    {
        if (!this.CanCommitTransfer(transfer, out CompanionRecord record, out Chest chest, out Inventory bag, out string failure))
        {
            this.FailTransfer(transfer, "LOCKED-REVALIDATION-FAILED", failure, transfer.Kind == TransferKind.ReturnLiability);
            return;
        }

        StorageActionResult result = transfer.Kind switch
        {
            TransferKind.BorrowTool => this.CommitBorrow(record, transfer, chest, bag),
            TransferKind.TakeMaterial => this.CommitMaterialTake(record, transfer, chest, bag),
            TransferKind.ReturnLiability => this.CommitReturn(record, transfer, chest, bag),
            _ => StorageActionResult.Failure("UNKNOWN-STORAGE-TRANSFER", "The storage transfer kind is unsupported."),
        };
        if (result.IsSuccess)
            this.CompleteTransfer(transfer, result.Code, result.Message);
        else
            this.FailTransfer(transfer, result.Code, result.Message, transfer.Kind == TransferKind.ReturnLiability);
    }

    private StorageActionResult CommitBorrow(CompanionRecord record, StorageTransfer transfer, Chest chest, Inventory bag)
    {
        SourceStack source = transfer.Sources[0];
        int sourceIndex = FindExactIndex(chest.Items, source.Item);
        if (sourceIndex < 0 || source.Item.Stack != source.ExpectedStack || source.Item is not Tool || source.Item.QualifiedItemId != transfer.QualifiedItemId)
            return StorageActionResult.Failure("TOOL-CHANGED", "The exact reserved Tool or stack changed under the lock.");
        if (FindExactIndex(bag, source.Item) >= 0)
            return StorageActionResult.Failure("TOOL-REFERENCE-DUPLICATED", "The reserved Tool reference already appears in the Yui bag; no additional move was attempted.");
        if (this.inventories.Count(record.Identity) >= CompanionInventoryStore.Capacity)
            return StorageActionResult.Failure("BAG-FULL", "The Yui bag filled before locked commit; the Tool stayed in the Chest.");
        if (source.Item.modData.ContainsKey(StorageTags.ResponsibilityId))
            return StorageActionResult.Failure("ITEM-ALREADY-RESPONSIBLE", "The Tool already carries another storage responsibility.");

        StorageLiabilityRecord liability = CreateLiability(transfer, sourceIndex, StorageLiabilityKinds.BorrowedTool, source.Item.Stack);
        source.Item.modData[StorageTags.ResponsibilityId] = liability.ResponsibilityId;
        bool bagAdded = false;
        try
        {
            AddToFirstFreeSlot(bag, source.Item);
            bagAdded = true;
            chest.Items.RemoveAt(sourceIndex);
            record.StorageLiabilities.Add(liability);
            return StorageActionResult.Success("TOOL-BORROWED", $"Moved the same real {source.Item.QualifiedItemId} instance into {record.Identity}'s bag; responsibility={liability.ResponsibilityId}.");
        }
        catch (Exception ex)
        {
            bool rolledBack = RollBackWholeMove(chest.Items, bag, source.Item, sourceIndex, bagAdded);
            if (rolledBack)
                source.Item.modData.Remove(StorageTags.ResponsibilityId);
            else
            {
                PreserveFaultLiability(record, liability, bag, source.Item);
                this.BlockStorage(record, liability.ResponsibilityId, $"Tool borrow rollback could not prove one responsibility location: {ex.Message}");
            }
            return StorageActionResult.Failure(rolledBack ? "TOOL-BORROW-ROLLED-BACK" : "STORAGE-FAULTED", rolledBack ? "Tool borrow failed and the same instance was restored to its Chest." : record.LastStorageFailure!);
        }
    }

    private StorageActionResult CommitMaterialTake(CompanionRecord record, StorageTransfer transfer, Chest chest, Inventory bag)
    {
        if (this.inventories.Count(record.Identity) >= CompanionInventoryStore.Capacity)
            return StorageActionResult.Failure("BAG-FULL", "The Yui bag filled before locked commit; every material stack stayed unchanged.");
        int remaining = transfer.RequestedCount;
        List<MaterialMutation> plan = new();
        foreach (SourceStack expected in transfer.Sources)
        {
            int index = FindExactIndex(chest.Items, expected.Item);
            if (index < 0 || expected.Item.Stack != expected.ExpectedStack || expected.Item.QualifiedItemId != transfer.QualifiedItemId)
                return StorageActionResult.Failure("MATERIAL-CHANGED", "A reserved material reference or stack changed under the lock.");
            if (FindExactIndex(bag, expected.Item) >= 0)
                return StorageActionResult.Failure("MATERIAL-REFERENCE-DUPLICATED", "A reserved material reference already appears in the Yui bag; no additional move was attempted.");
            if (!expected.Item.canStackWith(transfer.Sources[0].Item))
                return StorageActionResult.Failure("MATERIAL-INCOMPATIBLE", "Reserved source stacks are no longer legally compatible.");
            int take = Math.Min(remaining, expected.Item.Stack);
            plan.Add(new MaterialMutation(expected.Item, index, expected.Item.Stack, take));
            remaining -= take;
            if (remaining == 0)
                break;
        }
        if (remaining != 0)
            return StorageActionResult.Failure("MATERIAL-COUNT-CHANGED", "The exact requested count is no longer available in the reserved stacks.");

        bool wholeSingleStack = plan.Count == 1 && plan[0].Take == plan[0].Before;
        Item withdrawn = wholeSingleStack ? plan[0].Item : plan[0].Item.getOne();
        withdrawn.Stack = transfer.RequestedCount;
        StorageLiabilityRecord liability = CreateLiability(transfer, plan[0].Index, StorageLiabilityKinds.WithdrawnMaterial, transfer.RequestedCount);
        withdrawn.modData[StorageTags.ResponsibilityId] = liability.ResponsibilityId;
        bool bagAdded = false;
        try
        {
            AddToFirstFreeSlot(bag, withdrawn);
            bagAdded = true;
            foreach (MaterialMutation mutation in plan.OrderByDescending(candidate => candidate.Index))
            {
                if (mutation.Take == mutation.Before)
                    chest.Items.RemoveAt(mutation.Index);
                else
                    mutation.Item.Stack = mutation.Before - mutation.Take;
            }
            record.StorageLiabilities.Add(liability);
            return StorageActionResult.Success("MATERIAL-TAKEN", $"Moved exact {transfer.QualifiedItemId} x{transfer.RequestedCount} into {record.Identity}'s bag; responsibility={liability.ResponsibilityId}.");
        }
        catch (Exception ex)
        {
            bool rolledBack = RollBackMaterialTake(chest.Items, bag, withdrawn, plan, bagAdded);
            if (rolledBack)
                withdrawn.modData.Remove(StorageTags.ResponsibilityId);
            else
            {
                PreserveFaultLiability(record, liability, bag, withdrawn);
                this.BlockStorage(record, liability.ResponsibilityId, $"Material withdrawal rollback could not prove exact source counts: {ex.Message}");
            }
            return StorageActionResult.Failure(rolledBack ? "MATERIAL-TAKE-ROLLED-BACK" : "STORAGE-FAULTED", rolledBack ? "Material withdrawal failed and every exact source count was restored." : record.LastStorageFailure!);
        }
    }

    private StorageActionResult CommitReturn(CompanionRecord record, StorageTransfer transfer, Chest chest, Inventory bag)
    {
        StorageLiabilityRecord liability = transfer.Liability!;
        if (!record.StorageLiabilities.Contains(liability))
            return StorageActionResult.Failure("RESPONSIBILITY-CHANGED", "The persisted storage responsibility changed before locked return.");
        Item? item = this.FindResponsibleItem(transfer.Identity, liability.ResponsibilityId);
        if (item is null || item.QualifiedItemId != liability.QualifiedItemId || item.Stack <= 0 || item.Stack > liability.MaximumResponsibleStack)
            return StorageActionResult.Failure("RESPONSIBLE-ITEM-CHANGED", "The exact responsible bag item changed before locked return.");
        int bagIndex = FindExactIndex(bag, item);
        if (bagIndex < 0)
            return StorageActionResult.Failure("RESPONSIBLE-ITEM-MISSING", "The exact responsible item is no longer in the Yui bag.");
        if (FindExactIndex(chest.Items, item) >= 0)
        {
            this.BlockStorage(record, liability.ResponsibilityId, "The same responsible Item reference appears in both the Yui bag and source Chest; later storage writes are blocked without deleting either reference.");
            return StorageActionResult.Failure("RESPONSIBLE-ITEM-DUPLICATED", record.LastStorageFailure!);
        }

        if (liability.Kind == StorageLiabilityKinds.BorrowedTool)
            return this.ReturnWholeItem(record, liability, item, bagIndex, chest, bag);
        return this.ReturnMaterial(record, liability, item, bagIndex, chest, bag);
    }

    private StorageActionResult ReturnWholeItem(CompanionRecord record, StorageLiabilityRecord liability, Item item, int bagIndex, Chest chest, Inventory bag)
    {
        if (chest.Items.Count(candidate => candidate is not null) >= chest.GetActualCapacity())
            return StorageActionResult.Failure("SOURCE-CHEST-FULL", "The original Chest has no slot for the borrowed Tool; it remains pending in the Yui bag.");
        bool chestAdded = false;
        try
        {
            AddToFirstFreeSlot(chest.Items, item);
            chestAdded = true;
            bag.RemoveAt(bagIndex);
            item.modData.Remove(StorageTags.ResponsibilityId);
            item.modData.Remove(StorageTags.ReturnPending);
            record.StorageLiabilities.Remove(liability);
            record.PendingResponsibilities.RemoveAll(candidate => candidate.ResponsibilityId == liability.ResponsibilityId);
            this.nextReturnAttempt.Remove(liability.ResponsibilityId);
            return StorageActionResult.Success("TOOL-RETURNED", $"Returned the same real {item.QualifiedItemId} instance to its original Chest.");
        }
        catch (Exception ex)
        {
            bool rolledBack = RollBackReturnWhole(chest.Items, bag, item, bagIndex, chestAdded, liability.ResponsibilityId);
            if (rolledBack)
            {
                if (!record.StorageLiabilities.Contains(liability))
                    record.StorageLiabilities.Add(liability);
                this.MarkReturnPending(record.Identity, liability);
            }
            else
            {
                PreserveFaultLiability(record, liability, bag, item);
                this.MarkReturnPending(record.Identity, liability);
                this.BlockStorage(record, liability.ResponsibilityId, $"Tool return rollback could not prove one responsibility location: {ex.Message}");
            }
            return StorageActionResult.Failure(rolledBack ? "TOOL-RETURN-ROLLED-BACK" : "STORAGE-FAULTED", rolledBack ? "Tool return failed and the same instance remains in the Yui bag." : record.LastStorageFailure!);
        }
    }

    private StorageActionResult ReturnMaterial(CompanionRecord record, StorageLiabilityRecord liability, Item item, int bagIndex, Chest chest, Inventory bag)
    {
        List<(Item Item, int Before, int Add)> merges = new();
        int remaining = item.Stack;
        foreach (Item candidate in chest.Items.Where(candidate => candidate is not null
            && !candidate.modData.ContainsKey(StorageTags.ResponsibilityId)
            && CanMergeResponsibleItem(candidate, item)))
        {
            int add = Math.Min(remaining, candidate.getRemainingStackSpace());
            if (add > 0)
            {
                merges.Add((candidate, candidate.Stack, add));
                remaining -= add;
            }
            if (remaining == 0)
                break;
        }
        bool needsSlot = remaining > 0;
        if (needsSlot && chest.Items.Count(candidate => candidate is not null) >= chest.GetActualCapacity())
            return StorageActionResult.Failure("SOURCE-CHEST-FULL", "The original Chest cannot accept the unused exact material stack; it remains pending in the Yui bag.");

        int originalStack = item.Stack;
        bool chestAdded = false;
        try
        {
            foreach ((Item target, _, int add) in merges)
            {
                target.Stack += add;
                item.Stack -= add;
            }
            if (item.Stack > 0)
            {
                AddToFirstFreeSlot(chest.Items, item);
                chestAdded = true;
            }
            bag.RemoveAt(bagIndex);
            item.modData.Remove(StorageTags.ResponsibilityId);
            item.modData.Remove(StorageTags.ReturnPending);
            record.StorageLiabilities.Remove(liability);
            record.PendingResponsibilities.RemoveAll(candidate => candidate.ResponsibilityId == liability.ResponsibilityId);
            this.nextReturnAttempt.Remove(liability.ResponsibilityId);
            return StorageActionResult.Success("MATERIAL-RETURNED", $"Returned unused {liability.QualifiedItemId} x{originalStack} to its original Chest by exact instance or legal stack merge.");
        }
        catch (Exception ex)
        {
            bool rolledBack = RollBackMaterialReturn(chest.Items, bag, item, bagIndex, originalStack, merges, chestAdded, liability.ResponsibilityId);
            if (rolledBack)
            {
                if (!record.StorageLiabilities.Contains(liability))
                    record.StorageLiabilities.Add(liability);
                this.MarkReturnPending(record.Identity, liability);
            }
            else
            {
                PreserveFaultLiability(record, liability, bag, item);
                this.MarkReturnPending(record.Identity, liability);
                this.BlockStorage(record, liability.ResponsibilityId, $"Material return rollback could not prove exact destination counts: {ex.Message}");
            }
            return StorageActionResult.Failure(rolledBack ? "MATERIAL-RETURN-ROLLED-BACK" : "STORAGE-FAULTED", rolledBack ? "Material return failed and the responsible exact stack remains in the Yui bag." : record.LastStorageFailure!);
        }
    }

    private bool CanCommitTransfer(StorageTransfer transfer, out CompanionRecord record, out Chest chest, out Inventory bag, out string failure)
    {
        bag = this.inventories.Get(transfer.Identity);
        if (!this.registry.TryGet(transfer.Identity, out record!) || !this.CanCommit(transfer.Identity, record) || record.ActiveTransactionId != transfer.OperationId)
        {
            chest = transfer.Chest;
            failure = "Lifecycle, authority, identity, or transaction changed while locks were pending.";
            return false;
        }
        if (!TryResolveChest(transfer.Authorization, out GameLocation location, out chest)
            || !this.registry.AuthorizedChests.Any(candidate => candidate.Identity == transfer.Authorization.Identity
                && candidate.ChestToken == transfer.Authorization.ChestToken)
            || !ReferenceEquals(location, transfer.Location)
            || !ReferenceEquals(chest, transfer.Chest)
            || !chest.GetMutex().IsLockHeld()
            || !Game1.player.team.GetOrCreateGlobalInventoryMutex(CompanionInventoryStore.GetNamespace(transfer.Identity)).IsLockHeld()
            || !this.bodies.TryGetBody(transfer.Identity, out NPC body)
            || !ReferenceEquals(body.currentLocation, transfer.Location)
            || Manhattan(body.Tile, transfer.Tile) != 1)
        {
            failure = "The exact Chest, both mutexes, or physical adjacency changed before commit.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private void CompleteTransfer(StorageTransfer transfer, string code, string message)
    {
        if (this.registry.TryGet(transfer.Identity, out CompanionRecord record) && record.ActiveTransactionId == transfer.OperationId)
        {
            record.ActiveTransactionId = null;
            record.Mode = transfer.ResumeMode;
            if (!record.StorageWriteBlocked)
                record.LastStorageFailure = null;
        }
        this.ReleaseRuntime(transfer);
        this.monitor.Log($"HY-STORAGE-{code}: {message}", LogLevel.Info);
    }

    private void FailTransfer(StorageTransfer transfer, string code, string message, bool scheduleReturnRetry)
    {
        if (!this.transfers.TryGetValue(transfer.Identity, out StorageTransfer? current) || !ReferenceEquals(current, transfer))
            return;
        if (this.registry.TryGet(transfer.Identity, out CompanionRecord record) && record.ActiveTransactionId == transfer.OperationId)
        {
            record.ActiveTransactionId = null;
            record.Mode = transfer.ResumeMode;
            if (!record.StorageWriteBlocked)
                record.LastStorageFailure = $"{code}: {message}";
        }
        if (scheduleReturnRetry && transfer.Liability is not null)
        {
            this.MarkReturnPending(transfer.Identity, transfer.Liability);
            this.nextReturnAttempt[transfer.Liability.ResponsibilityId] = this.currentTick + ReturnRetryTicks;
        }
        this.ReleaseRuntime(transfer);
        this.monitor.Log($"HY-STORAGE-{code}: {message}", LogLevel.Warn);
    }

    private void ReleaseRuntime(StorageTransfer transfer)
    {
        if (this.bodies.TryGetBody(transfer.Identity, out NPC body))
        {
            body.controller = null;
            body.Halt();
        }
        if (this.transfers.TryGetValue(transfer.Identity, out StorageTransfer? current) && ReferenceEquals(current, transfer))
            this.transfers.Remove(transfer.Identity);
    }

    private void RequestAuthorizationRemoval(CompanionRecord record, AuthorizedChestRecord authorization, GameLocation location, Chest chest, Action<StorageActionResult> completed)
    {
        NetMutex mutex = chest.GetMutex();
        mutex.RequestLock(
            acquired: () =>
            {
                StorageActionResult result;
                try
                {
                    bool unchanged = this.CanCommit(record.Identity, record)
                        && !this.OwnerHasActiveTransaction(record.OwnerId)
                        && TryGetChestAt(location, authorization.TileX, authorization.TileY, out Chest current)
                        && ReferenceEquals(current, chest)
                        && ChestToken(current) == authorization.ChestToken;
                    result = unchanged && this.registry.RemoveAuthorization(authorization.Identity)
                        ? StorageActionResult.Success("CHEST-UNAUTHORIZED", $"Removed authorization {authorization.LocationKey}@{authorization.TileX},{authorization.TileY}.")
                        : StorageActionResult.Failure("CHEST-CHANGED", "The exact authorized Chest changed before cancellation; no replacement was modified.");
                }
                catch (Exception ex)
                {
                    result = StorageActionResult.Failure("UNAUTHORIZE-FAILED", $"Authorization cancellation failed without moving items: {ex.Message}");
                }
                finally
                {
                    mutex.ReleaseLock();
                }
                completed(result);
            },
            failed: () => completed(StorageActionResult.Failure("CHEST-LOCK-FAILED", "The Chest mutex could not be acquired; authorization was unchanged."))
        );
    }

    private bool TryFindSource(CompanionIdentity identity, string qualifiedItemId, bool requireTool, int requestedCount, out SourceSelection selection, out string failure)
    {
        selection = default!;
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
        {
            failure = "Summon the Yui in a location containing an authorized Chest.";
            return false;
        }
        GameLocation bodyLocation = body.currentLocation;
        foreach (AuthorizedChestRecord authorization in this.registry.AuthorizedChests
            .Where(candidate => candidate.OwnerId == identity.OwnerId
                && candidate.LocationKey == bodyLocation.NameOrUniqueName
                && candidate.IsStructure == bodyLocation.isStructure.Value)
            .OrderBy(candidate => Manhattan(body.Tile, new Vector2(candidate.TileX, candidate.TileY))))
        {
            if (!TryResolveChest(authorization, out GameLocation location, out Chest chest) || !ReferenceEquals(location, bodyLocation))
                continue;
            List<SourceStack> sources = new();
            int remaining = requestedCount;
            Item? stackModel = null;
            for (int index = 0; index < chest.Items.Count && remaining > 0; index++)
            {
                Item? item = chest.Items[index];
                if (item is null || item.QualifiedItemId != qualifiedItemId || item.modData.ContainsKey(StorageTags.ResponsibilityId))
                    continue;
                if (requireTool && item is not Tool)
                    continue;
                if (!requireTool && (item is Tool || (stackModel is not null && !item.canStackWith(stackModel))))
                    continue;
                stackModel ??= item;
                int take = Math.Min(remaining, item.Stack);
                sources.Add(new SourceStack(item, item.Stack, index));
                remaining -= take;
                if (requireTool)
                    break;
            }
            if (sources.Count > 0 && (requireTool || remaining == 0))
            {
                selection = new SourceSelection(authorization, location, chest, new Vector2(authorization.TileX, authorization.TileY), sources);
                failure = string.Empty;
                return true;
            }
        }
        failure = "No current-location Chest authorized for this Owner contains the requested exact item/count.";
        return false;
    }

    private bool TryGetWritableRecord(CompanionIdentity identity, bool allowNewWrites, out CompanionRecord record, out StorageActionResult failure)
    {
        if (!this.registry.TryGet(identity, out record!))
        {
            failure = StorageActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist.");
            return false;
        }
        if (!identity.IsCanonical)
        {
            failure = StorageActionResult.Failure("SINGLE-COMPANION-PER-OWNER", "Only the Owner's current Yui identity can start storage writes.");
            return false;
        }
        if (!this.CanCommit(identity, record))
        {
            failure = StorageActionResult.Failure("STORAGE-WRITE-GATE", "A loaded host-authoritative save with free player control is required.");
            return false;
        }
        if (allowNewWrites && record.StorageWriteBlocked)
        {
            failure = StorageActionResult.Failure("STORAGE-WRITES-BLOCKED", record.LastStorageFailure ?? "A prior rollback fault blocks new storage writes.");
            return false;
        }
        failure = default;
        return true;
    }

    private bool CanCommit(CompanionIdentity identity, CompanionRecord record) =>
        Context.IsWorldReady
        && Context.IsMainPlayer
        && this.getLifecycleState() == LifecycleState.SaveReady
        && this.canMutateSave()
        && record.OwnerId == identity.OwnerId
        && OwnerLifecycleGate.CanAdvance(identity);

    private bool IsCurrent(StorageTransfer transfer) =>
        this.transfers.TryGetValue(transfer.Identity, out StorageTransfer? current)
        && ReferenceEquals(current, transfer)
        && this.getLifecycleState() == LifecycleState.SaveReady
        && Context.IsMainPlayer
        && this.canMutateSave();

    private bool OwnerHasActiveTransaction(long ownerId) =>
        this.registry.Active.Any(record => record.OwnerId == ownerId && !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
        || this.transfers.Keys.Any(identity => identity.OwnerId == ownerId);

    private void BlockStorage(CompanionRecord record, string responsibilityId, string detail)
    {
        record.StorageWriteBlocked = true;
        record.LastStorageFailure = detail;
        if (!record.PendingResponsibilities.Any(candidate => candidate.ResponsibilityId == responsibilityId))
        {
            record.PendingResponsibilities.Add(new PendingResponsibilityRecord
            {
                ResponsibilityId = responsibilityId,
                Kind = "StorageRollbackFault",
                Detail = detail,
            });
        }
        this.monitor.Log($"HY-STORAGE-FAULTED: {record.Identity} {detail}", LogLevel.Error);
    }

    private static void PreserveFaultLiability(CompanionRecord record, StorageLiabilityRecord liability, IList<Item> bag, Item item)
    {
        if (FindExactIndex(bag, item) >= 0)
        {
            item.modData[StorageTags.ResponsibilityId] = liability.ResponsibilityId;
            if (!record.StorageLiabilities.Contains(liability))
                record.StorageLiabilities.Add(liability);
        }
        else
        {
            item.modData.Remove(StorageTags.ResponsibilityId);
        }
    }

    private Item? FindResponsibleItem(CompanionIdentity identity, string responsibilityId) => this.inventories.Get(identity).FirstOrDefault(item => item is not null
        && item.modData.TryGetValue(StorageTags.ResponsibilityId, out string? value)
        && value == responsibilityId);

    private void MarkReturnPending(CompanionIdentity identity, StorageLiabilityRecord liability)
    {
        liability.ReturnRequested = true;
        Item? item = this.FindResponsibleItem(identity, liability.ResponsibilityId);
        if (item is not null)
            item.modData[StorageTags.ReturnPending] = liability.ResponsibilityId;
    }

    private static StorageLiabilityRecord CreateLiability(StorageTransfer transfer, int sourceIndex, string kind, int maximumStack)
    {
        string responsibilityId = Guid.NewGuid().ToString("N");
        return new StorageLiabilityRecord
        {
            ResponsibilityId = responsibilityId,
            ItemToken = responsibilityId,
            Kind = kind,
            QualifiedItemId = transfer.QualifiedItemId,
            MaximumResponsibleStack = maximumStack,
            SourceLocationKey = transfer.Authorization.LocationKey,
            SourceIsStructure = transfer.Authorization.IsStructure,
            SourceTileX = transfer.Authorization.TileX,
            SourceTileY = transfer.Authorization.TileY,
            SourceChestToken = transfer.Authorization.ChestToken,
            OriginalSourceSlot = sourceIndex,
            CreatedTick = transfer.CreatedTick,
        };
    }

    private static bool TryResolveChest(AuthorizedChestRecord authorization, out GameLocation location, out Chest chest)
    {
        chest = null!;
        location = Game1.getLocationFromName(authorization.LocationKey, authorization.IsStructure)!;
        return location is not null
            && !location.IsTemporary
            && TryGetChestAt(location, authorization.TileX, authorization.TileY, out chest)
            && IsEligibleChest(chest, authorization.OwnerId)
            && ChestToken(chest) == authorization.ChestToken;
    }

    private static bool TryGetChestAt(GameLocation location, int tileX, int tileY, out Chest chest)
    {
        if (location.Objects.TryGetValue(new Vector2(tileX, tileY), out SObject? value) && value is Chest match)
        {
            chest = match;
            return true;
        }
        chest = null!;
        return false;
    }

    private static bool IsEligibleChest(Chest chest, long ownerId) =>
        chest.playerChest.Value
        && !chest.fridge.Value
        && !chest.giftbox.Value
        && string.IsNullOrEmpty(chest.GlobalInventoryId)
        && chest.SpecialChestType is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest
        && (chest.owner.Value == ownerId || chest.owner.Value == 0);

    private static string? ChestToken(Chest chest) => chest.modData.TryGetValue(StorageTags.ChestToken, out string? token) ? token : null;

    private static bool IsSource(StorageLiabilityRecord liability, AuthorizedChestRecord authorization) =>
        liability.SourceLocationKey == authorization.LocationKey
        && liability.SourceIsStructure == authorization.IsStructure
        && liability.SourceTileX == authorization.TileX
        && liability.SourceTileY == authorization.TileY
        && liability.SourceChestToken == authorization.ChestToken;

    private static bool CanMergeResponsibleItem(Item destination, Item responsibleItem)
    {
        Item comparison = responsibleItem.getOne();
        comparison.modData.Remove(StorageTags.ResponsibilityId);
        comparison.modData.Remove(StorageTags.ReturnPending);
        return destination.canStackWith(comparison);
    }

    private Vector2? FindApproach(GameLocation location, Vector2 chestTile, NPC body) =>
        this.navigation.FindReachableCardinalApproach(body, location, chestTile, SearchLimit);

    private static int Manhattan(Vector2 first, Vector2 second) => (int)(Math.Abs(first.X - second.X) + Math.Abs(first.Y - second.Y));

    private static int FindExactIndex(IList<Item> items, Item item)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], item))
                return index;
        }
        return -1;
    }

    private static void AddToFirstFreeSlot(IList<Item> items, Item item)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index] is null)
            {
                items[index] = item;
                return;
            }
        }
        items.Add(item);
    }

    private static bool RollBackWholeMove(IList<Item> chest, IList<Item> bag, Item item, int sourceIndex, bool bagAdded)
    {
        try
        {
            bool inChest = FindExactIndex(chest, item) >= 0;
            bool inBag = FindExactIndex(bag, item) >= 0;
            if (inChest && inBag)
                bag.RemoveAt(FindExactIndex(bag, item));
            else if (!inChest && inBag)
            {
                bag.RemoveAt(FindExactIndex(bag, item));
                chest.Insert(Math.Min(sourceIndex, chest.Count), item);
            }
            else if (!inChest && !inBag && bagAdded)
                chest.Insert(Math.Min(sourceIndex, chest.Count), item);
            return FindExactIndex(chest, item) >= 0 && FindExactIndex(bag, item) < 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool RollBackMaterialTake(IList<Item> chest, IList<Item> bag, Item withdrawn, List<MaterialMutation> plan, bool bagAdded)
    {
        try
        {
            int bagIndex = FindExactIndex(bag, withdrawn);
            if (bagIndex >= 0)
                bag.RemoveAt(bagIndex);
            foreach (MaterialMutation mutation in plan.OrderBy(candidate => candidate.Index))
            {
                mutation.Item.Stack = mutation.Before;
                if (FindExactIndex(chest, mutation.Item) < 0)
                    chest.Insert(Math.Min(mutation.Index, chest.Count), mutation.Item);
            }
            return (!bagAdded || FindExactIndex(bag, withdrawn) < 0)
                && plan.All(mutation => FindExactIndex(chest, mutation.Item) >= 0 && mutation.Item.Stack == mutation.Before);
        }
        catch
        {
            return false;
        }
    }

    private static bool RollBackReturnWhole(IList<Item> chest, IList<Item> bag, Item item, int bagIndex, bool chestAdded, string responsibilityId)
    {
        try
        {
            if (chestAdded && FindExactIndex(chest, item) >= 0)
                chest.RemoveAt(FindExactIndex(chest, item));
            if (FindExactIndex(bag, item) < 0)
                bag.Insert(Math.Min(bagIndex, bag.Count), item);
            item.modData[StorageTags.ResponsibilityId] = responsibilityId;
            item.modData[StorageTags.ReturnPending] = responsibilityId;
            return FindExactIndex(bag, item) >= 0 && FindExactIndex(chest, item) < 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool RollBackMaterialReturn(IList<Item> chest, IList<Item> bag, Item item, int bagIndex, int originalStack, List<(Item Item, int Before, int Add)> merges, bool chestAdded, string responsibilityId)
    {
        try
        {
            if (chestAdded && FindExactIndex(chest, item) >= 0)
                chest.RemoveAt(FindExactIndex(chest, item));
            foreach ((Item target, int before, _) in merges)
                target.Stack = before;
            item.Stack = originalStack;
            item.modData[StorageTags.ResponsibilityId] = responsibilityId;
            item.modData[StorageTags.ReturnPending] = responsibilityId;
            if (FindExactIndex(bag, item) < 0)
                bag.Insert(Math.Min(bagIndex, bag.Count), item);
            return FindExactIndex(bag, item) >= 0
                && FindExactIndex(chest, item) < 0
                && item.Stack == originalStack
                && merges.All(entry => entry.Item.Stack == entry.Before);
        }
        catch
        {
            return false;
        }
    }

    private enum TransferKind
    {
        BorrowTool,
        TakeMaterial,
        ReturnLiability,
    }

    private sealed record SourceSelection(AuthorizedChestRecord Authorization, GameLocation Location, Chest Chest, Vector2 Tile, List<SourceStack> Sources);
    private sealed record SourceStack(Item Item, int ExpectedStack, int OriginalIndex);
    private sealed record MaterialMutation(Item Item, int Index, int Before, int Take);

    private sealed class StorageTransfer
    {
        public StorageTransfer(CompanionIdentity identity, string operationId, TransferKind kind, AuthorizedChestRecord authorization, GameLocation location, Chest chest, Vector2 tile, Vector2 approachTile, List<SourceStack> sources, int requestedCount, StorageLiabilityRecord? liability, string resumeMode)
        {
            this.Identity = identity;
            this.OperationId = operationId;
            this.Kind = kind;
            this.Authorization = authorization;
            this.Location = location;
            this.Chest = chest;
            this.Tile = tile;
            this.ApproachTile = approachTile;
            this.Sources = sources;
            this.RequestedCount = requestedCount;
            this.Liability = liability;
            this.ResumeMode = resumeMode;
            this.QualifiedItemId = liability?.QualifiedItemId ?? sources[0].Item.QualifiedItemId;
        }

        public CompanionIdentity Identity { get; }
        public string OperationId { get; }
        public TransferKind Kind { get; }
        public AuthorizedChestRecord Authorization { get; }
        public GameLocation Location { get; }
        public Chest Chest { get; }
        public Vector2 Tile { get; }
        public Vector2 ApproachTile { get; }
        public List<SourceStack> Sources { get; }
        public int RequestedCount { get; }
        public StorageLiabilityRecord? Liability { get; }
        public string ResumeMode { get; }
        public string QualifiedItemId { get; }
        public ulong CreatedTick { get; set; }
        public string Phase => this.LockRequested ? "WaitingLocks" : "Approach";
        public bool LockRequested { get; set; }
        public ulong LockRequestedAt { get; set; }
        public int PathAttempts { get; set; }
        public ulong NextPathTick { get; set; }
        public Vector2 LastPosition { get; set; }
        public ulong LastProgressTick { get; set; }
    }
}
