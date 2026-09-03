using Microsoft.Xna.Framework;
using StardewValley;

namespace YuiToIssho;

internal sealed class AgentPerceptionService
{
    public const int ScanRadius = 12;
    public const int MaximumTargetsPerKind = 8;
    public const int MaximumTargets = 48;

    private readonly CompanionBodyBinder bodies;

    public AgentPerceptionService(CompanionBodyBinder bodies)
    {
        this.bodies = bodies;
    }

    public AgentPerceptionSnapshot Capture(AgentRuntime runtime, CompanionRecord record, ulong tick)
    {
        if (runtime.Identity != record.Identity || !record.Identity.IsCanonical)
            throw new InvalidOperationException("Perception may inspect only its canonical active record.");

        long version = checked(runtime.SnapshotVersion + 1);
        bool bodyPresent = this.bodies.TryGetBody(record.Identity, out NPC body) && body.currentLocation is not null;
        Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
        bool ownerOnline = owner?.currentLocation is not null;
        bool sameLocation = bodyPresent && ownerOnline && ReferenceEquals(body.currentLocation, owner!.currentLocation);
        string bodyLocation = bodyPresent ? Bounded(body.currentLocation!.NameOrUniqueName, 256) : string.Empty;
        int bodyX = bodyPresent ? body.TilePoint.X : 0;
        int bodyY = bodyPresent ? body.TilePoint.Y : 0;
        int ownerX = ownerOnline ? owner!.TilePoint.X : 0;
        int ownerY = ownerOnline ? owner!.TilePoint.Y : 0;
        int distance = sameLocation ? Manhattan(bodyX, bodyY, ownerX, ownerY) : int.MaxValue;

        var self = new AgentSelfPerception(
            bodyPresent,
            bodyLocation,
            bodyX,
            bodyY,
            bodyPresent ? Math.Clamp(body.FacingDirection, 0, 3) : 2,
            bodyPresent && body.isMoving(),
            record.Mode,
            record.Vitals.State,
            record.Vitals.Health,
            record.Vitals.Stamina,
            BoundedNullable(record.ActiveTransactionId, 160),
            BoundedNullable(record.WorkDirective?.DirectiveId, 64));
        var ownerView = new AgentOwnerPerception(ownerOnline, sameLocation, ownerOnline ? Bounded(owner!.currentLocation.NameOrUniqueName, 256) : string.Empty, ownerX, ownerY, distance);

        IReadOnlyList<AgentTargetPerception> targets = bodyPresent
            ? this.ObserveTargets(runtime, version, body.currentLocation!, body.Tile)
            : Array.Empty<AgentTargetPerception>();
        int dangers = targets.Count(target => target.Kind == "Monster");
        var world = new AgentWorldPerception(bodyLocation, Game1.Date.TotalDays, Game1.timeOfDay, dangers);
        IReadOnlyList<string> changes = BuildChanges(runtime.CurrentSnapshot, self, ownerView, world);
        IReadOnlyList<string> intents = ResolveAllowedIntents(record, bodyPresent, ownerOnline, sameLocation, distance);
        return new AgentPerceptionSnapshot(record.Identity, runtime.AuthorityGeneration, version, tick, self, ownerView, world, targets, changes, intents);
    }

    private IReadOnlyList<AgentTargetPerception> ObserveTargets(AgentRuntime runtime, long version, GameLocation location, Vector2 origin)
    {
        var raw = new List<RawTarget>();
        foreach (WorldTargetFact fact in WorldTargetClassifier.Observe(location))
            Add(raw, fact, origin);

        return raw
            .Where(target => target.Distance <= ScanRadius)
            .GroupBy(target => target.Kind, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .SelectMany(group => group.OrderBy(target => target.Distance).ThenBy(target => target.StableId, StringComparer.Ordinal).Take(MaximumTargetsPerKind))
            .OrderBy(target => target.Distance)
            .ThenBy(target => target.Kind, StringComparer.Ordinal)
            .ThenBy(target => target.StableId, StringComparer.Ordinal)
            .Take(MaximumTargets)
            .Select(target => new AgentTargetPerception(
                $"{runtime.Identity.OwnerId}:{runtime.Identity.Slot}:{runtime.AuthorityGeneration}:{version}:{target.Kind}:{target.StableId}",
                target.Kind,
                target.Subtype,
                target.StableId,
                target.X,
                target.Y,
                target.Distance,
                target.SuggestedWorkKind,
                target.Disposition,
                target.ReasonCode))
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveAllowedIntents(CompanionRecord record, bool bodyPresent, bool ownerOnline, bool sameLocation, int distance)
    {
        if (!bodyPresent || !ownerOnline)
            return new[] { AgentIntentIds.HoldForLifecycle };
        if (record.Vitals.State is CompanionVitalStates.Downed or CompanionVitalStates.Recovering or CompanionVitalStates.Retreating)
            return new[] { AgentIntentIds.Recover };
        if (record.Mode == CompanionModes.Wait)
            return new[] { AgentIntentIds.HonorWait };
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return new[] { AgentIntentIds.ObserveManualTask };
        if (record.Mode == CompanionModes.Work && record.WorkDirective is not null)
            return new[] { AgentIntentIds.MaintainContinuousWork };
        if (record.Mode == CompanionModes.Follow && sameLocation && distance > 3)
            return new[] { AgentIntentIds.FollowOwner, AgentIntentIds.IdleNearby };
        return new[] { AgentIntentIds.IdleNearby };
    }

    private static IReadOnlyList<string> BuildChanges(AgentPerceptionSnapshot? previous, AgentSelfPerception self, AgentOwnerPerception owner, AgentWorldPerception world)
    {
        if (previous is null)
            return new[] { "RUNTIME-OBSERVATION-STARTED" };
        var changes = new List<string>(6);
        if (previous.Self.LocationKey != self.LocationKey) changes.Add("SELF-LOCATION-CHANGED");
        if (previous.Owner.LocationKey != owner.LocationKey || previous.Owner.Online != owner.Online) changes.Add("OWNER-AVAILABILITY-CHANGED");
        if (previous.Self.Mode != self.Mode) changes.Add("MODE-CHANGED");
        if (previous.Self.VitalState != self.VitalState) changes.Add("VITAL-STATE-CHANGED");
        if (previous.Self.ActiveTransactionId != self.ActiveTransactionId) changes.Add("TRANSACTION-CHANGED");
        if (previous.Self.WorkDirectiveId != self.WorkDirectiveId) changes.Add("WORK-DIRECTIVE-CHANGED");
        if (previous.World.NearbyDangerCount != world.NearbyDangerCount) changes.Add("DANGER-COUNT-CHANGED");
        return changes.Take(8).ToArray();
    }

    private static void Add(List<RawTarget> targets, WorldTargetFact fact, Vector2 origin)
    {
        int x = (int)fact.Tile.X;
        int y = (int)fact.Tile.Y;
        targets.Add(new RawTarget(
            fact.Category,
            fact.Subtype,
            fact.StableId,
            x,
            y,
            Manhattan(x, y, (int)origin.X, (int)origin.Y),
            fact.SuggestedWorkKind,
            fact.Disposition,
            fact.ReasonCode));
    }

    private static int Manhattan(int x1, int y1, int x2, int y2) => Math.Abs(x1 - x2) + Math.Abs(y1 - y2);
    private static string Bounded(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private static string? BoundedNullable(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? null : Bounded(value, maximum);
    private readonly record struct RawTarget(
        string Kind,
        string Subtype,
        string StableId,
        int X,
        int Y,
        int Distance,
        string? SuggestedWorkKind,
        string Disposition,
        string ReasonCode);
}
