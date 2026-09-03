namespace YuiToIssho;

internal sealed class DeliveryRecord
{
    public string DeliveryId { get; set; } = string.Empty;

    public long RecipientPlayerId { get; set; }

    public string CargoToken { get; set; } = string.Empty;

    public string QualifiedItemId { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public string Phase { get; set; } = DeliveryPhases.Escrowed;

    public string? LastFailure { get; set; }

    public ulong CreatedTick { get; set; }

    public int Attempt { get; set; }

    public ulong NextAttemptTick { get; set; }
}

internal static class DeliveryPhases
{
    public const string Escrowed = "Escrowed";
    public const string Traveling = "Traveling";
    public const string Offering = "Offering";
    public const string Returning = "Returning";
    public const string Completed = "Completed";
    public const string Returned = "Returned";
    public const string Faulted = "Faulted";

    public static bool IsValid(string? phase) => phase is Escrowed or Traveling or Offering or Returning or Completed or Returned or Faulted;

    public static bool OwnsEscrow(string? phase) => phase is Escrowed or Traveling or Offering or Returning or Faulted;
}
