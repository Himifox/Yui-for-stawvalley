using Microsoft.Xna.Framework;
using StardewValley;

namespace YuiToIssho;

internal readonly record struct BondActionResult(bool IsSuccess, string Code, string Message)
{
    public static BondActionResult Success(string code, string message) => new(true, code, message);

    public static BondActionResult Failure(string code, string message) => new(false, code, message);
}

/// <summary>Owns Yui's social state without registering her as a vanilla villager.</summary>
internal sealed class CompanionBondCoordinator
{
    private const int TalkPoints = 20;
    private const int AffectionPoints = 10;
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly HashSet<CompanionIdentity> pendingGifts = new();

    public CompanionBondCoordinator(
        CompanionRegistry registry,
        CompanionBodyBinder bodies,
        CompanionInventoryStore inventories,
        CompanionAppearanceCoordinator appearance)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.appearance = appearance;
    }

    public BondActionResult Talk(CompanionIdentity identity, Farmer owner)
    {
        if (!this.TryGetNearby(identity, owner, out CompanionRecord record, out NPC body, out BondActionResult failure))
            return failure;

        int today = Today;
        bool firstToday = record.Bond.LastTalkedDay != today;
        if (firstToday)
        {
            record.Bond.LastTalkedDay = today;
            AddPoints(record.Bond, TalkPoints);
        }
        body.faceDirection(FacingToward(body.TilePoint, owner.TilePoint));
        return BondActionResult.Success(
            firstToday ? "TALK-BOND-GAINED" : "TALKED",
            firstToday ? $"Yui enjoyed talking with you. Bond +{TalkPoints}." : "Yui is listening.");
    }

    public BondActionResult Hug(CompanionIdentity identity, Farmer owner)
    {
        if (!this.TryGetNearby(identity, owner, out CompanionRecord record, out NPC body, out BondActionResult failure))
            return failure;

        int today = Today;
        bool firstToday = record.Bond.LastAffectionDay != today;
        if (firstToday)
        {
            record.Bond.LastAffectionDay = today;
            AddPoints(record.Bond, AffectionPoints);
        }
        int facing = FacingToward(body.TilePoint, owner.TilePoint);
        body.faceDirection(facing);
        if (string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            this.appearance.SetPhase(identity, $"affection:{today}", AppearanceActionKinds.Petting, "Commit", null, facing, 36);
        Celebrate(body);
        return BondActionResult.Success(
            firstToday ? "AFFECTION-BOND-GAINED" : "AFFECTION-SHARED",
            firstToday ? $"You shared a warm moment with Yui. Bond +{AffectionPoints}." : "Yui happily stays close.");
    }

    public void Gift(CompanionIdentity identity, Farmer owner, int playerSlot, string expectedItemId, Action<BondActionResult> completed)
    {
        if (!this.TryGetNearby(identity, owner, out CompanionRecord record, out _, out BondActionResult failure))
        {
            completed(failure);
            return;
        }

        RefreshGiftWeek(record.Bond);
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
        {
            completed(BondActionResult.Failure("COMPANION-BUSY", "Wait until Yui finishes her current item transaction before giving a gift."));
            return;
        }
        if (record.Bond.LastGiftDay == Today)
        {
            completed(BondActionResult.Failure("GIFT-DAILY-LIMIT", "Yui has already received a gift today."));
            return;
        }
        if (record.Bond.GiftsThisWeek >= 2)
        {
            completed(BondActionResult.Failure("GIFT-WEEKLY-LIMIT", "Yui has already received two gifts this week."));
            return;
        }
        if (!this.pendingGifts.Add(identity))
        {
            completed(BondActionResult.Failure("GIFT-PENDING", "Yui is still accepting the previous gift."));
            return;
        }

        CompanionGiftPreference preference = new(CompanionGiftTaste.Neutral, 15, "GIFT-NEUTRAL");
        string giftName = expectedItemId;

        this.inventories.RequestTransfer(
            identity,
            transfer: () =>
            {
                if (!this.TryGetNearby(identity, owner, out CompanionRecord current, out _, out BondActionResult proximity))
                    return InventoryActionResult.Failure(proximity.Code, proximity.Message);
                if (!string.IsNullOrWhiteSpace(current.ActiveTransactionId))
                    return InventoryActionResult.Failure("COMPANION-BUSY", "Yui started another item transaction before the gift transfer completed.");
                RefreshGiftWeek(current.Bond);
                if (current.Bond.LastGiftDay == Today || current.Bond.GiftsThisWeek >= 2)
                    return InventoryActionResult.Failure("GIFT-LIMIT-CHANGED", "Yui's gift limit changed before the transfer completed.");
                int index = playerSlot - 1;
                if (index < 0 || index >= owner.Items.Count || owner.Items[index] is not StardewValley.Object offered || offered.QualifiedItemId != expectedItemId)
                    return InventoryActionResult.Failure("GIFT-ITEM-CHANGED", "The exact offered gift changed before Yui's bag lock was acquired.");
                preference = CompanionGiftPreferences.Evaluate(offered);
                giftName = offered.DisplayName;
                return this.inventories.TryGiftOne(identity, owner, playerSlot, expectedItemId);
            },
            completed: result =>
            {
                this.pendingGifts.Remove(identity);
                if (!result.IsSuccess)
                {
                    completed(BondActionResult.Failure(result.Code, result.Message));
                    return;
                }
                if (!this.registry.TryGet(identity, out CompanionRecord current))
                {
                    completed(BondActionResult.Failure("BOND-STATE-MISSING", "The gift is safe in Yui's bag, but her bond state became unavailable."));
                    return;
                }
                RefreshGiftWeek(current.Bond);
                current.Bond.LastGiftDay = Today;
                current.Bond.GiftsThisWeek++;
                AddPoints(current.Bond, preference.BondPoints);
                if (this.bodies.TryGetBody(identity, out NPC body))
                {
                    int facing = FacingToward(body.TilePoint, owner.TilePoint);
                    body.faceDirection(facing);
                    this.appearance.SetPhase(identity, $"gift:{Today}:{current.Bond.GiftsThisWeek}", AppearanceActionKinds.Handoff, "Commit", null, facing, 36);
                    Celebrate(body);
                }
                completed(BondActionResult.Success(preference.Code, preference.Describe(giftName)));
            });
    }

    private bool TryGetNearby(
        CompanionIdentity identity,
        Farmer owner,
        out CompanionRecord record,
        out NPC body,
        out BondActionResult failure)
    {
        record = null!;
        body = null!;
        if (!this.registry.TryGet(identity, out record))
        {
            failure = BondActionResult.Failure("IDENTITY-NOT-FOUND", "Summon Yui before interacting with her.");
            return false;
        }
        if (!this.bodies.TryGetBody(identity, out body)
            || body.currentLocation is null
            || !ReferenceEquals(body.currentLocation, owner.currentLocation))
        {
            failure = BondActionResult.Failure("YUI-NOT-NEARBY", "Yui must be present in the same location.");
            return false;
        }
        if (Math.Max(Math.Abs(body.TilePoint.X - owner.TilePoint.X), Math.Abs(body.TilePoint.Y - owner.TilePoint.Y)) > 1)
        {
            failure = BondActionResult.Failure("YUI-TOO-FAR", "Stand next to Yui to interact with her.");
            return false;
        }
        failure = default;
        return true;
    }

    private static int Today => (int)Math.Min((uint)int.MaxValue, Game1.stats.DaysPlayed);

    private static void RefreshGiftWeek(CompanionBondRecord bond)
    {
        int week = Today / 7;
        if (bond.GiftWeek == week)
            return;
        bond.GiftWeek = week;
        bond.GiftsThisWeek = 0;
    }

    private static void AddPoints(CompanionBondRecord bond, int points) =>
        bond.Points = Math.Clamp(bond.Points + points, 0, CompanionBondRecord.MaxPoints);

    private static int FacingToward(Point from, Point to)
    {
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        return Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? 1 : 3) : (dy < 0 ? 0 : 2);
    }

    private static void Celebrate(NPC body)
    {
        if (body.currentLocation is null)
            return;
        body.doEmote(20);
        Game1.Multiplayer.broadcastSprites(
            body.currentLocation,
            new TemporaryAnimatedSprite(
                "LooseSprites\\Cursors",
                new Rectangle(211, 428, 7, 6),
                2000f,
                1,
                0,
                body.Tile * Game1.tileSize + new Vector2(16f, -64f),
                flicker: false,
                flipped: false,
                1f,
                0f,
                Color.White,
                4f,
                0f,
                0f,
                0f));
    }
}
