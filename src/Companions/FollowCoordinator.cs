using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace YuiToIssho;

internal sealed class FollowCoordinator
{
    private const int ComfortableDistance = 3;
    private const int NearDistance = 5;
    private const int FarDistance = 8;
    private const int HardRegroupDistance = 12;
    private const int NearMovementSpeed = 3;
    private const int FarMovementSpeed = 8;
    private const int LocationRegroupDelayTicks = 30;
    private const int MinimumRepathDelayTicks = 30;
    private const int MaximumRepathDelayTicks = 180;
    private const int PathSearchLimit = 256;
    private const int StuckTimeoutTicks = 300;

    private readonly CompanionBodyBinder bodies;
    private readonly TaskNavigationService navigation;
    private readonly Func<CompanionIdentity, bool> isPresenting;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, FollowRuntime> runtime = new();

    public FollowCoordinator(CompanionBodyBinder bodies, TaskNavigationService navigation, Func<CompanionIdentity, bool> isPresenting, IMonitor monitor)
    {
        this.bodies = bodies;
        this.navigation = navigation;
        this.isPresenting = isPresenting;
        this.monitor = monitor;
    }

    public void Update(IEnumerable<CompanionRecord> records, ulong tick)
    {
        CompanionRecord[] active = records.Where(record => record.WantsBody).ToArray();
        var activeIdentities = active.Select(record => record.Identity).ToHashSet();
        foreach (CompanionIdentity stale in this.runtime.Keys.Where(identity => !activeIdentities.Contains(identity)).ToArray())
            this.runtime.Remove(stale);

        if (!Context.IsMainPlayer)
        {
            this.PauseAll(active);
            return;
        }

        foreach (CompanionRecord record in active)
        {
            Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
            if (owner?.IsLocalPlayer == true && !Context.IsPlayerFree)
            {
                this.bodies.Halt(record.Identity);
                continue;
            }
            this.UpdateOne(record, tick);
        }
    }

    public void RegroupAfterWarp(Farmer owner, IEnumerable<CompanionRecord> records, ulong tick)
    {
        if (!Context.IsMainPlayer || owner.currentLocation is null)
            return;
        CompanionRecord? record = records.FirstOrDefault(candidate => candidate.OwnerId == owner.UniqueMultiplayerID);
        if (record is null
            || !record.WantsBody
            || record.Mode != CompanionModes.Follow
            || !string.IsNullOrWhiteSpace(record.ActiveTransactionId)
            || this.isPresenting(record.Identity))
            return;

        if (this.bodies.TryGetBody(record.Identity, out NPC body)
            && ReferenceEquals(body.currentLocation, owner.currentLocation))
            return;
        BodyBindResult result = this.bodies.TryGetBody(record.Identity, out _)
            ? this.bodies.Rebind(record, owner)
            : this.bodies.Bind(record, owner);
        FollowRuntime state = this.GetRuntime(record.Identity, owner.Position, tick);
        state.Reset(owner.Position, tick, tick + MinimumRepathDelayTicks);
        if (!result.IsSuccess)
            this.monitor.Log($"HY-NAV-{result.Code}: {result.Message}", LogLevel.Warn);
    }

    public void PauseAll(IEnumerable<CompanionRecord> records)
    {
        foreach (CompanionRecord record in records)
            this.bodies.Halt(record.Identity);
    }

    public void Clear()
    {
        this.runtime.Clear();
    }

    private void UpdateOne(CompanionRecord record, ulong tick)
    {
        CompanionIdentity identity = record.Identity;
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
        {
            this.runtime.Remove(identity);
            return;
        }
        if (this.isPresenting(identity))
        {
            this.bodies.Halt(identity);
            this.runtime.Remove(identity);
            return;
        }

        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (!this.bodies.TryGetBody(identity, out NPC body))
        {
            if (owner?.currentLocation is null)
                return;

            BodyBindResult restore = this.bodies.Bind(record, owner);
            if (!restore.IsSuccess || !this.bodies.TryGetBody(identity, out body))
            {
                this.monitor.Log($"HY-NAV-{restore.Code}: {restore.Message}", LogLevel.Warn);
                return;
            }
        }

        if (record.Mode != CompanionModes.Follow)
        {
            this.bodies.Halt(identity);
            this.runtime.Remove(identity);
            return;
        }

        if (owner?.currentLocation is null || body.currentLocation is null)
        {
            this.bodies.Halt(identity);
            return;
        }

        FollowRuntime state = this.GetRuntime(identity, body.Position, tick);
        if (!ReferenceEquals(body.currentLocation, owner.currentLocation))
        {
            this.bodies.Halt(identity);
            state.LocationMismatchSinceTick ??= tick;
            if (tick - state.LocationMismatchSinceTick.Value >= LocationRegroupDelayTicks)
            {
                BodyBindResult result = this.bodies.Rebind(record, owner);
                state.Reset(owner.Position, tick, tick + MinimumRepathDelayTicks);
                if (!result.IsSuccess)
                    this.monitor.Log($"HY-NAV-{result.Code}: {result.Message}", LogLevel.Warn);
            }
            return;
        }

        state.LocationMismatchSinceTick = null;
        int tileDistance = ManhattanDistance(body.TilePoint, owner.TilePoint);
        if (tileDistance > HardRegroupDistance)
        {
            this.bodies.Halt(identity);
            BodyBindResult result = this.bodies.Rebind(record, owner);
            state.Reset(owner.Position, tick, tick + MinimumRepathDelayTicks);
            if (!result.IsSuccess)
                this.monitor.Log($"HY-NAV-{result.Code}: {result.Message}", LogLevel.Warn);
            return;
        }

        if (tileDistance <= ComfortableDistance || (state.IsHoldingPosition && tileDistance <= NearDistance))
        {
            this.bodies.Halt(identity);
            FaceToward(body, owner.Position);
            state.Hold(body.Position, tick, tick + MinimumRepathDelayTicks);
            return;
        }

        state.IsHoldingPosition = false;

        body.Speed = tileDistance <= NearDistance
            ? NearMovementSpeed
            : tileDistance > FarDistance ? FarMovementSpeed : CompanionBodyBinder.DefaultMovementSpeed;

        this.TrackProgress(identity, body, state, tick);
        if (body.controller is not null
            && state.PathOwnerTile is Point pathOwnerTile
            && ManhattanDistance(pathOwnerTile, owner.TilePoint) > ComfortableDistance
            && tick >= state.NextPathTick)
        {
            this.bodies.Halt(identity);
            state.PathOwnerTile = null;
        }
        if (tick < state.NextPathTick || body.controller is not null)
            return;

        Vector2? target = this.FindTargetTile(body, owner.currentLocation, owner.Tile, owner.FacingDirection);
        if (target is null)
        {
            this.RegisterFailure(identity, body, state, tick);
            return;
        }

        PathFindController controller = CompanionPathing.CreateController(
            body,
            owner.currentLocation,
            target.Value.ToPoint(),
            owner.FacingDirection,
            PathSearchLimit
        );
        if (controller.pathToEndPoint is not { Count: > 0 })
        {
            this.RegisterFailure(identity, body, state, tick);
            return;
        }

        body.controller = controller;
        state.LastPosition = body.Position;
        state.LastProgressTick = tick;
        state.NextPathTick = tick + MinimumRepathDelayTicks;
        state.PathOwnerTile = owner.TilePoint;
    }

