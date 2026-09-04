using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;

namespace YuiToIssho;

internal static class OwnerWorkAssistContracts
{
    public const int Radius = 8;
    public const int ReanchorDistance = 2;
    public const int MaximumOwnerDistance = 5;
    public const ulong LeaseTicks = 30;
}

internal readonly record struct OwnerWorkAssistResult(bool IsSuccess, string Code, string Message)
{
    public static OwnerWorkAssistResult Success(string code, string message) => new(true, code, message);

    public static OwnerWorkAssistResult Failure(string code, string message) => new(false, code, message);
}

internal readonly record struct OwnerWorkAssistSnapshot(bool Enabled, string Kind, string State);

internal sealed class CompanionOwnerWorkAssistCoordinator
{
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionWorkCoordinator work;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, AssistRuntime> runtimes = new();
    private Action<CompanionIdentity, string>? startObserver;

    public CompanionOwnerWorkAssistCoordinator(CompanionRegistry registry, CompanionBodyBinder bodies, CompanionWorkCoordinator work, CompanionVitalsCoordinator vitals, IMonitor monitor)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.work = work;
        this.vitals = vitals;
        this.monitor = monitor;
    }

    public void AttachStartObserver(Action<CompanionIdentity, string> observer)
    {
        this.startObserver = observer;
    }

    public OwnerWorkAssistResult Start(CompanionIdentity identity, Farmer owner)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return OwnerWorkAssistResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        if (!identity.IsCanonical)
            return OwnerWorkAssistResult.Failure("SINGLE-COMPANION-PER-OWNER", "Only the Owner's current Yui can enter assist mode.");
        if (!record.WantsBody || !this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return OwnerWorkAssistResult.Failure("ASSIST-BODY-NOT-READY", $"{identity} must be summoned before assist mode starts.");
        if (owner.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return OwnerWorkAssistResult.Failure("ASSIST-OWNER-LOCATION-MISMATCH", "Owner and Yui must be in the same location when assist mode starts.");
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return OwnerWorkAssistResult.Failure("COMPANION-BUSY", $"{identity} must finish or stop transaction {record.ActiveTransactionId} first.");
        if (record.WorkDirective is not null && !record.WorkDirective.IsOwnerAssistLease)
            return OwnerWorkAssistResult.Failure("WORK-DIRECTIVE-ACTIVE", "Stop the current manual work directive before enabling assist mode.");
        if (record.OwnerWorkAssistEnabled)
            return OwnerWorkAssistResult.Success("ASSIST-ALREADY-ENABLED", $"{identity} is already waiting for Owner Axe/Scythe swings.");

        record.OwnerWorkAssistEnabled = true;
        record.Mode = CompanionModes.Follow;
        this.runtimes.Remove(identity);
        this.monitor.Log($"HY-ASSIST-ENABLED: {identity} is waiting for Owner Axe/Scythe use.", LogLevel.Info);
        return OwnerWorkAssistResult.Success("ASSIST-ENABLED", $"{identity} will now help with nearby chopping and mowing when the Owner uses the matching tool.");
    }

    public bool ArmNatural(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record)
            || !identity.IsCanonical
            || !record.WantsBody
            || record.Vitals.State != CompanionVitalStates.Active
            || record.Vitals.Health <= 0
            || record.Vitals.Stamina <= 0f
            || !string.IsNullOrWhiteSpace(record.ActiveTransactionId)
            || record.WorkDirective is not null)
            return false;
        if (record.OwnerWorkAssistEnabled)
            return true;

        record.OwnerWorkAssistEnabled = true;
        record.Mode = CompanionModes.Follow;
        this.runtimes.Remove(identity);
        this.monitor.Log($"HY-ASSIST-NATURAL: {identity} is waiting for Owner Axe/Scythe use as part of the default companion loop.", LogLevel.Info);
        return true;
    }

    public OwnerWorkAssistResult Stop(CompanionIdentity identity, string reason, bool stopOwnedDirective)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return OwnerWorkAssistResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");

        bool wasEnabled = record.OwnerWorkAssistEnabled;
        record.OwnerWorkAssistEnabled = false;
        if (stopOwnedDirective && record.WorkDirective?.IsOwnerAssistLease == true)
            this.work.Stop(identity, reason, useReturnMode: true);
        this.runtimes.Remove(identity);
        if (wasEnabled)
            this.monitor.Log($"HY-ASSIST-DISABLED: {identity} stopped Owner tool assistance ({reason}).", LogLevel.Info);
        return OwnerWorkAssistResult.Success(
            wasEnabled ? "ASSIST-DISABLED" : "ASSIST-ALREADY-DISABLED",
            wasEnabled ? $"{identity} stopped Owner tool assistance." : $"{identity} was not in Owner tool assist mode."
        );
    }

    public OwnerWorkAssistResult Status(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return OwnerWorkAssistResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
        OwnerWorkAssistSnapshot snapshot = this.GetSnapshot(identity);
        return OwnerWorkAssistResult.Success(
            "ASSIST-STATUS",
            $"{identity} assist enabled={snapshot.Enabled.ToString().ToLowerInvariant()} kind={snapshot.Kind} state={snapshot.State}."
        );
    }

    public OwnerWorkAssistSnapshot GetSnapshot(CompanionIdentity identity)
    {
        bool enabled = this.registry.TryGet(identity, out CompanionRecord record) && record.OwnerWorkAssistEnabled;
        if (!enabled)
            return new OwnerWorkAssistSnapshot(false, string.Empty, "Disabled");
        if (this.runtimes.TryGetValue(identity, out AssistRuntime? runtime))
            return new OwnerWorkAssistSnapshot(true, runtime.ActiveKind ?? runtime.DesiredKind ?? string.Empty, runtime.State);
        string kind = record!.WorkDirective?.IsOwnerAssistLease == true ? record.WorkDirective.Kind : string.Empty;
        return new OwnerWorkAssistSnapshot(true, kind, kind.Length > 0 ? "Active" : "Armed");
    }

    public void DisableForOverride(CompanionIdentity identity, string reason)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record) || !record.OwnerWorkAssistEnabled)
            return;
        record.OwnerWorkAssistEnabled = false;
        this.runtimes.Remove(identity);
        this.monitor.Log($"HY-ASSIST-DISABLED: {identity} stopped Owner tool assistance ({reason}).", LogLevel.Info);
    }

    public void ReleaseLeases(string reason)
    {
        foreach (CompanionRecord record in this.registry.Active.Where(candidate => candidate.WorkDirective?.IsOwnerAssistLease == true).ToArray())
        {
            if (string.IsNullOrWhiteSpace(record.ActiveTransactionId))
                this.ReleaseLease(record, reason);
            else
                record.WorkDirective!.SuspendedReason = reason;
        }
        this.runtimes.Clear();
    }

    public void ClearRuntime() => this.runtimes.Clear();

    public void Update(ulong tick)
    {
        CompanionRecord[] enabled = this.registry.Active.Where(record => record.OwnerWorkAssistEnabled).ToArray();
        foreach (CompanionRecord record in enabled)
        {
            Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
            if (OwnerLifecycleGate.CanObserve(owner))
                this.UpdateOne(record, tick);
        }

        HashSet<CompanionIdentity> active = enabled.Select(record => record.Identity).ToHashSet();
        foreach (CompanionIdentity stale in this.runtimes.Keys.Where(identity => !active.Contains(identity)).ToArray())
            this.runtimes.Remove(stale);
    }

    private void UpdateOne(CompanionRecord record, ulong tick)
    {
        AssistRuntime runtime = this.runtimes.TryGetValue(record.Identity, out AssistRuntime? existing)
            ? existing
            : this.runtimes[record.Identity] = new AssistRuntime();
        if (!this.bodies.TryGetBody(record.Identity, out NPC body) || body.currentLocation is null)
        {
            runtime.State = "BodyUnavailable";
            return;
        }
        Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
        {
            if (record.WorkDirective?.IsOwnerAssistLease == true && string.IsNullOrWhiteSpace(record.ActiveTransactionId))
                this.ReleaseLease(record, "OWNER-LOCATION-CHANGED");
            runtime.State = owner is null ? "OwnerOffline" : "WaitingSameLocation";
            return;
        }

        string? observedKind = ResolveObservedKind(owner);
        if (observedKind is not null)
        {
            runtime.DesiredKind = observedKind;
            runtime.LeaseUntilTick = tick + OwnerWorkAssistContracts.LeaseTicks;
            runtime.LocationKey = owner.currentLocation.NameOrUniqueName;
            runtime.AnchorX = owner.TilePoint.X;
            runtime.AnchorY = owner.TilePoint.Y;
        }

        WorkDirectiveRecord? directive = record.WorkDirective;
        if (directive is not null && !directive.IsOwnerAssistLease)
        {
            runtime.ActiveDirectiveId = null;
            runtime.ActiveKind = null;
            runtime.State = "ManualWorkOverride";
            return;
        }
        if (directive?.IsOwnerAssistLease == true)
        {
            runtime.ActiveDirectiveId = directive.DirectiveId;
            runtime.ActiveKind = directive.Kind;
            bool expired = tick > runtime.LeaseUntilTick;
            bool kindChanged = runtime.DesiredKind is not null && runtime.DesiredKind != directive.Kind;
            bool locationChanged = runtime.LocationKey is not null && runtime.LocationKey != directive.LocationKey;
            bool anchorChanged = runtime.LocationKey == directive.LocationKey
                && Math.Abs(runtime.AnchorX - directive.AnchorX) + Math.Abs(runtime.AnchorY - directive.AnchorY) >= OwnerWorkAssistContracts.ReanchorDistance;
            bool ownerTooFar = Math.Abs(body.TilePoint.X - owner.TilePoint.X) + Math.Abs(body.TilePoint.Y - owner.TilePoint.Y)
                > OwnerWorkAssistContracts.MaximumOwnerDistance;
            bool releaseRequested = runtime.PendingReleaseReason is not null
                || expired
                || kindChanged
                || locationChanged
                || anchorChanged
                || ownerTooFar;
            if (releaseRequested)
            {
                string reason = runtime.PendingReleaseReason
                    ?? (expired
                        ? "LEASE-EXPIRED"
                        : kindChanged
                            ? "OWNER-TOOL-CHANGED"
                            : ownerTooFar
                                ? "YUI-OWNER-DISTANCE"
                                : "OWNER-ANCHOR-CHANGED");
                if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
                {
                    runtime.PendingReleaseReason = reason;
                    directive.SuspendedReason = reason;
                    runtime.State = "Reanchoring";
                    return;
                }
                if (reason == "YUI-OWNER-DISTANCE" && !expired)
                {
                    runtime.LocationKey = owner.currentLocation.NameOrUniqueName;
                    runtime.AnchorX = owner.TilePoint.X;
                    runtime.AnchorY = owner.TilePoint.Y;
                }
                this.ReleaseLease(record, reason);
                runtime.PendingReleaseReason = null;
                directive = null;
                runtime.ActiveDirectiveId = null;
                runtime.ActiveKind = null;
            }
            else
            {
                runtime.State = string.IsNullOrWhiteSpace(record.ActiveTransactionId) ? "Active" : "Executing";
                return;
            }
        }

        if (directive is null && runtime.DesiredKind is not null && tick <= runtime.LeaseUntilTick)
        {
            if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            {
                runtime.State = "WaitingForCurrentTask";
                return;
            }
            string actionKind = runtime.DesiredKind == WorkKinds.Chop ? VitalActionKinds.Chopping : VitalActionKinds.Mowing;
            if (!this.vitals.CanStartActionWithoutInterruptingRest(record.Identity, actionKind, out VitalActionResult vitalGate))
            {
                runtime.State = $"Blocked:{vitalGate.Code}";
                return;
            }
            var request = new WorkScopeRequest(
                runtime.LocationKey!,
                runtime.AnchorX,
                runtime.AnchorY,
                WorkScopeShapes.Radius,
                OwnerWorkAssistContracts.Radius,
                runtime.DesiredKind,
                WorkCompletionPolicies.UntilStopped
            );
            string directiveId = Guid.NewGuid().ToString("N");
            WorkDirectiveResult result = this.work.Start(record.Identity, owner, directiveId, request);
            if (!result.IsSuccess || record.WorkDirective is null)
            {
                runtime.State = $"Blocked:{result.Code}";
                return;
            }
            record.WorkDirective.IsOwnerAssistLease = true;
            runtime.ActiveDirectiveId = directiveId;
            runtime.ActiveKind = runtime.DesiredKind;
            runtime.State = "Active";
            this.monitor.Log($"HY-ASSIST-TRIGGERED: {record.Identity} started {runtime.ActiveKind} near Owner at {runtime.LocationKey}:{runtime.AnchorX},{runtime.AnchorY}.", LogLevel.Info);
            try
            {
                this.startObserver?.Invoke(record.Identity, runtime.ActiveKind);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"HY-ASSIST-PRESENTATION-FAILED: {record.Identity} {ex.GetType().Name}.", LogLevel.Warn);
            }
            return;
        }

        runtime.State = "Armed";
    }

    private void ReleaseLease(CompanionRecord record, string reason)
    {
        if (record.WorkDirective?.IsOwnerAssistLease != true)
            return;
        string kind = record.WorkDirective.Kind;
        this.work.Stop(record.Identity, reason, useReturnMode: true);
        this.monitor.Log($"HY-ASSIST-RELEASED: {record.Identity} released {kind} lease ({reason}).", LogLevel.Info);
    }

    private static string? ResolveObservedKind(Farmer owner)
    {
        if (!owner.UsingTool)
            return null;
        if (owner.CurrentTool is Axe)
            return WorkKinds.Chop;
        return owner.CurrentTool is MeleeWeapon weapon && weapon.isScythe() ? WorkKinds.Mow : null;
    }

    private sealed class AssistRuntime
    {
        public string? DesiredKind { get; set; }
        public string? ActiveKind { get; set; }
        public string? ActiveDirectiveId { get; set; }
        public string? LocationKey { get; set; }
        public int AnchorX { get; set; }
        public int AnchorY { get; set; }
        public ulong LeaseUntilTick { get; set; }
        public string? PendingReleaseReason { get; set; }
        public string State { get; set; } = "Armed";
    }
}
