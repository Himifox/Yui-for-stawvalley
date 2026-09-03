namespace YuiToIssho;

internal sealed class CompanionAppearanceProfile
{
    public const int CurrentProfileSchemaVersion = 1;

    public bool IsInitialized { get; set; }

    public int ProfileSchemaVersion { get; set; } = CurrentProfileSchemaVersion;

    public int Generation { get; set; } = 1;

    public string BodyType { get; set; } = CompanionBodyTypes.Feminine;

    public string ProfileId { get; set; } = string.Empty;

    public int HairStyle { get; set; }

    public int Skin { get; set; }

    public string ShirtId { get; set; } = "1000";

    public string PantsId { get; set; } = "0";

    public string ShoeColorId { get; set; } = "2";

    public uint HairColor { get; set; }

    public uint EyeColor { get; set; }

    public uint PantsColor { get; set; }

    public int AccessoryId { get; set; } = -1;

    public string HatQualifiedItemId { get; set; } = string.Empty;
}

internal static class CompanionBodyTypes
{
    public const string Feminine = "Feminine";
    public static bool IsValid(string? value) => value == Feminine;
}
