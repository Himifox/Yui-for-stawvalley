using HarmonyLib;
using StardewValley;
using StardewValley.Monsters;

namespace YuiToIssho;

internal static class FarmerDamagePatch
{
    private static Action<Farmer, Monster>? observer;

    public static void Apply(Harmony harmony, Action<Farmer, Monster> damageObserver)
    {
        observer = damageObserver;
        harmony.Patch(
            AccessTools.Method(typeof(Farmer), nameof(Farmer.takeDamage), new[] { typeof(int), typeof(bool), typeof(Monster) }),
            prefix: new HarmonyMethod(typeof(FarmerDamagePatch), nameof(BeforeDamage)),
            postfix: new HarmonyMethod(typeof(FarmerDamagePatch), nameof(AfterDamage)));
    }

    private static void BeforeDamage(Farmer __instance, out int __state) => __state = __instance.health;

    private static void AfterDamage(Farmer __instance, Monster? __2, int __state)
    {
        if (__2 is null || __instance.health >= __state)
            return;
        try
        {
            observer?.Invoke(__instance, __2);
        }
        catch
        {
            // Damage observation must never interfere with vanilla's completed player-damage path.
        }
    }
}
