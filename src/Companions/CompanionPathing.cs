using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;

namespace YuiToIssho;

internal static class CompanionPathing
{
    public static PathFindController CreateController(
        NPC body,
        GameLocation location,
        Point destination,
        int facing,
        int pathSearchLimit)
    {
        var controller = new PathFindController(body, location, destination, facing, null, pathSearchLimit)
        {
            // The vanilla controller otherwise uses its destructive route mode for
            // ad-hoc NPC movement. Companions may open gates and warp, but must never
            // alter terrain or objects simply to reach their owner or a task target.
            nonDestructivePathing = true,
        };
        return controller;
    }

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

        PathFindController probe = CreateController(body, location, destination.ToPoint(), facing, pathSearchLimit);
        return probe.pathToEndPoint is { Count: > 0 };
    }
}
