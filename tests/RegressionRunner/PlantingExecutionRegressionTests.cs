using System.Collections;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.GameData.Locations;
using StardewValley.GameData.Objects;
using StardewValley.Inventories;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Network;
using StardewValley.TerrainFeatures;
using YuiToIssho;
using xTile;
using xTile.Dimensions;
using xTile.Layers;
using xTile.Tiles;

internal static class PlantingExecutionRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        var playerField = typeof(Game1).GetField("_player", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousPlayer = playerField.GetValue(null);
        var previousObjects = Game1.objectData;
        var previousCrops = Game1.cropData;
        var previousLocations = Game1.locationData;
        var previousFarmers = Game1.otherFarmers;
        var floorLookup = typeof(Flooring).GetField("_FloorPathItemLookup", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousFloorLookup = floorLookup.GetValue(null);
        var previousTypes = ItemRegistry.ItemTypes.ToArray();
        var lookup = (IDictionary)typeof(ItemRegistry).GetField("IdentifierLookup", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var harmony = new Harmony("Himifox.PlantingHeadlessRegression");
        try
        {
            // Only texture loading and audio require stand-ins. The coordinator,
            // navigation, vanilla crop planting, and escrow consumption stay real.
            var skip = new HarmonyMethod(typeof(PlantingExecutionRegressionTests).GetMethod(nameof(SkipPresentation), BindingFlags.Static | BindingFlags.NonPublic)!);
            harmony.Patch(AccessTools.Method(typeof(HoeDirt), "loadSprite"), prefix: skip);
            harmony.Patch(AccessTools.Method(typeof(GameLocation), "playSound"), prefix: skip);
            Game1.objectData = new Dictionary<string, ObjectData>
            {
                ["472"] = new() { Name = "Parsnip Seeds", DisplayName = "Parsnip Seeds", Description = "", Type = "Seeds", Category = StardewValley.Object.SeedsCategory },
                ["24"] = new() { Name = "Parsnip", DisplayName = "Parsnip", Description = "", Type = "Basic" },
            };
            Game1.cropData = new Dictionary<string, CropData> { ["472"] = new() { HarvestItemId = "24", Seasons = new() { Season.Spring }, DaysInPhase = new() { 1, 1 } } };
            Game1.locationData = new Dictionary<string, LocationData> { ["PlantTest"] = new() { CanPlantHere = true } };
            ItemRegistry.ItemTypes.Clear();
            lookup.Clear();
            ItemRegistry.AddTypeDefinition(new ObjectDataDefinition());
            var player = NewOwner(new GameLocation());
            player.teamRoot = CraftingSettlementRegressionTests.CreateInventoryPlayer().teamRoot;
            typeof(Farmer).GetField("uniqueMultiplayerID")!.SetValue(player, new NetLong(1));
            playerField.SetValue(null, player);
            Game1.otherFarmers = new NetRootDictionary<long, Farmer>();
            floorLookup.SetValue(null, new Dictionary<string, string>());
            ContinuousPlanting();
            SettlementRechecksOccupancy();
            OnlyApproachCanAlreadyContainYui();
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            playerField.SetValue(null, previousPlayer);
            Game1.objectData = previousObjects;
            Game1.cropData = previousCrops;
            Game1.locationData = previousLocations;
            Game1.otherFarmers = previousFarmers;
            floorLookup.SetValue(null, previousFloorLookup);
            ItemRegistry.ItemTypes.Clear();
            lookup.Clear();
            foreach (var definition in previousTypes) ItemRegistry.AddTypeDefinition(definition);
            ItemRegistry.ResetCache();
        }
        Console.WriteLine("Planting execution regressions passed (continuous vanilla planting, capacity, occupancy and seed conservation).");
    }

    private static bool SkipPresentation() => false;

    private static void ContinuousPlanting()
    {
        var f = new Fixture();
        Assert.Equal(2, f.Slots().Length, "The bound body's tile is movable area capacity.");
        f.Coordinator.Update(1); // reserve first real child step
        f.Coordinator.Update(2); // plant from adjacent body tile
        Assert.Equal(1, f.Transaction.PlantedCount, $"First real settlement: phase={f.Transaction.Phase}, failure={f.Transaction.LastFailure}, step={f.Transaction.CurrentStep?.Phase}");
        Assert.True(f.First.crop is not null && f.Second.crop is null);
        Assert.Equal(1, f.Escrow.Sum(item => item.Stack));
        f.AssertSaveRoundTrip();
        f.Coordinator.Update(3); // second target is the body's former approach
        Assert.Equal(PlantingPhases.Planting, f.Transaction.Phase, "Self occupancy cannot pause a sufficient area.");
        object task = f.RuntimeType.GetProperty("CurrentTask")!.GetValue(f.Runtime)!;
        Assert.True(task is not null, "A real second child step must be created.");
        Vector2 approach = (Vector2)task!.GetType().GetProperty("ApproachTile")!.GetValue(task)!;
        Assert.True(approach != f.Body.Tile, "The second crop must require leaving its target.");
        // Model arrival at the real navigation service's chosen approach.
        f.Body.Position = approach * Game1.tileSize;
        f.Coordinator.Update(4);
        Assert.Equal(PlantingPhases.Completed, f.Transaction.Phase);
        Assert.Equal(2, f.Transaction.PlantedCount);
        Assert.Equal(2, f.Transaction.SourcePlan.Single().ConsumedQuantity);
        Assert.True(f.First.crop is not null && f.Second.crop is not null);
        Assert.Equal(0, f.Escrow.Count);
        f.AssertSaveRoundTrip();
        f.Coordinator.Update(5);
        Assert.Equal(2, f.Transaction.PlantedCount, "Repeated ticks must not commit a tile twice.");
        Assert.Equal(0, f.Escrow.Count);
    }

    private static void SettlementRechecksOccupancy()
    {
        foreach (string blocker in new[] { "npc", "farmer", "object", "season", "self" })
        {
            var f = new Fixture();
            f.Coordinator.Update(10);
            if (blocker == "npc") f.Location.characters.Add(new NPC { Position = new Vector2(1, 2) * Game1.tileSize, Sprite = new AnimatedSprite() });
            if (blocker == "farmer") { Game1.player.currentLocation = f.Location; Game1.player.Position = new Vector2(1, 2) * Game1.tileSize; }
            if (blocker == "object") f.Location.Objects.Add(new Vector2(1, 2), new StardewValley.Object { ItemId = "24", Stack = 1 });
            if (blocker == "season") SetSeason(f.Location, Season.Winter);
            if (blocker == "self")
            {
                // Occupied target must never pass strict eligibility, even though
                // capacity deliberately permits only this exact body to move away.
                Assert.True(!PlantingPreviewService.IsPlantableTarget(f.Location, new Vector2(2, 2), f.Second, "472", Game1.cropData["472"]));
                f.Coordinator.Pause(f.Record.Identity, "TEST-STOP");
            }
            else f.Coordinator.Update(11);
            Assert.Equal(0, f.Transaction.PlantedCount, blocker);
            Assert.True(f.First.crop is null && f.Second.crop is null, blocker);
            Assert.Equal(2, f.Escrow.Sum(item => item.Stack), blocker + " cannot consume a seed.");
            f.Coordinator.Update(12);
            Assert.Equal(2, f.Escrow.Sum(item => item.Stack), "Stopped/failed ticks must preserve escrow.");
        }
    }

    private static void OnlyApproachCanAlreadyContainYui()
    {
        var f = new Fixture();
        // A single empty plot in a narrow opening; its only cardinal approach is
        // the exact bound body's current tile, which is plain ground here.
        f.Location.terrainFeatures.Remove(new Vector2(2, 2));
        f.Body.IsInvisible = false;
        foreach (Vector2 tile in new[] { new Vector2(0, 2), new Vector2(1, 1), new Vector2(1, 3) })
            f.Location.Objects.Add(tile, new StardewValley.Object { ItemId = "24", Stack = 1 });
        f.Transaction.RequestedCount = 1;
        f.Transaction.SourcePlan[0].Quantity = 1;
        f.Transaction.SourcePlan[0].AcquiredQuantity = 1;
        f.Escrow[0].Stack = 1;
        Assert.Equal(1, f.Slots().Length, "Yui already at the only approach must not remove a valid plot.");

        var other = new NPC { Sprite = new AnimatedSprite() };
        var approachBounds = new Microsoft.Xna.Framework.Rectangle(2 * Game1.tileSize, 2 * Game1.tileSize, Game1.tileSize, Game1.tileSize);
        bool overlapsFromNextTile = false;
        for (int offset = 1; offset < Game1.tileSize; offset++)
        {
            other.Position = f.Body.Position + new Vector2(offset, 0);
            if (other.Tile != f.Body.Tile && other.GetBoundingBox().Intersects(approachBounds))
            {
                overlapsFromNextTile = true;
                break;
            }
        }
        Assert.True(overlapsFromNextTile, "The fixture must represent a real NPC crossing a tile boundary.");
        f.Location.characters.Add(other);
        Assert.Equal(0, f.Slots().Length, "The self exception cannot ignore an outsider whose bounding box overlaps the only approach.");
        f.Location.characters.Remove(other);

        f.Location.Objects.Add(new Vector2(2, 2), new StardewValley.Object { ItemId = "24", Stack = 1 });
        Assert.Equal(0, f.Slots().Length, "Sharing an approach with Yui cannot hide an object blocker.");
        f.Location.Objects.Remove(new Vector2(2, 2));
        f.Coordinator.Update(20);
        f.Coordinator.Update(21);
        Assert.Equal(PlantingPhases.Completed, f.Transaction.Phase, "The actual selected child step must settle from its occupied sole approach.");
        Assert.True(f.First.crop is not null);
        Assert.Equal(1, f.Transaction.PlantedCount);
        Assert.Equal(0, f.Escrow.Count);
    }

    private static Farmer NewOwner(GameLocation location)
    {
        var owner = (Farmer)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Farmer));
        var character = new Character();
        foreach (FieldInfo field in typeof(Character).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            field.SetValue(owner, field.GetValue(character));
        typeof(Character).GetField("currentLocationRef", Private)!.SetValue(owner, new NetLocationRef(location));
        typeof(Farmer).GetField("uniqueMultiplayerID")!.SetValue(owner, new NetLong(2));
        typeof(Farmer).GetField("usingTool")!.SetValue(owner, new NetBool(false));
        typeof(Farmer).GetField("toolPower")!.SetValue(owner, new NetInt());
        typeof(Farmer).GetField("maxStamina")!.SetValue(owner, new NetInt(270));
        typeof(Farmer).GetField("netStamina", Private)!.SetValue(owner, new NetFloat(270));
        typeof(Farmer).GetField("currentToolIndex", Private)!.SetValue(owner, new NetInt(0));
        typeof(Farmer).GetField("netItemStowed")!.SetValue(owner, new NetBool());
        typeof(Farmer).GetField("professions")!.SetValue(owner, new NetIntHashSet());
        typeof(Farmer).GetField("netItems")!.SetValue(owner, new NetRef<Inventory>(new Inventory()));
        typeof(Farmer).GetField("temporaryItem", Private)!.SetValue(owner, new NetRef<Item>());
        typeof(Farmer).GetField("netMount")!.SetValue(owner, new NetRef<StardewValley.Characters.Horse>());
        typeof(Farmer).GetField("netSpouse", Private)!.SetValue(owner, new NetString());
        typeof(Farmer).GetField("stats")!.SetValue(owner, new Stats());
        typeof(Farmer).GetField("movementDirections")!.SetValue(owner, new List<int>());
        var buffs = new StardewValley.Buffs.BuffManager { Dirty = false };
        buffs.SetOwner(owner);
        typeof(Farmer).GetField("buffs")!.SetValue(owner, buffs);
        // A copied Character sprite field retains callbacks to its source object.
        // Give the Farmer its own field before attaching a real FarmerSprite.
        typeof(Character).GetField("sprite")!.SetValue(owner, new NetRef<AnimatedSprite>());
        owner.FarmerSprite = new FarmerSprite();
        owner.CanMove = true;
        owner.Position = new Vector2(4, 4) * Game1.tileSize;
        return owner;
    }

    private static void SetSeason(GameLocation location, Season season) =>
        typeof(GameLocation).GetField("seasonOverride", Private)!.SetValue(location, new Lazy<Season?>(() => season));

    public class QuietMonitor : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method!.ReturnType == typeof(bool) ? false : null;
    }

    private sealed class Fixture
    {
        internal readonly GameLocation Location = new();
        internal readonly CompanionRecord Record = new() { OwnerId = 2, Slot = 1, DisplayName = CompanionGenerationPolicy.DisplayName, Mode = CompanionModes.Follow };
        internal readonly NPC Body;
        internal readonly Farmer Owner;
        internal readonly HoeDirt First;
        internal readonly HoeDirt Second;
        internal readonly Inventory Escrow;
        internal readonly PlantingCoordinator Coordinator;
        internal readonly PlantingPreviewService Preview;
        internal readonly PlantingTransactionRecord Transaction;
        internal readonly object Runtime;
        internal readonly Type RuntimeType;
        private readonly CompanionRegistry registry;
        private readonly CompanionInventoryStore inventories;
        internal Fixture()
        {
            // GameLocation equality uses its stable name, so detach the prior
            // case's host before reusing this deliberately identical location.
            Game1.player.currentLocation = null;
            typeof(GameLocation).GetField("name")!.SetValue(Location, new NetString("PlantTest"));
            Location.IsOutdoors = true;
            SetSeason(Location, Season.Spring);
            var map = new Map();
            var sheet = new TileSheet("ground", map, "headless", new Size(1, 1), new Size(16, 16));
            map.AddTileSheet(sheet);
            foreach (string layerName in new[] { "Back", "Buildings", "Front", "AlwaysFront" })
            {
                var layer = new Layer(layerName, map, new Size(8, 8), new Size(16, 16));
                map.AddLayer(layer);
                if (layerName == "Back")
                    for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) layer.Tiles[x, y] = new StaticTile(layer, sheet, BlendMode.Alpha, 0);
            }
            Location.Map = map;
            Owner = NewOwner(Location);
            Owner.Position = new Vector2(0, 2) * Game1.tileSize;
            Body = new NPC { Name = "YuiPlantTest", Position = new Vector2(2, 2) * Game1.tileSize, currentLocation = Location, Sprite = new AnimatedSprite(), farmerPassesThrough = true, IsInvisible = true };
            Location.characters.Add(Body);
            First = new HoeDirt(0, Location);
            Second = new HoeDirt(0, Location);
            Location.terrainFeatures.Add(new Vector2(1, 2), First);
            Location.terrainFeatures.Add(new Vector2(2, 2), Second);
            var monitor = DispatchProxy.Create<IMonitor, QuietMonitor>();
            registry = new CompanionRegistry();
            ((IDictionary)typeof(CompanionRegistry).GetField("records", Private)!.GetValue(registry)!).Add(Record.Identity, Record);
            var bodies = new CompanionBodyBinder(monitor);
            ((IDictionary)typeof(CompanionBodyBinder).GetField("boundBodies", Private)!.GetValue(bodies)!).Add(Record.Identity, Body);
            inventories = new CompanionInventoryStore();
            Preview = new PlantingPreviewService(inventories, null!, bodies);
            Coordinator = new PlantingCoordinator(registry, bodies, inventories, null!, Preview, new TaskExecutionService(registry, bodies, monitor), new TaskNavigationService(bodies), new CompanionAppearanceCoordinator(registry, bodies, monitor), monitor);
            var seed = new StardewValley.Object { ItemId = "472", Stack = 2, Category = StardewValley.Object.SeedsCategory };
            string plantingId = Guid.NewGuid().ToString("N");
            string sourceId = Guid.NewGuid().ToString("N");
            string fingerprint = (string)typeof(PlantingCoordinator).GetMethod("Fingerprint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { seed })!;
            seed.modData[CompanionInventoryStore.PlantingIdTag] = plantingId;
            seed.modData[CompanionInventoryStore.PlantSourceTag] = sourceId;
            Escrow = inventories.GetPlantEscrow(Record.Identity);
            Escrow.Clear();
            Escrow.Add(seed);
            var source = new PlantingSourceRecord { SourceId = sourceId, SourceKind = PlantingSourceKinds.Bag, StorageId = CompanionInventoryStore.GetNamespace(Record.Identity), QualifiedItemId = seed.QualifiedItemId, ItemFingerprint = fingerprint, Quantity = 2, AcquiredQuantity = 2 };
            Transaction = new PlantingTransactionRecord { PlantingId = plantingId, RequestOperationId = "request-test", OwnerId = Record.OwnerId, SeedQualifiedItemId = seed.QualifiedItemId, SeedPolicyVersion = PlantingConstants.SeedPolicyVersion, Phase = PlantingPhases.Planting, LocationKey = Location.NameOrUniqueName, RequestedCount = 2, Shape = WorkScopeShapes.Rectangle, AnchorX = 1, AnchorY = 2, EndX = 2, EndY = 2, SourcePlan = new() { source } };
            Record.PlantingTransaction = Transaction;
            Record.ActiveTransactionId = Transaction.PlantingId;
            RuntimeType = typeof(PlantingCoordinator).GetNestedType("PlantingRuntime", BindingFlags.NonPublic)!;
            Type sourceType = typeof(PlantingCoordinator).GetNestedType("PlantingRuntimeSource", BindingFlags.NonPublic)!;
            var sources = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sourceType))!;
            sources.Add(Activator.CreateInstance(sourceType, new object?[] { source, null, null, seed })!);
            Runtime = Activator.CreateInstance(RuntimeType, new object[] { Record, Owner, Transaction, sources })!;
            ((IDictionary)typeof(PlantingCoordinator).GetField("runtimes", Private)!.GetValue(Coordinator)!).Add(Record.Identity, Runtime);
        }
        internal PlantSlotPreview[] Slots() => Preview.CaptureEligibleSlots(Record.Identity, Owner, Location, new PlantingScope(Location.NameOrUniqueName, 1, 2, 2, 2, WorkScopeShapes.Rectangle, 0), Escrow[0]);
        internal void AssertSaveRoundTrip()
        {
            var snapshot = JsonSerializer.Deserialize<YuiToIsshoSaveData>(JsonSerializer.Serialize(registry.CreateSnapshot()))!;
            var reloaded = new CompanionRegistry();
            var loaded = reloaded.Load(snapshot, new HashSet<long> { Record.OwnerId });
            Assert.True(loaded.IsSuccess, loaded.Message);
            var validation = inventories.Validate(reloaded.All);
            Assert.True(validation.IsSuccess, validation.Message);
        }
    }
}
