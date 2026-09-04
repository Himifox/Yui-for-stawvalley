using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Pathfinding;

namespace YuiToIssho;

internal static class LeisurePhases
{
    public const string Traveling = "Traveling";
    public const string Seated = "Seated";
}

internal readonly record struct LeisureActionResult(bool IsSuccess, string Code, string Message)
{
    public static LeisureActionResult Success(string code, string message) => new(true, code, message);
    public static LeisureActionResult Failure(string code, string message) => new(false, code, message);
}

internal readonly record struct LeisureSnapshot(string Phase, string SeatKind, bool Automatic);

internal sealed class CompanionLeisureCoordinator
{
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 30;
    private const int StuckTimeoutTicks = 300;
    private const int MaximumPathAttempts = 5;
    private const int ManualSearchRadius = 12;
    private const int AutomaticSearchRadius = 6;
    private const ulong AutomaticRetryTicks = 120;

    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, SeatRuntime> runtime = new();
    private readonly Dictionary<CompanionIdentity, ulong> nextAutomaticAttempt = new();
    private readonly HashSet<CompanionIdentity> automaticJoinSuppressed = new();
    private long nextSyntheticOccupantId = long.MinValue;
    private ulong currentTick;

    public CompanionLeisureCoordinator(
        CompanionRegistry registry,
        CompanionBodyBinder bodies,
        CompanionAppearanceCoordinator appearance,
        TaskNavigationService navigation,
        IMonitor monitor)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.appearance = appearance;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public bool IsActive(CompanionIdentity identity) => this.runtime.ContainsKey(identity);

    public bool IsSyntheticOccupant(long playerId) => playerId < 0 && this.runtime.Values.Any(state => state.SyntheticOccupantId == playerId);

    public bool IsBodySeatedAt(MapSeat seat, NPC body)
    {
        return this.bodies.TryGetIdentity(body, out CompanionIdentity identity)
            && this.runtime.TryGetValue(identity, out SeatRuntime? state)
            && state.Phase == LeisurePhases.Seated
            && ReferenceEquals(state.Seat, seat);
    }

    public LeisureSnapshot? GetSnapshot(CompanionIdentity identity) => this.runtime.TryGetValue(identity, out SeatRuntime? state)
        ? new LeisureSnapshot(state.Phase, state.SeatKind, state.Automatic)
        : null;

    public LeisureActionResult Sit(CompanionIdentity identity, Farmer owner, bool automatic = false)
    {
        if (this.runtime.TryGetValue(identity, out SeatRuntime? active))
            return LeisureActionResult.Success(active.Phase == LeisurePhases.Seated ? "ALREADY-SITTING" : "ALREADY-APPROACHING", $"{identity} is already {active.Phase.ToLowerInvariant()} toward {active.SeatKind.ToLowerInvariant()} seating.");
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return LeisureActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return LeisureActionResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before sitting.");
        if (owner.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return LeisureActionResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and Yui must be on the same map before sitting.");
        if (owner.currentLocation.IsTemporary)
            return LeisureActionResult.Failure("TEMPORARY-LOCATION", "Yui does not reserve event or festival seating.");
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId) || record.Mode == CompanionModes.Work || record.WorkDirective is not null)
            return LeisureActionResult.Failure("COMPANION-BUSY", $"{identity} must finish or stop current work before sitting.");
        if (record.Vitals.State != CompanionVitalStates.Active)
            return LeisureActionResult.Failure("VITALS-BLOCKED", $"{identity} cannot sit while state={record.Vitals.State}.");

        SeatChoice? choice = this.FindSeat(body, owner, automatic);
        if (choice is null)
            return LeisureActionResult.Failure("NO-REACHABLE-SEAT", $"No free reachable seat was found within {(automatic ? AutomaticSearchRadius : ManualSearchRadius)} tiles.");

