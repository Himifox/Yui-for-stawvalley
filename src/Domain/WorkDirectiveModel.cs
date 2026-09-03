namespace YuiToIssho;

internal static class CompanionModes
{
    public const string Follow = "Follow";

    public const string Wait = "Wait";

    public const string Work = "Work";

    public static bool IsValid(string? mode) => mode is Follow or Wait or Work;
}

internal sealed class WorkDirectiveRecord
{
    public string DirectiveId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string LocationKey { get; set; } = string.Empty;

    public int AnchorX { get; set; }

    public int AnchorY { get; set; }

    public int EndX { get; set; }

    public int EndY { get; set; }

    public string Shape { get; set; } = WorkScopeShapes.Radius;

    public int Radius { get; set; } = WorkScopeContracts.DefaultRadius;

    public string CompletionPolicy { get; set; } = WorkCompletionPolicies.UntilClear;

    public string ReturnMode { get; set; } = CompanionModes.Follow;

    public long NextStepSequence { get; set; }

    public int CreatedDay { get; set; }

    public int LastConfirmedDay { get; set; }

    public string? SuspendedReason { get; set; }

    public bool IsOwnerAssistLease { get; set; }
}
