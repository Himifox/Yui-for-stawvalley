using StardewValley;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal enum CompanionGiftTaste
{
    Hated,
    Disliked,
    Neutral,
    Liked,
    Loved,
}

internal readonly record struct CompanionGiftPreference(CompanionGiftTaste Taste, int BondPoints, string Code)
{
    public string Describe(string itemName) => this.Taste switch
    {
        CompanionGiftTaste.Loved => $"Yui loves {itemName}. Bond +{this.BondPoints}.",
        CompanionGiftTaste.Liked => $"Yui likes {itemName}. Bond +{this.BondPoints}.",
        CompanionGiftTaste.Neutral => $"Yui accepted {itemName}. Bond +{this.BondPoints}.",
        CompanionGiftTaste.Disliked => $"Yui accepted {itemName}, but it is not to her taste. Bond {this.BondPoints}.",
        _ => $"Yui really dislikes {itemName}. Bond {this.BondPoints}.",
    };
}

/// <summary>Yui-owned gift tastes, independent from every vanilla NPC friendship table.</summary>
internal static class CompanionGiftPreferences
{
    private static readonly HashSet<string> LovedItems = new(StringComparer.Ordinal)
    {
        "(O)595", // Fairy Rose
        "(O)797", // Pearl
        "(O)StardropTea",
        "(O)279", // Magic Rock Candy
    };

    private static readonly HashSet<string> HatedItems = new(StringComparer.Ordinal)
    {
        "(O)167", // Joja Cola
        "(O)168", // Trash
        "(O)169", // Driftwood
        "(O)170", // Broken Glasses
        "(O)171", // Broken CD
        "(O)172", // Soggy Newspaper
        "(O)308", // Void Mayonnaise
    };

    public static CompanionGiftPreference Evaluate(SObject item)
    {
        string id = item.QualifiedItemId;
        if (LovedItems.Contains(id))
            return new CompanionGiftPreference(CompanionGiftTaste.Loved, 80, "GIFT-LOVED");
        if (HatedItems.Contains(id) || item.Category == SObject.junkCategory || item.HasContextTag("trash_item"))
            return new CompanionGiftPreference(CompanionGiftTaste.Hated, -40, "GIFT-HATED");
        if (item.HasContextTag("alcohol_item") || item.Category is SObject.monsterLootCategory or SObject.baitCategory)
            return new CompanionGiftPreference(CompanionGiftTaste.Disliked, -15, "GIFT-DISLIKED");
        if (item.Category is SObject.flowersCategory or SObject.CookingCategory or SObject.FruitsCategory or SObject.GemCategory
            || item.HasContextTag("book_item"))
            return new CompanionGiftPreference(CompanionGiftTaste.Liked, 45, "GIFT-LIKED");
        return new CompanionGiftPreference(CompanionGiftTaste.Neutral, 15, "GIFT-NEUTRAL");
    }
}