        string returnMode = record.Mode is CompanionModes.Follow or CompanionModes.Wait ? record.Mode : CompanionModes.Wait;
        long syntheticId = this.AllocateSyntheticOccupantId();
        var state = new SeatRuntime(
            identity,
            choice.Value.Seat,
            choice.Value.Location,
            choice.Value.SeatIndex,
            choice.Value.SeatPosition,
            choice.Value.ApproachTile,
            choice.Value.Facing,
            choice.Value.SeatKind,
            returnMode,
            automatic,
            syntheticId,
            body.Position,
            this.currentTick);

        this.bodies.Halt(identity);
        record.Mode = CompanionModes.Wait;
        this.runtime.Add(identity, state);
        this.automaticJoinSuppressed.Remove(identity);
        this.SetSeatOccupancy(state);
        this.monitor.Log($"HY-LEISURE-STARTED: {identity} reserved {state.SeatKind} seat {state.SeatIndex} in {state.Location.NameOrUniqueName}.", LogLevel.Info);
        return LeisureActionResult.Success("SIT-STARTED", $"Yui is walking to the nearest free {state.SeatKind.ToLowerInvariant()} seat.");
    }

    public LeisureActionResult Stand(CompanionIdentity identity, string reason, bool suppressAutomaticJoin = false)
    {
        if (suppressAutomaticJoin)
            this.automaticJoinSuppressed.Add(identity);
        if (!this.runtime.TryGetValue(identity, out SeatRuntime? state))
            return LeisureActionResult.Success("ALREADY-STANDING", $"{identity} is already standing.");

        this.ReleaseSeatOccupancy(state);
        this.appearance.Clear(identity, reason);
        this.runtime.Remove(identity);
        this.nextAutomaticAttempt[identity] = this.currentTick + AutomaticRetryTicks;
        if (this.registry.TryGet(identity, out CompanionRecord record) && record.Mode == CompanionModes.Wait)
            record.Mode = state.ReturnMode;
        if (this.bodies.TryGetBody(identity, out NPC body) && ReferenceEquals(body.currentLocation, state.Location))
        {
            this.bodies.Halt(identity);
            Vector2? exit = this.FindExitTile(state, body);
            if (exit.HasValue)
                body.Position = exit.Value * Game1.tileSize;
            body.faceDirection(state.Facing);
        }
        this.monitor.Log($"HY-LEISURE-{reason}: {identity} released {state.SeatKind} seat {state.SeatIndex}.", LogLevel.Trace);
        return LeisureActionResult.Success("STANDING", "Yui stood up and released the seat.");
    }

    public void Update(IEnumerable<CompanionRecord> records, ulong tick)
    {
        this.currentTick = tick;
        foreach (SeatRuntime state in this.runtime.Values.ToArray())
            this.UpdateOne(state, tick);

        foreach (CompanionRecord record in records)
        {
            CompanionIdentity identity = record.Identity;
            Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
            if (owner is null || !owner.IsSitting())
            {
                this.automaticJoinSuppressed.Remove(identity);
                continue;
            }
            if (this.runtime.ContainsKey(identity)
                || this.automaticJoinSuppressed.Contains(identity)
                || record.Mode != CompanionModes.Follow
                || !string.IsNullOrWhiteSpace(record.ActiveTransactionId)
                || record.Vitals.State != CompanionVitalStates.Active
                || (this.nextAutomaticAttempt.TryGetValue(identity, out ulong next) && tick < next))
                continue;

            this.nextAutomaticAttempt[identity] = tick + AutomaticRetryTicks;
            LeisureActionResult result = this.Sit(identity, owner, automatic: true);
            if (!result.IsSuccess && result.Code != "NO-REACHABLE-SEAT")
                this.monitor.Log($"HY-LEISURE-AUTO-{result.Code}: {result.Message}", LogLevel.Trace);
        }
    }

    public void ClearAll(string reason)
    {
        foreach (CompanionIdentity identity in this.runtime.Keys.ToArray())
            this.Stand(identity, reason);
        this.nextAutomaticAttempt.Clear();
        this.automaticJoinSuppressed.Clear();
    }

    private void UpdateOne(SeatRuntime state, ulong tick)
    {
        Farmer? owner = Game1.GetPlayer(state.Identity.OwnerId, onlyOnline: true);
        if (!this.registry.TryGet(state.Identity, out CompanionRecord record)
            || !this.bodies.TryGetBody(state.Identity, out NPC body)
            || body.currentLocation is null
            || !ReferenceEquals(body.currentLocation, state.Location)
            || owner?.currentLocation is null
            || !ReferenceEquals(owner.currentLocation, state.Location)
            || !state.Seat.IsSeatHere(state.Location)
            || !this.SeatPositionStillMatches(state))
        {
            this.Stand(state.Identity, "SEAT-INVALID");
            return;
        }
        if (state.Automatic && !owner.IsSitting())
        {
            this.Stand(state.Identity, "OWNER-STOOD");
            return;
        }
        if (!this.OwnsSeatIndex(state))
        {
            this.Stand(state.Identity, "SEAT-OCCUPIED");
            return;
        }
        this.SetSeatOccupancy(state);

        if (state.Phase == LeisurePhases.Seated)
        {
            this.bodies.Halt(state.Identity);
            body.Position = state.SeatPosition * Game1.tileSize;
            body.faceDirection(state.Facing);
            AppearanceActionSnapshot? presentation = this.appearance.GetActionSnapshot(state.Identity);
            if (presentation is null || presentation.Value.OperationId != state.OperationId)
                this.appearance.SetPersistentPhase(state.Identity, state.OperationId, state.AppearanceKind, "Waiting", state.Facing);
            return;
        }

        if (body.TilePoint == state.ApproachTile.ToPoint())
        {
            this.bodies.Halt(state.Identity);
            body.Position = state.SeatPosition * Game1.tileSize;
            body.faceDirection(state.Facing);
            state.Phase = LeisurePhases.Seated;
            this.appearance.SetPersistentPhase(state.Identity, state.OperationId, state.AppearanceKind, "Waiting", state.Facing);
            if (ReferenceEquals(Game1.currentLocation, state.Location))
                state.Location.playSound("woodyStep");
            this.monitor.Log($"HY-LEISURE-SEATED: {state.Identity} sat on {state.SeatKind} seat {state.SeatIndex}.", LogLevel.Info);
            return;
        }

        TaskNavigationResult progress = this.navigation.Observe(state.Identity, body, state.Navigation, tick, StuckTimeoutTicks, MaximumPathAttempts, RepathDelayTicks);
        if (progress.BudgetExhausted)
        {
            this.Stand(state.Identity, "PATH-BUDGET-EXHAUSTED");
            return;
        }
        if (!progress.CanIssuePath)
            return;
        body.controller = new PathFindController(body, state.Location, state.ApproachTile.ToPoint(), state.Facing, null, PathSearchLimit);
        this.navigation.MarkPathIssued(state.Navigation, body.Position, tick, RepathDelayTicks);
    }

    private SeatChoice? FindSeat(NPC body, Farmer owner, bool automatic)
    {
        GameLocation location = body.currentLocation!;
        ISittable? preferred = owner.IsSitting() && owner.sittingFurniture?.IsSeatHere(location) == true
            ? owner.sittingFurniture
            : null;
        var seats = new List<ISittable>();
        if (preferred is not null)
            seats.Add(preferred);
        seats.AddRange(location.furniture.Where(item => item.GetSeatCapacity() > 0));
        seats.AddRange(location.mapSeats);

        Vector2 origin = automatic ? owner.Tile : body.Tile;
        int radius = automatic ? AutomaticSearchRadius : ManualSearchRadius;
        var candidates = new List<(ISittable Seat, int Index, Vector2 Position, float Distance, int Priority)>();
        var seen = new HashSet<ISittable>(ReferenceEqualityComparer.Instance);
        foreach (ISittable seat in seats)
        {
            if (!seen.Add(seat) || !seat.IsSeatHere(location))
                continue;
            if (seat is MapSeat mapSeat && this.IsMapSeatBlocked(mapSeat, location, body))
                continue;

            List<Vector2> positions;
            try
            {
                positions = seat.GetSeatPositions();
            }
            catch
            {
                continue;
            }
            HashSet<int> occupied = GetOccupiedSeatIndices(seat);
            foreach (int reserved in this.runtime.Values.Where(state => ReferenceEquals(state.Seat, seat)).Select(state => state.SeatIndex))
                occupied.Add(reserved);
            for (int index = 0; index < positions.Count; index++)
            {
                Vector2 position = positions[index];
                float distance = Vector2.Distance(origin, position);
                if (distance <= radius && !occupied.Contains(index) && !this.IsSeatPositionBlocked(location, body, position))
                    candidates.Add((seat, index, position, distance, ReferenceEquals(seat, preferred) ? 0 : 1));
            }
        }

        foreach ((ISittable seat, int index, Vector2 position, _, _) in candidates
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Position.Y)
            .ThenBy(candidate => candidate.Position.X)
            .Take(32))
        {
            int facing = ResolveFacing(seat, position, body.Tile);
            Vector2? approach = this.FindApproachTile(body, location, seat.GetSeatBounds(), position, facing);
            if (approach.HasValue)
                return new SeatChoice(seat, location, index, position, approach.Value, facing, SeatKind(seat));
        }
        return null;
    }

    private Vector2? FindApproachTile(NPC body, GameLocation location, Rectangle bounds, Vector2 seatPosition, int facing)
    {
        IEnumerable<Vector2> ring = Enumerable.Range(bounds.Left, Math.Max(1, bounds.Width))
            .SelectMany(x => new[] { new Vector2(x, bounds.Top - 1), new Vector2(x, bounds.Bottom) })
            .Concat(Enumerable.Range(bounds.Top, Math.Max(1, bounds.Height))
                .SelectMany(y => new[] { new Vector2(bounds.Left - 1, y), new Vector2(bounds.Right, y) }));
        foreach (Vector2 candidate in ring
            .Distinct()
            .OrderBy(tile => Vector2.DistanceSquared(tile, seatPosition))
            .ThenBy(tile => Vector2.DistanceSquared(tile, body.Tile)))
        {
            if (this.navigation.CanReach(body, location, candidate, facing, PathSearchLimit))
                return candidate;
        }
        return null;
    }

    private Vector2? FindExitTile(SeatRuntime state, NPC body)
    {
        if (CompanionPathing.IsStandable(body, state.Location, state.ApproachTile))
            return state.ApproachTile;
        Rectangle bounds = state.Seat.GetSeatBounds();
        IEnumerable<Vector2> ring = Enumerable.Range(bounds.Left, Math.Max(1, bounds.Width))
            .SelectMany(x => new[] { new Vector2(x, bounds.Top - 1), new Vector2(x, bounds.Bottom) })
            .Concat(Enumerable.Range(bounds.Top, Math.Max(1, bounds.Height))
                .SelectMany(y => new[] { new Vector2(bounds.Left - 1, y), new Vector2(bounds.Right, y) }));
        return ring.Distinct()
            .OrderBy(tile => Vector2.DistanceSquared(tile, state.SeatPosition))
            .FirstOrDefault(tile => CompanionPathing.IsStandable(body, state.Location, tile), new Vector2(float.NaN, float.NaN)) is Vector2 exit
                && float.IsFinite(exit.X) ? exit : null;
    }

    private bool OwnsSeatIndex(SeatRuntime state)
    {
        return !GetOccupiedSeatIndices(state.Seat, state.SyntheticOccupantId).Contains(state.SeatIndex);
    }

    private void SetSeatOccupancy(SeatRuntime state)
    {
        switch (state.Seat)
        {
            case Furniture furniture:
                furniture.sittingFarmers[state.SyntheticOccupantId] = state.SeatIndex;
                break;
            case MapSeat mapSeat:
                mapSeat.sittingFarmers[state.SyntheticOccupantId] = state.SeatIndex;
                break;
        }
    }

    private void ReleaseSeatOccupancy(SeatRuntime state)
    {
        switch (state.Seat)
        {
            case Furniture furniture when furniture.sittingFarmers.TryGetValue(state.SyntheticOccupantId, out int furnitureIndex) && furnitureIndex == state.SeatIndex:
                furniture.sittingFarmers.Remove(state.SyntheticOccupantId);
                break;
            case MapSeat mapSeat when mapSeat.sittingFarmers.TryGetValue(state.SyntheticOccupantId, out int mapIndex) && mapIndex == state.SeatIndex:
                mapSeat.sittingFarmers.Remove(state.SyntheticOccupantId);
                break;
        }
    }

    private bool SeatPositionStillMatches(SeatRuntime state)
    {
        try
        {
            List<Vector2> positions = state.Seat.GetSeatPositions();
            return state.SeatIndex >= 0 && state.SeatIndex < positions.Count && Vector2.DistanceSquared(positions[state.SeatIndex], state.SeatPosition) < 0.001f;
        }
        catch
        {
            return false;
        }
    }

    private bool IsMapSeatBlocked(MapSeat seat, GameLocation location, NPC body)
    {
        Rectangle seatBounds = seat.GetSeatBounds();
        seatBounds.X *= Game1.tileSize;
        seatBounds.Y *= Game1.tileSize;
        seatBounds.Width *= Game1.tileSize;
        seatBounds.Height *= Game1.tileSize;
        Rectangle approachBounds = seatBounds;
        switch (seat.direction.Value)
        {
            case 0: approachBounds.Y -= Game1.tileSize / 2; approachBounds.Height += Game1.tileSize / 2; break;
            case 1: approachBounds.Width += Game1.tileSize / 2; break;
            case 2: approachBounds.Height += Game1.tileSize / 2; break;
            case 3: approachBounds.X -= Game1.tileSize / 2; approachBounds.Width += Game1.tileSize / 2; break;
        }
        return location.characters.Any(character => !ReferenceEquals(character, body)
            && (character.GetBoundingBox().Intersects(seatBounds)
                || !character.isMovingOnPathFindPath.Value && character.GetBoundingBox().Intersects(approachBounds)));
    }

    private bool IsSeatPositionBlocked(GameLocation location, NPC body, Vector2 position)
    {
        var bounds = new Rectangle((int)(position.X * Game1.tileSize) + 16, (int)(position.Y * Game1.tileSize) + 16, 32, 32);
        return location.characters.Any(character => !ReferenceEquals(character, body) && character.GetBoundingBox().Intersects(bounds));
    }

    private long AllocateSyntheticOccupantId()
    {
        while (this.nextSyntheticOccupantId >= 0 || this.IsSyntheticOccupant(this.nextSyntheticOccupantId))
            this.nextSyntheticOccupantId++;
        return this.nextSyntheticOccupantId++;
    }

    private static HashSet<int> GetOccupiedSeatIndices(ISittable seat, long? excludingPlayerId = null)
    {
        IEnumerable<KeyValuePair<long, int>> entries = seat switch
        {
            Furniture furniture => furniture.sittingFarmers.Pairs,
            MapSeat mapSeat => mapSeat.sittingFarmers.Pairs,
            _ => Enumerable.Empty<KeyValuePair<long, int>>(),
        };
        return entries.Where(entry => entry.Key != excludingPlayerId).Select(entry => entry.Value).ToHashSet();
    }

    private static int ResolveFacing(ISittable seat, Vector2 seatPosition, Vector2 approach)
    {
        int approachFacing = TaskNavigationService.FacingToward(approach, seatPosition);
        if (seat is Furniture furniture && furniture.Name.Contains("Stool", StringComparison.OrdinalIgnoreCase))
            return approachFacing;
        if (seat is not MapSeat mapSeat)
            return seat.GetSittingDirection();

        string seatType = mapSeat.seatType.Value ?? string.Empty;
        if (seatType.StartsWith("stool", StringComparison.OrdinalIgnoreCase))
            return approachFacing;
        if (mapSeat.direction.Value == -2)
            return Utility.GetOppositeFacingDirection(approachFacing);
        if (seatType.StartsWith("bathchair", StringComparison.OrdinalIgnoreCase) && mapSeat.direction.Value == 0)
            return 2;
        return mapSeat.direction.Value is >= 0 and <= 3 ? mapSeat.direction.Value : 2;
    }

    private static string SeatKind(ISittable seat) => seat is MapSeat mapSeat && mapSeat.seatType.Value?.EndsWith("swings", StringComparison.OrdinalIgnoreCase) == true
        ? "Swing"
        : "Chair";

    private readonly record struct SeatChoice(ISittable Seat, GameLocation Location, int SeatIndex, Vector2 SeatPosition, Vector2 ApproachTile, int Facing, string SeatKind);

    private sealed class SeatRuntime
    {
        public SeatRuntime(CompanionIdentity identity, ISittable seat, GameLocation location, int seatIndex, Vector2 seatPosition, Vector2 approachTile, int facing, string seatKind, string returnMode, bool automatic, long syntheticOccupantId, Vector2 initialPosition, ulong tick)
        {
            this.Identity = identity;
            this.Seat = seat;
            this.Location = location;
            this.SeatIndex = seatIndex;
            this.SeatPosition = seatPosition;
            this.ApproachTile = approachTile;
            this.Facing = facing;
            this.SeatKind = seatKind;
            this.ReturnMode = returnMode;
            this.Automatic = automatic;
            this.SyntheticOccupantId = syntheticOccupantId;
            this.Navigation = new TaskNavigationState(initialPosition, tick);
            this.OperationId = $"leisure-{Guid.NewGuid():N}";
        }

        public CompanionIdentity Identity { get; }
        public ISittable Seat { get; }
        public GameLocation Location { get; }
        public int SeatIndex { get; }
        public Vector2 SeatPosition { get; }
        public Vector2 ApproachTile { get; }
        public int Facing { get; }
        public string SeatKind { get; }
        public string ReturnMode { get; }
        public bool Automatic { get; }
        public long SyntheticOccupantId { get; }
        public TaskNavigationState Navigation { get; }
        public string OperationId { get; }
        public string AppearanceKind => this.SeatKind == "Swing" ? AppearanceActionKinds.Swinging : AppearanceActionKinds.Sitting;
        public string Phase { get; set; } = LeisurePhases.Traveling;
    }
}

internal static class CompanionSeatedPose
{
    public static bool IsSeatedKind(string? kind) => kind is AppearanceActionKinds.Sitting or AppearanceActionKinds.Swinging;

    public static int Apply(Farmer farmer, int facing, out bool flip, out bool secondaryArm)
    {
        flip = facing == 3;
        secondaryArm = facing == 2;
        switch (facing)
        {
            case 0:
                farmer.xOffset = 0f;
                farmer.yOffset = -40f;
                return 113;
            case 1:
                farmer.xOffset = -4f;
                farmer.yOffset = -32f;
                return 117;
            case 3:
                farmer.xOffset = 4f;
                farmer.yOffset = -32f;
                return 117;
            default:
                farmer.xOffset = 0f;
                farmer.yOffset = -48f;
                return 107;
        }
    }

    public static void Reset(Farmer farmer)
    {
        farmer.xOffset = 0f;
        farmer.yOffset = 0f;
    }
}
