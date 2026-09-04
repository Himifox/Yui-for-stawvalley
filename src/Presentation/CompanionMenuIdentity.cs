using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Reflection;
using StardewValley;
using StardewValley.GameData.Characters;

namespace YuiToIssho;

internal sealed record CompanionMenuIdentitySnapshot(
    CompanionIdentity Identity,
    string DisplayName,
    string OwnerDisplayName,
    bool IsOwnedByViewer,
    bool OwnerOnline,
    bool WantsBody,
    bool BodyPresent,
    string LocationSummary,
    string Mode,
    string WorkKind,
    string VitalState,
    int BondPoints,
    int HeartLevel,
    bool TalkedToday,
    bool AffectionToday,
    int Health,
    int MaxHealth,
    float Stamina,
    float MaxStamina,
    CompanionAppearanceDto Appearance,
    ulong SnapshotVersion);

internal static class CompanionMenuIdentityFactory
{
    private static readonly FieldInfo? SpriteTextureField = typeof(AnimatedSprite).GetField(
        "spriteTexture",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    internal static (NPC Character, CharacterData Data) CreateNativeSocialNpc(CompanionMenuIdentitySnapshot identity, Texture2D transparentSprite)
    {
        string internalName = $"YuiToIsshoSocial_{identity.Identity.OwnerId}_{identity.Identity.Slot}";
        var sprite = new AnimatedSprite("Characters/Abigail", 0, 16, 32);
        if (SpriteTextureField is null)
            throw new MissingFieldException(typeof(AnimatedSprite).FullName, "spriteTexture");
        SpriteTextureField.SetValue(sprite, transparentSprite);
        var character = new NPC
        {
            Name = internalName,
            Sprite = sprite,
            SimpleNonVillagerNPC = false,
            AllowDynamicAppearance = false,
        };
        character.displayName = identity.DisplayName;
        character.faceDirection(2);
        var data = new CharacterData
        {
            DisplayName = identity.DisplayName,
            CanBeRomanced = false,
            CanReceiveGifts = false,
            SocialTab = SocialTabBehavior.AlwaysShown,
            TextureName = "Characters/Abigail",
            Size = new Point(16, 32),
            Breather = false,
        };
        return (character, data);
    }
}

/// <summary>Draws only Yui's character pixels inside positions reserved by vanilla menus.</summary>
internal sealed class CompanionNativeMenuPortraitRenderer
{
    private readonly Dictionary<(CompanionIdentity Identity, string ProfileId, int Generation), Farmer> visuals = new();

    public void Draw(SpriteBatch batch, CompanionMenuIdentitySnapshot identity, Vector2 position, int facing = 2)
    {
        var key = (identity.Identity, identity.Appearance.ProfileId, identity.Appearance.Generation);
        if (!this.visuals.TryGetValue(key, out Farmer? farmer))
        {
            foreach (var stale in this.visuals.Keys.Where(candidate => candidate.Identity == identity.Identity).ToArray())
                this.visuals.Remove(stale);
            CompanionAppearanceProfile profile = ToProfile(identity.Appearance);
            farmer = CompanionAppearanceCoordinator.CreateVisualFarmer(profile, $"YuiToIsshoNativeMenu_{identity.Identity.OwnerId}_{identity.Identity.Slot}");
            this.visuals.Add(key, farmer);
        }

        facing = facing is >= 0 and <= 3 ? facing : 2;
        bool flip = facing == 3;
        int frame = facing switch { 0 => 12, 1 => 6, 2 => 0, _ => 6 };
        farmer.faceDirection(facing);
        farmer.currentEyes = Farmer.eyesOpen;
        farmer.FarmerSprite.setCurrentSingleFrame(frame, 32000, secondaryArm: false, flip);
        Vector2 origin = new(farmer.xOffset, (farmer.yOffset + 128f - farmer.GetBoundingBox().Height / 2f) / 4f + 4f);
        farmer.FarmerRenderer.draw(batch, farmer.FarmerSprite, farmer.FarmerSprite.SourceRect, position, origin, 0.99f, Color.White, 0f, farmer);
    }

    public void Clear() => this.visuals.Clear();

    private static CompanionAppearanceProfile ToProfile(CompanionAppearanceDto dto) => new()
    {
        IsInitialized = true,
        ProfileSchemaVersion = dto.ProfileSchemaVersion,
        Generation = dto.Generation,
        BodyType = dto.BodyType,
        ProfileId = dto.ProfileId,
        HairStyle = dto.HairStyle,
        Skin = dto.Skin,
        ShirtId = dto.ShirtId,
        PantsId = dto.PantsId,
        ShoeColorId = dto.ShoeColorId,
        HairColor = dto.HairColor,
        EyeColor = dto.EyeColor,
        PantsColor = dto.PantsColor,
        AccessoryId = dto.AccessoryId,
        HatQualifiedItemId = dto.HatQualifiedItemId,
    };
}
