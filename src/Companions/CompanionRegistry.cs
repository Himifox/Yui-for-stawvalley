namespace YuiToIssho;

internal readonly record struct RegistryLoadResult(bool IsSuccess, string Code, string Message)
{
    public static RegistryLoadResult Success(string message = "Loaded.") => new(true, "OK", message);

    public static RegistryLoadResult Failure(string code, string message) => new(false, code, message);
}

internal readonly record struct DeleteResult(bool IsSuccess, string Code, string Message)
{
    public static DeleteResult Success(string code, string message) => new(true, code, message);

    public static DeleteResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class CompanionRegistry
{
    private readonly Dictionary<CompanionIdentity, CompanionRecord> records = new();
    private readonly List<AuthorizedChestRecord> authorizedChests = new();

    public bool CompanionIntroductionCompleted { get; private set; }

    public int Count => this.records.Count;

    public IEnumerable<CompanionRecord> All => this.records.Values;

    public IEnumerable<CompanionRecord> Active => this.records.Values;

    public IReadOnlyList<AuthorizedChestRecord> AuthorizedChests => this.authorizedChests;

    public void Clear()
    {
        this.records.Clear();
        this.authorizedChests.Clear();
        this.CompanionIntroductionCompleted = false;
    }

    public RegistryLoadResult Load(YuiToIsshoSaveData data)
    {
        this.records.Clear();
        this.authorizedChests.Clear();

        if (data.SchemaVersion != YuiToIsshoSaveData.CurrentSchemaVersion)
            return RegistryLoadResult.Failure("UNMIGRATED-SCHEMA", $"Registry input must be migrated to schema {YuiToIsshoSaveData.CurrentSchemaVersion}; found {data.SchemaVersion}.");

        if (data.Companions is null)
            return RegistryLoadResult.Failure("INVALID-DATA", "The companion collection is missing.");
        data.AuthorizedChests ??= new List<AuthorizedChestRecord>();

        HashSet<AuthorizedChestIdentity> authorizationKeys = new();
        HashSet<string> chestTokens = new(StringComparer.Ordinal);
        foreach (AuthorizedChestRecord authorization in data.AuthorizedChests)
        {
            if (authorization is null
                || authorization.OwnerId == 0
                || string.IsNullOrWhiteSpace(authorization.LocationKey)
                || authorization.LocationKey.Length > 256
                || authorization.TileX < 0
                || authorization.TileY < 0
                || !Guid.TryParseExact(authorization.ChestToken, "N", out _)
                || !authorizationKeys.Add(authorization.Identity)
                || !chestTokens.Add(authorization.ChestToken))
                return RegistryLoadResult.Failure("INVALID-CHEST-AUTHORIZATION", "An authorized Chest identity or token is invalid or duplicated.");
        }

        var loaded = new Dictionary<CompanionIdentity, CompanionRecord>();
        var itemTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (CompanionRecord record in data.Companions)
        {
            if (record is null || record.OwnerId == 0 || !CompanionIdentity.IsValidSlot(record.Slot))
                return RegistryLoadResult.Failure("INVALID-IDENTITY", "A companion must use its Owner's single current identity.");

            CompanionIdentity identity = record.Identity;
            if (!loaded.TryAdd(identity, record))
                return RegistryLoadResult.Failure("DUPLICATE-IDENTITY", $"Identity {identity} appears more than once.");

            record.DisplayName = string.IsNullOrWhiteSpace(record.DisplayName) || record.DisplayName == "YuiToIssho" ? "Yui" : record.DisplayName;
            record.Mode = CompanionModes.IsValid(record.Mode) ? record.Mode : CompanionModes.Wait;
            record.Inventory ??= new List<CompanionItemRecord>();
            record.PendingResponsibilities ??= new List<PendingResponsibilityRecord>();
            record.RecentOperations ??= new List<OperationReceiptRecord>();
            record.StorageLiabilities ??= new List<StorageLiabilityRecord>();
            record.Deliveries ??= new List<DeliveryRecord>();
            record.Vitals ??= new CompanionVitalsRecord();
            record.Vitals.RecentCosts ??= new List<VitalCostReceiptRecord>();
            record.Bond ??= new CompanionBondRecord();
            record.Appearance ??= new CompanionAppearanceProfile();

            if (record.Bond.Points is < 0 or > CompanionBondRecord.MaxPoints
                || record.Bond.LastTalkedDay < -1
                || record.Bond.LastAffectionDay < -1
                || record.Bond.LastGiftDay < -1
                || record.Bond.GiftWeek < -1
                || record.Bond.GiftsThisWeek is < 0 or > 2)
                return RegistryLoadResult.Failure("INVALID-COMPANION-BOND", $"Identity {identity} has invalid independent bond state.");

            if (record.WorkDirective is not null)
            {
                WorkDirectiveRecord directive = record.WorkDirective;
                if (!Guid.TryParseExact(directive.DirectiveId, "N", out _)
                    || !WorkKinds.IsContinuous(directive.Kind)
                    || string.IsNullOrWhiteSpace(directive.LocationKey)
                    || directive.LocationKey.Length > 256
                    || directive.AnchorX < 0
                    || directive.AnchorY < 0
                    || directive.Shape is not (WorkScopeShapes.Radius or WorkScopeShapes.Rectangle)
                    || (directive.Shape == WorkScopeShapes.Radius && directive.Radius is < WorkScopeContracts.MinimumRadius or > WorkScopeContracts.MaximumRadius)
                    || (directive.Shape == WorkScopeShapes.Rectangle && (directive.Radius != 0 || directive.EndX < 0 || directive.EndY < 0
                        || !WorkScopeContracts.IsRectangleWithinLimit(directive.AnchorX, directive.AnchorY, directive.EndX, directive.EndY)))
                    || directive.CompletionPolicy is not (WorkCompletionPolicies.UntilClear or WorkCompletionPolicies.UntilStopped)
                    || directive.ReturnMode is not (CompanionModes.Follow or CompanionModes.Wait)
                    || directive.NextStepSequence < 0
                    || directive.CreatedDay < 0
                    || directive.LastConfirmedDay < directive.CreatedDay
                    || (directive.SuspendedReason is not null && (string.IsNullOrWhiteSpace(directive.SuspendedReason) || directive.SuspendedReason.Length > 128))
                    || (directive.IsOwnerAssistLease && (!record.OwnerWorkAssistEnabled
                        || directive.Kind is not (WorkKinds.Chop or WorkKinds.Mow)
                        || directive.Shape != WorkScopeShapes.Radius
                        || directive.Radius != OwnerWorkAssistContracts.Radius
                        || directive.CompletionPolicy != WorkCompletionPolicies.UntilStopped))
                    || (record.OwnerWorkAssistEnabled && !directive.IsOwnerAssistLease))
                    return RegistryLoadResult.Failure("INVALID-WORK-DIRECTIVE", $"Identity {identity} has an invalid continuous work directive.");
            }
            if (record.Mode == CompanionModes.Work && record.WorkDirective is null)
                return RegistryLoadResult.Failure("WORK-DIRECTIVE-MISSING", $"Identity {identity} is in Work mode without a directive.");
            if (record.Vitals.ResumeMode == CompanionModes.Work && record.WorkDirective is null)
                return RegistryLoadResult.Failure("WORK-RESUME-DIRECTIVE-MISSING", $"Identity {identity} would resume Work without a directive.");

            if (record.RecentOperations.Any(receipt => receipt is null || string.IsNullOrWhiteSpace(receipt.OperationId))
                || record.RecentOperations.GroupBy(receipt => receipt.OperationId, StringComparer.Ordinal).Any(group => group.Count() > 1))
                return RegistryLoadResult.Failure("INVALID-OPERATION-RECEIPT", $"Identity {identity} has an invalid or duplicated operation receipt.");

            if (record.Deliveries.Any(delivery => delivery is null
                    || string.IsNullOrWhiteSpace(delivery.DeliveryId)
                    || delivery.DeliveryId.Length > 128
                    || delivery.RecipientPlayerId == 0
                    || !Guid.TryParseExact(delivery.CargoToken, "N", out _)
                    || string.IsNullOrWhiteSpace(delivery.QualifiedItemId)
                    || delivery.Quantity <= 0
                    || delivery.Attempt < 0
                    || !DeliveryPhases.IsValid(delivery.Phase))
                || record.Deliveries.GroupBy(delivery => delivery.DeliveryId, StringComparer.Ordinal).Any(group => group.Count() > 1)
                || record.Deliveries.GroupBy(delivery => delivery.CargoToken, StringComparer.Ordinal).Any(group => group.Count() > 1))
                return RegistryLoadResult.Failure("INVALID-DELIVERY-RESPONSIBILITY", $"Identity {identity} has an invalid or duplicated delivery responsibility.");

            if (record.CraftTransaction is CraftTransactionRecord craft)
            {
                craft.RecipeSnapshot ??= new CraftRecipeSnapshot();
                craft.RecipeSnapshot.Ingredients ??= new List<CraftIngredientRecord>();
                craft.SourcePlan ??= new List<CraftSourceRecord>();
                craft.OutputTokens ??= new List<string>();
                if (!Guid.TryParseExact(craft.CraftId, "N", out _)
                    || string.IsNullOrWhiteSpace(craft.OperationId) || craft.OperationId.Length > 128
                    || craft.OwnerId != identity.OwnerId
                    || string.IsNullOrWhiteSpace(craft.RecipeKey) || craft.RecipeKey.Length > 128
                    || craft.RecipePolicyVersion <= 0
                    || craft.CraftCount is < 1 or > 25
                    || craft.CompletedCount < 0 || craft.CompletedCount > craft.CraftCount
                    || !CraftPhases.IsValid(craft.Phase)
                    || string.IsNullOrWhiteSpace(craft.RecipeSnapshot.OutputQualifiedItemId)
                    || craft.RecipeSnapshot.OutputPerCraft is < 1 or > 999
                    || craft.RecipeSnapshot.Ingredients.Count is < 1 or > 16
                    || craft.RecipeSnapshot.Ingredients.Any(item => string.IsNullOrWhiteSpace(item.IngredientId) || item.RequiredPerCraft is < 1 or > 9999)
                    || craft.SourcePlan.Any(source => !CraftSourceKinds.IsValid(source.SourceKind) || source.SourceSlot < 0
                        || string.IsNullOrWhiteSpace(source.ItemFingerprint) || string.IsNullOrWhiteSpace(source.QualifiedItemId) || source.Quantity <= 0)
                    || (craft.OutputToken is not null && !Guid.TryParseExact(craft.OutputToken, "N", out _)))
                    return RegistryLoadResult.Failure("INVALID-CRAFT-RESPONSIBILITY", $"Identity {identity} has an invalid crafting transaction.");
                if (craft.OutputTokens.Any(token => !Guid.TryParseExact(token, "N", out _))
                    || craft.OutputTokens.Distinct(StringComparer.Ordinal).Count() != craft.OutputTokens.Count
                    || craft.OutputTokens.Count > craft.CraftCount)
                    return RegistryLoadResult.Failure("INVALID-CRAFT-OUTPUT-TOKENS", $"Identity {identity} has invalid or duplicated crafting output tokens.");
            }

            if (record.PlantingTransaction is PlantingTransactionRecord planting
                && !ValidatePlanting(identity, planting, out string plantingFailure))
                return RegistryLoadResult.Failure("INVALID-PLANTING-RESPONSIBILITY", $"Identity {identity} has an invalid planting transaction: {plantingFailure}");

            PlantingTransactionRecord? activePlanting = record.PlantingTransaction;
            bool plantingOwnsResponsibility = activePlanting is not null && PlantingPhases.OwnsResponsibility(activePlanting.Phase);
            if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId)
                && (!plantingOwnsResponsibility || record.ActiveTransactionId != activePlanting?.PlantingId))
                return RegistryLoadResult.Failure("UNRESOLVED-ACTIVE-TRANSACTION", $"Identity {identity} contains unresolved transaction {record.ActiveTransactionId}.");
            if (plantingOwnsResponsibility && record.ActiveTransactionId != activePlanting?.PlantingId)
                return RegistryLoadResult.Failure("PLANTING-TRANSACTION-GATE-MISMATCH", $"Identity {identity} has planting responsibility outside its exclusive transaction gate.");

            foreach (CompanionItemRecord item in record.Inventory)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.InstanceToken) || item.Stack <= 0 || !itemTokens.Add(item.InstanceToken))
                    return RegistryLoadResult.Failure("INVALID-ITEM-RESPONSIBILITY", $"Identity {identity} has an invalid or duplicated item responsibility.");
                item.ModData ??= new Dictionary<string, string>();
            }

            HashSet<string> liabilityIds = new(StringComparer.Ordinal);
            HashSet<string> liabilityItemTokens = new(StringComparer.Ordinal);
            foreach (StorageLiabilityRecord liability in record.StorageLiabilities)
            {
                if (liability is null
                    || !Guid.TryParseExact(liability.ResponsibilityId, "N", out _)
                    || !StorageLiabilityKinds.IsValid(liability.Kind)
                    || !Guid.TryParseExact(liability.ItemToken, "N", out _)
                    || liability.ItemToken != liability.ResponsibilityId
                    || string.IsNullOrWhiteSpace(liability.QualifiedItemId)
                    || liability.MaximumResponsibleStack <= 0
                    || string.IsNullOrWhiteSpace(liability.SourceLocationKey)
                    || liability.SourceTileX < 0
                    || liability.SourceTileY < 0
                    || !Guid.TryParseExact(liability.SourceChestToken, "N", out _)
                    || liability.OriginalSourceSlot < 0
                    || !liabilityIds.Add(liability.ResponsibilityId)
                    || !liabilityItemTokens.Add(liability.ItemToken))
                    return RegistryLoadResult.Failure("INVALID-STORAGE-LIABILITY", $"Identity {identity} has an invalid or duplicated storage liability.");

                bool sourceIsAuthorized = data.AuthorizedChests.Any(authorization =>
                    authorization.OwnerId == identity.OwnerId
                    && authorization.LocationKey == liability.SourceLocationKey
                    && authorization.IsStructure == liability.SourceIsStructure
                    && authorization.TileX == liability.SourceTileX
                    && authorization.TileY == liability.SourceTileY
                    && authorization.ChestToken == liability.SourceChestToken);
                if (!sourceIsAuthorized)
                    return RegistryLoadResult.Failure("LIABILITY-SOURCE-NOT-AUTHORIZED", $"Identity {identity} has a liability whose source Chest is no longer authorized.");
            }
        }

        foreach ((CompanionIdentity identity, CompanionRecord record) in loaded)
            this.records.Add(identity, record);
        this.authorizedChests.AddRange(data.AuthorizedChests);
        this.CompanionIntroductionCompleted = data.CompanionIntroductionCompleted || this.records.Count > 0;

        return RegistryLoadResult.Success($"Loaded {this.records.Count} current companion record(s).");
    }

    public CompanionRecord GetOrCreate(CompanionIdentity identity)
    {
        if (!identity.IsCanonical)
            throw new ArgumentOutOfRangeException(nameof(identity), "Only the canonical Slot 1 identity can create a normal companion record.");
        if (!this.records.TryGetValue(identity, out CompanionRecord? record))
        {
            record = new CompanionRecord
            {
                OwnerId = identity.OwnerId,
                Slot = identity.Slot,
                DisplayName = "Yui",
            };
            this.records.Add(identity, record);
        }

        return record;
    }

    public bool TryGet(CompanionIdentity identity, out CompanionRecord record) => this.records.TryGetValue(identity, out record!);

    public void MarkCompanionIntroductionCompleted() => this.CompanionIntroductionCompleted = true;

    public DeleteResult Delete(CompanionIdentity identity)
    {
        if (!this.records.TryGetValue(identity, out CompanionRecord? record))
            return DeleteResult.Success("ALREADY-ABSENT", $"{identity} is already absent.");

        if (record.Inventory.Count > 0)
            return DeleteResult.Failure("INVENTORY-RESPONSIBILITY", $"{identity} owns {record.Inventory.Count} inventory item record(s).");
        if (record.PendingResponsibilities.Count > 0)
            return DeleteResult.Failure("PENDING-RESPONSIBILITY", $"{identity} has {record.PendingResponsibilities.Count} pending responsibility record(s).");
        if (record.StorageLiabilities.Count > 0)
            return DeleteResult.Failure("STORAGE-LIABILITY", $"{identity} has {record.StorageLiabilities.Count} storage liability record(s).");
        if (record.Deliveries.Any(delivery => DeliveryPhases.OwnsEscrow(delivery.Phase)))
            return DeleteResult.Failure("DELIVERY-RESPONSIBILITY", $"{identity} has active delivery cargo responsibility.");
        if (record.CraftTransaction is not null && CraftPhases.OwnsResponsibility(record.CraftTransaction.Phase))
            return DeleteResult.Failure("CRAFT-RESPONSIBILITY", $"{identity} has active craft {record.CraftTransaction.CraftId} in phase {record.CraftTransaction.Phase}.");
        if (record.PlantingTransaction is not null && PlantingPhases.OwnsResponsibility(record.PlantingTransaction.Phase))
            return DeleteResult.Failure("PLANTING-RESPONSIBILITY", $"{identity} has active planting {record.PlantingTransaction.PlantingId} in phase {record.PlantingTransaction.Phase}.");
        if (!string.IsNullOrWhiteSpace(record.Vitals.RecoveryEpisodeId))
            return DeleteResult.Failure("RECOVERY-ACTIVE", $"{identity} has active recovery episode {record.Vitals.RecoveryEpisodeId}.");
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return DeleteResult.Failure("ACTIVE-TRANSACTION", $"{identity} has active transaction {record.ActiveTransactionId}.");
        if (record.WorkDirective is not null)
            return DeleteResult.Failure("WORK-DIRECTIVE-ACTIVE", $"{identity} has active work directive {record.WorkDirective.DirectiveId}; stop it before deletion.");

        this.records.Remove(identity);
        return DeleteResult.Success("DELETED", $"Deleted {identity}.");
    }

    public YuiToIsshoSaveData CreateSnapshot() => new()
    {
        SchemaVersion = YuiToIsshoSaveData.CurrentSchemaVersion,
        CompanionIntroductionCompleted = this.CompanionIntroductionCompleted,
        Companions = this.records.Values.OrderBy(record => record.OwnerId).ThenBy(record => record.Slot).ToList(),
        AuthorizedChests = this.authorizedChests.OrderBy(record => record.OwnerId).ThenBy(record => record.LocationKey, StringComparer.Ordinal).ThenBy(record => record.TileX).ThenBy(record => record.TileY).ToList(),
    };

    public bool TryAddAuthorization(AuthorizedChestRecord record)
    {
        if (this.authorizedChests.Any(existing => existing.Identity == record.Identity || existing.ChestToken == record.ChestToken))
            return false;
        this.authorizedChests.Add(record);
        return true;
    }

    public bool RemoveAuthorization(AuthorizedChestIdentity identity) => this.authorizedChests.RemoveAll(record => record.Identity == identity) > 0;

    private static bool ValidatePlanting(CompanionIdentity identity, PlantingTransactionRecord planting, out string failure)
    {
        planting.SourcePlan ??= new List<PlantingSourceRecord>();
        PlantingScope scope = new(
            planting.LocationKey,
            planting.AnchorX,
            planting.AnchorY,
            planting.EndX,
            planting.EndY,
            planting.Shape,
            planting.Radius);
        bool baseValid = Guid.TryParseExact(planting.PlantingId, "N", out _)
            && !string.IsNullOrWhiteSpace(planting.RequestOperationId) && planting.RequestOperationId.Length <= 128
            && planting.OwnerId == identity.OwnerId
            && !string.IsNullOrWhiteSpace(planting.SeedQualifiedItemId) && planting.SeedQualifiedItemId.Length <= 128
            && planting.SeedPolicyVersion == PlantingConstants.SeedPolicyVersion
            && planting.AnchorX >= 0 && planting.AnchorY >= 0 && planting.EndX >= 0 && planting.EndY >= 0
            && scope.IsValid()
            && planting.RequestedCount is >= 1 and <= PlantingConstants.MaximumCount
            && planting.PlantedCount >= 0 && planting.PlantedCount <= planting.RequestedCount
            && planting.NextStepSequence >= 0
            && PlantingPhases.IsValid(planting.Phase)
            && planting.ReturnMode is CompanionModes.Follow or CompanionModes.Wait
            && planting.SourcePlan.Count is >= 1 and <= PlantingConstants.MaximumCount
            && planting.CreatedDay >= 0 && planting.LastConfirmedDay >= planting.CreatedDay
            && (planting.LastFailure is null || planting.LastFailure.Length <= 256);
        if (!baseValid)
        {
            failure = "identity, policy, scope, count, phase, return mode, day, or bounded text is invalid";
            return false;
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        int planned = 0;
        int consumed = 0;
        foreach (PlantingSourceRecord source in planting.SourcePlan)
        {
            bool storageValid = source.SourceKind == PlantingSourceKinds.Bag
                ? source.StorageId == CompanionInventoryStore.GetNamespace(identity)
                : source.SourceKind == PlantingSourceKinds.AuthorizedChest && Guid.TryParseExact(source.StorageId, "N", out _);
            if (!Guid.TryParseExact(source.SourceId, "N", out _)
                || !sourceIds.Add(source.SourceId)
                || !PlantingSourceKinds.IsValid(source.SourceKind)
                || !storageValid
                || source.SourceSlot is < 0 or > 9999
                || string.IsNullOrWhiteSpace(source.ItemFingerprint) || source.ItemFingerprint.Length > 256
                || source.QualifiedItemId != planting.SeedQualifiedItemId
                || source.Quantity is < 1 or > PlantingConstants.MaximumCount
                || source.AcquiredQuantity < 0 || source.AcquiredQuantity > source.Quantity
                || source.ConsumedQuantity < 0 || source.ReturnedQuantity < 0
                || source.ConsumedQuantity + source.ReturnedQuantity > source.AcquiredQuantity)
            {
                failure = "a source identity, storage binding, fingerprint, seed identity, or quantity is invalid";
                return false;
            }
            planned += source.Quantity;
            consumed += source.ConsumedQuantity;
        }
        if (planned != planting.RequestedCount || consumed != planting.PlantedCount)
        {
            failure = "planned/consumed source totals do not equal requested/planted counts";
            return false;
        }

        if (planting.CurrentStep is PlantingStepRecord step)
        {
            if (string.IsNullOrWhiteSpace(step.StepOperationId) || step.StepOperationId.Length > 128
                || step.LocationKey != planting.LocationKey
                || step.TileX < 0 || step.TileY < 0
                || !sourceIds.Contains(step.SeedSourceId)
                || step.SeedCountBefore is < 1 or > PlantingConstants.MaximumCount
                || !PlantingStepPhases.IsValid(step.Phase)
                || step.PostconditionSummary.Length > 256)
            {
                failure = "the frozen current step cannot be explained";
                return false;
            }
        }
        if (planting.Phase is PlantingPhases.Completed or PlantingPhases.Cancelled)
        {
            if (planting.CurrentStep is not null || (planting.Phase == PlantingPhases.Completed && planting.PlantedCount != planting.RequestedCount))
            {
                failure = "a terminal planting phase has an active step or incomplete exact count";
                return false;
            }
        }
        else if (planting.Phase == PlantingPhases.Reconciling && planting.CurrentStep is null)
        {
            failure = "Reconciling requires a frozen current step";
            return false;
        }

        failure = string.Empty;
        return true;
    }

}
