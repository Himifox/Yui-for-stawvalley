using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Monsters;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal static class WorldTargetCategories
{
    public const string Grass = "Grass";
    public const string Weed = "Weed";
    public const string Bush = "Bush";
    public const string WildTree = "WildTree";
    public const string FruitTree = "FruitTree";
    public const string WoodDebris = "WoodDebris";
    public const string HardwoodClump = "HardwoodClump";
    public const string BreakableStone = "BreakableStone";
    public const string BoulderClump = "BoulderClump";
    public const string MeteoriteClump = "MeteoriteClump";
    public const string UnknownClump = "UnknownClump";
    public const string GiantCrop = "GiantCrop";
    public const string Crop = "Crop";
    public const string Soil = "Soil";
    public const string PlantSlot = "PlantSlot";
    public const string DigSpot = "DigSpot";
    public const string Forage = "Forage";
    public const string PlacedObject = "PlacedObject";
    public const string FarmAnimal = "FarmAnimal";
    public const string Monster = "Monster";
}

internal static class WorldTargetDispositions
{
    public const string Candidate = "Candidate";
    public const string ObserveOnly = "ObserveOnly";
}

internal static class WorldTargetReasons
{
    public const string Ready = "READY";
    public const string NotRemovable = "NOT-REMOVABLE";
    public const string ProtectedTarget = "PROTECTED-TARGET";
    public const string NotRipe = "NOT-RIPE";
    public const string AlreadyWatered = "ALREADY-WATERED";
    public const string ClumpNotClassified = "CLUMP-NOT-CLASSIFIED";
    public const string ObserveOnly = "OBSERVE-ONLY";
    public const string GiantCropProtected = "GIANT-CROP-PROTECTED";
    public const string GiantCropPotential = "GIANT-CROP-POTENTIAL";
    public const string SpecialCropProtected = "SPECIAL-CROP-PROTECTED";
}

internal readonly record struct WorldTargetFact(
    string StableId,
    string Category,
    string Subtype,
    Vector2 Tile,
    string? SuggestedWorkKind,
    string Disposition,
    string ReasonCode);

internal static class WorldTargetClassifier
{
    public static IReadOnlyList<WorldTargetFact> Observe(GameLocation location)
    {
        var facts = new List<WorldTargetFact>();
        foreach ((Vector2 tile, TerrainFeature feature) in location.terrainFeatures.Pairs)
        {
            switch (feature)
            {
                case Grass:
                    Add(facts, tile, WorldTargetCategories.Grass, "Grass", WorkKinds.Mow);
                    break;
                case Tree tree:
                    Add(facts, tile, WorldTargetCategories.WildTree, Bounded(tree.treeType.Value, 64), WorkKinds.Chop);
                    break;
                case FruitTree fruitTree:
                    Add(facts, tile, WorldTargetCategories.FruitTree, Bounded(fruitTree.treeId.Value, 64), null, WorldTargetReasons.ProtectedTarget);
                    break;
                case Bush bush:
                    Add(facts, tile, WorldTargetCategories.Bush, bush.townBush.Value ? "TownBush" : "Bush", null, WorldTargetReasons.NotRemovable);
                    break;
                case HoeDirt dirt:
                    AddSoilAndCrop(facts, tile, dirt);
                    break;
            }
        }

        foreach ((Vector2 tile, SObject item) in location.Objects.Pairs)
        {
            if (item.IsWeeds())
                Add(facts, tile, WorldTargetCategories.Weed, Bounded(item.QualifiedItemId, 64), WorkKinds.Mow);
            else if (item.IsTwig())
                Add(facts, tile, WorldTargetCategories.WoodDebris, Bounded(item.QualifiedItemId, 64), WorkKinds.Chop);
            else if (item.IsBreakableStone())
                Add(facts, tile, WorldTargetCategories.BreakableStone, Bounded(item.QualifiedItemId, 64), WorkKinds.Mine);
            else if (item.QualifiedItemId is "(O)590" or "(O)SeedSpot")
                Add(facts, tile, WorldTargetCategories.DigSpot, Bounded(item.QualifiedItemId, 64), WorkKinds.Till);
            else if (item.IsSpawnedObject)
                Add(facts, tile, WorldTargetCategories.Forage, Bounded(item.QualifiedItemId, 64), WorkKinds.Forage);
            else
                Add(facts, tile, WorldTargetCategories.PlacedObject, Bounded(item.QualifiedItemId, 64), null, WorldTargetReasons.ObserveOnly);
        }

        foreach (ResourceClump clump in location.resourceClumps)
        {
            int type = clump.parentSheetIndex.Value;
            if (clump is GiantCrop giantCrop)
                Add(
                    facts,
                    giantCrop.Tile,
                    WorldTargetCategories.GiantCrop,
                    Bounded(giantCrop.Id, 64),
                    null,
                    WorldTargetReasons.GiantCropProtected,
                    $"giant-crop:{giantCrop.Id}:{(int)giantCrop.Tile.X},{(int)giantCrop.Tile.Y}");
            else if (type is ResourceClump.stumpIndex or ResourceClump.hollowLogIndex)
                Add(facts, clump.Tile, WorldTargetCategories.HardwoodClump, type.ToString(), WorkKinds.Chop);
            else if (type == ResourceClump.meteoriteIndex)
                Add(facts, clump.Tile, WorldTargetCategories.MeteoriteClump, type.ToString(), WorkKinds.Mine);
            else if (type == ResourceClump.boulderIndex)
                Add(facts, clump.Tile, WorldTargetCategories.BoulderClump, type.ToString(), WorkKinds.Mine);
            else
                Add(facts, clump.Tile, WorldTargetCategories.UnknownClump, type.ToString(), null, WorldTargetReasons.ClumpNotClassified);
        }

        foreach (Monster monster in location.characters.OfType<Monster>())
            Add(facts, monster.Tile, WorldTargetCategories.Monster, Bounded(monster.Name, 64), null, WorldTargetReasons.ObserveOnly, $"monster-{monster.GetHashCode()}");
        foreach (FarmAnimal animal in location.animals.Values)
            Add(facts, animal.Tile, WorldTargetCategories.FarmAnimal, Bounded(animal.type.Value, 64), null, WorldTargetReasons.ObserveOnly, $"animal-{animal.myID.Value}");
        return facts;
    }

