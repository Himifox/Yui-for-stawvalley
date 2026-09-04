using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;

namespace YuiToIssho;

internal static class CompanionPathing
{
    public static bool IsStandable(NPC body, GameLocation location, Vector2 tile)
    {
        if (!location.isTileOnMap(tile))
            return false;

        Rectangle projectedBounds = body.GetBoundingBox();
        projectedBounds.Offset(
            (int)(tile.X * Game1.tileSize - body.Position.X),
            (int)(tile.Y * Game1.tileSize - body.Position.Y));
        return !location.isCollidingPosition(
            projectedBounds,
            Game1.viewport,
            isFarmer: false,
            damagesFarmer: 0,
            glider: false,
            character: body,
            pathfinding: false,
            projectile: false,
            ignoreCharacterRequirement: false,
            skipCollisionEffects: true);
    }

    public static bool CanReach(NPC body, GameLocation location, Vector2 destination, int facing, int pathSearchLimit)
    {
        if (!IsStandable(body, location, destination))
            return false;
        if (body.TilePoint == destination.ToPoint())
            return true;

        var probe = new PathFindController(body, location, destination.ToPoint(), facing, null, pathSearchLimit);
        return probe.pathToEndPoint is { Count: > 0 };
    }
}
