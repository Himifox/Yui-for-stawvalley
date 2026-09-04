namespace YuiToIssho;

internal sealed class CompanionAppearanceProfile
{
    public const int CurrentProfileSchemaVersion = 1;

    public bool IsInitialized { get; set; }

    public int ProfileSchemaVersion { get; set; } = CurrentProfileSchemaVersion;

    public int Generation { get; set; } = CompanionGenerationPolicy.ProfileGeneration;

    public string BodyType { get; set; } = CompanionGenerationPolicy.BodyType;

    public string ProfileId { get; set; } = string.Empty;

    public int HairStyle { get; set; }

    public int Skin { get; set; }

    public string ShirtId { get; set; } = CompanionGenerationPolicy.ShirtId;

    public string PantsId { get; set; } = CompanionGenerationPolicy.PantsId;

    public string ShoeColorId { get; set; } = CompanionGenerationPolicy.ShoeColorId;

    public uint HairColor { get; set; }

    public uint EyeColor { get; set; }

    public uint PantsColor { get; set; }

    public int AccessoryId { get; set; } = CompanionGenerationPolicy.AccessoryId;

    public string HatQualifiedItemId { get; set; } = CompanionGenerationPolicy.HatQualifiedItemId;
}

internal static class CompanionBodyTypes
{
    public const string Feminine = "Feminine";
    public static bool IsValid(string? value) => value == Feminine;
}