    private static void AddSoilAndCrop(List<WorldTargetFact> facts, Vector2 tile, HoeDirt dirt)
    {
        bool dry = dirt.state.Value == HoeDirt.dry;
        Add(
            facts,
            tile,
            WorldTargetCategories.Soil,
            dry ? "Dry" : "Watered",
            dry ? WorkKinds.Water : null,
            dry ? WorldTargetReasons.Ready : WorldTargetReasons.AlreadyWatered,
            Stable(tile, "soil"));
        if (dirt.crop is null)
        {
            Add(
                facts,
                tile,
                WorldTargetCategories.PlantSlot,
                "EmptyHoeDirt",
                PlantingConstants.WorkKind,
                WorldTargetReasons.Ready,
                Stable(tile, "plant-slot"));
        }
        if (dirt.crop is not Crop crop)
            return;
        bool ready = !crop.dead.Value
            && crop.currentPhase.Value >= crop.phaseDays.Count - 1
            && (!crop.fullyGrown.Value || crop.dayOfCurrentPhase.Value <= 0);
        string? protectionReason = crop.dead.Value ? null : CropProtectionPolicy.GetReason(crop);
        string state = crop.dead.Value ? "Dead" : ready ? "Ready" : "Growing";
        string harvestItemId = Bounded(crop.indexOfHarvest.Value, 48);
        Add(
            facts,
            tile,
            WorldTargetCategories.Crop,
            $"{state}:{harvestItemId}",
            ready && protectionReason is null ? WorkKinds.Harvest : null,
            protectionReason ?? (ready ? WorldTargetReasons.Ready : WorldTargetReasons.NotRipe),
            Stable(tile, "crop"));
    }

    private static void Add(
        List<WorldTargetFact> facts,
        Vector2 tile,
        string category,
        string subtype,
        string? workKind,
        string reason = WorldTargetReasons.Ready,
        string? stableId = null)
    {
        facts.Add(new WorldTargetFact(
            stableId ?? Stable(tile, category),
            category,
            string.IsNullOrWhiteSpace(subtype) ? "Unknown" : subtype,
            tile,
            workKind,
            workKind is null ? WorldTargetDispositions.ObserveOnly : WorldTargetDispositions.Candidate,
            reason));
    }

    private static string Stable(Vector2 tile, string suffix) => $"{(int)tile.X},{(int)tile.Y}:{suffix}";
    private static string Bounded(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Length <= maximum ? value : value[..maximum];
}
