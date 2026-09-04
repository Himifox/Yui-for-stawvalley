using StardewModdingAPI;
using StardewValley;

namespace YuiToIssho;

internal sealed class CompanionSocialMenuCoordinator
{
    private readonly CompanionProjectionCoordinator projection;
    private readonly CompanionSpeechCoordinator speech;
    private readonly Func<LifecycleState> getLifecycle;

    public CompanionSocialMenuCoordinator(CompanionProjectionCoordinator projection, CompanionSpeechCoordinator speech, Func<LifecycleState> getLifecycle)
    {
        this.projection = projection;
        this.speech = speech;
        this.getLifecycle = getLifecycle;
    }

    public bool TryOpenDialogue(CompanionIdentity identity)
    {
        if (!Context.IsWorldReady || this.getLifecycle() != LifecycleState.SaveReady || Game1.activeClickableMenu is not null)
            return false;
        CompanionMenuIdentitySnapshot? snapshot = this.GetIdentityView().FirstOrDefault(candidate => candidate.Identity == identity);
        if (snapshot is null)
            return false;
        Game1.drawObjectDialogue($"{snapshot.DisplayName}: {this.speech.BuildInteractionLine(snapshot)}");
        return true;
    }

    public bool TryGetIdentity(CompanionIdentity identity, out CompanionMenuIdentitySnapshot snapshot)
    {
        snapshot = this.GetIdentityView().FirstOrDefault(candidate => candidate.Identity == identity)!;
        return snapshot is not null;
    }

    internal IReadOnlyList<CompanionMenuIdentitySnapshot> GetIdentityView() =>
        Context.IsWorldReady ? this.projection.BuildMenuIdentityView(Game1.player.UniqueMultiplayerID) : Array.Empty<CompanionMenuIdentitySnapshot>();

}
