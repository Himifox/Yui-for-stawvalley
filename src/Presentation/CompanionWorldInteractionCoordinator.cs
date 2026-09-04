using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace YuiToIssho;

internal sealed class CompanionWorldInteractionCoordinator
{
    private readonly IModHelper helper;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionProjectionCoordinator projection;
    private readonly CompanionSocialMenuCoordinator socialMenu;
    private readonly CompanionMultiplayerCoordinator multiplayer;
    private readonly Func<LifecycleState> getLifecycle;

    public CompanionWorldInteractionCoordinator(
        IModHelper helper,
        CompanionBodyBinder bodies,
        CompanionProjectionCoordinator projection,
        CompanionSocialMenuCoordinator socialMenu,
        CompanionMultiplayerCoordinator multiplayer,
        Func<LifecycleState> getLifecycle)
    {
        this.helper = helper;
        this.bodies = bodies;
        this.projection = projection;
        this.socialMenu = socialMenu;
        this.multiplayer = multiplayer;
        this.getLifecycle = getLifecycle;
    }

    public void Attach() => this.helper.Events.Input.ButtonPressed += this.OnButtonPressed;

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!e.Button.IsActionButton() || !Context.IsWorldReady || this.getLifecycle() != LifecycleState.SaveReady
            || Game1.activeClickableMenu is not null || !Context.IsPlayerFree || Game1.currentLocation is null)
            return;

        bool mouseTarget = e.Button == SButton.MouseRight;
        Vector2 absolutePixels = e.Cursor.AbsolutePixels;
        Point playerTile = Game1.player.TilePoint;
        Point grabTile = Game1.player.GetGrabTile().ToPoint();
        if (!this.TryFindTarget(mouseTarget, absolutePixels, grabTile, playerTile, out CompanionIdentity identity, out Point targetTile, out NPC? hostBody))
            return;

        this.helper.Input.Suppress(e.Button);
        Game1.player.faceDirection(FacingToward(playerTile, targetTile));
        hostBody?.faceDirection(FacingToward(targetTile, playerTile));
        bool owned = identity.OwnerId == Game1.player.UniqueMultiplayerID;
        if (owned && Game1.player.ActiveObject is StardewValley.Object gift && gift.canBeGivenAsGift() && !gift.questItem.Value)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["playerSlot"] = (Game1.player.CurrentToolIndex + 1).ToString(),
                ["itemId"] = gift.QualifiedItemId,
            };
            NetworkCommandResult result = this.multiplayer.Submit(identity, "gift", fields);
            if (!result.IsSuccess)
                Game1.showRedMessage(result.Message);
            else
                Game1.playSound("give_gift");
            return;
        }

        if (owned && e.Button == SButton.MouseRight)
        {
            NetworkCommandResult result = this.multiplayer.Submit(identity, "hug", new Dictionary<string, string>());
            if (!result.IsSuccess)
                Game1.showRedMessage(result.Message);
            else
                Game1.playSound("dwop");
            return;
        }

        if (owned)
        {
            NetworkCommandResult result = this.multiplayer.Submit(identity, "talk", new Dictionary<string, string>());
            if (!result.IsSuccess)
            {
                Game1.showRedMessage(result.Message);
                return;
            }
        }
        if (this.socialMenu.TryOpenDialogue(identity))
            Game1.playSound("smallSelect");
    }

    private bool TryFindTarget(bool mouseTarget, Vector2 absolutePixels, Point grabTile, Point playerTile, out CompanionIdentity identity, out Point targetTile, out NPC? hostBody)
    {
        identity = default;
        targetTile = default;
        hostBody = null;
        if (!Context.IsMainPlayer)
            return this.projection.TryFindInteractionTarget(Game1.currentLocation!, absolutePixels, grabTile, playerTile, mouseTarget, out identity, out targetTile);

        float bestDistance = float.MaxValue;
        foreach ((CompanionIdentity candidate, NPC body) in this.bodies.BoundBodies)
        {
            if (!ReferenceEquals(body.currentLocation, Game1.currentLocation))
                continue;
            Point tile = body.TilePoint;
            if (Math.Max(Math.Abs(tile.X - playerTile.X), Math.Abs(tile.Y - playerTile.Y)) > 1)
                continue;
            Rectangle visualBounds = body.GetBoundingBox();
            visualBounds.Y -= 72;
            visualBounds.Height += 96;
            visualBounds.Inflate(16, 0);
            bool hit = mouseTarget ? visualBounds.Contains(absolutePixels.ToPoint()) : tile == grabTile;
            if (!hit)
                continue;
            float distance = Vector2.DistanceSquared(body.Position, absolutePixels);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            identity = candidate;
            targetTile = tile;
            hostBody = body;
        }
        return identity.OwnerId != 0;
    }

    private static int FacingToward(Point from, Point to)
    {
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        return Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? 1 : 3) : (dy < 0 ? 0 : 2);
    }
}
