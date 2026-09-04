using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;

namespace YuiToIssho;

internal static class SeatInteractionPatch
{
    private static Func<long, bool>? isSyntheticOccupant;
    private static Func<MapSeat, NPC, bool>? isSeatedCompanion;

    public static void Apply(Harmony harmony, Func<long, bool> syntheticOccupant, Func<MapSeat, NPC, bool> seatedCompanion)
    {
        isSyntheticOccupant = syntheticOccupant;
        isSeatedCompanion = seatedCompanion;
        harmony.Patch(
            AccessTools.Method(typeof(FarmerTeam), nameof(FarmerTeam.playerIsOnline), new[] { typeof(long) }),
            postfix: new HarmonyMethod(typeof(SeatInteractionPatch), nameof(AfterPlayerIsOnline)));
        harmony.Patch(
            AccessTools.Method(typeof(MapSeat), nameof(MapSeat.IsBlocked)),
            prefix: new HarmonyMethod(typeof(SeatInteractionPatch), nameof(BeforeMapSeatBlocked)));
    }

    private static void AfterPlayerIsOnline(long __0, ref bool __result)
    {
        try
        {
            if (!__result && __0 < 0)
                __result = isSyntheticOccupant?.Invoke(__0) == true;
        }
        catch
        {
            // Seat bookkeeping must never interfere with vanilla online-player checks.
        }
    }

    private static bool BeforeMapSeatBlocked(MapSeat __instance, GameLocation location, ref bool __result)
    {
        try
        {
            Func<MapSeat, NPC, bool>? predicate = isSeatedCompanion;
            if (predicate is null)
                return true;

            IReadOnlyList<NPC> characters = Game1.CurrentEvent is not null
                ? Game1.CurrentEvent.actors
                : location.characters.ToList();
            if (!characters.Any(character => predicate(__instance, character)))
                return true;

            Rectangle seatBounds = __instance.GetSeatBounds();
            seatBounds.X *= Game1.tileSize;
            seatBounds.Y *= Game1.tileSize;
            seatBounds.Width *= Game1.tileSize;
            seatBounds.Height *= Game1.tileSize;
            Rectangle approachBounds = seatBounds;
            switch (__instance.direction.Value)
            {
                case 0: approachBounds.Y -= Game1.tileSize / 2; approachBounds.Height += Game1.tileSize / 2; break;
                case 1: approachBounds.Width += Game1.tileSize / 2; break;
                case 2: approachBounds.Height += Game1.tileSize / 2; break;
                case 3: approachBounds.X -= Game1.tileSize / 2; approachBounds.Width += Game1.tileSize / 2; break;
            }

            foreach (NPC character in characters)
            {
                if (predicate(__instance, character))
                    continue;
                Rectangle bodyBounds = character.GetBoundingBox();
                if (bodyBounds.Intersects(seatBounds)
                    || !character.isMovingOnPathFindPath.Value && bodyBounds.Intersects(approachBounds))
                {
                    __result = true;
                    return false;
                }
            }
            __result = false;
            return false;
        }
        catch
        {
            return true;
        }
    }
}
