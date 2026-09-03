using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace YuiToIssho;

internal static class BodyReplicationModes
{
    public const string NativeNpc = "NativeNpc";
    public const string SnapshotProjection = "SnapshotProjection";
}

internal sealed class CompanionNetworkBodyResolver
{
    private readonly Dictionary<CompanionIdentity, ResolvedNetworkBody> currentLocationBodies = new();
    private string locationKey = string.Empty;

    public string CurrentMode => this.currentLocationBodies.Count > 0 ? BodyReplicationModes.NativeNpc : BodyReplicationModes.SnapshotProjection;

    public void Update()
    {
        if (!Context.IsWorldReady || Game1.currentLocation is null)
        {
            this.Clear();
            return;
        }

        GameLocation location = Game1.currentLocation;
        string nextLocation = location.NameOrUniqueName;
        var next = new Dictionary<CompanionIdentity, ResolvedNetworkBody>();
        foreach (NPC body in location.characters)
        {
            if (!CompanionBodyBinder.TryReadIdentity(body, out CompanionIdentity identity, out ulong generation))
                continue;
            if (!next.TryGetValue(identity, out ResolvedNetworkBody existing) || generation > existing.Generation)
                next[identity] = new ResolvedNetworkBody(body, generation);
        }

        this.currentLocationBodies.Clear();
        foreach ((CompanionIdentity identity, ResolvedNetworkBody body) in next)
            this.currentLocationBodies[identity] = body;
        this.locationKey = nextLocation;
    }

    public bool TryGetBody(CompanionIdentity identity, ulong expectedGeneration, string expectedLocationKey, out NPC body)
    {
        body = null!;
        if (expectedGeneration == 0
            || !string.Equals(this.locationKey, expectedLocationKey, StringComparison.Ordinal)
            || !this.currentLocationBodies.TryGetValue(identity, out ResolvedNetworkBody resolved)
            || resolved.Generation != expectedGeneration
            || !ReferenceEquals(resolved.Body.currentLocation, Game1.currentLocation))
            return false;
        body = resolved.Body;
        return true;
    }

    public bool TryFindInteractionTarget(GameLocation location, Vector2 absolutePixels, Point grabTile, Point playerTile, bool mouseTarget, out CompanionIdentity identity, out Point targetTile)
    {
        identity = default;
        targetTile = default;
        float bestDistance = float.MaxValue;
        foreach ((CompanionIdentity candidate, ResolvedNetworkBody resolved) in this.currentLocationBodies)
        {
            NPC body = resolved.Body;
            Point tile = body.TilePoint;
            if (!ReferenceEquals(body.currentLocation, location) || Math.Max(Math.Abs(tile.X - playerTile.X), Math.Abs(tile.Y - playerTile.Y)) > 1)
                continue;
            Rectangle visualBounds = new((int)body.Position.X - 16, (int)body.Position.Y - 72, Game1.tileSize + 32, Game1.tileSize + 96);
            bool hit = mouseTarget ? visualBounds.Contains(absolutePixels.ToPoint()) : tile == grabTile;
            if (!hit)
                continue;
            float distance = Vector2.DistanceSquared(body.Position, absolutePixels);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            identity = candidate;
            targetTile = tile;
        }
        return identity.OwnerId != 0;
    }

    public void Clear()
    {
        this.currentLocationBodies.Clear();
        this.locationKey = string.Empty;
    }

    private readonly record struct ResolvedNetworkBody(NPC Body, ulong Generation);
}
