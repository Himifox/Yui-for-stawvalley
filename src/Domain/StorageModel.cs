namespace YuiToIssho;

internal sealed class AuthorizedChestRecord
{
    public long OwnerId { get; set; }

    public string LocationKey { get; set; } = string.Empty;

    public bool IsStructure { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public string ChestToken { get; set; } = string.Empty;

    public AuthorizedChestIdentity Identity => new(this.OwnerId, this.LocationKey, this.IsStructure, this.TileX, this.TileY);
}

internal readonly record struct AuthorizedChestIdentity(long OwnerId, string LocationKey, bool IsStructure, int TileX, int TileY);

internal sealed class StorageLiabilityRecord
{
    public string ResponsibilityId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string ItemToken { get; set; } = string.Empty;

    public string QualifiedItemId { get; set; } = string.Empty;

    public int MaximumResponsibleStack { get; set; }

    public string SourceLocationKey { get; set; } = string.Empty;

    public bool SourceIsStructure { get; set; }

    public int SourceTileX { get; set; }

    public int SourceTileY { get; set; }

    public string SourceChestToken { get; set; } = string.Empty;

    public int OriginalSourceSlot { get; set; }

    public string? TaskOperationId { get; set; }

    public bool ReturnRequested { get; set; }

    public ulong CreatedTick { get; set; }
}

internal static class StorageLiabilityKinds
{
    public const string BorrowedTool = "BorrowedTool";

    public const string WithdrawnMaterial = "WithdrawnMaterial";

    public static bool IsValid(string? kind) => kind is BorrowedTool or WithdrawnMaterial;
}

internal sealed class CompanionItemRecord
{
    public string InstanceToken { get; set; } = string.Empty;

    public string QualifiedItemId { get; set; } = string.Empty;

    public int Stack { get; set; }

    public int Quality { get; set; }

    public Dictionary<string, string> ModData { get; set; } = new();
}

internal sealed class PendingResponsibilityRecord
{
    public string ResponsibilityId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}
