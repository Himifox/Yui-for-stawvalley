using HarmonyLib;
using StardewModdingAPI;

namespace YuiToIssho;

public sealed class ModEntry : Mod
{
    private Bootstrap? bootstrap;

    public override void Entry(IModHelper helper)
    {
        this.bootstrap = new Bootstrap(helper, this.Monitor);
        var harmony = new Harmony(this.ModManifest.UniqueID);
        CompanionBodyDrawPatch.Apply(harmony, this.bootstrap.TryRenderNetworkBody);
        FarmerDamagePatch.Apply(harmony, this.bootstrap.ObserveOwnerDamage);
        this.bootstrap.Attach();
    }
}
