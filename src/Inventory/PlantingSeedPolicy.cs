using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.Inventories;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal static class PlantingConstants
{
    public const int SeedPolicyVersion = 1;
    public const string WorkKind = "Plant";
    public const int MaximumOptions = 16;
    public const int MaximumQueryLength = 64;
    public const int MaximumCount = 625;
    public static readonly TimeSpan OptionLifetime = TimeSpan.FromSeconds(60);
}

internal readonly record struct PlantSeedPolicyResult(
    bool IsAllowed,
    string Code,
    string Message,
    CropData? CropData,
    string CropDisplayName)
{
    public static PlantSeedPolicyResult Allowed(CropData data, string cropDisplayName) =>
        new(true, "SEED-ALLOWED", "The real seed instance is allowed by PlantSeedPolicy v1.", data, cropDisplayName);

    public static PlantSeedPolicyResult Rejected(string message) =>
        new(false, "SEED-POLICY-NOT-ALLOWED", message, null, string.Empty);
}

internal static class PlantSeedPolicy
{
    public static PlantSeedPolicyResult Evaluate(Item item, GameLocation location)
    {
        if (item is not SObject seed || seed.GetType() != typeof(SObject) || seed.Stack <= 0 || seed.maximumStackSize() <= 1)
            return PlantSeedPolicyResult.Rejected("Planting requires a positive stack of standard object instances.");
        if (seed.Category != SObject.SeedsCategory)
            return PlantSeedPolicyResult.Rejected("The item is not in Stardew Valley's standard seed category.");
        if (!Crop.TryGetData(seed.ItemId, out CropData? cropData) || cropData is null)
            return PlantSeedPolicyResult.Rejected("The original seed ID does not resolve directly to public CropData.");
        if (!string.Equals(Crop.ResolveSeedId(seed.ItemId, location), seed.ItemId, StringComparison.Ordinal))
            return PlantSeedPolicyResult.Rejected("The seed resolves contextually or randomly and is outside deterministic policy v1.");
        if (cropData.IsRaised)
            return PlantSeedPolicyResult.Rejected("Raised or trellis crops are outside automatic area planting policy v1.");
        if (cropData.IsPaddyCrop)
            return PlantSeedPolicyResult.Rejected("Paddy crops with water-neighbor behavior are outside planting policy v1.");

        return PlantSeedPolicyResult.Allowed(cropData, GetCropDisplayName(cropData));
    }

    private static string GetCropDisplayName(CropData cropData)
    {
        try
        {
            return Bounded(ItemRegistry.GetData(cropData.HarvestItemId).DisplayName, 96);
        }
        catch
        {
            return Bounded(cropData.HarvestItemId, 96);
        }
    }

