namespace YuiToIssho;

internal sealed class CompanionVitalsRecord
{
    public int MaxHealth { get; set; } = 100;

    public int Health { get; set; } = 100;

    public float MaxStamina { get; set; } = 270f;

    public float Stamina { get; set; } = 270f;

    public int LastNormalizedDay { get; set; } = -1;

    public string State { get; set; } = CompanionVitalStates.Active;

    public string ResumeMode { get; set; } = CompanionModes.Wait;

    public int InvulnerabilityTicksRemaining { get; set; }

    public int DownedTicksRemaining { get; set; }

    public int RestTicksRemaining { get; set; }

    public bool DownedForDay { get; set; }

    public string? RecoveryEpisodeId { get; set; }

    public string? LastSettledRecoveryId { get; set; }

    public int RecoveryDay { get; set; } = -1;

    public string? RecoveryReason { get; set; }

    public string? LastDamageSource { get; set; }

    public int LastDamageTaken { get; set; }

    public string? LastActionCommitId { get; set; }

    public float LastActionCost { get; set; }

    public string? LastFoodItemId { get; set; }

    public int LastFoodHealthRestored { get; set; }

    public float LastFoodStaminaRestored { get; set; }

    public string? LastFailure { get; set; }

    public List<VitalCostReceiptRecord> RecentCosts { get; set; } = new();
}

internal sealed class VitalCostReceiptRecord
{
    public string CommitId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public float Cost { get; set; }
}

internal static class CompanionVitalStates
{
    public const string Active = "Active";
    public const string Resting = "Resting";
    public const string Retreating = "Retreating";
    public const string Downed = "Downed";
    public const string Recovering = "Recovering";

    public static bool IsValid(string? state) => state is Active or Resting or Retreating or Downed or Recovering;
}
