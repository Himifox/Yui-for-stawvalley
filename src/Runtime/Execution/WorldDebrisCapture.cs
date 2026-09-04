using StardewValley;

namespace YuiToIssho;

internal readonly record struct WorldDebrisRouteResult(InventoryActionResult Result, int StackCount)
{
    public static WorldDebrisRouteResult Success(int stackCount) => new(
        InventoryActionResult.Success("WORLD-OUTPUTS-ROUTED", $"Routed {stackCount} world drop stack(s) to Yui responsibility."),
        stackCount);
}

internal sealed class WorldDebrisCapture
{
    private readonly Dictionary<GameLocation, HashSet<Debris>> snapshots;

    private WorldDebrisCapture(IEnumerable<GameLocation?> locations)
    {
        this.snapshots = new Dictionary<GameLocation, HashSet<Debris>>(ReferenceEqualityComparer.Instance);
        foreach (GameLocation? location in locations)
        {
            if (location is not null && !this.snapshots.ContainsKey(location))
                this.snapshots.Add(location, Snapshot(location));
        }
    }

    public static WorldDebrisCapture Begin(params GameLocation?[] locations) => new(locations);

    public WorldDebrisRouteResult RouteNewLocked(CompanionIdentity identity, CompanionInventoryStore inventories)
    {
        int routed = 0;
        foreach ((GameLocation location, HashSet<Debris> before) in this.snapshots)
        {
            Debris[] additions = location.debris
                .Where(debris => !before.Contains(debris) && IsItemDrop(debris))
                .ToArray();
            WorldDebrisRouteResult result = RouteSpecificLocked(identity, inventories, location, additions);
            routed += result.StackCount;
            if (!result.Result.IsSuccess)
                return new WorldDebrisRouteResult(result.Result, routed);
        }

        return WorldDebrisRouteResult.Success(routed);
    }

    public static HashSet<Debris> Snapshot(GameLocation location) =>
        new(location.debris, ReferenceEqualityComparer.Instance);

    public static bool IsItemDrop(Debris debris) =>
        debris.item is not null
        || (debris.Chunks.Count > 0
            && !string.IsNullOrWhiteSpace(debris.itemId.Value)
            && debris.debrisType.Value is Debris.DebrisType.ARCHAEOLOGY or Debris.DebrisType.OBJECT or Debris.DebrisType.RESOURCE);

    public static WorldDebrisRouteResult RouteSpecificLocked(
        CompanionIdentity identity,
        CompanionInventoryStore inventories,
        GameLocation location,
        IEnumerable<Debris> drops)
    {
        int routed = 0;
        HashSet<Debris> visited = new(ReferenceEqualityComparer.Instance);
        foreach (Debris? debris in drops)
        {
            if (debris is null
                || !visited.Add(debris)
                || !location.debris.Contains(debris)
                || !TryDetachOutput(debris, out Item output, out bool detachedExactItem))
                continue;

            if (!location.debris.Remove(debris))
            {
                if (detachedExactItem)
                    debris.item = output;
                continue;
            }

            InventoryActionResult result = inventories.StoreGeneratedOutput(identity, output);
            bool retained = result.IsSuccess || result.Code == "OUTPUT-IN-RECOVERY";
            if (retained)
            {
                routed++;
                if (!result.IsSuccess)
                    return new WorldDebrisRouteResult(result, routed);
                continue;
            }

            if (detachedExactItem)
                debris.item = output;
            if (!location.debris.Contains(debris))
                location.debris.Add(debris);
            return new WorldDebrisRouteResult(result, routed);
        }

        return WorldDebrisRouteResult.Success(routed);
    }

    private static bool TryDetachOutput(Debris debris, out Item output, out bool detachedExactItem)
    {
        if (debris.item is Item exactItem)
        {
            debris.item = null;
            output = exactItem;
            detachedExactItem = true;
            if (exactItem.Stack > 0)
                return true;
            debris.item = exactItem;
            return false;
        }

        detachedExactItem = false;
        output = null!;
        if (!IsItemDrop(debris))
            return false;

        try
        {
            output = ItemRegistry.Create(debris.itemId.Value, debris.Chunks.Count, debris.itemQuality);
            return output.Stack > 0;
        }
        catch
        {
            return false;
        }
    }
}
