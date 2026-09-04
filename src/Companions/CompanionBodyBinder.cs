using System.Globalization;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace YuiToIssho;

internal readonly record struct BodyBindResult(bool IsSuccess, string Code, string Message)
{
    public static BodyBindResult Success(string code, string message) => new(true, code, message);

    public static BodyBindResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class CompanionBodyBinder
{
    public const int DefaultMovementSpeed = 5;
    internal const string BodyTag = "Himifox.YuiToIssho/Body";
    internal const string OwnerIdTag = "Himifox.YuiToIssho/OwnerId";
    internal const string SlotTag = "Himifox.YuiToIssho/Slot";
    internal const string BodyGenerationTag = "Himifox.YuiToIssho/BodyGeneration";
    internal const string ProfileIdTag = "Himifox.YuiToIssho/ProfileId";
    internal const string ProfileGenerationTag = "Himifox.YuiToIssho/ProfileGeneration";
    private readonly Dictionary<CompanionIdentity, NPC> boundBodies = new();
    private readonly Dictionary<CompanionIdentity, ulong> bodyGenerations = new();
    private readonly IMonitor monitor;

    public CompanionBodyBinder(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    public BodyBindResult Bind(CompanionRecord record, Farmer owner)
    {
        CompanionIdentity identity = record.Identity;
        if (!CompanionGenerationPolicy.TryValidateOwnerBinding(identity, owner, out string ownerFailure))
            return BodyBindResult.Failure("OWNER-MISMATCH", ownerFailure);
        if (record.DisplayName != CompanionGenerationPolicy.DisplayName)
            return BodyBindResult.Failure("INVALID-DISPLAY-NAME", $"The companion must use the fixed display name {CompanionGenerationPolicy.DisplayName}.");
        if (this.boundBodies.TryGetValue(identity, out NPC? existing) && existing.currentLocation is not null)
            return BodyBindResult.Success("ALREADY-SUMMONED", $"{identity} already has one body.");

        if (!Context.IsWorldReady || owner.currentLocation is null)
            return BodyBindResult.Failure("OWNER-LOCATION-UNAVAILABLE", $"Owner {identity.OwnerId} has no available location.");

        string internalName = $"YuiToIssho_{identity.OwnerId}_{identity.Slot}";
        var body = new NPC
        {
            Name = internalName,
            Sprite = new AnimatedSprite("Characters/Abigail", 0, 16, 32),
            Position = owner.Position,
            currentLocation = owner.currentLocation,
            Speed = DefaultMovementSpeed,
            // PathFindController routes movement and final facing through Character.faceDirection,
            // which intentionally ignores simple non-villager NPCs. This body has no NPC data and
            // remains invisible, so keep the normal direction pipeline enabled for authoritative state.
            SimpleNonVillagerNPC = false,
            AllowDynamicAppearance = false,
            IsInvisible = true,
            HideShadow = true,
            // Yui is a companion, not a solid villager obstacle. This keeps narrow paths,
            // doors, beds, and seat approach tiles usable while she follows the player.
            farmerPassesThrough = true,
        };
        Vector2? spawnTile = FindSpawnTile(owner.currentLocation, owner.Tile, body);
        if (spawnTile is null)
            return BodyBindResult.Failure("NO-SAFE-SPAWN", $"No collision-free spawn tile is available near owner {identity.OwnerId}.");

        body.Position = spawnTile.Value * Game1.tileSize;
        body.faceDirection(2);
        ulong generation = this.NextGeneration(identity);
        body.modData[BodyTag] = "1";
        body.modData[OwnerIdTag] = identity.OwnerId.ToString(CultureInfo.InvariantCulture);
        body.modData[SlotTag] = identity.Slot.ToString(CultureInfo.InvariantCulture);
        body.modData[BodyGenerationTag] = generation.ToString(CultureInfo.InvariantCulture);
        body.modData[ProfileIdTag] = record.Appearance.ProfileId ?? string.Empty;
        body.modData[ProfileGenerationTag] = record.Appearance.Generation.ToString(CultureInfo.InvariantCulture);
        owner.currentLocation.characters.Add(body);
        this.boundBodies[identity] = body;
        return BodyBindResult.Success("SUMMONED", $"Summoned {identity} as {record.DisplayName} with body generation {generation}.");
    }

    public BodyBindResult Unbind(CompanionIdentity identity)
    {
        if (!this.boundBodies.Remove(identity, out NPC? body))
            return BodyBindResult.Success("ALREADY-RECALLED", $"{identity} has no bound body.");

        StopBody(body);
        body.currentLocation?.characters.Remove(body);
        return BodyBindResult.Success("RECALLED", $"Recalled {identity}.");
    }

    public bool TryGetBody(CompanionIdentity identity, out NPC body) => this.boundBodies.TryGetValue(identity, out body!);

    public IEnumerable<KeyValuePair<CompanionIdentity, NPC>> BoundBodies => this.boundBodies;

    public bool TryGetIdentity(NPC body, out CompanionIdentity identity)
    {
        foreach ((CompanionIdentity candidate, NPC current) in this.boundBodies)
        {
            if (ReferenceEquals(current, body))
            {
                identity = candidate;
                return true;
            }
        }
        return TryReadIdentity(body, out identity, out _);
    }

    public bool TryGetBodyGeneration(CompanionIdentity identity, out ulong generation) => this.bodyGenerations.TryGetValue(identity, out generation);

    internal static bool TryReadIdentity(NPC? body, out CompanionIdentity identity, out ulong bodyGeneration)
    {
        identity = default;
        bodyGeneration = 0;
        if (body is null
            || !body.modData.TryGetValue(BodyTag, out string? marker) || marker != "1"
            || !body.modData.TryGetValue(OwnerIdTag, out string? ownerText)
            || !long.TryParse(ownerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ownerId) || ownerId == 0
            || !body.modData.TryGetValue(SlotTag, out string? slotText)
            || !int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) || !CompanionIdentity.IsValidSlot(slot)
            || !body.modData.TryGetValue(BodyGenerationTag, out string? generationText)
            || !ulong.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out bodyGeneration) || bodyGeneration == 0)
            return false;
        identity = new CompanionIdentity(ownerId, slot);
        return true;
    }

    public BodyBindResult Rebind(CompanionRecord record, Farmer owner)
    {
        this.Unbind(record.Identity);
        return this.Bind(record, owner);
    }

    public void Halt(CompanionIdentity identity)
    {
        if (this.boundBodies.TryGetValue(identity, out NPC? body))
            StopBody(body);
    }

    public void DetachAll(bool clearGenerations = false)
    {
        foreach (NPC body in this.boundBodies.Values)
        {
            StopBody(body);
            body.currentLocation?.characters.Remove(body);
        }
        this.boundBodies.Clear();
        if (clearGenerations)
            this.bodyGenerations.Clear();
    }

    public void RestoreDesired(IEnumerable<CompanionRecord> records)
    {
        foreach (CompanionRecord record in records.Where(record => record.WantsBody))
        {
            Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
            if (owner is null)
            {
                this.monitor.Log($"HY-BODY-OWNER-OFFLINE: Cannot restore {record.Identity} until its owner is online.", LogLevel.Debug);
                continue;
            }

            BodyBindResult result = this.Bind(record, owner);
            if (!result.IsSuccess)
                this.monitor.Log($"HY-BODY-{result.Code}: {result.Message}", LogLevel.Warn);
        }
    }

    private static Vector2? FindSpawnTile(GameLocation location, Vector2 ownerTile, NPC body)
    {
        const int searchLimit = 64;
        var pending = new Queue<Vector2>();
        var visited = new HashSet<Vector2> { ownerTile };
        pending.Enqueue(ownerTile);

        for (int checkedTiles = 0; checkedTiles < searchLimit && pending.Count > 0; checkedTiles++)
        {
            Vector2 candidate = pending.Dequeue();
            if (candidate != ownerTile && CompanionPathing.IsStandable(body, location, candidate))
                return candidate;

            foreach (Vector2 direction in Utility.DirectionsTileVectors)
            {
                Vector2 next = candidate + direction;
                if (location.isTileOnMap(next) && visited.Add(next))
                    pending.Enqueue(next);
            }
        }

        return null;
    }

    private static void StopBody(NPC body)
    {
        body.controller = null;
        body.Halt();
        body.Speed = DefaultMovementSpeed;
    }

    private ulong NextGeneration(CompanionIdentity identity)
    {
        ulong next = this.bodyGenerations.TryGetValue(identity, out ulong previous) && previous < ulong.MaxValue ? previous + 1 : 1;
        this.bodyGenerations[identity] = next;
        return next;
    }
}
