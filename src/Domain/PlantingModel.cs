namespace YuiToIssho;

internal sealed class PlantingTransactionRecord
{
    public string PlantingId { get; set; } = string.Empty;
    public string RequestOperationId { get; set; } = string.Empty;
    public long OwnerId { get; set; }
    public string SeedQualifiedItemId { get; set; } = string.Empty;
    public int SeedPolicyVersion { get; set; }
    public string LocationKey { get; set; } = string.Empty;
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public int EndX { get; set; }
    public int EndY { get; set; }
    public string Shape { get; set; } = WorkScopeShapes.Radius;
    public int Radius { get; set; }
    public int RequestedCount { get; set; }
    public int PlantedCount { get; set; }
    public long NextStepSequence { get; set; }
    public string Phase { get; set; } = PlantingPhases.Planned;
    public string ReturnMode { get; set; } = CompanionModes.Follow;
    public List<PlantingSourceRecord> SourcePlan { get; set; } = new();
    public PlantingStepRecord? CurrentStep { get; set; }
    public int CreatedDay { get; set; }
    public int LastConfirmedDay { get; set; }
    public ulong UpdatedTick { get; set; }
    public string? LastFailure { get; set; }
}

internal sealed class PlantingSourceRecord
{
    public string SourceId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = PlantingSourceKinds.Bag;
    public string StorageId { get; set; } = string.Empty;
    public int SourceSlot { get; set; }
    public string ItemFingerprint { get; set; } = string.Empty;
    public string QualifiedItemId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int AcquiredQuantity { get; set; }
    public int ConsumedQuantity { get; set; }
    public int ReturnedQuantity { get; set; }
}

internal sealed class PlantingStepRecord
{
    public string StepOperationId { get; set; } = string.Empty;
    public string LocationKey { get; set; } = string.Empty;
    public int TileX { get; set; }
    public int TileY { get; set; }
    public string SeedSourceId { get; set; } = string.Empty;
    public int SeedCountBefore { get; set; }
    public string Phase { get; set; } = PlantingStepPhases.PreparingStep;
    public string PostconditionSummary { get; set; } = string.Empty;
}

internal static class PlantingSourceKinds
{
    public const string Bag = "Bag";
    public const string AuthorizedChest = "AuthorizedChest";
    public static bool IsValid(string? value) => value is Bag or AuthorizedChest;
}

internal static class PlantingPhases
{
    public const string Planned = "Planned";
    public const string AcquiringSeeds = "AcquiringSeeds";
    public const string SeedsEscrowed = "SeedsEscrowed";
    public const string Planting = "Planting";
    public const string ReturningSeeds = "ReturningSeeds";
    public const string Paused = "Paused";
    public const string Cancelling = "Cancelling";
    public const string Reconciling = "Reconciling";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Faulted = "Faulted";

    public static bool IsValid(string? value) => value is Planned or AcquiringSeeds or SeedsEscrowed or Planting
        or ReturningSeeds or Paused or Cancelling or Reconciling or Completed or Cancelled or Faulted;

    public static bool OwnsResponsibility(string? value) => value is not (null or Completed or Cancelled);
}

internal static class PlantingStepPhases
{
    public const string PreparingStep = "PreparingStep";
    public const string Navigating = "Navigating";
    public const string CommitReady = "CommitReady";
    public const string WorldCommitted = "WorldCommitted";
    public const string ReconcilingStep = "ReconcilingStep";

    public static bool IsValid(string? value) => value is PreparingStep or Navigating or CommitReady or WorldCommitted or ReconcilingStep;
}