    private static string Bounded(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Length <= maximum ? value : value[..maximum];
}

internal readonly record struct PlantingScope(
    string LocationKey,
    int AnchorX,
    int AnchorY,
    int EndX,
    int EndY,
    string Shape,
    int Radius)
{
    public bool Contains(int x, int y) => this.Shape == WorkScopeShapes.Rectangle
        ? x >= Math.Min(this.AnchorX, this.EndX)
            && x <= Math.Max(this.AnchorX, this.EndX)
            && y >= Math.Min(this.AnchorY, this.EndY)
            && y <= Math.Max(this.AnchorY, this.EndY)
        : this.Shape == WorkScopeShapes.SingleTarget
            ? x == this.AnchorX && y == this.AnchorY
            : WorkScopeContracts.ContainsTile(this.AnchorX, this.AnchorY, this.Radius, x, y);

    public bool IsValid() => this.LocationKey.Length is > 0 and <= 256
        && (this.Shape == WorkScopeShapes.SingleTarget
            ? this.AnchorX == this.EndX && this.AnchorY == this.EndY && this.Radius == 0
            : this.Shape == WorkScopeShapes.Radius
            ? this.Radius is >= WorkScopeContracts.MinimumRadius and <= WorkScopeContracts.MaximumRadius
            : this.Shape == WorkScopeShapes.Rectangle
                && WorkScopeContracts.IsRectangleWithinLimit(this.AnchorX, this.AnchorY, this.EndX, this.EndY));
}

internal readonly record struct PlantSeedOption(
    string SeedOptionId,
    string SeedDisplayName,
    string CropDisplayName,
    int AvailableCount,
    bool PlantableHere,
    string ReasonCode,
    int ExpiresInSeconds);

internal readonly record struct PlantSeedOptionsResult(
    bool IsSuccess,
    string Code,
    string Message,
    IReadOnlyList<PlantSeedOption> Options)
{
    public static PlantSeedOptionsResult Success(IReadOnlyList<PlantSeedOption> options) =>
        new(true, "SEED-OPTIONS", $"Found {options.Count} bounded seed option(s).", options);

    public static PlantSeedOptionsResult Failure(string code, string message) =>
        new(false, code, message, Array.Empty<PlantSeedOption>());
}

internal readonly record struct PlantSlotPreview(string StableId, int TileX, int TileY, int OwnerDistance);

internal sealed record PlantingSeedSource(
    Item Item,
    int ExpectedStack,
    IList<Item> Container,
    int SourceSlot,
    string SourceKind,
    string StorageId,
    CraftChestAccess? Chest);

internal readonly record struct PlantingPreviewResult(
    bool IsSuccess,
    string Code,
    string Message,
    string SeedOptionId,
    string SeedDisplayName,
    string CropDisplayName,
    int RequestedCount,
    int AvailableSeedCount,
    int MatchingSlotCount,
    IReadOnlyList<PlantSlotPreview> NearestSlots)
{
    public static PlantingPreviewResult Failure(string code, string message) =>
        new(false, code, message, string.Empty, string.Empty, string.Empty, 0, 0, 0, Array.Empty<PlantSlotPreview>());
}

internal sealed class PlantingPreviewService
{
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionStorageCoordinator storage;
    private readonly CompanionBodyBinder bodies;
    private readonly Dictionary<string, CachedSeedOption> options = new(StringComparer.Ordinal);
    private string sessionToken = Guid.NewGuid().ToString("N");

    public PlantingPreviewService(CompanionInventoryStore inventories, CompanionStorageCoordinator storage, CompanionBodyBinder bodies)
    {
        this.inventories = inventories;
        this.storage = storage;
        this.bodies = bodies;
    }

    public void ResetSession()
    {
        this.options.Clear();
        this.sessionToken = Guid.NewGuid().ToString("N");
    }

