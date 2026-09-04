using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Pathfinding;
using StardewValley.Tools;

namespace YuiToIssho;

internal readonly record struct CombatCommandResult(bool IsSuccess, string Code, string Message)
{
    public static CombatCommandResult Success(string code, string message) => new(true, code, message);
    public static CombatCommandResult Failure(string code, string message) => new(false, code, message);
}

internal readonly record struct CombatOption(
    string CombatOptionId,
    string MonsterKind,
    string DistanceBand,
    string ThreatBand,
    bool CanIsolate,
    int ExpiresInSeconds);

internal readonly record struct CombatOptionsResult(bool IsSuccess, string Code, string Message, IReadOnlyList<CombatOption> Options)
{
    public static CombatOptionsResult Success(IReadOnlyList<CombatOption> options) =>
        new(true, options.Count == 0 ? "COMBAT-OPTIONS-EMPTY" : "COMBAT-OPTIONS", $"Host issued {options.Count} bounded combat option(s).", options);

    public static CombatOptionsResult Failure(string code, string message) =>
        new(false, code, message, Array.Empty<CombatOption>());
}

internal readonly record struct CombatRuntimeSnapshot(
    string Mode,
    string Phase,
    int RemainingSeconds,
    int CommittedSwings,
    int MaximumSwings,
    string TargetKind,
    string TargetDistanceBand,
    string LastOutcome);

internal sealed class CombatCoordinator
{
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 18;
    private const int StuckSampleLimit = 10;
    private const int MaximumPathAttempts = 5;
    private const int MaximumTaskUpdates = 300;
    private const int AttackSearchRadius = 2;
    private const int VanillaSwingFrame = 0;
    private const int VanillaSwingPower = 1;
    private const int CombatPolicyVersion = 1;
    private const int MaximumOptions = 8;
    private const int MinimumOptionRadius = 1;
    private const int MaximumOptionRadius = 8;
    private const ulong OptionLifetimeTicks = 600;
    private const int MaximumHitSetSize = 8;
    private const int MinimumGuardSeconds = 5;
    private const int MaximumGuardSeconds = 60;
    private const int MinimumGuardSwings = 1;
    private const int MaximumGuardSwings = 30;
    private const int CounterStrikeRadius = 4;
    private const int DamageEventHistoryLimit = 128;
    private const ulong GuardTargetCooldownTicks = 60;

    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, CombatTask> tasks = new();
    private readonly Dictionary<string, CachedCombatOption> options = new(StringComparer.Ordinal);
    private readonly Dictionary<CompanionIdentity, GuardDirective> guards = new();
    private readonly HashSet<string> observedDamageEvents = new(StringComparer.Ordinal);
    private readonly Queue<string> damageEventOrder = new();
    private ulong hostTick;

    public CombatCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public CombatCommandResult TryStart(CompanionIdentity identity, int tileX, int tileY, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return CombatCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before fighting.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return CombatCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when combat starts.");

        MeleeWeapon? weapon = this.inventories.FindFirst<MeleeWeapon>(identity, IsSupportedWeapon);
        if (weapon is null)
            return CombatCommandResult.Failure("COMBAT-WEAPON-REQUIRED", "A real non-scythe sword, dagger, or club in this Yui's bag is required.");

        Rectangle requestedTile = new(tileX * Game1.tileSize, tileY * Game1.tileSize, Game1.tileSize, Game1.tileSize);
        Monster[] matches = owner.currentLocation.characters.OfType<Monster>()
            .Where(monster => IsLiveTarget(monster, owner.currentLocation) && monster.GetBoundingBox().Intersects(requestedTile))
            .ToArray();
        if (matches.Length == 0)
            return CombatCommandResult.Failure("MONSTER-NOT-FOUND", $"No living visible Monster occupies tile {tileX},{tileY}.");
        if (matches.Length > 1)
            return CombatCommandResult.Failure("MONSTER-TARGET-AMBIGUOUS", $"More than one Monster overlaps tile {tileX},{tileY}; choose a tile that identifies exactly one.");

        Monster target = matches[0];
        return this.StartExact(identity, owner, body, target, weapon, operationId);
    }

    public CombatOptionsResult GetOptions(CompanionIdentity identity, int radius)
    {
        if (radius is < MinimumOptionRadius or > MaximumOptionRadius)
            return CombatOptionsResult.Failure("COMBAT-RADIUS-INVALID", $"Combat option radius must be {MinimumOptionRadius}..{MaximumOptionRadius} tiles.");
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return CombatOptionsResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before listing combat options.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return CombatOptionsResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location.");
        MeleeWeapon? weapon = this.inventories.FindFirst<MeleeWeapon>(identity, IsSupportedWeapon);
        if (weapon is null)
            return CombatOptionsResult.Failure("COMBAT-WEAPON-REQUIRED", "A real non-scythe sword, dagger, or club in this Yui's bag is required.");
        if (!this.bodies.TryGetBodyGeneration(identity, out ulong bodyGeneration))
            return CombatOptionsResult.Failure("BODY-GENERATION-UNAVAILABLE", "The authoritative body generation is unavailable.");

        this.RemoveExpiredOptions();
        foreach (string stale in this.options.Where(pair => pair.Value.Identity == identity).Select(pair => pair.Key).ToArray())
            this.options.Remove(stale);

        Vector2 origin = body.StandingPixel.ToVector2();
        float maximumDistance = radius * Game1.tileSize;
        List<(Monster Monster, float Distance, int Index)> candidates = body.currentLocation.characters.OfType<Monster>()
            .Select((monster, index) => (Monster: monster, Distance: Vector2.Distance(origin, Center(monster.GetBoundingBox())), Index: index))
            .Where(candidate => IsLiveTarget(candidate.Monster, body.currentLocation)
                && !candidate.Monster.isInvincible()
                && candidate.Distance <= maximumDistance)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Monster.TilePoint.Y)
            .ThenBy(candidate => candidate.Monster.TilePoint.X)
            .ThenBy(candidate => candidate.Index)
            .Take(MaximumOptions)
            .ToList();

