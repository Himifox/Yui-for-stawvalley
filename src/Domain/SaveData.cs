namespace YuiToIssho;

internal sealed class YuiToIsshoSaveData
{
    public const int CurrentSchemaVersion = 10;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool CompanionIntroductionCompleted { get; set; }

    public List<CompanionRecord> Companions { get; set; } = new();

    public List<AuthorizedChestRecord> AuthorizedChests { get; set; } = new();
}

internal sealed class CompanionRecord
{
    public long OwnerId { get; set; }

    public int Slot { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public bool WantsBody { get; set; }

    public string Mode { get; set; } = CompanionModes.Wait;

    public WorkDirectiveRecord? WorkDirective { get; set; }

    public bool OwnerWorkAssistEnabled { get; set; }

    public List<CompanionItemRecord> Inventory { get; set; } = new();

    public List<PendingResponsibilityRecord> PendingResponsibilities { get; set; } = new();

    public string? ActiveTransactionId { get; set; }

    public List<OperationReceiptRecord> RecentOperations { get; set; } = new();

    public List<StorageLiabilityRecord> StorageLiabilities { get; set; } = new();

    public List<DeliveryRecord> Deliveries { get; set; } = new();

    public CraftTransactionRecord? CraftTransaction { get; set; }

    public PlantingTransactionRecord? PlantingTransaction { get; set; }

    public bool StorageWriteBlocked { get; set; }

    public string? LastStorageFailure { get; set; }

    public CompanionVitalsRecord Vitals { get; set; } = new();

    public CompanionBondRecord Bond { get; set; } = new();

    public CompanionAppearanceProfile Appearance { get; set; } = new();

    public CompanionIdentity Identity => new(this.OwnerId, this.Slot);
}

internal sealed class CompanionBondRecord
{
    public const int MaxPoints = 2500;

    public int Points { get; set; }

    public int LastTalkedDay { get; set; } = -1;

    public int LastAffectionDay { get; set; } = -1;

    public int LastGiftDay { get; set; } = -1;

    public int GiftWeek { get; set; } = -1;

    public int GiftsThisWeek { get; set; }

    public int GetHeartLevel() => Math.Clamp(this.Points / 250, 0, 10);
}