    public PlantSeedOptionsResult GetOptions(CompanionIdentity identity, Farmer owner, string? query)
    {
        if (identity.OwnerId != owner.UniqueMultiplayerID || owner.currentLocation is null)
            return PlantSeedOptionsResult.Failure("PLANT-OWNER-UNAVAILABLE", "The exact online Owner and current location are required.");
        query = query?.Trim() ?? string.Empty;
        if (query.Length > PlantingConstants.MaximumQueryLength)
            return PlantSeedOptionsResult.Failure("SEED-QUERY-TOO-LONG", $"Seed query is limited to {PlantingConstants.MaximumQueryLength} characters.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        this.Prune(now);
        foreach (string key in this.options.Where(pair => pair.Value.Identity == identity).Select(pair => pair.Key).ToArray())
            this.options.Remove(key);

        GameLocation location = owner.currentLocation;
        List<SeedSupply> supplies = this.CaptureSupplies(identity, location);
        PlantSeedOption[] results = supplies
            .Where(supply => query.Length == 0
                || supply.SeedDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || supply.CropDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(supply => supply.CropDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(supply => supply.SeedDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(supply => supply.QualifiedItemId, StringComparer.Ordinal)
            .Take(PlantingConstants.MaximumOptions)
            .Select(supply => this.CacheOption(identity, supply, location, now))
            .ToArray();
        return results.Length == 0
            ? PlantSeedOptionsResult.Failure("SEED-OPTIONS-EMPTY", "No real Yui-bag or current-location authorized-chest seed passes policy v1 and the query.")
            : PlantSeedOptionsResult.Success(results);
    }

    public PlantingPreviewResult Preview(CompanionIdentity identity, Farmer owner, string seedOptionId, int count, PlantingScope scope)
    {
        if (count is < 1 or > PlantingConstants.MaximumCount)
            return PlantingPreviewResult.Failure("PLANT-COUNT-INVALID", $"Plant count must be between 1 and {PlantingConstants.MaximumCount}.");
        if (!scope.IsValid())
            return PlantingPreviewResult.Failure("PLANT-SCOPE-INVALID", "Plant scope must be a radius 1..24 or a rectangle no larger than 25 x 25.");
        if (owner.currentLocation is not GameLocation location || identity.OwnerId != owner.UniqueMultiplayerID || scope.LocationKey != location.NameOrUniqueName)
            return PlantingPreviewResult.Failure("PLANT-LOCATION-MISMATCH", "The scope must remain in the exact online Owner location.");
        if (!this.TryResolve(identity, seedOptionId, out CachedSeedOption option, out string code, out string message))
            return PlantingPreviewResult.Failure(code, message);

        SeedSupply[] currentSupplies = this.CaptureSupplies(identity, location)
            .Where(supply => supply.QualifiedItemId == option.QualifiedItemId)
            .Take(1)
            .ToArray();
        if (currentSupplies.Length == 0)
            return PlantingPreviewResult.Failure("SEED-SUPPLY-INSUFFICIENT", "The selected real seed supply is no longer available.");
        SeedSupply current = currentSupplies[0];
        if (current.AvailableCount < count)
            return PlantingPreviewResult.Failure("SEED-SUPPLY-INSUFFICIENT", $"Only {current.AvailableCount} matching real seed(s) remain; {count} are required before start.");

        PlantSeedPolicyResult policy = PlantSeedPolicy.Evaluate(current.Example, location);
        if (!policy.IsAllowed || policy.CropData is null)
            return PlantingPreviewResult.Failure(policy.Code, policy.Message);
        PlantSlotPreview[] slots = CaptureSlots(owner, location, scope, current.Example.ItemId, policy.CropData);
        bool sufficient = slots.Length >= count;
        return new PlantingPreviewResult(
            sufficient,
            sufficient ? "PLANT-PREVIEW-READY" : "PLANT-SCOPE-INSUFFICIENT",
            sufficient
                ? $"Preview proves {current.AvailableCount} seed(s) and {slots.Length} currently eligible slot(s) for requested count {count}; nothing changed."
                : $"Only {slots.Length} currently eligible slot(s) exist in the full scope; {count} are required and nothing changed.",
            seedOptionId,
            current.SeedDisplayName,
            current.CropDisplayName,
            count,
            current.AvailableCount,
            slots.Length,
            slots.Take(64).ToArray());
    }

    public bool TryResolveSelection(CompanionIdentity identity, string seedOptionId, out string qualifiedItemId, out string code, out string message)
    {
        if (!this.TryResolve(identity, seedOptionId, out CachedSeedOption option, out code, out message))
        {
            qualifiedItemId = string.Empty;
            return false;
        }
        qualifiedItemId = option.QualifiedItemId;
        return true;
    }

    public IReadOnlyList<PlantingSeedSource> CaptureSources(CompanionIdentity identity, GameLocation location, string qualifiedItemId)
    {
        var sources = new List<PlantingSeedSource>();
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        Inventory bag = this.inventories.Get(identity);
        for (int slot = 0; slot < bag.Count; slot++)
        {
            if (bag[slot] is Item item && IsAvailableSource(item, location, qualifiedItemId) && seen.Add(item))
                sources.Add(new PlantingSeedSource(item, item.Stack, bag, slot, PlantingSourceKinds.Bag, CompanionInventoryStore.GetNamespace(identity), null));
        }
        if (this.bodies.TryGetBody(identity, out NPC body) && ReferenceEquals(body.currentLocation, location))
        {
            foreach (CraftChestAccess access in this.storage.GetCraftingChests(identity, body))
            {
                for (int slot = 0; slot < access.Chest.Items.Count; slot++)
                {
                    if (access.Chest.Items[slot] is Item item && IsAvailableSource(item, location, qualifiedItemId) && seen.Add(item))
                        sources.Add(new PlantingSeedSource(item, item.Stack, access.Chest.Items, slot, PlantingSourceKinds.AuthorizedChest, access.Authorization.ChestToken, access));
                }
            }
        }
        return sources;
    }

    public PlantSlotPreview[] CaptureEligibleSlots(Farmer owner, GameLocation location, PlantingScope scope, Item seed)
    {
        PlantSeedPolicyResult policy = PlantSeedPolicy.Evaluate(seed, location);
        return policy.IsAllowed && policy.CropData is not null
            ? CaptureSlots(owner, location, scope, seed.ItemId, policy.CropData)
            : Array.Empty<PlantSlotPreview>();
    }

    private PlantSeedOption CacheOption(CompanionIdentity identity, SeedSupply supply, GameLocation location, DateTimeOffset now)
    {
        string optionId = Guid.NewGuid().ToString("N");
        DateTimeOffset expiresAt = now + PlantingConstants.OptionLifetime;
        bool plantable = HasAnyPlantableSlot(location, supply.Example.ItemId, supply.CropData);
        this.options[optionId] = new CachedSeedOption(
            identity,
            this.sessionToken,
            supply.QualifiedItemId,
            PlantingConstants.SeedPolicyVersion,
            expiresAt);
        return new PlantSeedOption(
            optionId,
            supply.SeedDisplayName,
            supply.CropDisplayName,
            supply.AvailableCount,
            plantable,
            plantable ? "SEED-PLANTABLE-HERE" : "SEED-NOT-PLANTABLE-HERE",
            (int)PlantingConstants.OptionLifetime.TotalSeconds);
    }

    private bool TryResolve(CompanionIdentity identity, string optionId, out CachedSeedOption option, out string code, out string message)
    {
        this.Prune(DateTimeOffset.UtcNow);
        option = default;
        if (optionId.Length != 32 || !this.options.TryGetValue(optionId, out option)
            || option.Identity != identity || option.SessionToken != this.sessionToken
            || option.PolicyVersion != PlantingConstants.SeedPolicyVersion)
        {
            code = "SEED-SELECTION-EXPIRED";
            message = "SeedOptionId is expired, unknown, or belongs to another Owner/session.";
            return false;
        }
        code = string.Empty;
        message = string.Empty;
        return true;
    }

    private List<SeedSupply> CaptureSupplies(CompanionIdentity identity, GameLocation location)
    {
        IEnumerable<Item> items = this.CaptureAllAvailableSources(identity, location).Select(source => source.Item);
        return items
            .Select(item => (Item: item, Policy: PlantSeedPolicy.Evaluate(item, location)))
            .Where(candidate => candidate.Policy.IsAllowed && candidate.Policy.CropData is not null)
            .GroupBy(candidate => candidate.Item.QualifiedItemId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new SeedSupply(
                    group.Key,
                    first.Item,
                    Bound(first.Item.DisplayName, 96),
                    first.Policy.CropDisplayName,
                    (int)Math.Min(group.Sum(candidate => (long)candidate.Item.Stack), int.MaxValue),
                    first.Policy.CropData!);
            })
            .ToList();
    }

    private IReadOnlyList<PlantingSeedSource> CaptureAllAvailableSources(CompanionIdentity identity, GameLocation location)
    {
        var sources = new List<PlantingSeedSource>();
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        Inventory bag = this.inventories.Get(identity);
        for (int slot = 0; slot < bag.Count; slot++)
        {
            if (bag[slot] is Item item && IsAvailableSource(item, location, null) && seen.Add(item))
                sources.Add(new PlantingSeedSource(item, item.Stack, bag, slot, PlantingSourceKinds.Bag, CompanionInventoryStore.GetNamespace(identity), null));
        }
        if (this.bodies.TryGetBody(identity, out NPC body) && ReferenceEquals(body.currentLocation, location))
        {
            foreach (CraftChestAccess access in this.storage.GetCraftingChests(identity, body))
            {
                for (int slot = 0; slot < access.Chest.Items.Count; slot++)
                {
                    if (access.Chest.Items[slot] is Item item && IsAvailableSource(item, location, null) && seen.Add(item))
                        sources.Add(new PlantingSeedSource(item, item.Stack, access.Chest.Items, slot, PlantingSourceKinds.AuthorizedChest, access.Authorization.ChestToken, access));
                }
            }
        }
        return sources;
    }

    private static bool IsAvailableSource(Item item, GameLocation location, string? qualifiedItemId) =>
        item.Stack > 0
        && (qualifiedItemId is null || item.QualifiedItemId == qualifiedItemId)
        && !item.modData.ContainsKey(StorageTags.ResponsibilityId)
        && !item.modData.ContainsKey(StorageTags.ReturnPending)
        && !item.modData.ContainsKey(CompanionInventoryStore.CraftIdTag)
        && !item.modData.ContainsKey(CompanionInventoryStore.PlantingIdTag)
        && PlantSeedPolicy.Evaluate(item, location).IsAllowed;

    private static bool HasAnyPlantableSlot(GameLocation location, string seedItemId, CropData cropData)
    {
        foreach ((Vector2 tile, TerrainFeature feature) in location.terrainFeatures.Pairs)
        {
            if (feature is HoeDirt dirt && IsEligibleSlot(location, tile, dirt, seedItemId, cropData))
                return true;
        }
        return false;
    }

    private static PlantSlotPreview[] CaptureSlots(Farmer owner, GameLocation location, PlantingScope scope, string seedItemId, CropData cropData)
    {
        return location.terrainFeatures.Pairs
            .Where(pair => pair.Value is HoeDirt dirt
                && scope.Contains((int)pair.Key.X, (int)pair.Key.Y)
                && IsEligibleSlot(location, pair.Key, dirt, seedItemId, cropData))
            .Select(pair => new PlantSlotPreview(
                $"{(int)pair.Key.X},{(int)pair.Key.Y}:plant-slot",
                (int)pair.Key.X,
                (int)pair.Key.Y,
                Math.Abs((int)owner.Tile.X - (int)pair.Key.X) + Math.Abs((int)owner.Tile.Y - (int)pair.Key.Y)))
            .OrderBy(slot => slot.OwnerDistance)
            .ThenBy(slot => slot.TileY)
            .ThenBy(slot => slot.TileX)
            .ThenBy(slot => slot.StableId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsEligibleSlot(GameLocation location, Vector2 tile, HoeDirt dirt, string seedItemId, CropData cropData)
    {
        if (dirt.crop is not null || location.Objects.ContainsKey(tile))
            return false;
        if (location.characters.Any(character => character.Tile == tile)
            || location.farmers.Any(farmer => farmer.Tile == tile))
            return false;
        if (location.IsOutdoors && !location.SeedsIgnoreSeasonsHere() && cropData.Seasons.Count > 0 && !cropData.Seasons.Contains(location.GetSeason()))
            return false;
        if (!location.CanPlantSeedsHere(seedItemId, (int)tile.X, (int)tile.Y, false, out _))
            return false;
        return HasOpenApproach(location, tile);
    }

    private static bool HasOpenApproach(GameLocation location, Vector2 tile)
    {
        Vector2[] directions = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        return directions.Select(direction => tile + direction).Any(candidate =>
            location.isTileOnMap(candidate)
            && location.isTileLocationOpen(candidate)
            && location.characters.All(character => character.Tile != candidate)
            && location.farmers.All(farmer => farmer.Tile != candidate));
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (string key in this.options.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            this.options.Remove(key);
    }

    private static string Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Length <= maximum ? value : value[..maximum];

    private readonly record struct CachedSeedOption(
        CompanionIdentity Identity,
        string SessionToken,
        string QualifiedItemId,
        int PolicyVersion,
        DateTimeOffset ExpiresAt);

    private readonly record struct SeedSupply(
        string QualifiedItemId,
        Item Example,
        string SeedDisplayName,
        string CropDisplayName,
        int AvailableCount,
        CropData CropData);
}
