using Microsoft.Xna.Framework;
using StardewValley;

namespace YuiToIssho;

internal static class GeneratedOutputSettlement
{
    public static InventoryActionResult Route(
        IEnumerable<Item> outputs,
        Func<Item, InventoryActionResult> store,
        Action<Item> restore,
        Func<Item, bool>? isRetained = null)
    {
        InventoryActionResult? failure = null;
        foreach (Item output in outputs)
        {
            InventoryActionResult result;
            try { result = store(output); }
            catch (Exception ex)
            {
                result = InventoryActionResult.Failure("OUTPUT-ROUTING-FAILED", $"Output routing failed: {ex.GetType().Name}.");
            }
            if (result.IsSuccess)
                continue;
            failure ??= result;
            bool retained = result.Code == "OUTPUT-IN-RECOVERY";
            if (!retained && isRetained is not null)
            {
                try { retained = isRetained(output); }
                catch { /* An unavailable inventory provides no evidence of a completed transfer. */ }
            }
            if (!retained)
                restore(output);
        }
        return failure ?? InventoryActionResult.Success("OUTPUTS-ROUTED", "All extracted outputs retained.");
    }

    public static InventoryActionResult RouteLocked(CompanionIdentity identity, CompanionInventoryStore inventories,
        GameLocation location, Vector2 tile, IReadOnlyList<Item> outputs) => Route(
            outputs,
            output => inventories.StoreGeneratedOutput(identity, output),
            output => location.debris.Add(new Debris(output, (tile + new Vector2(0.5f)) * Game1.tileSize)),
            output => inventories.ContainsGeneratedOutputExact(identity, output));
}
