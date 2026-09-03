using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace YuiToIssho;

internal static class CompanionBodyDrawPatch
{
    private static Func<NPC, SpriteBatch, float, bool>? renderer;

    public static void Apply(Harmony harmony, Func<NPC, SpriteBatch, float, bool> bodyRenderer)
    {
        renderer = bodyRenderer;
        harmony.Patch(
            AccessTools.Method(typeof(NPC), nameof(NPC.draw), new[] { typeof(SpriteBatch), typeof(float) }),
            prefix: new HarmonyMethod(typeof(CompanionBodyDrawPatch), nameof(BeforeDraw)));
    }

    private static bool BeforeDraw(NPC __instance, SpriteBatch b, float alpha)
    {
        if (!CompanionBodyBinder.TryReadIdentity(__instance, out _, out _))
            return true;
        try
        {
            return renderer?.Invoke(__instance, b, alpha) != true;
        }
        catch
        {
            return true;
        }
    }
}