    private void TrackProgress(CompanionIdentity identity, NPC body, FollowRuntime state, ulong tick)
    {
        if (body.controller is null)
        {
            state.LastPosition = body.Position;
            state.LastProgressTick = tick;
            return;
        }

        if (body.Position != state.LastPosition)
        {
            state.LastPosition = body.Position;
            state.LastProgressTick = tick;
            state.ConsecutiveFailures = 0;
            return;
        }

        if (tick - state.LastProgressTick < StuckTimeoutTicks)
            return;

        this.RegisterFailure(identity, body, state, tick);
    }

    private void RegisterFailure(CompanionIdentity identity, NPC body, FollowRuntime state, ulong tick)
    {
        this.bodies.Halt(identity);
        state.LastPosition = body.Position;
        state.LastProgressTick = tick;
        state.PathOwnerTile = null;
        state.ConsecutiveFailures++;
        int delay = Math.Min(MaximumRepathDelayTicks, MinimumRepathDelayTicks * state.ConsecutiveFailures);
        state.NextPathTick = tick + (ulong)delay;
        if (state.ConsecutiveFailures == 3)
            this.monitor.Log("HY-NAV-STUCK: Follow path is blocked; retries are now backed off.", LogLevel.Warn);
    }

    private FollowRuntime GetRuntime(CompanionIdentity identity, Vector2 position, ulong tick)
    {
        if (!this.runtime.TryGetValue(identity, out FollowRuntime? state))
        {
            state = new FollowRuntime(position, tick);
            this.runtime.Add(identity, state);
        }
        return state;
    }

    private Vector2? FindTargetTile(NPC body, GameLocation location, Vector2 ownerTile, int ownerFacing)
    {
        Vector2[] offsets =
        {
            new(-2, 0), new(0, 2), new(2, 0), new(0, -2),
            new(-1, 0), new(0, 1), new(1, 0), new(0, -1),
        };

        foreach (Vector2 candidate in offsets
            .Select(offset => ownerTile + offset)
            .OrderBy(candidate => ManhattanDistance(candidate.ToPoint(), body.TilePoint)))
        {
            if (this.navigation.CanReach(body, location, candidate, ownerFacing, PathSearchLimit))
                return candidate;
        }

        return null;
    }

    private static int ManhattanDistance(Point left, Point right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static void FaceToward(NPC body, Vector2 targetPosition)
    {
        Vector2 delta = targetPosition - body.Position;
        if (delta == Vector2.Zero)
            return;
        body.faceDirection(Math.Abs(delta.X) > Math.Abs(delta.Y) ? (delta.X > 0f ? 1 : 3) : (delta.Y > 0f ? 2 : 0));
    }

    private sealed class FollowRuntime
    {
        public FollowRuntime(Vector2 initialPosition, ulong tick)
        {
            this.LastPosition = initialPosition;
            this.LastProgressTick = tick;
        }

        public Vector2 LastPosition { get; set; }

        public ulong LastProgressTick { get; set; }

        public int ConsecutiveFailures { get; set; }

        public ulong NextPathTick { get; set; }

        public ulong? LocationMismatchSinceTick { get; set; }

        public bool IsHoldingPosition { get; set; }

        public Point? PathOwnerTile { get; set; }

        public void Hold(Vector2 position, ulong tick, ulong nextPathTick)
        {
            this.LastPosition = position;
            this.LastProgressTick = tick;
            this.ConsecutiveFailures = 0;
            this.NextPathTick = nextPathTick;
            this.LocationMismatchSinceTick = null;
            this.IsHoldingPosition = true;
            this.PathOwnerTile = null;
        }

        public void Reset(Vector2 position, ulong tick, ulong nextPathTick)
        {
            this.LastPosition = position;
            this.LastProgressTick = tick;
            this.ConsecutiveFailures = 0;
            this.NextPathTick = nextPathTick;
            this.LocationMismatchSinceTick = null;
            this.IsHoldingPosition = false;
            this.PathOwnerTile = null;
        }
    }
}