        List<CombatOption> issued = new(candidates.Count);
        foreach ((Monster monster, float distance, _) in candidates)
        {
            string optionId = Guid.NewGuid().ToString("N");
            bool canIsolate = CanIsolate(body, owner, body.currentLocation, monster, weapon);
            var option = new CombatOption(
                optionId,
                BoundedMonsterKind(monster),
                DistanceBand(distance),
                ThreatBand(monster, owner, body),
                canIsolate,
                10);
            this.options.Add(optionId, new CachedCombatOption(
                option,
                identity,
                body,
                bodyGeneration,
                body.currentLocation,
                monster,
                weapon,
                new WeaponFingerprint(weapon, CombatPolicyVersion),
                this.hostTick + OptionLifetimeTicks));
            issued.Add(option);
        }
        return CombatOptionsResult.Success(issued);
    }

    public CombatCommandResult TryStartOption(CompanionIdentity identity, string optionId, string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);
        this.RemoveExpiredOptions();
        if (!this.options.Remove(optionId, out CachedCombatOption? cached) || cached.Identity != identity)
            return CombatCommandResult.Failure("COMBAT-SELECTION-EXPIRED", "The combat option is unknown, expired, or belongs to another companion.");
        if (this.hostTick > cached.ExpiresAtTick
            || !this.bodies.TryGetBody(identity, out NPC body)
            || !ReferenceEquals(body, cached.Body)
            || !this.bodies.TryGetBodyGeneration(identity, out ulong generation)
            || generation != cached.BodyGeneration
            || !ReferenceEquals(body.currentLocation, cached.Location))
            return CombatCommandResult.Failure("COMBAT-SELECTION-EXPIRED", "The body, location, or option generation changed.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, cached.Location))
            return CombatCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must still share the option location.");
        if (!IsLiveTarget(cached.Target, cached.Location) || cached.Target.isInvincible())
            return CombatCommandResult.Failure("COMBAT-TARGET-INVALID", "The option's exact Monster is no longer a valid target.");
        if (!this.inventories.ContainsExact(identity, cached.Weapon)
            || !IsSupportedWeapon(cached.Weapon)
            || new WeaponFingerprint(cached.Weapon, CombatPolicyVersion) != cached.WeaponFingerprint)
            return CombatCommandResult.Failure("COMBAT-WEAPON-CHANGED", "The option's exact weapon or policy fingerprint changed.");
        if (!cached.Option.CanIsolate || !CanIsolate(body, owner, cached.Location, cached.Target, cached.Weapon))
            return CombatCommandResult.Failure("COMBAT-TARGET-NOT-ISOLATED", "No legal SingleStrike position currently isolates the selected Monster.");
        return this.StartExact(identity, owner, body, cached.Target, cached.Weapon, operationId);
    }

    private CombatCommandResult StartExact(CompanionIdentity identity, Farmer owner, NPC body, Monster target, MeleeWeapon weapon, string operationId)
    {
        TaskTargetKey targetKey = TargetKey(owner.currentLocation, target);
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "Combat", targetKey);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new CombatTask(begin.Session, target, owner.currentLocation, owner, weapon, new WeaponFingerprint(weapon, CombatPolicyVersion), body.Position));
        this.monitor.Log($"HY-FIGHT-STARTED: {identity} reserved exact Monster '{target.Name}' for operation {operationId}.", LogLevel.Info);
        return CombatCommandResult.Success("STARTED", $"Combat operation {operationId} is pursuing the reserved {target.displayName}.");
    }

    public CombatCommandResult TryStartGuard(CompanionIdentity identity, int radius, int seconds, int maximumSwings, string directiveId)
    {
        if (radius is < MinimumOptionRadius or > MaximumOptionRadius)
            return CombatCommandResult.Failure("COMBAT-RADIUS-INVALID", $"Guard radius must be {MinimumOptionRadius}..{MaximumOptionRadius} tiles.");
        if (seconds is < MinimumGuardSeconds or > MaximumGuardSeconds)
            return CombatCommandResult.Failure("COMBAT-DURATION-INVALID", $"Guard duration must be {MinimumGuardSeconds}..{MaximumGuardSeconds} seconds.");
        if (maximumSwings is < MinimumGuardSwings or > MaximumGuardSwings)
            return CombatCommandResult.Failure("COMBAT-SWING-BUDGET-INVALID", $"Guard maximumSwings must be {MinimumGuardSwings}..{MaximumGuardSwings}.");
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return CombatCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before guarding.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return CombatCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must share the Guard location.");
        if (this.inventories.FindFirst<MeleeWeapon>(identity, IsSupportedWeapon) is null)
            return CombatCommandResult.Failure("COMBAT-WEAPON-REQUIRED", "A real non-scythe sword, dagger, or club in this Yui's bag is required.");
        TaskDirectiveBeginResult begin = this.execution.TryBeginDirective(identity, directiveId, "CombatGuard");
        if (!begin.Started)
            return FromExecution(begin.Result);

        var guard = new GuardDirective(identity, directiveId, body.currentLocation, owner, body.StandingPixel.ToVector2(), radius, this.hostTick + (ulong)(seconds * 60), maximumSwings, begin.ResumeMode);
        this.guards.Add(identity, guard);
        this.monitor.Log($"HY-COMBAT-GUARD-STARTED: {identity} radius={radius}, seconds={seconds}, maximumSwings={maximumSwings}.", LogLevel.Info);
        return CombatCommandResult.Success("STARTED", $"Guard {directiveId} started for {seconds}s within radius {radius}, with at most {maximumSwings} swing(s).");
    }

    public CombatCommandResult Status(CompanionIdentity identity)
    {
        if (this.guards.TryGetValue(identity, out GuardDirective? guard))
        {
            ulong remainingTicks = guard.ExpiresAtTick > this.hostTick ? guard.ExpiresAtTick - this.hostTick : 0;
            string phase = this.tasks.ContainsKey(identity) ? "exchange" : "observing";
            return CombatCommandResult.Success("COMBAT-GUARD-ACTIVE", $"Guard {guard.DirectiveId} is {phase}; remainingSeconds={(remainingTicks + 59) / 60}, swings={guard.CommittedSwings}/{guard.MaximumSwings}, last={guard.LastReason}.");
        }
        if (this.tasks.TryGetValue(identity, out CombatTask? task))
            return CombatCommandResult.Success("COMBAT-EXCHANGE-ACTIVE", $"{task.Source} exchange {task.OperationId} is {task.Session.Phase} against {BoundedMonsterKind(task.Target)}.");
        return CombatCommandResult.Success("COMBAT-IDLE", $"{identity} has no active combat directive or exchange.");
    }

    public CombatRuntimeSnapshot? GetSnapshot(CompanionIdentity identity)
    {
        this.tasks.TryGetValue(identity, out CombatTask? task);
        if (this.guards.TryGetValue(identity, out GuardDirective? guard))
        {
            int remaining = (int)Math.Min(60UL, guard.ExpiresAtTick > this.hostTick ? (guard.ExpiresAtTick - this.hostTick + 59) / 60 : 0);
            return new CombatRuntimeSnapshot(
                "GuardArea",
                task?.Session.Phase.ToString() ?? (guard.LastReason == "COMBAT-BLOCKED" ? "Blocked" : "Observing"),
                remaining,
                guard.CommittedSwings,
                guard.MaximumSwings,
                task is null ? string.Empty : BoundedMonsterKind(task.Target),
                task is null || !this.bodies.TryGetBody(identity, out NPC body) ? string.Empty : DistanceBand(Vector2.Distance(body.StandingPixel.ToVector2(), Center(task.Target.GetBoundingBox()))),
                guard.LastReason);
        }
        if (task is null)
            return null;
        return new CombatRuntimeSnapshot(
            task.Source,
            task.Session.Phase.ToString(),
            0,
            task.SubmissionAttempted ? 1 : 0,
            1,
            BoundedMonsterKind(task.Target),
            this.bodies.TryGetBody(identity, out NPC exchangeBody) ? DistanceBand(Vector2.Distance(exchangeBody.StandingPixel.ToVector2(), Center(task.Target.GetBoundingBox()))) : string.Empty,
            string.Empty);
    }

    public CombatCommandResult ObserveDamage(CompanionIdentity identity, Monster attacker, string damageEventId)
    {
        if (!this.observedDamageEvents.Add(damageEventId))
            return CombatCommandResult.Failure("COMBAT-COUNTER-DUPLICATE", $"Damage event {damageEventId} was already consumed.");
        this.damageEventOrder.Enqueue(damageEventId);
        while (this.damageEventOrder.Count > DamageEventHistoryLimit)
            this.observedDamageEvents.Remove(this.damageEventOrder.Dequeue());

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return CombatCommandResult.Failure("BODY-UNAVAILABLE", "CounterStrike requires the authoritative Yui body.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return CombatCommandResult.Failure("OWNER-LOCATION-MISMATCH", "CounterStrike requires Owner and Yui in the same location.");
        if (!IsLiveTarget(attacker, body.currentLocation) || attacker.isInvincible())
            return CombatCommandResult.Failure("COMBAT-ATTACKER-UNRESOLVED", "The exact damage attacker is no longer a living, damageable Monster in the same location.");
        Vector2 defenseAnchor = body.StandingPixel.ToVector2();
        if (!IntersectsScope(attacker.GetBoundingBox(), defenseAnchor, CounterStrikeRadius))
            return CombatCommandResult.Failure("COMBAT-COUNTER-EXPIRED", "The exact attacker left the bounded CounterStrike radius.");
        if (!this.vitals.CanSafelyCounter(identity, out VitalActionResult vitalGate))
            return CombatCommandResult.Failure(vitalGate.Code, vitalGate.Message);
        MeleeWeapon? weapon = this.inventories.FindFirst<MeleeWeapon>(identity, IsSupportedWeapon);
        if (weapon is null)
            return CombatCommandResult.Failure("COMBAT-WEAPON-NOT-ALLOWED", "CounterStrike requires a policy-allowed melee weapon in Yui's bag.");

        GuardDirective? parentGuard = this.guards.GetValueOrDefault(identity);
        if (this.tasks.TryGetValue(identity, out CombatTask? current))
        {
            if (current.Session.Phase == TaskSessionPhase.Settling)
                return CombatCommandResult.Failure("COMBAT-SETTLEMENT-UNCERTAIN", "An existing swing already owns the no-retry settlement boundary.");
            this.Complete(current, "COMBAT-COUNTER-PREEMPTED", "A verified damage event preempted the uncommitted exchange.", false);
        }

        string operationId = $"counter-{damageEventId}";
        TaskBeginResult begin = parentGuard is null
            ? this.execution.TryBegin(identity, operationId, "CombatCounterStrike", TargetKey(body.currentLocation, attacker))
            : this.execution.TryBeginChild(identity, operationId, parentGuard.DirectiveId, "CombatCounterStrike", TargetKey(body.currentLocation, attacker));
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);
        this.tasks.Add(identity, new CombatTask(begin.Session, attacker, body.currentLocation, owner, weapon, new WeaponFingerprint(weapon, CombatPolicyVersion), body.Position, "CounterStrike", parentGuard, enforceGuardScope: false, defenseAnchorPixel: defenseAnchor));
        this.monitor.Log($"HY-COMBAT-COUNTER-STARTED: {identity} consumed verified event {damageEventId} for one bounded strike.", LogLevel.Info);
        return CombatCommandResult.Success("STARTED", $"CounterStrike {operationId} is pursuing only the verified attacker, at most once.");
    }

    public void Update(ulong tick)
    {
        this.hostTick = tick;
        this.RemoveExpiredOptions();
        foreach (CombatTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
        foreach (GuardDirective guard in this.guards.Values.ToArray())
            this.UpdateGuard(guard);
    }

    public CombatCommandResult Cancel(CompanionIdentity identity, string code)
    {
        bool cancelledExchange = false;
        if (this.tasks.TryGetValue(identity, out CombatTask? task))
        {
            this.Complete(task, code, $"Combat exchange {task.OperationId} was cancelled before its one permitted swing.", false);
            cancelledExchange = true;
        }
        if (this.guards.TryGetValue(identity, out GuardDirective? guard))
            return this.CompleteGuard(guard, code, $"Guard {guard.DirectiveId} stopped before any further attack submission.", false);
        return cancelledExchange
            ? CombatCommandResult.Success(code, $"{identity} combat exchange was stopped.")
            : CombatCommandResult.Success("ALREADY-IDLE", $"{identity} has no offensive-combat task.");
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out CombatTask? task) ? task.Weapon : null;

    public void CancelAll(string code)
    {
        foreach (CompanionIdentity identity in this.tasks.Keys.Concat(this.guards.Keys).Distinct().ToArray())
            this.Cancel(identity, code);
        this.observedDamageEvents.Clear();
        this.damageEventOrder.Clear();
    }

    private void UpdateOne(CombatTask task, ulong tick)
    {
        if (!OwnerLifecycleGate.CanAdvance(task.Owner))
            return;
        if (!this.execution.IsCurrent(task.Session))
        {
            this.execution.AbandonRuntime(task.Session);
            this.tasks.Remove(task.Identity);
            return;
        }
        if (++task.UpdateCount > MaximumTaskUpdates)
        {
            this.Complete(task, "COMBAT-BUDGET-EXHAUSTED", "The moving Monster was not safely reachable within the bounded combat session.", false);
            return;
        }
        if (!this.bodies.TryGetBody(task.Identity, out NPC body) || body.currentLocation is null || !ReferenceEquals(body.currentLocation, task.Location))
        {
            this.Complete(task, "BODY-INVALID", "The companion body became unavailable or changed location.", false);
            return;
        }
        if (task.Owner.currentLocation is null || !ReferenceEquals(task.Owner.currentLocation, task.Location))
        {
            this.Complete(task, "OWNER-LEFT-LOCATION", "The owner left the combat location.", false);
            return;
        }
        if (!IsExactWeaponAvailable(task))
        {
            this.Complete(task, "COMBAT-WEAPON-CHANGED", "The exact reserved non-scythe weapon left inventory or changed type.", false);
            return;
        }
        if (!IsLiveTarget(task.Target, task.Location))
        {
            this.Complete(task, "COMBAT-TARGET-INVALID", "The exact reserved Monster died, disappeared, or became invalid before the swing.", false);
            return;
        }
        if (task.Guard is not null && task.EnforceGuardScope && !IntersectsScope(task.Target.GetBoundingBox(), task.Guard.AnchorPixel, task.Guard.Radius))
        {
            this.Complete(task, "COMBAT-TARGET-OUT-OF-SCOPE", "The exact Guard target left the fixed scope before submission.", false);
            return;
        }
        if (task.DefenseAnchorPixel is Vector2 defenseAnchor
            && !IntersectsScope(task.Target.GetBoundingBox(), defenseAnchor, CounterStrikeRadius))
        {
            this.Complete(task, "COMBAT-COUNTER-EXPIRED", "The verified attacker left the bounded CounterStrike radius before submission.", false);
            return;
        }
        if (task.Target.Tile != task.LastTargetTile)
        {
            task.LastTargetTile = task.Target.Tile;
            task.NextPathTick = tick;
            task.StuckSamples = 0;
            this.bodies.Halt(task.Identity);
        }

        if (body.controller is null
            && TryCreateAttackSetup(task, body.Position, requireIsolated: !task.EnforceGuardScope, out AttackSetup currentSetup)
            && IsSetupAllowed(task, currentSetup))
        {
            this.bodies.Halt(task.Identity);
            this.Settle(task, body, currentSetup);
            return;
        }

        this.TrackProgress(task, body, tick);
        if (!this.tasks.ContainsKey(task.Identity) || tick < task.NextPathTick || body.controller is not null)
            return;

        Vector2? attackTile = FindAttackTile(task, body);
        if (attackTile is null)
        {
            task.PathAttempts++;
            if (task.PathAttempts >= MaximumPathAttempts)
                this.Complete(task, "COMBAT-NO-SAFE-POSITION", "No open attack tile satisfied the bounded combat geometry and safety policy.", false);
            else
                task.NextPathTick = tick + (ulong)(RepathDelayTicks * task.PathAttempts);
            return;
        }

        int facing = FacingToward(attackTile.Value * Game1.tileSize, Center(task.Target.GetBoundingBox()));
        body.controller = CompanionPathing.CreateController(body, task.Location, attackTile.Value.ToPoint(), facing, PathSearchLimit);
        task.NextPathTick = tick + RepathDelayTicks;
    }

    private void TrackProgress(CombatTask task, NPC body, ulong tick)
    {
        if (body.Position != task.LastPosition)
        {
            task.LastPosition = body.Position;
            task.StuckSamples = 0;
            return;
        }
        if (++task.StuckSamples < StuckSampleLimit)
            return;
        task.StuckSamples = 0;
        task.PathAttempts++;
        this.bodies.Halt(task.Identity);
        if (task.PathAttempts >= MaximumPathAttempts)
            this.Complete(task, "COMBAT-BUDGET-EXHAUSTED", "The reserved Monster stayed unreachable through the bounded path budget.", false);
        else
            task.NextPathTick = tick + (ulong)(RepathDelayTicks * task.PathAttempts);
    }

    private void Settle(CombatTask task, NPC body, AttackSetup setup)
    {
        Farmer engineActor = task.Owner;
        if (!OwnerContextLease.CanProject(engineActor))
            return;

        if (!ValidateSettlement(task, body, setup, out string failure))
        {
            this.Complete(task, "COMBAT-TARGET-INVALID", failure, false);
            return;
        }
        TaskExecutionResult reservation = this.execution.TryReplaceTargets(task.Session, setup.HitSet.Select(monster => TargetKey(task.Location, monster)).ToArray());
        if (!reservation.IsSuccess)
        {
            this.Complete(task, "COMBAT-TARGET-RESERVED", reservation.Message, false);
            return;
        }
        if (!task.Session.TryEnterSettlement())
            return;
        this.inventories.RequestTransfer(
            task.Identity,
            () => this.SettleLocked(task, body, setup, engineActor),
            result =>
            {
                if (!this.tasks.TryGetValue(task.Identity, out CombatTask? current) || !ReferenceEquals(current, task))
                    return;
                this.Complete(task, result.Code, result.Message, result.IsSuccess);
            });
    }

    private InventoryActionResult SettleLocked(CombatTask task, NPC expectedBody, AttackSetup setup, Farmer engineActor)
    {
        if (!this.execution.IsCurrent(task.Session)
            || !this.bodies.TryGetBody(task.Identity, out NPC body)
            || !ReferenceEquals(body, expectedBody)
            || !OwnerContextLease.CanProject(engineActor))
            return InventoryActionResult.Failure("COMBAT-TARGET-INVALID", "The combat task, body, or owner changed while the bag lock was pending.");
        if (!ValidateSettlement(task, body, setup, out string failure))
            return InventoryActionResult.Failure("COMBAT-TARGET-INVALID", failure);

        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, VitalActionKinds.Combat, $"{task.OperationId}:swing");
        if (!cost.IsSuccess)
        {
            string costCode = cost.Result.Code == "ENERGY-INSUFFICIENT" ? "COMBAT-ENERGY-INSUFFICIENT" : cost.Result.Code;
            return InventoryActionResult.Failure(costCode, cost.Result.Message);
        }

        Dictionary<Monster, int> healthBefore = setup.HitSet.ToDictionary(monster => monster, monster => monster.Health);
        string visualKind = task.Weapon.type.Value switch { var type when type == MeleeWeapon.dagger => AppearanceActionKinds.CombatDagger, var type when type == MeleeWeapon.club => AppearanceActionKinds.CombatClub, _ => AppearanceActionKinds.CombatSword };
        body.faceDirection(setup.Facing);
        this.appearance.Prepare(task.Identity, task.OperationId, visualKind, task.Weapon, setup.Facing);
        bool submitted = false;
        Exception? settlementError = null;
        WorldDebrisCapture worldDrops = WorldDebrisCapture.Begin(task.Location, Game1.currentLocation);
        try
        {
            body.Position = setup.BodyPosition;
            using OwnerContextLease context = OwnerContextLease.Project(engineActor, setup.BodyPosition, setup.Facing, task.Location);
            engineActor.FarmerSprite.currentAnimationIndex = VanillaSwingFrame;
            Vector2 toolLocation = engineActor.GetToolLocation(ignoreClick: true);
            submitted = true;
            task.SubmissionAttempted = true;
            this.inventories.RunWithMeleeWeaponSelected(
                task.Identity,
                engineActor,
                task.Weapon,
                () => task.Weapon.DoDamage(task.Location, (int)toolLocation.X, (int)toolLocation.Y, setup.Facing, VanillaSwingPower, engineActor));
        }
        catch (Exception ex)
        {
            settlementError = ex;
        }

        if (submitted)
            cost.Commit();
        WorldDebrisRouteResult worldResult = worldDrops.RouteNewLocked(task.Identity, this.inventories);
        if (!worldResult.Result.IsSuccess)
            return worldResult.Result;
        if (settlementError is not null)
        {
            string code = submitted ? "COMBAT-SETTLEMENT-UNCERTAIN" : "SETTLEMENT-ERROR";
            return InventoryActionResult.Failure(code, $"The one permitted vanilla swing stopped without retry after an error: {settlementError.Message}");
        }

        int defeatedCount = setup.HitSet.Count(monster => !task.Location.characters.Any(character => ReferenceEquals(character, monster)) || monster.Health <= 0);
        int damagedCount = setup.HitSet.Count(monster => monster.Health < healthBefore[monster]);
        if (defeatedCount == 0 && damagedCount == 0)
            return InventoryActionResult.Failure("COMBAT-VANILLA-NO-DAMAGE", $"Vanilla applied no verifiable damage to the complete {setup.HitSet.Length}-Monster Hit Set; the operation is terminal and was not retried.");

        this.appearance.Commit(task.Identity, task.OperationId);
        return InventoryActionResult.Success("COMMITTED", $"One real {WeaponKind(task.Weapon)} swing committed once; affected={setup.HitSet.Length}, damaged={damagedCount}, defeated={defeatedCount}, routedDrops={worldResult.StackCount}.");
    }

    private bool ValidateSettlement(CombatTask task, NPC body, AttackSetup setup, out string failure)
    {
        if (!IsExactWeaponAvailable(task) || !ReferenceEquals(body.currentLocation, task.Location) || !ReferenceEquals(task.Owner.currentLocation, task.Location))
        {
            failure = "Owner, body, location, or exact weapon changed immediately before the swing.";
            return false;
        }
        if (!IsLiveTarget(task.Target, task.Location) || task.Target.isInvincible())
        {
            failure = "The exact Monster is no longer a living, visible, damageable target.";
            return false;
        }
        if (body.Position != setup.BodyPosition
            || !TryCreateAttackSetup(task, setup.BodyPosition, requireIsolated: !task.EnforceGuardScope, out AttackSetup refreshed)
            || refreshed.Facing != setup.Facing
            || refreshed.Area != setup.Area
            || refreshed.ToolPixel != setup.ToolPixel
            || !refreshed.HitSet.SequenceEqual(setup.HitSet)
            || setup.HitSet.Where((monster, index) => monster.Health != setup.PreHealth[index]).Any()
            || !IsSetupAllowed(task, refreshed))
        {
            failure = "The Monster or vanilla hit rectangle changed before submission.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private CombatCommandResult Complete(CombatTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        if (task.Guard is not null && this.guards.TryGetValue(task.Identity, out GuardDirective? guard) && ReferenceEquals(guard, task.Guard))
        {
            if (task.SubmissionAttempted)
                guard.CommittedSwings++;
            else if (code is "COMBAT-NO-SAFE-POSITION" or "COMBAT-BUDGET-EXHAUSTED" or "COMBAT-TARGET-RESERVED")
                guard.TargetCooldowns[task.Target] = this.hostTick + GuardTargetCooldownTicks;
            guard.LastReason = code;
        }
        this.monitor.Log($"HY-FIGHT-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
    }

    private Vector2? FindAttackTile(CombatTask task, NPC body)
    {
        Rectangle targetBounds = task.Target.GetBoundingBox();
        int minimumX = Math.Max(0, (targetBounds.Left / Game1.tileSize) - AttackSearchRadius);
        int maximumX = (targetBounds.Right / Game1.tileSize) + AttackSearchRadius;
        int minimumY = Math.Max(0, (targetBounds.Top / Game1.tileSize) - AttackSearchRadius);
        int maximumY = (targetBounds.Bottom / Game1.tileSize) + AttackSearchRadius;

        List<Vector2> candidates = new();
        for (int x = minimumX; x <= maximumX; x++)
        {
            for (int y = minimumY; y <= maximumY; y++)
            {
                Vector2 tile = new(x, y);
                if (!CompanionPathing.IsStandable(body, task.Location, tile))
                    continue;
                Vector2 projectedPosition = tile * Game1.tileSize;
                if (ProjectedBodyOverlapsCharacter(task, body, projectedPosition))
                    continue;
                if (task.Guard is not null && task.EnforceGuardScope
                    && !PointInScope(projectedPosition + new Vector2(Game1.tileSize / 2f), task.Guard.AnchorPixel, task.Guard.Radius))
                    continue;
                if (task.DefenseAnchorPixel is Vector2 defenseAnchor
                    && !PointInScope(projectedPosition + new Vector2(Game1.tileSize / 2f), defenseAnchor, CounterStrikeRadius))
                    continue;
                if (TryCreateAttackSetup(task, projectedPosition, requireIsolated: !task.EnforceGuardScope, out AttackSetup candidateSetup)
                    && IsSetupAllowed(task, candidateSetup)
                    && this.navigation.CanReach(body, task.Location, tile, candidateSetup.Facing, PathSearchLimit))
                    candidates.Add(tile);
            }
        }

        return candidates.OrderBy(tile => ManhattanDistance(tile.ToPoint(), body.TilePoint)).Cast<Vector2?>().FirstOrDefault();
    }

    private static bool ProjectedBodyOverlapsCharacter(CombatTask task, NPC body, Vector2 projectedPosition)
    {
        Rectangle projectedBounds = body.GetBoundingBox();
        projectedBounds.Offset((int)(projectedPosition.X - body.Position.X), (int)(projectedPosition.Y - body.Position.Y));
        return task.Location.characters.Any(character => !ReferenceEquals(character, body) && character.GetBoundingBox().Intersects(projectedBounds));
    }

    private static bool TryCreateAttackSetup(CombatTask task, Vector2 projectedOwnerPosition, bool requireIsolated, out AttackSetup setup) =>
        TryCreateAttackSetup(task.Owner, task.Location, task.Target, task.Weapon, projectedOwnerPosition, requireIsolated, out setup);

    private static bool TryCreateAttackSetup(Farmer owner, GameLocation location, Monster target, MeleeWeapon weapon, Vector2 projectedOwnerPosition, bool requireIsolated, out AttackSetup setup)
    {
        Rectangle targetBounds = target.GetBoundingBox();
        Vector2 targetCenter = Center(targetBounds);
        Vector2 projectedStandingPixel = projectedOwnerPosition + (owner.StandingPixel.ToVector2() - owner.Position);
        int facing = FacingToward(projectedStandingPixel, targetCenter);
        Rectangle ownerBounds = owner.GetBoundingBox();
        ownerBounds.Offset((int)(projectedOwnerPosition.X - owner.Position.X), (int)(projectedOwnerPosition.Y - owner.Position.Y));
        if (ownerBounds.Intersects(targetBounds))
        {
            setup = default;
            return false;
        }
        Vector2 toolPixel = ProjectToolLocation(owner, projectedOwnerPosition, facing);
        Vector2 firstTile = Vector2.Zero;
        Vector2 secondTile = Vector2.Zero;
        Rectangle area = weapon.getAreaOfEffect((int)toolPixel.X, (int)toolPixel.Y, facing, ref firstTile, ref secondTile, ownerBounds, VanillaSwingFrame);

        Monster[] eligible = location.characters.OfType<Monster>()
            .Where(monster => monster.Health > 0 && !monster.IsInvisible && monster.TakesDamageFromHitbox(area))
            .ToArray();
        if (!eligible.Any(monster => ReferenceEquals(monster, target))
            || (requireIsolated && (eligible.Length != 1 || !ReferenceEquals(eligible[0], target))))
        {
            setup = default;
            return false;
        }

        setup = new AttackSetup(projectedOwnerPosition, area, facing, toolPixel.ToPoint(), eligible, eligible.Select(monster => monster.Health).ToArray());
        return true;
    }

    private bool CanIsolate(NPC body, Farmer owner, GameLocation location, Monster target, MeleeWeapon weapon)
    {
        Rectangle targetBounds = target.GetBoundingBox();
        int minimumX = Math.Max(0, (targetBounds.Left / Game1.tileSize) - AttackSearchRadius);
        int maximumX = (targetBounds.Right / Game1.tileSize) + AttackSearchRadius;
        int minimumY = Math.Max(0, (targetBounds.Top / Game1.tileSize) - AttackSearchRadius);
        int maximumY = (targetBounds.Bottom / Game1.tileSize) + AttackSearchRadius;
        for (int x = minimumX; x <= maximumX; x++)
        {
            for (int y = minimumY; y <= maximumY; y++)
            {
                Vector2 tile = new(x, y);
                if (!CompanionPathing.IsStandable(body, location, tile))
                    continue;
                Vector2 position = tile * Game1.tileSize;
                Rectangle projectedBounds = body.GetBoundingBox();
                projectedBounds.Offset((int)(position.X - body.Position.X), (int)(position.Y - body.Position.Y));
                if (location.characters.Any(character => !ReferenceEquals(character, body) && character.GetBoundingBox().Intersects(projectedBounds)))
                    continue;
                if (TryCreateAttackSetup(owner, location, target, weapon, position, requireIsolated: true, out AttackSetup setup)
                    && this.navigation.CanReach(body, location, tile, setup.Facing, PathSearchLimit))
                    return true;
            }
        }
        return false;
    }

    private void UpdateGuard(GuardDirective guard)
    {
        if (!this.guards.ContainsKey(guard.Identity) || this.tasks.ContainsKey(guard.Identity))
            return;
        if (!OwnerLifecycleGate.CanAdvance(guard.Owner))
            return;
        if (this.hostTick >= guard.ExpiresAtTick || guard.CommittedSwings >= guard.MaximumSwings)
        {
            this.CompleteGuard(guard, "COMBAT-BUDGET-EXHAUSTED", $"Guard {guard.DirectiveId} reached its bounded time or swing budget.", true);
            return;
        }
        if (!this.bodies.TryGetBody(guard.Identity, out NPC body)
            || !ReferenceEquals(body.currentLocation, guard.Location)
            || !ReferenceEquals(guard.Owner.currentLocation, guard.Location))
        {
            this.CompleteGuard(guard, "COMBAT-CANCELLED", "Guard stopped because Owner, body, or location changed.", false);
            return;
        }
        MeleeWeapon? weapon = this.inventories.FindFirst<MeleeWeapon>(guard.Identity, IsSupportedWeapon);
        if (weapon is null)
        {
            this.CompleteGuard(guard, "COMBAT-WEAPON-NOT-ALLOWED", "Guard stopped because no policy-allowed melee weapon remains in Yui's bag.", false);
            return;
        }

        foreach (Monster expired in guard.TargetCooldowns.Where(pair => this.hostTick >= pair.Value || !IsLiveTarget(pair.Key, guard.Location)).Select(pair => pair.Key).ToArray())
            guard.TargetCooldowns.Remove(expired);
        List<GuardCandidate> eligible = guard.Location.characters.OfType<Monster>()
            .Select((monster, index) => new GuardCandidate(monster, index))
            .Where(candidate => IsLiveTarget(candidate.Monster, guard.Location)
                && !candidate.Monster.isInvincible()
                && IntersectsScope(candidate.Monster.GetBoundingBox(), guard.AnchorPixel, guard.Radius))
            .ToList();
        if (eligible.Count == 0)
        {
            this.CompleteGuard(guard, "COMBAT-AREA-CLEAR", $"Guard {guard.DirectiveId} ended because its bounded scope is clear.", true);
            return;
        }
        Monster? target = eligible
            .Where(candidate => !guard.TargetCooldowns.TryGetValue(candidate.Monster, out ulong nextAttempt) || this.hostTick >= nextAttempt)
            .OrderByDescending(candidate => candidate.Monster.GetBoundingBox().Intersects(guard.Owner.GetBoundingBox()))
            .ThenByDescending(candidate => candidate.Monster.GetBoundingBox().Intersects(body.GetBoundingBox()))
            .ThenBy(candidate => Vector2.DistanceSquared(Center(candidate.Monster.GetBoundingBox()), guard.AnchorPixel))
            .ThenBy(candidate => Vector2.DistanceSquared(Center(candidate.Monster.GetBoundingBox()), body.StandingPixel.ToVector2()))
            .ThenBy(candidate => candidate.Monster.TilePoint.Y)
            .ThenBy(candidate => candidate.Monster.TilePoint.X)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Monster)
            .FirstOrDefault();
        if (target is null)
        {
            guard.LastReason = "COMBAT-BLOCKED";
            return;
        }

        string stepId = $"{guard.DirectiveId}:s{guard.NextStep++}";
        TaskBeginResult begin = this.execution.TryBeginChild(guard.Identity, stepId, guard.DirectiveId, "CombatGuardExchange", TargetKey(guard.Location, target));
        if (!begin.Started || begin.Session is null)
        {
            guard.LastReason = begin.Result.Code;
            return;
        }
        this.tasks.Add(guard.Identity, new CombatTask(begin.Session, target, guard.Location, guard.Owner, weapon, new WeaponFingerprint(weapon, CombatPolicyVersion), body.Position, "GuardArea", guard, enforceGuardScope: true));
        this.monitor.Log($"HY-COMBAT-GUARD-EXCHANGE: {guard.Identity} started {stepId} against {BoundedMonsterKind(target)}.", LogLevel.Trace);
    }

    private CombatCommandResult CompleteGuard(GuardDirective guard, string code, string message, bool success)
    {
        if (this.tasks.TryGetValue(guard.Identity, out CombatTask? child) && ReferenceEquals(child.Guard, guard))
        {
            this.execution.Complete(child.Session, false, code, message);
            this.tasks.Remove(guard.Identity);
            this.appearance.Fail(guard.Identity, child.OperationId, code);
        }
        this.guards.Remove(guard.Identity);
        return FromExecution(this.execution.CompleteDirective(guard.Identity, guard.DirectiveId, guard.ResumeMode, success, code, message));
    }

    private static bool IsSetupAllowed(CombatTask task, AttackSetup setup)
    {
        if (!task.EnforceGuardScope)
            return setup.HitSet.Length == 1
                && ReferenceEquals(setup.HitSet[0], task.Target)
                && (task.DefenseAnchorPixel is not Vector2 defenseAnchor
                    || PointInScope(setup.BodyPosition + new Vector2(Game1.tileSize / 2f), defenseAnchor, CounterStrikeRadius));
        if (task.Guard is not GuardDirective guard)
            return false;
        return setup.HitSet.Length is > 0 and <= MaximumHitSetSize
            && PointInScope(setup.BodyPosition + new Vector2(Game1.tileSize / 2f), guard.AnchorPixel, guard.Radius)
            && setup.HitSet.All(monster => IsLiveTarget(monster, task.Location)
                && !monster.isInvincible()
                && IntersectsScope(monster.GetBoundingBox(), guard.AnchorPixel, guard.Radius));
    }

    private static bool PointInScope(Vector2 position, Vector2 anchorPixel, int radius) =>
        Vector2.DistanceSquared(position, anchorPixel) <= radius * radius * Game1.tileSize * Game1.tileSize;

    private static bool IntersectsScope(Rectangle bounds, Vector2 anchorPixel, int radius)
    {
        float nearestX = Math.Clamp(anchorPixel.X, bounds.Left, bounds.Right);
        float nearestY = Math.Clamp(anchorPixel.Y, bounds.Top, bounds.Bottom);
        return PointInScope(new Vector2(nearestX, nearestY), anchorPixel, radius);
    }

    private static Vector2 ProjectToolLocation(Farmer owner, Vector2 projectedPosition, int facing)
    {
        Vector2 standing = projectedPosition + (owner.StandingPixel.ToVector2() - owner.Position);
        return facing switch
        {
            0 => standing - new Vector2(0, Game1.tileSize),
            1 => standing + new Vector2(Game1.tileSize, 0),
            2 => standing + new Vector2(0, Game1.tileSize),
            _ => standing - new Vector2(Game1.tileSize, 0),
        };
    }

    private static bool IsLiveTarget(Monster target, GameLocation location) =>
        target.Health > 0
        && !target.IsInvisible
        && ReferenceEquals(target.currentLocation, location)
        && location.characters.Any(character => ReferenceEquals(character, target));

    private bool IsExactWeaponAvailable(CombatTask task) =>
        this.inventories.ContainsExact(task.Identity, task.Weapon)
        && IsSupportedWeapon(task.Weapon)
        && new WeaponFingerprint(task.Weapon, CombatPolicyVersion) == task.WeaponFingerprint;

    private static bool IsSupportedWeapon(MeleeWeapon? weapon)
    {
        if (weapon is null || weapon.isScythe())
            return false;
        int type = weapon.type.Value;
        return type == MeleeWeapon.stabbingSword
            || type == MeleeWeapon.defenseSword
            || type == MeleeWeapon.dagger
            || type == MeleeWeapon.club;
    }

    private static string WeaponKind(MeleeWeapon weapon) => weapon.type.Value switch
    {
        var type when type == MeleeWeapon.dagger => "dagger",
        var type when type == MeleeWeapon.club => "club",
        _ => "sword",
    };

    private static Vector2 Center(Rectangle rectangle) => new(rectangle.Center.X, rectangle.Center.Y);

    private static int FacingToward(Vector2 fromPixels, Vector2 toPixels)
    {
        Vector2 delta = toPixels - fromPixels;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }

    private static int ManhattanDistance(Point left, Point right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static CombatCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? CombatCommandResult.Success(result.Code, result.Message)
        : CombatCommandResult.Failure(result.Code, result.Message);

    private void RemoveExpiredOptions()
    {
        foreach (string optionId in this.options.Where(pair => this.hostTick > pair.Value.ExpiresAtTick).Select(pair => pair.Key).ToArray())
            this.options.Remove(optionId);
    }

    private static TaskTargetKey TargetKey(GameLocation location, Monster target) =>
        new(location.NameOrUniqueName, "Monster", $"runtime-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target)}");

    private static string BoundedMonsterKind(Monster monster)
    {
        string kind = monster.GetType().Name;
        return kind.Length <= 32 ? kind : kind[..32];
    }

    private static string DistanceBand(float pixelDistance) => pixelDistance <= Game1.tileSize * 2 ? "near" : pixelDistance <= Game1.tileSize * 5 ? "medium" : "far";

    private static string ThreatBand(Monster monster, Farmer owner, NPC body) =>
        monster.GetBoundingBox().Intersects(owner.GetBoundingBox()) || monster.GetBoundingBox().Intersects(body.GetBoundingBox()) ? "contacting" : "present";

    private readonly record struct WeaponFingerprint(string QualifiedItemId, int WeaponType, int PolicyVersion)
    {
        public WeaponFingerprint(MeleeWeapon weapon, int policyVersion) : this(weapon.QualifiedItemId, weapon.type.Value, policyVersion) { }
    }

    private readonly record struct AttackSetup(Vector2 BodyPosition, Rectangle Area, int Facing, Point ToolPixel, Monster[] HitSet, int[] PreHealth);

    private sealed record CachedCombatOption(
        CombatOption Option,
        CompanionIdentity Identity,
        NPC Body,
        ulong BodyGeneration,
        GameLocation Location,
        Monster Target,
        MeleeWeapon Weapon,
        WeaponFingerprint WeaponFingerprint,
        ulong ExpiresAtTick);

    private readonly record struct GuardCandidate(Monster Monster, int Index);

    private sealed class GuardDirective
    {
        public GuardDirective(CompanionIdentity identity, string directiveId, GameLocation location, Farmer owner, Vector2 anchorPixel, int radius, ulong expiresAtTick, int maximumSwings, string resumeMode)
        {
            this.Identity = identity; this.DirectiveId = directiveId; this.Location = location; this.Owner = owner; this.AnchorPixel = anchorPixel;
            this.Radius = radius; this.ExpiresAtTick = expiresAtTick; this.MaximumSwings = maximumSwings; this.ResumeMode = resumeMode;
        }

        public CompanionIdentity Identity { get; }
        public string DirectiveId { get; }
        public GameLocation Location { get; }
        public Farmer Owner { get; }
        public Vector2 AnchorPixel { get; }
        public int Radius { get; }
        public ulong ExpiresAtTick { get; }
        public int MaximumSwings { get; }
        public string ResumeMode { get; }
        public int CommittedSwings { get; set; }
        public int NextStep { get; set; } = 1;
        public string LastReason { get; set; } = "STARTED";
        public Dictionary<Monster, ulong> TargetCooldowns { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class CombatTask
    {
        public CombatTask(TaskSession session, Monster target, GameLocation location, Farmer owner, MeleeWeapon weapon, WeaponFingerprint weaponFingerprint, Vector2 position, string source = "SingleStrike", GuardDirective? guard = null, bool enforceGuardScope = false, Vector2? defenseAnchorPixel = null)
        {
            this.Session = session; this.Target = target; this.Location = location; this.Owner = owner;
            this.Weapon = weapon; this.WeaponFingerprint = weaponFingerprint; this.LastPosition = position; this.LastTargetTile = target.Tile;
            this.Source = source; this.Guard = guard; this.EnforceGuardScope = enforceGuardScope; this.DefenseAnchorPixel = defenseAnchorPixel;
        }

        public TaskSession Session { get; }
        public CompanionIdentity Identity => this.Session.Identity;
        public string OperationId => this.Session.OperationId;
        public Monster Target { get; }
        public GameLocation Location { get; }
        public Farmer Owner { get; }
        public MeleeWeapon Weapon { get; }
        public WeaponFingerprint WeaponFingerprint { get; }
        public string Source { get; }
        public GuardDirective? Guard { get; }
        public bool EnforceGuardScope { get; }
        public Vector2? DefenseAnchorPixel { get; }
        public bool SubmissionAttempted { get; set; }
        public Vector2 LastPosition { get; set; }
        public Vector2 LastTargetTile { get; set; }
        public int StuckSamples { get; set; }
        public int PathAttempts { get; set; }
        public int UpdateCount { get; set; }
        public ulong NextPathTick { get; set; }
    }
}
