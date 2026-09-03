using StardewValley;

namespace YuiToIssho;

internal readonly record struct CraftRecipeResolution(bool IsSuccess, string Code, string Message, CraftRecipeDescriptor? Recipe)
{
    public static CraftRecipeResolution Success(CraftRecipeDescriptor recipe) => new(true, "CRAFT-RECIPE-READY", $"Resolved {recipe.RecipeKey}.", recipe);
    public static CraftRecipeResolution Failure(string code, string message) => new(false, code, message, null);
}

internal sealed record CraftRecipeDescriptor(
    string RecipeKey,
    CraftingRecipe Recipe,
    IReadOnlyList<CraftIngredientRecord> Ingredients,
    string OutputQualifiedItemId,
    int OutputPerCraft);

internal sealed class CraftingRecipePolicy
{
    public const int Version = 1;
    private static readonly HashSet<string> VanillaAllowlist = new(StringComparer.Ordinal)
    {
        "Chest", "Wood Fence", "Stone Fence", "Iron Fence", "Hardwood Fence", "Gate", "Torch", "Campfire",
        "Wood Path", "Gravel Path", "Cobblestone Path", "Stepping Stone Path", "Crystal Path", "Wood Floor",
        "Stone Floor", "Weathered Floor", "Crystal Floor", "Straw Floor", "Grass Starter", "Scarecrow",
        "Deluxe Scarecrow", "Sprinkler", "Quality Sprinkler", "Iridium Sprinkler", "Furnace", "Charcoal Kiln",
        "Bee House", "Tapper", "Heavy Tapper", "Mushroom Log", "Lightning Rod", "Recycling Machine", "Seed Maker",
        "Wood Chipper", "Cheese Press", "Mayonnaise Machine", "Loom", "Oil Maker", "Preserves Jar", "Keg", "Cask",
        "Fish Smoker", "Dehydrator", "Bait Maker", "Worm Bin", "Deluxe Worm Bin", "Garden Pot",
        "Bone Mill", "Geode Crusher", "Solar Panel", "Crystalarium", "Slime Incubator", "Slime Egg-Press",
        "Ostrich Incubator", "Farm Computer", "Mini-Jukebox", "Mini-Obelisk", "Cookout Kit", "Tent Kit", "Staircase",
        "Cherry Bomb", "Bomb", "Mega Bomb", "Basic Fertilizer", "Quality Fertilizer", "Deluxe Fertilizer", "Speed-Gro",
        "Deluxe Speed-Gro", "Hyper Speed-Gro", "Basic Retaining Soil", "Quality Retaining Soil", "Deluxe Retaining Soil",
        "Tree Fertilizer", "Wild Seeds (Sp)", "Wild Seeds (Su)", "Wild Seeds (Fa)", "Wild Seeds (Wi)", "Fiber Seeds",
        "Tea Sapling", "Flute Block", "Drum Block", "Wood Sign", "Stone Sign", "Dark Sign", "Text Sign",
        "Tub o' Flowers", "Jack-O-Lantern", "Wedding Ring"
    };

    public IReadOnlyList<string> ListAvailable(Farmer owner)
    {
        return owner.craftingRecipes.Keys
            .Where(VanillaAllowlist.Contains)
            .Where(key => this.TryResolve(owner, key).IsSuccess)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    public CraftRecipeResolution TryResolve(Farmer owner, string recipeKey)
    {
        if (string.IsNullOrWhiteSpace(recipeKey) || recipeKey.Length > 128 || recipeKey.Any(char.IsControl))
            return CraftRecipeResolution.Failure("INVALID-RECIPE-KEY", "Recipe key must contain 1 to 128 printable characters.");
        if (!VanillaAllowlist.Contains(recipeKey))
            return CraftRecipeResolution.Failure("RECIPE-NOT-ALLOWED", $"{recipeKey} is outside the built-in vanilla crafting allowlist.");
        if (!owner.craftingRecipes.ContainsKey(recipeKey))
            return CraftRecipeResolution.Failure("RECIPE-NOT-LEARNED", $"The exact Owner has not learned {recipeKey}.");

        try
        {
            var recipe = new CraftingRecipe(recipeKey, isCookingRecipe: false);
            if (recipe.isCookingRecipe)
                return CraftRecipeResolution.Failure("COOKING-NOT-SUPPORTED", "Cooking recipes are outside the first crafting release.");
            if (recipe.recipeList.Count is < 1 or > 16)
                return CraftRecipeResolution.Failure("INVALID-RECIPE-INGREDIENTS", "Recipe ingredient count is outside the bounded contract.");
            if (recipe.numberProducedPerCraft is < 1 or > 999)
                return CraftRecipeResolution.Failure("INVALID-RECIPE-YIELD", "Recipe output quantity is outside the bounded contract.");

            var ingredients = new List<CraftIngredientRecord>();
            foreach ((string ingredientId, int required) in recipe.recipeList.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(ingredientId) || ingredientId.Length > 128 || required is < 1 or > 9999)
                    return CraftRecipeResolution.Failure("INVALID-RECIPE-INGREDIENT", "A recipe ingredient ID or quantity is invalid.");
                ingredients.Add(new CraftIngredientRecord { IngredientId = ingredientId, RequiredPerCraft = required });
            }

            if (recipe.itemToProduce.Count != 1 || string.IsNullOrWhiteSpace(recipe.itemToProduce[0]))
                return CraftRecipeResolution.Failure("RANDOM-OUTPUT-NOT-SUPPORTED", "The recipe does not have one deterministic output ID.");
            Item output = recipe.createItem();
            if (output is not StardewValley.Object || string.IsNullOrWhiteSpace(output.QualifiedItemId) || output.Name == "Error Item")
                return CraftRecipeResolution.Failure("UNSAFE-OUTPUT-TYPE", "The recipe output is not a validated ordinary item or BigCraftable.");
            return CraftRecipeResolution.Success(new CraftRecipeDescriptor(recipeKey, recipe, ingredients, output.QualifiedItemId, recipe.numberProducedPerCraft));
        }
        catch (Exception ex)
        {
            return CraftRecipeResolution.Failure("RECIPE-RESOLUTION-FAILED", $"Recipe resolution stopped safely: {ex.GetType().Name}.");
        }
    }

    public static bool Matches(Item item, string ingredientId) => CraftingRecipe.ItemMatchesForCrafting(item, ingredientId);

    public static bool SnapshotMatches(CraftRecipeDescriptor descriptor, CraftRecipeSnapshot snapshot) =>
        descriptor.OutputQualifiedItemId == snapshot.OutputQualifiedItemId
        && descriptor.OutputPerCraft == snapshot.OutputPerCraft
        && descriptor.Ingredients.Count == snapshot.Ingredients.Count
        && descriptor.Ingredients.Zip(snapshot.Ingredients).All(pair => pair.First.IngredientId == pair.Second.IngredientId
            && pair.First.RequiredPerCraft == pair.Second.RequiredPerCraft);
}
