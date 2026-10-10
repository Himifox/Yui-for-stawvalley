using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace YuiToIssho;

/// <summary>Attributes delayed drops to the exact vanilla tree update that created them.</summary>
internal static class ChoppingTreeDropCapture
{
    private static readonly Dictionary<Tree, Action<IReadOnlyList<Debris>>> tracked = new(ReferenceEqualityComparer.Instance);

    public static void Apply(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(Tree), nameof(Tree.tickUpdate), new[] { typeof(GameTime) }),
        prefix: new HarmonyMethod(typeof(ChoppingTreeDropCapture), nameof(BeforeUpdate)),
        finalizer: new HarmonyMethod(typeof(ChoppingTreeDropCapture), nameof(AfterUpdate)));

    public static void Track(Tree tree, Action<IReadOnlyList<Debris>> receive) => tracked[tree] = receive;
    public static void Release(Tree tree) => tracked.Remove(tree);

    private static void BeforeUpdate(Tree __instance, out CaptureState? __state)
    {
        __state = null;
        if (!__instance.falling.Value || !tracked.TryGetValue(__instance, out var receive)
            || __instance.Location is not GameLocation location)
            return;
        __state = new CaptureState(location, WorldDebrisCapture.Snapshot(location), receive);
    }

    private static void AfterUpdate(Tree __instance, CaptureState? __state)
    {
        if (__state is not CaptureState capture
            || !tracked.TryGetValue(__instance, out var receive)
            || !ReferenceEquals(receive, capture.Receive))
            return;
        Debris[] additions = capture.Location.debris
            .Where(drop => !capture.Before.Contains(drop) && WorldDebrisCapture.IsItemDrop(drop)).ToArray();
        if (additions.Length > 0)
            receive(additions);
    }

    private sealed record CaptureState(GameLocation Location, HashSet<Debris> Before, Action<IReadOnlyList<Debris>> Receive);
}
