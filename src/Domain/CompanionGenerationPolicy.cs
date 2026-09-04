using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewValley;

namespace YuiToIssho;

internal static class CompanionGenerationPolicy
{
    public const int MaximumCompanions = 64;
    public const string DisplayName = "Yui";
    public const string BodyType = CompanionBodyTypes.Feminine;
    public const int ProfileGeneration = 1;
    public const int MinimumSkin = 4;
    public const int MaximumSkin = 17;
    public const string ShirtId = "1000";
    public const string PantsId = "0";
    public const string ShoeColorId = "2";
    public const int AccessoryId = -1;
    public const string HatQualifiedItemId = "";

    private static readonly uint[] HairColors =
    {
        Packed(43, 38, 52), Packed(81, 50, 42), Packed(132, 78, 48),
        Packed(45, 72, 105), Packed(116, 58, 85), Packed(202, 153, 87),
    };

    private static readonly uint[] PantsColors =
    {
        Packed(39, 55, 89), Packed(59, 74, 69), Packed(81, 52, 69), Packed(58, 58, 64),
    };

    private static readonly uint EyeColor = Packed(59, 105, 142);

    public static bool TryValidateIdentity(CompanionIdentity identity, IReadOnlySet<long> knownOwnerIds, out string failure)
    {
        if (!identity.IsCanonical)
        {
            failure = "The companion identity must use a positive OwnerId and canonical Slot 1.";
            return false;
        }
        if (!knownOwnerIds.Contains(identity.OwnerId))
        {
            failure = $"Owner {identity.OwnerId} is not a member of this farm.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool TryValidateOwnerBinding(CompanionIdentity identity, Farmer? owner, out string failure)
    {
        if (!identity.IsCanonical)
        {
            failure = "The companion identity must use a positive OwnerId and canonical Slot 1.";
            return false;
        }
        if (owner is null || owner.UniqueMultiplayerID != identity.OwnerId)
        {
            failure = $"The supplied Farmer does not own companion {identity}.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool TryValidateProfile(CompanionAppearanceProfile? profile, IReadOnlyCollection<int> validHairStyles, out string failure)
    {
        if (profile is null || !profile.IsInitialized)
        {
            failure = "The appearance profile is not initialized.";
            return false;
        }
        if (!Guid.TryParseExact(profile.ProfileId, "N", out _))
        {
            failure = "ProfileId must be one compact GUID.";
            return false;
        }
        if (profile.ProfileSchemaVersion != CompanionAppearanceProfile.CurrentProfileSchemaVersion
            || profile.Generation != ProfileGeneration
            || profile.BodyType != BodyType)
        {
            failure = "The appearance schema, generation, or body type is unsupported.";
            return false;
        }
        if (!validHairStyles.Contains(profile.HairStyle)
            || profile.Skin is < MinimumSkin or > MaximumSkin)
        {
            failure = "The hairstyle or skin is outside Yui's generation range.";
            return false;
        }
        if (profile.ShirtId != ShirtId
            || profile.PantsId != PantsId
            || profile.ShoeColorId != ShoeColorId
            || !Game1.shirtData.ContainsKey(profile.ShirtId)
            || !Game1.pantsData.ContainsKey(profile.PantsId))
        {
            failure = "The clothing IDs do not match Yui's validated vanilla outfit.";
            return false;
        }
        if (!HairColors.Contains(profile.HairColor)
            || profile.EyeColor != EyeColor
            || !PantsColors.Contains(profile.PantsColor))
        {
            failure = "The saved colors are outside Yui's approved palettes.";
            return false;
        }
        if (profile.AccessoryId != AccessoryId || profile.HatQualifiedItemId != HatQualifiedItemId)
        {
            failure = "Yui's generated profile cannot contain an accessory or hat.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static void GenerateProfile(CompanionAppearanceProfile profile, IReadOnlyList<int> validHairStyles)
    {
        if (validHairStyles.Count == 0)
            throw new InvalidOperationException("A Yui profile cannot be generated without a vanilla hairstyle catalog.");

        profile.ProfileId = Guid.NewGuid().ToString("N");
        profile.ProfileSchemaVersion = CompanionAppearanceProfile.CurrentProfileSchemaVersion;
        profile.Generation = ProfileGeneration;
        profile.BodyType = BodyType;
        profile.HairStyle = validHairStyles[RandomNumberGenerator.GetInt32(validHairStyles.Count)];
        profile.Skin = RandomNumberGenerator.GetInt32(MinimumSkin, MaximumSkin + 1);
        profile.ShirtId = ShirtId;
        profile.PantsId = PantsId;
        profile.ShoeColorId = ShoeColorId;
        profile.HairColor = HairColors[RandomNumberGenerator.GetInt32(HairColors.Length)];
        profile.EyeColor = EyeColor;
        profile.PantsColor = PantsColors[RandomNumberGenerator.GetInt32(PantsColors.Length)];
        profile.AccessoryId = AccessoryId;
        profile.HatQualifiedItemId = HatQualifiedItemId;
        profile.IsInitialized = true;
    }

    private static uint Packed(byte red, byte green, byte blue) => new Color(red, green, blue).PackedValue;
}
