namespace YuiToIssho;

internal sealed class CraftTransactionRecord
{
    public string CraftId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public long OwnerId { get; set; }
    public string RecipeKey { get; set; } = string.Empty;
    public int RecipePolicyVersion { get; set; }
    public int CraftCount { get; set; }
    public int CompletedCount { get; set; }
    public CraftRecipeSnapshot RecipeSnapshot { get; set; } = new();
    public List<CraftSourceRecord> SourcePlan { get; set; } = new();
    public string Phase { get; set; } = CraftPhases.Planned;
    public string? OutputToken { get; set; }
    public List<string> OutputTokens { get; set; } = new();
    public string? OutputLocation { get; set; }
    public bool ProgressApplied { get; set; }
    public string? LastFailure { get; set; }
    public int CreatedDay { get; set; }
    public ulong UpdatedTick { get; set; }
}

internal sealed class CraftRecipeSnapshot
{
    public List<CraftIngredientRecord> Ingredients { get; set; } = new();
    public string OutputQualifiedItemId { get; set; } = string.Empty;
    public int OutputPerCraft { get; set; }
}

internal sealed class CraftIngredientRecord
{
    public string IngredientId { get; set; } = string.Empty;
    public int RequiredPerCraft { get; set; }
}

internal sealed class CraftSourceRecord
{
    public string SourceKind { get; set; } = CraftSourceKinds.Bag;
    public string StorageId { get; set; } = string.Empty;
    public int SourceSlot { get; set; }
    public string ItemFingerprint { get; set; } = string.Empty;
    public string QualifiedItemId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool Acquired { get; set; }
}

internal static class CraftSourceKinds
{
    public const string Bag = "Bag";
    public const string AuthorizedChest = "AuthorizedChest";
    public static bool IsValid(string? value) => value is Bag or AuthorizedChest;
}

internal static class CraftPhases
{
    public const string Planned = "Planned";
    public const string AcquiringMaterials = "AcquiringMaterials";
    public const string MaterialsEscrowed = "MaterialsEscrowed";
    public const string CommitReady = "CommitReady";
    public const string OutputCreated = "OutputCreated";
    public const string MaterialsConsumed = "MaterialsConsumed";
    public const string ProgressApplied = "ProgressApplied";
    public const string ReturningMaterials = "ReturningMaterials";
    public const string Reconciling = "Reconciling";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string Faulted = "Faulted";

    public static bool IsValid(string? value) => value is Planned or AcquiringMaterials or MaterialsEscrowed or CommitReady
        or OutputCreated or MaterialsConsumed or ProgressApplied or ReturningMaterials or Reconciling or Completed or Cancelled or Faulted;

    public static bool OwnsResponsibility(string? value) => value is not (null or Completed or Cancelled);
    public static bool CanCancel(string? value) => value is Planned or AcquiringMaterials or MaterialsEscrowed or CommitReady or ReturningMaterials;
}
