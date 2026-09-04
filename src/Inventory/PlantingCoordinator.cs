using System.Security.Cryptography;
using System.Text;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.Inventories;
using StardewValley.Network;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;

namespace YuiToIssho;

internal readonly record struct PlantingActionResult(bool IsSuccess, string Code, string Message)
{
    public static PlantingActionResult Success(string code, string message) => new(true, code, message);
    public static PlantingActionResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class PlantingCoordinator
{
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 30;
    private const int StuckTimeoutTicks = 300;
    private const int MaximumPathAttempts = 5;

    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionStorageCoordinator storage;
    private readonly PlantingPreviewService preview;
    private readonly TaskExecutionService execution;
    private readonly TaskNavigationService navigation;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, PlantingRuntime> runtimes = new();
    private ulong currentTick;

    public PlantingCoordinator(
        CompanionRegistry registry,
        CompanionBodyBinder bodies,
        CompanionInventoryStore inventories,
        CompanionStorageCoordinator storage,
        PlantingPreviewService preview,
        TaskExecutionService execution,
        TaskNavigationService navigation,
        CompanionAppearanceCoordinator appearance,
        IMonitor monitor)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.storage = storage;
        this.preview = preview;
        this.execution = execution;
        this.navigation = navigation;
        this.appearance = appearance;
        this.monitor = monitor;
    }

    public PlantSeedOptionsResult GetOptions(CompanionIdentity identity, Farmer owner, string? query) =>
        this.preview.GetOptions(identity, owner, query);

    public PlantingPreviewResult Preview(CompanionIdentity identity, Farmer owner, string seedOptionId, int count, PlantingScope scope) =>
        this.preview.Preview(identity, owner, seedOptionId, count, scope);

    public PlantingActionResult Start(CompanionIdentity identity, Farmer owner, string seedOptionId, int count, PlantingScope scope, string operationId)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return PlantingActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (identity.OwnerId != owner.UniqueMultiplayerID)
            return PlantingActionResult.Failure("NOT-OWNER", "The exact Owner must own the planting companion.");
        if (!IsBoundedOperationId(operationId))
            return PlantingActionResult.Failure("INVALID-OPERATION-ID", "OperationId must contain 1..128 non-control characters.");
        if (TaskReceiptStore.TryGet(record, operationId, out TaskExecutionResult receipt))
            return new PlantingActionResult(receipt.IsSuccess, receipt.Code, receipt.Message);
        if (record.PlantingTransaction is PlantingTransactionRecord existing && PlantingPhases.OwnsResponsibility(existing.Phase))
            return existing.RequestOperationId == operationId
                ? PlantingActionResult.Success("PLANT-ALREADY-ACTIVE", $"Planting {existing.PlantingId} is already {existing.Phase}.")
                : PlantingActionResult.Failure("PLANT-BUSY", $"{identity} already owns planting {existing.PlantingId}.");
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId)
            || record.CraftTransaction is CraftTransactionRecord craft && CraftPhases.OwnsResponsibility(craft.Phase))
            return PlantingActionResult.Failure("COMPANION-BUSY", "Finish the active item/world responsibility before planting.");
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null || !ReferenceEquals(body.currentLocation, owner.currentLocation))
            return PlantingActionResult.Failure("PLANT-BODY-REQUIRED", "Yui and the exact Owner must be together in the planting location.");
        if (this.inventories.PlantEscrowCount(identity) != 0)
            return PlantingActionResult.Failure("ORPHANED-PLANT-ESCROW", "Plant Escrow must be empty before a new planting transaction.");

        PlantingPreviewResult previewResult = this.preview.Preview(identity, owner, seedOptionId, count, scope);
        if (!previewResult.IsSuccess)
            return PlantingActionResult.Failure(previewResult.Code, previewResult.Message);
        if (!this.preview.TryResolveSelection(identity, seedOptionId, out string qualifiedItemId, out string code, out string message))
            return PlantingActionResult.Failure(code, message);
        IReadOnlyList<PlantingSeedSource> available = this.preview.CaptureSources(identity, owner.currentLocation!, qualifiedItemId);
        if (!TryBuildPlan(identity, qualifiedItemId, count, available, out List<PlantingRuntimeSource> sources))
            return PlantingActionResult.Failure("SEED-SUPPLY-INSUFFICIENT", "The complete real seed source plan changed before start; nothing moved.");

        string plantingId = Guid.NewGuid().ToString("N");
        var transaction = new PlantingTransactionRecord
        {
            PlantingId = plantingId,
            RequestOperationId = operationId,
            OwnerId = identity.OwnerId,
            SeedQualifiedItemId = qualifiedItemId,
            SeedPolicyVersion = PlantingConstants.SeedPolicyVersion,
            LocationKey = scope.LocationKey,
            AnchorX = scope.AnchorX,
            AnchorY = scope.AnchorY,
            EndX = scope.EndX,
            EndY = scope.EndY,
            Shape = scope.Shape,
            Radius = scope.Radius,
            RequestedCount = count,
            Phase = PlantingPhases.Planned,
            ReturnMode = record.Mode is CompanionModes.Follow or CompanionModes.Wait ? record.Mode : CompanionModes.Follow,
            SourcePlan = sources.Select(source => source.Record).ToList(),
            CreatedDay = Game1.Date.TotalDays,
            LastConfirmedDay = Game1.Date.TotalDays,
            UpdatedTick = this.currentTick,
        };
        record.PlantingTransaction = transaction;
        record.ActiveTransactionId = plantingId;
        record.Mode = CompanionModes.Wait;
        this.bodies.Halt(identity);
        var runtime = new PlantingRuntime(record, owner, transaction, sources);
        this.runtimes[identity] = runtime;
        this.BeginBagAcquisition(runtime);
        return PlantingActionResult.Success("PLANT-STARTED", $"Planting {plantingId} reserved an exact plan for {count} seed(s); world tiles are still unchanged.");
    }

    public PlantingActionResult Status(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record) || record.PlantingTransaction is not PlantingTransactionRecord transaction)
            return PlantingActionResult.Success("PLANT-IDLE", $"{identity} has no planting transaction; Plant Escrow={this.inventories.PlantEscrowCount(identity)}.");
        string step = transaction.CurrentStep is null ? "none" : $"{transaction.CurrentStep.Phase}@{transaction.CurrentStep.TileX},{transaction.CurrentStep.TileY}";
        return PlantingActionResult.Success(
            "PLANT-STATUS",
            $"planting={transaction.PlantingId}, phase={transaction.Phase}, planted={transaction.PlantedCount}/{transaction.RequestedCount}, escrowStacks={this.inventories.PlantEscrowCount(identity)}, step={step}, reason={transaction.LastFailure ?? "none"}.");
    }

    public PlantingActionResult Resume(CompanionIdentity identity, Farmer owner)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record) || record.PlantingTransaction is not PlantingTransactionRecord transaction)
            return PlantingActionResult.Failure("PLANT-IDLE", "There is no planting transaction to resume.");
        if (transaction.Phase is PlantingPhases.Completed or PlantingPhases.Cancelled)
            return PlantingActionResult.Success("PLANT-TERMINAL", $"Planting {transaction.PlantingId} is already {transaction.Phase}.");
        if (transaction.Phase == PlantingPhases.Faulted)
            return PlantingActionResult.Failure("PLANT-FAULTED", $"Planting {transaction.PlantingId} is faulted and preserves its evidence: {transaction.LastFailure ?? "unknown"}.");
        if (this.runtimes.ContainsKey(identity))
            return PlantingActionResult.Success("PLANT-ALREADY-ACTIVE", $"Planting {transaction.PlantingId} already has an active runtime.");
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null || owner.currentLocation is null
            || body.currentLocation.NameOrUniqueName != transaction.LocationKey || owner.currentLocation.NameOrUniqueName != transaction.LocationKey)
            return PlantingActionResult.Failure("PLANT-LOCATION-MISMATCH", "Yui and Owner must return to the frozen planting location before resume.");
        bool returning = transaction.Phase is PlantingPhases.ReturningSeeds or PlantingPhases.Cancelling;
        if (!this.TryRebuildRuntime(record, owner, out PlantingRuntime runtime, out string failure))
            return this.Fault(record, transaction, "PLANT-RECONCILE-FAILED", failure);
        if (!this.ReconcileCurrentStep(runtime, out PlantingActionResult reconciliation))
            return reconciliation;
        transaction.LastConfirmedDay = Game1.Date.TotalDays;
        transaction.LastFailure = null;
        transaction.Phase = returning
            ? PlantingPhases.ReturningSeeds
            : transaction.SourcePlan.Any(source => source.AcquiredQuantity < source.Quantity)
                ? PlantingPhases.AcquiringSeeds
                : PlantingPhases.Planting;
        transaction.UpdatedTick = this.currentTick;
        record.ActiveTransactionId = transaction.PlantingId;
        record.Mode = CompanionModes.Wait;
        this.runtimes[identity] = runtime;
        if (transaction.Phase == PlantingPhases.AcquiringSeeds)
            this.BeginBagAcquisition(runtime);
        return PlantingActionResult.Success("PLANT-RESUMED", $"Planting {transaction.PlantingId} resumed from a fresh world and inventory snapshot.");
    }

    public PlantingActionResult Cancel(CompanionIdentity identity, Farmer owner, string reason = "PLAYER-CANCELLED")
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record) || record.PlantingTransaction is not PlantingTransactionRecord transaction)
            return PlantingActionResult.Success("PLANT-IDLE", "There is no planting responsibility to cancel.");
        if (transaction.Phase is PlantingPhases.Completed or PlantingPhases.Cancelled)
            return PlantingActionResult.Success("PLANT-TERMINAL", $"Planting {transaction.PlantingId} is already {transaction.Phase}.");
        if (transaction.CurrentStep?.Phase is PlantingStepPhases.WorldCommitted or PlantingStepPhases.ReconcilingStep)
            return PlantingActionResult.Failure("PLANT-RECONCILE-REQUIRED", "Reconcile the frozen committed step before cancelling or returning any seed.");
        transaction.Phase = PlantingPhases.Cancelling;
        if (!this.runtimes.TryGetValue(identity, out PlantingRuntime? runtime)
            && !this.TryRebuildRuntime(record, owner, out runtime!, out string failure))
            return this.Fault(record, transaction, "PLANT-RETURN-UNAVAILABLE", failure);
        this.AbortCurrentStep(runtime, reason);
        transaction.Phase = PlantingPhases.ReturningSeeds;
        transaction.LastFailure = Bound(reason, 256);
        transaction.UpdatedTick = this.currentTick;
        runtime.IsCancelling = true;
        this.runtimes[identity] = runtime;
        return PlantingActionResult.Success("PLANT-CANCELLING", $"Planting {transaction.PlantingId} will return unused real seeds; {transaction.PlantedCount} planted crop(s) remain in the world.");
    }

    public void Update(ulong tick)
    {
        this.currentTick = tick;
        foreach (PlantingRuntime runtime in this.runtimes.Values.ToArray())
        {
            if (!this.IsCurrent(runtime))
            {
                this.DropRuntime(runtime);
                continue;
            }
            if (runtime.Transaction.Phase is PlantingPhases.ReturningSeeds or PlantingPhases.Cancelling)
                this.UpdateReturning(runtime);
            else if (runtime.Transaction.SourcePlan.Any(source => source.AcquiredQuantity < source.Quantity))
                this.UpdateAcquisition(runtime);
            else if (runtime.Transaction.Phase is PlantingPhases.SeedsEscrowed or PlantingPhases.Planting)
                this.UpdatePlanting(runtime);
        }
    }

    public void RestoreAfterLoad()
    {
        this.runtimes.Clear();
        foreach (CompanionRecord record in this.registry.All.Where(record => record.PlantingTransaction is not null))
        {
            PlantingTransactionRecord transaction = record.PlantingTransaction!;
            if (PlantingPhases.OwnsResponsibility(transaction.Phase))
            {
                if (transaction.Phase is not (PlantingPhases.Reconciling or PlantingPhases.Faulted or PlantingPhases.ReturningSeeds))
                    transaction.Phase = PlantingPhases.Paused;
                transaction.LastFailure ??= "LOAD-REQUIRES-RESUME";
                record.ActiveTransactionId = transaction.PlantingId;
            }
        }
    }

    public void Pause(CompanionIdentity identity, string reason)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record) || record.PlantingTransaction is not PlantingTransactionRecord transaction
            || !PlantingPhases.OwnsResponsibility(transaction.Phase))
            return;
        if (this.runtimes.TryGetValue(identity, out PlantingRuntime? runtime))
        {
            this.AbortCurrentStep(runtime, reason);
            this.DropRuntime(runtime);
        }
        if (transaction.Phase is not (PlantingPhases.ReturningSeeds or PlantingPhases.Cancelling or PlantingPhases.Reconciling or PlantingPhases.Faulted))
            transaction.Phase = PlantingPhases.Paused;
        transaction.LastFailure = Bound(reason, 256);
        transaction.UpdatedTick = this.currentTick;
        record.ActiveTransactionId = transaction.PlantingId;
        this.bodies.Halt(identity);
    }

    public void PauseAll(string reason)
    {
        foreach (CompanionRecord record in this.registry.All.ToArray())
            this.Pause(record.Identity, reason);
    }

    public void ClearRuntime()
    {
        foreach (PlantingRuntime runtime in this.runtimes.Values.ToArray())
            this.DropRuntime(runtime);
        this.runtimes.Clear();
    }

    private void BeginBagAcquisition(PlantingRuntime runtime)
    {
        if (runtime.BagRequestPending)
            return;
        runtime.BagRequestPending = true;
        this.inventories.RequestTransfer(runtime.Identity, () =>
        {
            if (!this.IsCurrent(runtime))
                return InventoryActionResult.Failure("PLANT-TRANSACTION-STALE", "Planting changed before bag acquisition.");
            try
            {
                foreach (PlantingRuntimeSource source in runtime.Sources
                    .Where(source => source.Record.SourceKind == PlantingSourceKinds.Bag && source.Record.AcquiredQuantity == 0)
                    .OrderByDescending(source => source.Record.SourceSlot))
                    MoveToEscrow(runtime.Transaction.PlantingId, source, this.inventories.GetPlantEscrow(runtime.Identity));
                runtime.Transaction.Phase = PlantingPhases.AcquiringSeeds;
                runtime.Transaction.UpdatedTick = this.currentTick;
                return InventoryActionResult.Success("PLANT-BAG-ACQUIRED", "Planned Yui-bag seed quantities moved into Plant Escrow.");
            }
            catch (Exception ex)
            {
                return InventoryActionResult.Failure("PLANT-BAG-SOURCE-CHANGED", $"A bag seed source changed before acquisition: {ex.GetType().Name}.");
            }
        }, result =>
        {
            runtime.BagRequestPending = false;
            if (!this.IsCurrent(runtime))
                return;
            runtime.BagAcquired = result.IsSuccess;
            if (!result.IsSuccess)
                this.BeginReturning(runtime, result.Code);
        });
    }

    private void UpdateAcquisition(PlantingRuntime runtime)
    {
        if (!runtime.BagAcquired)
        {
            this.BeginBagAcquisition(runtime);
            return;
        }
        if (runtime.LockPending)
            return;
        PlantingRuntimeSource[] unacquired = runtime.Sources
            .Where(source => source.Record.AcquiredQuantity == 0 && source.Record.SourceKind == PlantingSourceKinds.AuthorizedChest)
            .ToArray();
        string? nextStorage = unacquired.FirstOrDefault()?.Record.StorageId;
        PlantingRuntimeSource? source = unacquired
            .Where(source => source.Record.StorageId == nextStorage)
            .OrderByDescending(source => source.Record.SourceSlot)
            .FirstOrDefault();
        if (source is null)
        {
            runtime.Transaction.Phase = PlantingPhases.SeedsEscrowed;
            runtime.Transaction.UpdatedTick = this.currentTick;
            runtime.Transaction.Phase = PlantingPhases.Planting;
            return;
        }
        if (source.Chest is not CraftChestAccess access || !CompanionStorageCoordinator.IsCurrentCraftChest(access, runtime.Identity.OwnerId)
            || !this.bodies.TryGetBody(runtime.Identity, out NPC body) || !ReferenceEquals(body.currentLocation, access.Location))
        {
            this.BeginReturning(runtime, "PLANT-CHEST-SOURCE-CHANGED");
            return;
        }
        if (Manhattan(body.Tile, access.Tile) == 1)
        {
            this.bodies.Halt(runtime.Identity);
            body.faceDirection(TaskNavigationService.FacingToward(body.Tile, access.Tile));
            this.RequestChestAcquisition(runtime, source, access);
            return;
        }
        if (body.controller is not null || this.currentTick < runtime.NextPathTick)
            return;
        if (runtime.PathAttempts++ >= MaximumPathAttempts)
        {
            this.BeginReturning(runtime, "PLANT-CHEST-PATH-EXHAUSTED");
            return;
        }
        runtime.NextPathTick = this.currentTick + RepathDelayTicks;
        body.controller = CompanionPathing.CreateController(body, access.Location, access.ApproachTile.ToPoint(), TaskNavigationService.FacingToward(access.ApproachTile, access.Tile), PathSearchLimit);
    }

    private void RequestChestAcquisition(PlantingRuntime runtime, PlantingRuntimeSource source, CraftChestAccess access)
    {
        runtime.LockPending = true;
        NetMutex chestMutex = access.Chest.GetMutex();
        chestMutex.RequestLock(() =>
        {
            NetMutex bagMutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(CompanionInventoryStore.GetNamespace(runtime.Identity));
            bagMutex.RequestLock(() =>
            {
                string? failure = null;
                try
                {
                    if (!this.IsCurrent(runtime) || !CompanionStorageCoordinator.IsCurrentCraftChest(access, runtime.Identity.OwnerId))
                        failure = "PLANT-CHEST-SOURCE-CHANGED";
                    else
                        MoveToEscrow(runtime.Transaction.PlantingId, source, this.inventories.GetPlantEscrow(runtime.Identity));
                }
                catch
                {
                    failure = "PLANT-CHEST-SOURCE-CHANGED";
                }
                finally
                {
                    runtime.LockPending = false;
                    bagMutex.ReleaseLock();
                    chestMutex.ReleaseLock();
                }
                if (failure is not null)
                    this.BeginReturning(runtime, failure);
                else
                    runtime.PathAttempts = 0;
            }, () =>
            {
                runtime.LockPending = false;
                chestMutex.ReleaseLock();
                this.BeginReturning(runtime, "PLANT-BAG-LOCK-FAILED");
            });
        }, () =>
        {
            runtime.LockPending = false;
            this.BeginReturning(runtime, "PLANT-CHEST-LOCK-FAILED");
        });
    }

    private void UpdatePlanting(PlantingRuntime runtime)
    {
        if (runtime.CurrentTask is PlantingTileTask task)
        {
            this.UpdateTileTask(runtime, task);
            return;
        }
        if (runtime.Transaction.PlantedCount >= runtime.Transaction.RequestedCount)
        {
            this.CompletePlanting(runtime);
            return;
        }
        if (!this.bodies.TryGetBody(runtime.Identity, out NPC body) || body.currentLocation is null
            || !ReferenceEquals(body.currentLocation, runtime.Owner.currentLocation)
            || body.currentLocation.NameOrUniqueName != runtime.Transaction.LocationKey)
        {
            this.Pause(runtime.Identity, "PLANT-LOCATION-CHANGED");
            return;
        }
        Item? seed = FindNextEscrowSeed(runtime, out PlantingRuntimeSource? seedSource);
        if (seed is null || seedSource is null)
        {
            this.Fault(runtime.Record, runtime.Transaction, "PLANT-ESCROW-INCOMPLETE", "No Plant Escrow source can explain the remaining requested seed count.");
            this.DropRuntime(runtime);
            return;
        }
        PlantingScope scope = Scope(runtime.Transaction);
        PlantSlotPreview[] slots = this.preview.CaptureEligibleSlots(runtime.Owner, body.currentLocation, scope, seed);
        int remaining = runtime.Transaction.RequestedCount - runtime.Transaction.PlantedCount;
        if (slots.Length < remaining)
        {
            this.Pause(runtime.Identity, runtime.Transaction.PlantedCount == 0 ? "PLANT-SCOPE-INSUFFICIENT" : "PLANT-SCOPE-INSUFFICIENT-AFTER-PARTIAL");
            return;
        }

        foreach (PlantSlotPreview slot in slots.Where(slot => !runtime.BlockedSlots.Contains(slot.StableId)).Take(8))
        {
            Vector2 targetTile = new(slot.TileX, slot.TileY);
            Vector2? approach = this.navigation.FindReachableCardinalApproach(body, body.currentLocation, targetTile, PathSearchLimit);
            if (approach is null)
            {
                runtime.BlockedSlots.Add(slot.StableId);
                continue;
            }
            if (!TryGetEmptyDirt(body.currentLocation, targetTile, out HoeDirt dirt))
                continue;
            long sequence = runtime.Transaction.NextStepSequence++;
            string stepOperationId = $"plant:{runtime.Transaction.PlantingId}:{sequence}";
            TaskTargetKey target = new(runtime.Transaction.LocationKey, WorldTargetCategories.PlantSlot, slot.StableId);
            TaskBeginResult begin = this.execution.TryBeginChild(runtime.Identity, stepOperationId, runtime.Transaction.PlantingId, "Planting", target);
            if (!begin.Started || begin.Session is null)
            {
                if (begin.Result.Code == "TARGET-RESERVED")
                    continue;
                runtime.BlockedSlots.Add(slot.StableId);
                continue;
            }
            runtime.Transaction.CurrentStep = new PlantingStepRecord
            {
                StepOperationId = stepOperationId,
                LocationKey = runtime.Transaction.LocationKey,
                TileX = slot.TileX,
                TileY = slot.TileY,
                SeedSourceId = seedSource.Record.SourceId,
                SeedCountBefore = seed.Stack,
                Phase = PlantingStepPhases.PreparingStep,
                PostconditionSummary = "EMPTY-HOE-DIRT",
            };
            runtime.Transaction.UpdatedTick = this.currentTick;
            runtime.CurrentTask = new PlantingTileTask(
                begin.Session,
                targetTile,
                approach.Value,
                TaskNavigationService.FacingToward(approach.Value, targetTile),
                body.currentLocation,
                dirt,
                seed,
                seedSource,
                body.Position);
            return;
        }
        this.Pause(runtime.Identity, "PLANT-PATH-CANDIDATES-EXHAUSTED");
    }

    private void UpdateTileTask(PlantingRuntime runtime, PlantingTileTask task)
    {
        if (!this.execution.IsCurrent(task.Session))
        {
            runtime.CurrentTask = null;
            runtime.Transaction.CurrentStep = null;
            return;
        }
        if (!this.bodies.TryGetBody(runtime.Identity, out NPC body) || !ReferenceEquals(body.currentLocation, task.Location)
            || !ReferenceEquals(runtime.Owner.currentLocation, task.Location))
        {
            this.FailTile(runtime, task, "PLANT-LOCATION-CHANGED", pause: true);
            return;
        }
        if (!TryGetEmptyDirt(task.Location, task.TargetTile, out HoeDirt dirt) || !ReferenceEquals(dirt, task.Dirt)
            || FindEscrowItem(runtime.Identity, task.Source.Record.SourceId) is not Item currentSeed
            || !ReferenceEquals(currentSeed, task.Seed) || currentSeed.Stack != runtime.Transaction.CurrentStep?.SeedCountBefore)
        {
            this.FailTile(runtime, task, "PLANT-TARGET-OR-SEED-CHANGED", pause: false);
            return;
        }
        if (body.TilePoint == task.ApproachTile.ToPoint())
        {
            this.bodies.Halt(runtime.Identity);
            body.faceDirection(task.Facing);
            this.SettleTile(runtime, task, body, dirt, currentSeed);
            return;
        }
        TaskNavigationResult progress = this.navigation.Observe(runtime.Identity, body, task.Navigation, this.currentTick, StuckTimeoutTicks, MaximumPathAttempts, RepathDelayTicks);
        if (progress.BudgetExhausted)
        {
            runtime.BlockedSlots.Add(task.Session.Target.StableId);
            this.FailTile(runtime, task, "PLANT-PATH-BUDGET-EXHAUSTED", pause: false);
            return;
        }
        if (!progress.CanIssuePath)
            return;
        body.controller = CompanionPathing.CreateController(body, task.Location, task.ApproachTile.ToPoint(), task.Facing, PathSearchLimit);
        task.Session.MarkTraveling();
        runtime.Transaction.CurrentStep!.Phase = PlantingStepPhases.Navigating;
        this.navigation.MarkPathIssued(task.Navigation, body.Position, this.currentTick, RepathDelayTicks);
    }

    private void SettleTile(PlantingRuntime runtime, PlantingTileTask task, NPC body, HoeDirt dirt, Item seed)
    {
        PlantingStepRecord step = runtime.Transaction.CurrentStep!;
        if (!task.Session.TryEnterSettlement(step.StepOperationId))
            return;
        if (!OwnerContextLease.CanProject(runtime.Owner))
        {
            this.FailTile(runtime, task, "OWNER-CONTEXT-BUSY", pause: true);
            return;
        }
        PlantSeedPolicyResult policy = PlantSeedPolicy.Evaluate(seed, task.Location);
        if (!policy.IsAllowed || policy.CropData is null)
        {
            this.FailTile(runtime, task, policy.Code, pause: true);
            return;
        }

        step.Phase = PlantingStepPhases.CommitReady;
        step.PostconditionSummary = "COMMIT-READY";
        runtime.Transaction.UpdatedTick = this.currentTick;
        this.appearance.Prepare(runtime.Identity, step.StepOperationId, AppearanceActionKinds.Planting, null, task.Facing);
        try
        {
            using (OwnerContextLease.Project(runtime.Owner, body.Position, task.Facing, task.Location))
                dirt.plant(seed.ItemId, runtime.Owner, false);
            if (!HasExpectedCrop(dirt, policy.CropData))
            {
                if (dirt.crop is not null)
                {
                    this.Fault(runtime.Record, runtime.Transaction, "PLANT-POSTCONDITION-UNKNOWN", "The target changed to an unexpected crop during the vanilla commit window.");
                    this.execution.Complete(task.Session, false, "PLANT-POSTCONDITION-UNKNOWN", "Unexpected crop identity after vanilla planting.");
                    this.DropRuntime(runtime);
                    return;
                }
                this.FailTile(runtime, task, "PLANT-VANILLA-REJECTED", pause: true);
                return;
            }

            step.Phase = PlantingStepPhases.WorldCommitted;
            step.PostconditionSummary = $"CROP:{Bound(policy.CropData.HarvestItemId, 96)}";
            int before = seed.Stack;
            ConsumeOne(runtime.Identity, task.Source, seed);
            int after = FindEscrowItem(runtime.Identity, task.Source.Record.SourceId)?.Stack ?? 0;
            if (before - after != 1)
                throw new InvalidOperationException("Plant Escrow seed delta was not exactly one.");
            task.Source.Record.ConsumedQuantity++;
            runtime.Transaction.PlantedCount++;
            runtime.Transaction.CurrentStep = null;
            runtime.Transaction.Phase = PlantingPhases.Planting;
            runtime.Transaction.UpdatedTick = this.currentTick;
            this.appearance.Commit(runtime.Identity, step.StepOperationId);
            this.execution.Complete(task.Session, true, "PLANT-STEP-COMMITTED", $"Planted tile {task.TargetTile.X},{task.TargetTile.Y} exactly once.");
            runtime.CurrentTask = null;
            runtime.BlockedSlots.Clear();
            if (runtime.Transaction.PlantedCount == runtime.Transaction.RequestedCount)
                this.CompletePlanting(runtime);
        }
        catch (Exception ex)
        {
            if (HasExpectedCrop(dirt, policy.CropData))
            {
                step.Phase = PlantingStepPhases.ReconcilingStep;
                step.PostconditionSummary = "WORLD-COMMITTED-SEED-UNCONFIRMED";
                runtime.Transaction.Phase = PlantingPhases.Reconciling;
                runtime.Transaction.LastFailure = Bound(ex.GetType().Name, 256);
                this.execution.AbandonRuntime(task.Session);
                runtime.CurrentTask = null;
                this.DropRuntime(runtime);
            }
            else
                this.FailTile(runtime, task, "PLANT-SETTLEMENT-ERROR", pause: true);
        }
    }

    private void FailTile(PlantingRuntime runtime, PlantingTileTask task, string code, bool pause)
    {
        this.execution.Complete(task.Session, false, code, $"Planting step {task.Session.OperationId} stopped before a proven commit.");
        runtime.CurrentTask = null;
        runtime.Transaction.CurrentStep = null;
        runtime.Transaction.UpdatedTick = this.currentTick;
        if (pause)
            this.Pause(runtime.Identity, code);
        else
            runtime.Transaction.Phase = PlantingPhases.Planting;
    }

    private void AbortCurrentStep(PlantingRuntime runtime, string reason)
    {
        if (runtime.CurrentTask is PlantingTileTask task)
        {
            this.execution.Complete(task.Session, false, reason, "Uncommitted planting step was cancelled.");
            runtime.CurrentTask = null;
        }
        if (runtime.Transaction.CurrentStep?.Phase is PlantingStepPhases.WorldCommitted or PlantingStepPhases.ReconcilingStep)
            return;
        runtime.Transaction.CurrentStep = null;
    }

    private void CompletePlanting(PlantingRuntime runtime)
    {
        if (this.inventories.GetPlantEscrow(runtime.Identity).Any(item => item is not null)
            || runtime.Transaction.SourcePlan.Sum(source => source.ConsumedQuantity) != runtime.Transaction.RequestedCount)
        {
            this.Fault(runtime.Record, runtime.Transaction, "PLANT-COMPLETION-CONSERVATION", "Exact count completed without an empty and balanced Plant Escrow.");
            this.DropRuntime(runtime);
            return;
        }
        runtime.Transaction.Phase = PlantingPhases.Completed;
        runtime.Transaction.CurrentStep = null;
        runtime.Transaction.LastFailure = null;
        runtime.Transaction.UpdatedTick = this.currentTick;
        runtime.Record.ActiveTransactionId = null;
        runtime.Record.Mode = runtime.Transaction.ReturnMode;
        TaskReceiptStore.Add(runtime.Record, runtime.Transaction.RequestOperationId, true, "PLANT-COMPLETED", $"Planted exactly {runtime.Transaction.PlantedCount} crop(s); Plant Escrow is empty.");
        this.runtimes.Remove(runtime.Identity);
        this.bodies.Halt(runtime.Identity);
    }

    private void BeginReturning(PlantingRuntime runtime, string reason)
    {
        this.AbortCurrentStep(runtime, reason);
        runtime.Transaction.Phase = PlantingPhases.ReturningSeeds;
        runtime.Transaction.LastFailure = Bound(reason, 256);
        runtime.Transaction.UpdatedTick = this.currentTick;
        runtime.IsCancelling = true;
    }

    private void UpdateReturning(PlantingRuntime runtime)
    {
        if (runtime.LockPending || runtime.ReturnPending)
            return;
        PlantingRuntimeSource? source = runtime.Sources.FirstOrDefault(source =>
            source.Record.AcquiredQuantity > source.Record.ConsumedQuantity + source.Record.ReturnedQuantity
            && FindEscrowItem(runtime.Identity, source.Record.SourceId) is not null);
        if (source is null)
        {
            if (this.inventories.GetPlantEscrow(runtime.Identity).Any(item => item is not null))
            {
                this.Fault(runtime.Record, runtime.Transaction, "PLANT-RETURN-LEDGER-MISMATCH", "Plant Escrow contains a stack not explained by the return ledger.");
                this.DropRuntime(runtime);
                return;
            }
            runtime.Transaction.Phase = PlantingPhases.Cancelled;
            runtime.Transaction.CurrentStep = null;
            runtime.Transaction.UpdatedTick = this.currentTick;
            runtime.Record.ActiveTransactionId = null;
            runtime.Record.Mode = runtime.Transaction.ReturnMode;
            TaskReceiptStore.Add(runtime.Record, runtime.Transaction.RequestOperationId, false, "PLANT-CANCELLED", $"Planting stopped after {runtime.Transaction.PlantedCount}/{runtime.Transaction.RequestedCount}; every unused seed was returned.");
            this.runtimes.Remove(runtime.Identity);
            return;
        }

        if (source.Record.SourceKind == PlantingSourceKinds.AuthorizedChest
            && source.Chest is CraftChestAccess access
            && CompanionStorageCoordinator.IsCurrentCraftChest(access, runtime.Identity.OwnerId)
            && this.bodies.TryGetBody(runtime.Identity, out NPC body)
            && ReferenceEquals(body.currentLocation, access.Location))
        {
            if (Manhattan(body.Tile, access.Tile) == 1)
            {
                this.bodies.Halt(runtime.Identity);
                this.RequestChestReturn(runtime, source, access);
                return;
            }
            if (body.controller is null && this.currentTick >= runtime.NextPathTick)
            {
                if (runtime.PathAttempts++ < MaximumPathAttempts)
                {
                    runtime.NextPathTick = this.currentTick + RepathDelayTicks;
                    body.controller = CompanionPathing.CreateController(body, access.Location, access.ApproachTile.ToPoint(), TaskNavigationService.FacingToward(access.ApproachTile, access.Tile), PathSearchLimit);
                    return;
                }
                source.ReturnToBag = true;
            }
            else
                return;
        }
        else
            source.ReturnToBag = true;

        if (source.ReturnToBag)
            this.RequestBagReturn(runtime, source);
    }

    private void RequestBagReturn(PlantingRuntime runtime, PlantingRuntimeSource source)
    {
        runtime.ReturnPending = true;
        this.inventories.RequestTransfer(runtime.Identity, () =>
        {
            Item? seed = FindEscrowItem(runtime.Identity, source.Record.SourceId);
            if (seed is null)
                return InventoryActionResult.Failure("PLANT-RETURN-SEED-MISSING", "The exact Plant Escrow source disappeared before return.");
            return TryReturnSeed(
                this.inventories.GetPlantEscrow(runtime.Identity),
                this.inventories.Get(runtime.Identity),
                this.inventories.Count(runtime.Identity),
                CompanionInventoryStore.Capacity,
                source.Record,
                seed)
                ? InventoryActionResult.Success("PLANT-SEED-RETURNED", "Unused seed returned to the Yui bag.")
                : InventoryActionResult.Failure("PLANT-RETURN-BAG-FULL", "The Yui bag cannot accept the unused seed; Plant Escrow keeps return-only responsibility.");
        }, result =>
        {
            runtime.ReturnPending = false;
            if (!result.IsSuccess)
            {
                runtime.Transaction.LastFailure = result.Code;
                runtime.NextPathTick = this.currentTick + 300;
            }
            else
                runtime.PathAttempts = 0;
        });
    }

    private void RequestChestReturn(PlantingRuntime runtime, PlantingRuntimeSource source, CraftChestAccess access)
    {
        runtime.LockPending = true;
        NetMutex chestMutex = access.Chest.GetMutex();
        chestMutex.RequestLock(() =>
        {
            NetMutex bagMutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(CompanionInventoryStore.GetNamespace(runtime.Identity));
            bagMutex.RequestLock(() =>
            {
                bool returned = false;
                try
                {
                    Item? seed = FindEscrowItem(runtime.Identity, source.Record.SourceId);
                    returned = seed is not null
                        && CompanionStorageCoordinator.IsCurrentCraftChest(access, runtime.Identity.OwnerId)
                        && TryReturnSeed(
                            this.inventories.GetPlantEscrow(runtime.Identity),
                            access.Chest.Items,
                            access.Chest.Items.Count(item => item is not null),
                            access.Chest.GetActualCapacity(),
                            source.Record,
                            seed);
                }
                finally
                {
                    runtime.LockPending = false;
                    bagMutex.ReleaseLock();
                    chestMutex.ReleaseLock();
                }
                if (!returned)
                {
                    source.ReturnToBag = true;
                    runtime.Transaction.LastFailure = "PLANT-RETURN-CHEST-UNAVAILABLE";
                }
                else
                    runtime.PathAttempts = 0;
            }, () =>
            {
                runtime.LockPending = false;
                chestMutex.ReleaseLock();
                source.ReturnToBag = true;
            });
        }, () =>
        {
            runtime.LockPending = false;
            source.ReturnToBag = true;
        });
    }

    private bool TryRebuildRuntime(CompanionRecord record, Farmer owner, out PlantingRuntime runtime, out string failure)
    {
        PlantingTransactionRecord transaction = record.PlantingTransaction!;
        GameLocation? location = Game1.getLocationFromName(transaction.LocationKey);
        if (location is null || owner.currentLocation is null || owner.currentLocation.NameOrUniqueName != transaction.LocationKey)
        {
            runtime = null!;
            failure = "The frozen planting location is unavailable.";
            return false;
        }
        IReadOnlyList<PlantingSeedSource> available = this.preview.CaptureSources(record.Identity, location, transaction.SeedQualifiedItemId);
        IReadOnlyList<CraftChestAccess> chests = this.bodies.TryGetBody(record.Identity, out NPC body) && ReferenceEquals(body.currentLocation, location)
            ? this.storage.GetCraftingChests(record.Identity, body)
            : Array.Empty<CraftChestAccess>();
        var sources = new List<PlantingRuntimeSource>();
        foreach (PlantingSourceRecord sourceRecord in transaction.SourcePlan)
        {
            Item? escrowItem = FindEscrowItem(record.Identity, sourceRecord.SourceId);
            PlantingSeedSource? origin = available.FirstOrDefault(candidate =>
                candidate.SourceKind == sourceRecord.SourceKind
                && candidate.StorageId == sourceRecord.StorageId
                && candidate.SourceSlot == sourceRecord.SourceSlot
                && Fingerprint(candidate.Item) == sourceRecord.ItemFingerprint);
            CraftChestAccess? chest = sourceRecord.SourceKind == PlantingSourceKinds.AuthorizedChest
                ? chests.Cast<CraftChestAccess?>().FirstOrDefault(candidate => candidate?.Authorization.ChestToken == sourceRecord.StorageId)
                : null;
            bool returning = transaction.Phase is PlantingPhases.ReturningSeeds or PlantingPhases.Cancelling;
            if (!returning && sourceRecord.AcquiredQuantity < sourceRecord.Quantity && origin is null)
            {
                runtime = null!;
                failure = $"Unacquired source {sourceRecord.SourceId} changed or disappeared.";
                return false;
            }
            sources.Add(new PlantingRuntimeSource(sourceRecord, origin, chest, escrowItem));
        }
        runtime = new PlantingRuntime(record, owner, transaction, sources)
        {
            BagAcquired = transaction.SourcePlan.Where(source => source.SourceKind == PlantingSourceKinds.Bag).All(source => source.AcquiredQuantity == source.Quantity),
            IsCancelling = transaction.Phase is PlantingPhases.ReturningSeeds or PlantingPhases.Cancelling,
        };
        failure = string.Empty;
        return true;
    }

    private bool ReconcileCurrentStep(PlantingRuntime runtime, out PlantingActionResult result)
    {
        PlantingStepRecord? step = runtime.Transaction.CurrentStep;
        if (step is null)
        {
            result = PlantingActionResult.Success("PLANT-RECONCILE-CLEAR", "No frozen step requires reconciliation.");
            return true;
        }
        if (step.Phase is not (PlantingStepPhases.WorldCommitted or PlantingStepPhases.ReconcilingStep))
        {
            runtime.Transaction.CurrentStep = null;
            result = PlantingActionResult.Success("PLANT-UNCOMMITTED-STEP-CLEARED", "A pre-commit step was discarded without changing world or inventory.");
            return true;
        }
        GameLocation? location = Game1.getLocationFromName(step.LocationKey);
        PlantingRuntimeSource? source = runtime.Sources.FirstOrDefault(source => source.Record.SourceId == step.SeedSourceId);
        if (location is null || source is null || !TryGetDirt(location, new Vector2(step.TileX, step.TileY), out HoeDirt dirt))
        {
            result = this.Fault(runtime.Record, runtime.Transaction, "PLANT-RECONCILE-UNKNOWN", "The frozen world target or seed source cannot be resolved.");
            return false;
        }
        Item? seed = FindEscrowItem(runtime.Identity, source.Record.SourceId);
        int actual = seed?.Stack ?? 0;
        int expected = source.Record.AcquiredQuantity - source.Record.ConsumedQuantity - source.Record.ReturnedQuantity;
        CropData? cropData = ResolveCropData(runtime.Transaction.SeedQualifiedItemId, location);
        bool expectedCrop = cropData is not null && HasExpectedCrop(dirt, cropData);
        if (!expectedCrop)
        {
            if (dirt.crop is null && actual == expected)
            {
                runtime.Transaction.CurrentStep = null;
                result = PlantingActionResult.Success("PLANT-RECONCILED-NO-COMMIT", "Frozen step had no world or seed delta and was safely cleared.");
                return true;
            }
            result = this.Fault(runtime.Record, runtime.Transaction, "PLANT-RECONCILE-UNKNOWN", "World and seed evidence do not describe a safe uncommitted step.");
            return false;
        }
        if (actual == expected)
        {
            if (seed is null)
            {
                result = this.Fault(runtime.Record, runtime.Transaction, "PLANT-RECONCILE-SEED-MISSING", "The committed crop has no seed available for its one-unit reconciliation.");
                return false;
            }
            ConsumeOne(runtime.Identity, source, seed);
        }
        else if (actual != expected - 1)
        {
            result = this.Fault(runtime.Record, runtime.Transaction, "PLANT-RECONCILE-DELTA", "The frozen step seed delta is not zero or one.");
            return false;
        }
        source.Record.ConsumedQuantity++;
        runtime.Transaction.PlantedCount++;
        TaskReceiptStore.Add(runtime.Record, step.StepOperationId, true, "PLANT-STEP-RECONCILED", "Recovered one previously committed crop without replanting it.");
        runtime.Transaction.CurrentStep = null;
        result = PlantingActionResult.Success("PLANT-STEP-RECONCILED", "Recovered one committed planting step from frozen evidence.");
        return true;
    }

    private PlantingActionResult Fault(CompanionRecord record, PlantingTransactionRecord transaction, string code, string message)
    {
        transaction.Phase = PlantingPhases.Faulted;
        transaction.LastFailure = Bound(code, 256);
        transaction.UpdatedTick = this.currentTick;
        record.ActiveTransactionId = transaction.PlantingId;
        this.monitor.Log($"HY-PLANT-{code}: {record.Identity} {message}", LogLevel.Error);
        return PlantingActionResult.Failure(code, message);
    }

    private bool IsCurrent(PlantingRuntime runtime) =>
        this.runtimes.TryGetValue(runtime.Identity, out PlantingRuntime? current)
        && ReferenceEquals(current, runtime)
        && runtime.Record.PlantingTransaction == runtime.Transaction
        && runtime.Record.ActiveTransactionId == runtime.Transaction.PlantingId;

    private void DropRuntime(PlantingRuntime runtime)
    {
        if (runtime.CurrentTask is not null)
            this.execution.AbandonRuntime(runtime.CurrentTask.Session);
        this.runtimes.Remove(runtime.Identity);
        this.bodies.Halt(runtime.Identity);
    }

    private static bool TryBuildPlan(
        CompanionIdentity identity,
        string qualifiedItemId,
        int count,
        IReadOnlyList<PlantingSeedSource> available,
        out List<PlantingRuntimeSource> plan)
    {
        plan = new List<PlantingRuntimeSource>();
        int remaining = count;
        foreach (PlantingSeedSource candidate in available)
        {
            if (candidate.Item.QualifiedItemId != qualifiedItemId || candidate.Item.Stack <= 0)
                continue;
            int take = Math.Min(remaining, candidate.Item.Stack);
            var record = new PlantingSourceRecord
            {
                SourceId = Guid.NewGuid().ToString("N"),
                SourceKind = candidate.SourceKind,
                StorageId = candidate.SourceKind == PlantingSourceKinds.Bag ? CompanionInventoryStore.GetNamespace(identity) : candidate.StorageId,
                SourceSlot = candidate.SourceSlot,
                ItemFingerprint = Fingerprint(candidate.Item),
                QualifiedItemId = qualifiedItemId,
                Quantity = take,
            };
            plan.Add(new PlantingRuntimeSource(record, candidate, candidate.Chest, null));
            remaining -= take;
            if (remaining == 0)
                return true;
        }
        plan.Clear();
        return false;
    }

    private static void MoveToEscrow(string plantingId, PlantingRuntimeSource source, Inventory escrow)
    {
        if (source.Origin is not PlantingSeedSource origin)
            throw new InvalidOperationException("The planned origin is unavailable.");
        int index = FindExactIndex(origin.Container, origin.Item);
        if (index < 0 || index != source.Record.SourceSlot || origin.Item.Stack != origin.ExpectedStack
            || Fingerprint(origin.Item) != source.Record.ItemFingerprint)
            throw new InvalidOperationException("The exact source instance, slot, stack, or fingerprint changed.");
        Item reserved;
        if (source.Record.Quantity == origin.Item.Stack)
        {
            reserved = origin.Item;
            origin.Container.RemoveAt(index);
        }
        else
        {
            reserved = origin.Item.getOne();
            reserved.Stack = source.Record.Quantity;
            origin.Item.Stack -= source.Record.Quantity;
        }
        reserved.modData[CompanionInventoryStore.PlantingIdTag] = plantingId;
        reserved.modData[CompanionInventoryStore.PlantSourceTag] = source.Record.SourceId;
        escrow.Add(reserved);
        source.EscrowItem = reserved;
        source.Record.AcquiredQuantity = source.Record.Quantity;
    }

    private static bool TryReturnSeed(Inventory escrow, IList<Item> destination, int occupiedSlots, int capacity, PlantingSourceRecord source, Item seed)
    {
        Item comparison = seed.getOne();
        comparison.modData.Remove(CompanionInventoryStore.PlantingIdTag);
        comparison.modData.Remove(CompanionInventoryStore.PlantSourceTag);
        int mergeIndex = Enumerable.Range(0, destination.Count)
            .OrderBy(index => index == source.SourceSlot ? 0 : 1)
            .FirstOrDefault(index => destination[index] is Item target
                && target.canStackWith(comparison)
                && target.Stack + seed.Stack <= target.maximumStackSize(), -1);
        bool canAddSlot = occupiedSlots < capacity;
        if (mergeIndex < 0 && !canAddSlot)
            return false;

        int escrowIndex = FindExactIndex(escrow, seed);
        if (escrowIndex < 0)
            return false;
        int quantity = seed.Stack;
        escrow.RemoveAt(escrowIndex);
        seed.modData.Remove(CompanionInventoryStore.PlantingIdTag);
        seed.modData.Remove(CompanionInventoryStore.PlantSourceTag);
        if (mergeIndex >= 0)
            destination[mergeIndex].Stack += quantity;
        else
            destination.Add(seed);
        source.ReturnedQuantity += quantity;
        return true;
    }

    private static Item? FindNextEscrowSeed(PlantingRuntime runtime, out PlantingRuntimeSource? source)
    {
        foreach (PlantingRuntimeSource candidate in runtime.Sources)
        {
            Item? item = FindEscrowItem(runtime.Identity, candidate.Record.SourceId);
            if (item is not null && item.Stack > 0)
            {
                source = candidate;
                candidate.EscrowItem = item;
                return item;
            }
        }
        source = null;
        return null;
    }

    private static Item? FindEscrowItem(CompanionIdentity identity, string sourceId)
    {
        Inventory escrow = Game1.player.team.GetOrCreateGlobalInventory(CompanionInventoryStore.GetPlantEscrowNamespace(identity));
        return escrow.FirstOrDefault(item => item is not null
            && item.modData.GetValueOrDefault(CompanionInventoryStore.PlantSourceTag) == sourceId);
    }

    private static void ConsumeOne(CompanionIdentity identity, PlantingRuntimeSource source, Item seed)
    {
        Inventory escrow = Game1.player.team.GetOrCreateGlobalInventory(CompanionInventoryStore.GetPlantEscrowNamespace(identity));
        int index = FindExactIndex(escrow, seed);
        if (index < 0 || seed.Stack <= 0 || seed.modData.GetValueOrDefault(CompanionInventoryStore.PlantSourceTag) != source.Record.SourceId)
            throw new InvalidOperationException("The exact Plant Escrow seed source is unavailable.");
        if (seed.Stack == 1)
        {
            escrow.RemoveAt(index);
            source.EscrowItem = null;
        }
        else
            seed.Stack--;
    }

    private static bool TryGetEmptyDirt(GameLocation location, Vector2 tile, out HoeDirt dirt) =>
        TryGetDirt(location, tile, out dirt) && dirt.crop is null && !location.Objects.ContainsKey(tile);

    private static bool TryGetDirt(GameLocation location, Vector2 tile, out HoeDirt dirt)
    {
        if (location.terrainFeatures.TryGetValue(tile, out TerrainFeature? feature) && feature is HoeDirt found)
        {
            dirt = found;
            return true;
        }
        dirt = null!;
        return false;
    }

    private static bool HasExpectedCrop(HoeDirt dirt, CropData expected)
    {
        try
        {
            return dirt.crop is Crop crop && crop.GetData()?.HarvestItemId == expected.HarvestItemId;
        }
        catch
        {
            return false;
        }
    }

    private static CropData? ResolveCropData(string qualifiedItemId, GameLocation location)
    {
        try
        {
            Item seed = ItemRegistry.Create(qualifiedItemId, 1);
            return PlantSeedPolicy.Evaluate(seed, location).CropData;
        }
        catch
        {
            return null;
        }
    }

    private static PlantingScope Scope(PlantingTransactionRecord transaction) => new(
        transaction.LocationKey,
        transaction.AnchorX,
        transaction.AnchorY,
        transaction.EndX,
        transaction.EndY,
        transaction.Shape,
        transaction.Radius);

    private static string Fingerprint(Item item)
    {
        string modData = string.Join(";", item.modData.Pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{item.QualifiedItemId}|{item.Stack}|{item.Quality}|{modData}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int FindExactIndex(IList<Item> items, Item item)
    {
        for (int index = 0; index < items.Count; index++)
            if (ReferenceEquals(items[index], item))
                return index;
        return -1;
    }

    private static int Manhattan(Vector2 first, Vector2 second) => (int)(Math.Abs(first.X - second.X) + Math.Abs(first.Y - second.Y));
    private static string Bound(string? value, int maximum) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maximum ? value : value[..maximum];
    private static bool IsBoundedOperationId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);

    private sealed class PlantingRuntime
    {
        public PlantingRuntime(CompanionRecord record, Farmer owner, PlantingTransactionRecord transaction, List<PlantingRuntimeSource> sources)
        {
            this.Record = record;
            this.Owner = owner;
            this.Transaction = transaction;
            this.Sources = sources;
        }

        public CompanionRecord Record { get; }
        public CompanionIdentity Identity => this.Record.Identity;
        public Farmer Owner { get; }
        public PlantingTransactionRecord Transaction { get; }
        public List<PlantingRuntimeSource> Sources { get; }
        public HashSet<string> BlockedSlots { get; } = new(StringComparer.Ordinal);
        public PlantingTileTask? CurrentTask { get; set; }
        public bool BagRequestPending { get; set; }
        public bool BagAcquired { get; set; }
        public bool LockPending { get; set; }
        public bool ReturnPending { get; set; }
        public bool IsCancelling { get; set; }
        public int PathAttempts { get; set; }
        public ulong NextPathTick { get; set; }
    }

    private sealed class PlantingRuntimeSource
    {
        public PlantingRuntimeSource(PlantingSourceRecord record, PlantingSeedSource? origin, CraftChestAccess? chest, Item? escrowItem)
        {
            this.Record = record;
            this.Origin = origin;
            this.Chest = chest;
            this.EscrowItem = escrowItem;
        }

        public PlantingSourceRecord Record { get; }
        public PlantingSeedSource? Origin { get; }
        public CraftChestAccess? Chest { get; }
        public Item? EscrowItem { get; set; }
        public bool ReturnToBag { get; set; }
    }

    private sealed class PlantingTileTask
    {
        public PlantingTileTask(TaskSession session, Vector2 targetTile, Vector2 approachTile, int facing, GameLocation location, HoeDirt dirt, Item seed, PlantingRuntimeSource source, Vector2 initialPosition)
        {
            this.Session = session;
            this.TargetTile = targetTile;
            this.ApproachTile = approachTile;
            this.Facing = facing;
            this.Location = location;
            this.Dirt = dirt;
            this.Seed = seed;
            this.Source = source;
            this.Navigation = new TaskNavigationState(initialPosition, 0);
        }

        public TaskSession Session { get; }
        public Vector2 TargetTile { get; }
        public Vector2 ApproachTile { get; }
        public int Facing { get; }
        public GameLocation Location { get; }
        public HoeDirt Dirt { get; }
        public Item Seed { get; }
        public PlantingRuntimeSource Source { get; }
        public TaskNavigationState Navigation { get; }
    }
}
