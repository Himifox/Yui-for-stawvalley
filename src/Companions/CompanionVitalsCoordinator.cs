using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Monsters;
using StardewValley.Network;
using StardewValley.Pathfinding;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal static class VitalActionKinds
{
    public const string Watering = "Watering";
    public const string Chopping = "Chopping";
    public const string Mining = "Mining";
    public const string Digging = "Digging";
    public const string Milking = "Milking";
    public const string Shearing = "Shearing";
    public const string Fishing = "Fishing";
    public const string Harvesting = "Harvesting";
    public const string Foraging = "Foraging";
    public const string Mowing = "Mowing";
    public const string Petting = "Petting";
    public const string Combat = "Combat";

    public static float Cost(string kind) => kind switch
    {
        Watering or Chopping or Mining or Digging => 2f,
        Milking or Shearing => 4f,
        Fishing => 8f,
        Harvesting or Foraging or Mowing or Petting or Combat => 0f,
        _ => float.NaN,
    };
}

internal static class CompanionFatigueLevels
{
    public const string Normal = "Normal";
    public const string Tired = "Tired";
    public const string Critical = "Critical";
    public const string Exhausted = "Exhausted";

    public static string From(CompanionVitalsRecord vitals)
    {
        if (vitals.Stamina <= 0f)
            return Exhausted;
        float ratio = vitals.Stamina / vitals.MaxStamina;
        if (ratio <= 0.10f)
            return Critical;
        return ratio <= 0.25f ? Tired : Normal;
    }

    public static int Severity(string level) => level switch
    {
        Tired => 1,
        Critical => 2,
        Exhausted => 3,
        _ => 0,
    };
}

internal readonly record struct VitalActionResult(bool IsSuccess, string Code, string Message)
{
    public static VitalActionResult Success(string code, string message) => new(true, code, message);
    public static VitalActionResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class VitalCostLease : IDisposable
{
    private readonly CompanionVitalsCoordinator? coordinator;
    private readonly CompanionRecord? record;
    private readonly VitalCostReceiptRecord? receipt;
    private bool committed;
    private bool disposed;

    internal VitalCostLease(VitalActionResult result)
    {
        this.Result = result;
    }

    internal VitalCostLease(CompanionVitalsCoordinator coordinator, CompanionRecord record, VitalCostReceiptRecord? receipt, VitalActionResult result)
    {
        this.coordinator = coordinator;
        this.record = record;
        this.receipt = receipt;
        this.Result = result;
    }

    public VitalActionResult Result { get; }

    public bool IsSuccess => this.Result.IsSuccess;

    public void Commit()
    {
        if (!this.IsSuccess || this.disposed || this.committed)
            return;
        this.committed = true;
        if (this.receipt is not null)
            this.coordinator!.CommitCost(this.record!, this.receipt);
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        if (this.IsSuccess && !this.committed && this.receipt is not null)
            this.coordinator!.RefundCost(this.record!, this.receipt);
    }
}

internal sealed class CompanionVitalsCoordinator
{
    private const int CostReceiptLimit = 128;
    private const int InvulnerabilityTicks = 72;
    private const int DownedDelayTicks = 120;
    private const int ExhaustionDepartureTicks = 60;
    private const int RetreatPathLimit = 64;
    private const ulong RetreatRetryTicks = 30;
    private const float RetreatThreshold = 0.35f;
    private const float EmergencyThreshold = 0.20f;

    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly IMonitor monitor;
    private readonly Func<LifecycleState> getLifecycleState;
    private readonly Func<bool> canMutateSave;
    private readonly Dictionary<CompanionIdentity, string> foodTransactions = new();
    private readonly Dictionary<CompanionIdentity, ulong> nextRetreatTick = new();
    private readonly Dictionary<CompanionIdentity, int> recoveryDepartureTicks = new();
    private Action<CompanionIdentity, string>? cancelActions;
    private Action<CompanionIdentity, Monster, string>? damageObserver;
    private ulong lastUpdateTick;

    public CompanionVitalsCoordinator(
        CompanionRegistry registry,
        CompanionBodyBinder bodies,
        CompanionInventoryStore inventories,
        IMonitor monitor,
        Func<LifecycleState> getLifecycleState,
        Func<bool> canMutateSave)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.monitor = monitor;
        this.getLifecycleState = getLifecycleState;
        this.canMutateSave = canMutateSave;
    }

    public void AttachCancellation(Action<CompanionIdentity, string> cancellation) => this.cancelActions = cancellation;

    public void AttachDamageObserver(Action<CompanionIdentity, Monster, string> observer) => this.damageObserver = observer;

    public bool CanSafelyCounter(CompanionIdentity identity, out VitalActionResult result)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
        {
            result = VitalActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist.");
            return false;
        }
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.State != CompanionVitalStates.Active || vitals.Health <= 0 || (float)vitals.Health / vitals.MaxHealth <= RetreatThreshold)
        {
            result = VitalActionResult.Failure("COMBAT-LOW-HEALTH", "Yui is at or below the retreat health gate; withdrawal takes priority over CounterStrike.");
            return false;
        }
        float cost = VitalActionKinds.Cost(VitalActionKinds.Combat);
        if (vitals.Stamina < cost)
        {
            result = VitalActionResult.Failure("COMBAT-ENERGY-INSUFFICIENT", "Yui lacks the stamina required for one defensive swing.");
            return false;
        }
        result = VitalActionResult.Success("COMBAT-SAFE", "Vitals allow one bounded defensive swing.");
        return true;
    }

    public InventoryValidationResult ValidateAndInitialize()
    {
        int currentDay = CurrentDay();
        HashSet<string> episodeIds = new(StringComparer.Ordinal);
        foreach (CompanionRecord record in this.registry.Active)
        {
            CompanionVitalsRecord vitals = record.Vitals;
            vitals.RecentCosts ??= new List<VitalCostReceiptRecord>();
            if (vitals.MaxHealth is < 1 or > 1000
                || vitals.Health < 0 || vitals.Health > vitals.MaxHealth
                || !float.IsFinite(vitals.MaxStamina) || vitals.MaxStamina is < 1f or > 2000f
                || !float.IsFinite(vitals.Stamina) || vitals.Stamina < 0f || vitals.Stamina > vitals.MaxStamina
                || !CompanionVitalStates.IsValid(vitals.State)
                || !CompanionModes.IsValid(vitals.ResumeMode)
                || vitals.InvulnerabilityTicksRemaining < 0
                || vitals.DownedTicksRemaining < 0
                || vitals.RestTicksRemaining < 0
                || vitals.LastNormalizedDay > currentDay)
                return InventoryValidationResult.Failure("INVALID-VITALS", $"{record.Identity} contains invalid health, stamina, timer, state, or day values.");

            bool recoveryState = vitals.State is CompanionVitalStates.Downed or CompanionVitalStates.Recovering;
            if (recoveryState != !string.IsNullOrWhiteSpace(vitals.RecoveryEpisodeId)
                || (recoveryState && (!Guid.TryParseExact(vitals.RecoveryEpisodeId, "N", out _) || !episodeIds.Add(vitals.RecoveryEpisodeId!) || vitals.RecoveryDay <= vitals.LastNormalizedDay))
                || (vitals.Health == 0 && !recoveryState))
                return InventoryValidationResult.Failure("INVALID-RECOVERY", $"{record.Identity} contains an inconsistent recovery episode or zero-health state.");

            if (vitals.RecentCosts.Any(receipt => receipt is null
                || string.IsNullOrWhiteSpace(receipt.CommitId)
                || receipt.CommitId.Length > 160
                || !float.IsFinite(receipt.Cost)
                || receipt.Cost < 0f
                || receipt.Cost != VitalActionKinds.Cost(receipt.Kind))
                || vitals.RecentCosts.GroupBy(receipt => receipt.CommitId, StringComparer.Ordinal).Any(group => group.Count() > 1)
                || vitals.RecentCosts.Count > CostReceiptLimit)
                return InventoryValidationResult.Failure("INVALID-VITAL-COST-RECEIPT", $"{record.Identity} contains invalid or duplicated action-cost receipts.");

            if (vitals.LastNormalizedDay < 0)
                vitals.LastNormalizedDay = currentDay;
            else if (vitals.LastNormalizedDay < currentDay && (!recoveryState || currentDay >= vitals.RecoveryDay))
                this.NormalizeNewDay(record, currentDay);

            if (vitals.State == CompanionVitalStates.Resting)
            {
                vitals.State = CompanionVitalStates.Active;
                vitals.RestTicksRemaining = 0;
                record.Mode = vitals.ResumeMode;
            }
        }
        return InventoryValidationResult.Success($"Validated {this.registry.Count} isolated companion vitals record(s).");
    }

    public bool CanStartAction(CompanionIdentity identity, string kind, out VitalActionResult result)
        => this.CanStartAction(identity, kind, interruptRest: true, out result);

    public bool CanStartActionWithoutInterruptingRest(CompanionIdentity identity, string kind, out VitalActionResult result)
        => this.CanStartAction(identity, kind, interruptRest: false, out result);

    private bool CanStartAction(CompanionIdentity identity, string kind, bool interruptRest, out VitalActionResult result)
    {
        float cost = VitalActionKinds.Cost(kind);
        if (!float.IsFinite(cost))
        {
            result = VitalActionResult.Failure("UNKNOWN-ACTION-COST", $"No vitals cost is registered for {kind}.");
            return false;
        }
        if (!this.TryGetWritable(identity, out CompanionRecord record, out result))
            return false;
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.State == CompanionVitalStates.Resting && interruptRest)
            this.EndRest(record, "REST-INTERRUPTED-BY-ACTION");
        if (vitals.State != CompanionVitalStates.Active)
        {
            result = VitalActionResult.Failure("VITALS-ACTION-BLOCKED", $"{identity} cannot start {kind} while vitals state is {vitals.State}.");
            return false;
        }
        if (vitals.Health <= 0)
        {
            result = VitalActionResult.Failure("VITALS-HEALTH-EMPTY", $"{identity} cannot start {kind} with no health remaining.");
            return false;
        }
        if ((float)vitals.Health / vitals.MaxHealth <= RetreatThreshold)
        {
            result = VitalActionResult.Failure("LOW-HEALTH-RETREAT", $"{identity} is at or below 35% health and will not start {kind}.");
            return false;
        }
        if (vitals.Stamina <= 0f || vitals.Stamina < cost)
        {
            vitals.LastFailure = vitals.Stamina <= 0f
                ? $"ENERGY-INSUFFICIENT: {kind} cannot start because no stamina remains."
                : $"ENERGY-INSUFFICIENT: {kind} requires {cost:0.##}, available {vitals.Stamina:0.##}.";
            result = VitalActionResult.Failure("ENERGY-INSUFFICIENT", vitals.LastFailure);
            return false;
        }
        result = VitalActionResult.Success("VITALS-READY", $"{identity} can afford {kind} cost {cost:0.##}.");
        return true;
    }

    public VitalCostLease ReserveCost(CompanionIdentity identity, string kind, string commitId)
    {
        if (!this.CanStartAction(identity, kind, out VitalActionResult gate))
            return new VitalCostLease(gate);
        if (string.IsNullOrWhiteSpace(commitId) || commitId.Length > 160)
            return new VitalCostLease(VitalActionResult.Failure("INVALID-COST-COMMIT-ID", "A bounded action cost commit ID is required."));

        CompanionRecord record = this.registry.Active.First(candidate => candidate.Identity == identity);
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.RecentCosts.Any(receipt => receipt.CommitId == commitId))
            return new VitalCostLease(VitalActionResult.Failure("ACTION-COST-ALREADY-COMMITTED", $"Cost commit {commitId} was already settled."));

        float cost = VitalActionKinds.Cost(kind);
        if (cost == 0f)
            return new VitalCostLease(this, record, null, VitalActionResult.Success("ZERO-COST-RESERVED", $"{kind} has explicit zero stamina cost."));

        var receipt = new VitalCostReceiptRecord { CommitId = commitId, Kind = kind, Cost = cost };
        vitals.Stamina -= cost;
        vitals.RecentCosts.Add(receipt);
        return new VitalCostLease(this, record, receipt, VitalActionResult.Success("ACTION-COST-RESERVED", $"Reserved {cost:0.##} stamina for {kind} before world commit."));
    }

    public VitalActionResult TryStartRest(CompanionIdentity identity, int seconds)
    {
        if (seconds is < 2 or > 8)
            return VitalActionResult.Failure("INVALID-REST-DURATION", "Rest duration must be between 2 and 8 seconds.");
        if (!this.TryGetWritable(identity, out CompanionRecord record, out VitalActionResult gate))
            return gate;
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.State == CompanionVitalStates.Resting)
            return VitalActionResult.Success("ALREADY-RESTING", $"{identity} is already resting with {vitals.RestTicksRemaining} tick(s) remaining.");
        if (vitals.State != CompanionVitalStates.Active || vitals.Health <= 0 || vitals.Stamina <= 0f || !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return VitalActionResult.Failure("REST-BLOCKED", $"{identity} cannot rest while state={vitals.State}, transaction={record.ActiveTransactionId ?? "none"}.");
        if (!this.bodies.TryGetBody(identity, out NPC body))
            return VitalActionResult.Failure("BODY-UNAVAILABLE", "The Yui must be summoned to start a nearby rest.");
        if (HasNearbyMonster(body, 3f))
            return VitalActionResult.Failure("REST-UNSAFE", "Rest cannot start while a live Monster is within three tiles.");
        vitals.ResumeMode = record.Mode;
        vitals.State = CompanionVitalStates.Resting;
        vitals.RestTicksRemaining = seconds * 60;
        record.Mode = CompanionModes.Wait;
        this.bodies.Halt(identity);
        return VitalActionResult.Success("REST-STARTED", $"{identity} started a bounded {seconds}-second rest; health and stamina will not change.");
    }

    public VitalActionResult CanChangeMode(CompanionIdentity identity)
    {
        if (!this.TryGetWritable(identity, out CompanionRecord record, out VitalActionResult gate))
            return gate;
        if (record.Vitals.State is CompanionVitalStates.Downed or CompanionVitalStates.Recovering or CompanionVitalStates.Retreating)
            return VitalActionResult.Failure("VITALS-MODE-BLOCKED", $"{identity} cannot override safety state {record.Vitals.State}.");
        if (record.Vitals.State == CompanionVitalStates.Resting)
            this.EndRest(record, "REST-INTERRUPTED-BY-COMMAND");
        return VitalActionResult.Success("MODE-ALLOWED", $"{identity} can change normal movement mode.");
    }

    public VitalActionResult RequestEat(CompanionIdentity identity, int? oneBasedBagSlot, Action<VitalActionResult> completed)
    {
        if (!this.TryGetWritable(identity, out CompanionRecord record, out VitalActionResult gate))
            return gate;
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.State is CompanionVitalStates.Downed or CompanionVitalStates.Recovering || vitals.Health <= 0)
            return VitalActionResult.Failure("FOOD-RECOVERY-BLOCKED", $"{identity} cannot eat while state={vitals.State}.");
        if (vitals.State == CompanionVitalStates.Resting)
            this.EndRest(record, "REST-INTERRUPTED-BY-FOOD");
        if (this.foodTransactions.ContainsKey(identity) || !string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return VitalActionResult.Failure("COMPANION-BUSY", $"{identity} already has an active food or world transaction.");
        if (!this.bodies.TryGetBody(identity, out _))
            return VitalActionResult.Failure("BODY-UNAVAILABLE", "The Yui must be summoned to eat.");

        Inventory bag = this.inventories.Get(identity);
        int index = FindFoodIndex(bag);
        if (oneBasedBagSlot is not null
            && !this.inventories.TryResolveRegularSlot(identity, oneBasedBagSlot.Value, out index, out _))
            index = -1;
        if (index < 0 || index >= bag.Count || bag[index] is not SObject food || !TryReadFood(food, out int staminaGain, out int healthGain))
            return VitalActionResult.Failure("ELIGIBLE-FOOD-NOT-FOUND", "The selected Yui bag slot does not contain a positive-recovery edible Object without another responsibility.");
        if (vitals.Health >= vitals.MaxHealth && vitals.Stamina >= vitals.MaxStamina)
            return VitalActionResult.Failure("VITALS-ALREADY-FULL", "Food was not consumed because both health and stamina are full.");

        string operationId = $"food-{Guid.NewGuid():N}";
        this.foodTransactions.Add(identity, operationId);
        record.ActiveTransactionId = operationId;
        NetMutex mutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(CompanionInventoryStore.GetNamespace(identity));
        int expectedStack = food.Stack;
        string expectedId = food.QualifiedItemId;
        int expectedQuality = food.Quality;
        mutex.RequestLock(
            acquired: () =>
            {
                VitalActionResult result;
                try
                {
                    if (!this.IsFoodCurrent(identity, operationId, record)
                        || index >= bag.Count
                        || !ReferenceEquals(bag[index], food)
                        || food.Stack != expectedStack
                        || food.QualifiedItemId != expectedId
                        || food.Quality != expectedQuality
                        || !TryReadFood(food, out int currentStaminaGain, out int currentHealthGain)
                        || currentStaminaGain != staminaGain
                        || currentHealthGain != healthGain)
                    {
                        result = VitalActionResult.Failure("FOOD-LOCKED-REVALIDATION-FAILED", "Authority, slot, exact food instance, stack, quality, or vanilla recovery changed before the bag lock.");
                    }
                    else
                    {
                        result = this.CommitFood(record, bag, index, food, staminaGain, healthGain);
                    }
                }
                catch (Exception ex)
                {
                    result = VitalActionResult.Failure("FOOD-TRANSACTION-ERROR", $"Food transaction failed without an automatic retry: {ex.Message}");
                }
                finally
                {
                    mutex.ReleaseLock();
                    this.FinishFood(identity, operationId, record);
                }
                completed(result);
            },
            failed: () =>
            {
                this.FinishFood(identity, operationId, record);
                completed(VitalActionResult.Failure("BAG-LOCK-FAILED", "The Yui bag mutex could not be acquired; food and vitals were unchanged."));
            }
        );
        return VitalActionResult.Success("FOOD-SCHEDULED", $"Food transaction {operationId} will revalidate the exact {expectedId} instance under the Yui bag lock.");
    }

    public void Update(ulong tick)
    {
        int elapsed = this.lastUpdateTick == 0 ? Math.Max(1, (int)tick) : Math.Max(1, (int)Math.Min(600UL, tick - this.lastUpdateTick));
        this.lastUpdateTick = tick;
        int day = CurrentDay();

        foreach (CompanionRecord record in this.registry.Active.ToArray())
        {
            CompanionVitalsRecord vitals = record.Vitals;
            bool recoveryState = vitals.State is CompanionVitalStates.Downed or CompanionVitalStates.Recovering;
            if (vitals.LastNormalizedDay < day && (!recoveryState || day >= vitals.RecoveryDay))
                this.NormalizeNewDay(record, day);
            if (vitals.InvulnerabilityTicksRemaining > 0)
                vitals.InvulnerabilityTicksRemaining = Math.Max(0, vitals.InvulnerabilityTicksRemaining - elapsed);

            if (vitals.State == CompanionVitalStates.Resting)
            {
                Monster? interruption = this.bodies.TryGetBody(record.Identity, out NPC restingBody) && restingBody.currentLocation is not null
                    ? restingBody.currentLocation.characters.OfType<Monster>().FirstOrDefault(monster => monster.Health > 0 && monster.GetBoundingBox().Intersects(restingBody.GetBoundingBox()))
                    : null;
                if (interruption is not null)
                {
                    this.EndRest(record, "REST-INTERRUPTED-BY-DANGER");
                    this.ApplyMonsterDamage(record, interruption);
                    if (vitals.State == CompanionVitalStates.Downed)
                        continue;
                }
                else
                {
                    vitals.RestTicksRemaining = Math.Max(0, vitals.RestTicksRemaining - elapsed);
                    if (vitals.RestTicksRemaining == 0)
                        this.EndRest(record, "REST-COMPLETED");
                    continue;
                }
            }
            if (vitals.State == CompanionVitalStates.Downed)
            {
                vitals.DownedTicksRemaining = Math.Max(0, vitals.DownedTicksRemaining - elapsed);
                if (vitals.DownedTicksRemaining == 0)
                    this.EnterOffscreenRecovery(record, "DOWNED-DELAY-COMPLETE");
                continue;
            }
            if (vitals.State == CompanionVitalStates.Recovering)
            {
                if (this.recoveryDepartureTicks.TryGetValue(record.Identity, out int remaining))
                {
                    remaining = Math.Max(0, remaining - elapsed);
                    if (remaining == 0)
                        this.FinalizeRecoveryDeparture(record);
                    else
                        this.recoveryDepartureTicks[record.Identity] = remaining;
                }
                continue;
            }
            if (!this.bodies.TryGetBody(record.Identity, out NPC body) || body.currentLocation is null)
            {
                if (vitals.Stamina <= 0f)
                    this.EnterRecovery(record, "StaminaExhausted");
                continue;
            }

            Monster? contact = body.currentLocation.characters.OfType<Monster>()
                .FirstOrDefault(monster => monster.Health > 0 && monster.GetBoundingBox().Intersects(body.GetBoundingBox()));
            if (contact is not null && vitals.InvulnerabilityTicksRemaining == 0)
                this.ApplyMonsterDamage(record, contact);
            if (vitals.State == CompanionVitalStates.Downed)
                continue;

            float healthRatio = (float)vitals.Health / vitals.MaxHealth;
            if (healthRatio <= RetreatThreshold)
                this.EnterRetreat(record);

            bool wantsFood = vitals.Stamina <= 0f || healthRatio <= RetreatThreshold;
            bool safeToEat = !HasNearbyMonster(body, 3f);
            if (wantsFood && safeToEat && !this.foodTransactions.ContainsKey(record.Identity) && string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            {
                if (FindFoodIndex(this.inventories.Get(record.Identity)) >= 0)
                {
                    VitalActionResult scheduled = this.RequestEat(record.Identity, null, result => this.monitor.Log($"HY-VITALS-{result.Code}: {result.Message}", result.IsSuccess ? LogLevel.Info : LogLevel.Warn));
                    if (!scheduled.IsSuccess)
                        vitals.LastFailure = $"{scheduled.Code}: {scheduled.Message}";
                    continue;
                }
                if (vitals.Stamina <= 0f)
                {
                    this.EnterRecovery(record, "StaminaExhausted");
                    continue;
                }
            }

            if (vitals.Stamina <= 0f && !safeToEat)
            {
                this.EnterRecovery(record, "StaminaExhaustedInDanger");
                continue;
            }

            if (vitals.State == CompanionVitalStates.Retreating)
                this.UpdateRetreat(record, body, tick, healthRatio <= EmergencyThreshold && FindFoodIndex(this.inventories.Get(record.Identity)) < 0);
        }
    }

    public void OnSaving()
    {
        foreach ((CompanionIdentity identity, string operationId) in this.foodTransactions.ToArray())
        {
            if (this.registry.TryGet(identity, out CompanionRecord record) && record.ActiveTransactionId == operationId)
                record.ActiveTransactionId = null;
        }
        this.foodTransactions.Clear();
        foreach (CompanionRecord record in this.registry.Active)
        {
            if (record.Vitals.State == CompanionVitalStates.Resting)
                this.EndRest(record, "REST-CANCELLED-BY-SAVE");
            else if (record.Vitals.State == CompanionVitalStates.Downed)
                this.EnterOffscreenRecovery(record, "DOWNED-SAVED-OFFSCREEN");
            else if (record.Vitals.State == CompanionVitalStates.Recovering && this.recoveryDepartureTicks.ContainsKey(record.Identity))
                this.FinalizeRecoveryDeparture(record);
        }
    }

    public void HandleRecall(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return;
        this.CancelFood(record);
        if (record.Vitals.State == CompanionVitalStates.Resting)
            this.EndRest(record, "REST-CANCELLED-BY-RECALL");
        else if (record.Vitals.State == CompanionVitalStates.Downed)
            this.EnterOffscreenRecovery(record, "DOWNED-RECALLED-OFFSCREEN");
        else if (record.Vitals.State == CompanionVitalStates.Recovering && this.recoveryDepartureTicks.ContainsKey(record.Identity))
            this.FinalizeRecoveryDeparture(record);
    }

    public void HandleOwnerDisconnected(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return;
        this.CancelFood(record);
        if (record.Vitals.State == CompanionVitalStates.Resting)
            this.EndRest(record, "REST-CANCELLED-BY-OWNER-DISCONNECT");
        else if (record.Vitals.State == CompanionVitalStates.Recovering && this.recoveryDepartureTicks.ContainsKey(record.Identity))
            this.FinalizeRecoveryDeparture(record);
        record.Vitals.LastFailure = "OWNER-DISCONNECTED: Uncommitted activity stopped while persistent vitals and recovery were retained.";
    }

    public bool CanSummon(CompanionIdentity identity, out VitalActionResult result)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
        {
            result = VitalActionResult.Success("NEW-IDENTITY", "A new identity starts with full vitals.");
            return true;
        }
        if (record.Vitals.State is CompanionVitalStates.Downed or CompanionVitalStates.Recovering)
        {
            result = VitalActionResult.Failure("RECOVERY-ACTIVE", $"{identity} cannot be summoned until day {record.Vitals.RecoveryDay}; episode={record.Vitals.RecoveryEpisodeId}.");
            return false;
        }
        result = VitalActionResult.Success("SUMMON-ALLOWED", $"{identity} is not recovery-locked.");
        return true;
    }

    public IReadOnlyList<string> Describe(CompanionIdentity identity)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return new[] { $"{identity} does not exist." };
        CompanionVitalsRecord v = record.Vitals;
        return new[]
        {
            $"state={v.State}, health={v.Health}/{v.MaxHealth}, stamina={v.Stamina:0.##}/{v.MaxStamina:0.##}, invulnerability={v.InvulnerabilityTicksRemaining}, day={v.LastNormalizedDay}",
            $"episode={v.RecoveryEpisodeId ?? "none"}, reason={v.RecoveryReason ?? "none"}, recoveryDay={v.RecoveryDay}, downedTicks={v.DownedTicksRemaining}, restTicks={v.RestTicksRemaining}",
            $"lastDamage={v.LastDamageTaken} from {v.LastDamageSource ?? "none"}, lastAction={v.LastActionCommitId ?? "none"} cost={v.LastActionCost:0.##}, lastFood={v.LastFoodItemId ?? "none"} health+={v.LastFoodHealthRestored} stamina+={v.LastFoodStaminaRestored:0.##}, failure={v.LastFailure ?? "none"}",
        };
    }

    public void ClearRuntime()
    {
        this.foodTransactions.Clear();
        this.nextRetreatTick.Clear();
        this.recoveryDepartureTicks.Clear();
        this.lastUpdateTick = 0;
    }

    internal void CommitCost(CompanionRecord record, VitalCostReceiptRecord receipt)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        vitals.LastActionCommitId = receipt.CommitId;
        vitals.LastActionCost = receipt.Cost;
        vitals.LastFailure = null;
        while (vitals.RecentCosts.Count > CostReceiptLimit)
            vitals.RecentCosts.RemoveAt(0);
    }

    internal void RefundCost(CompanionRecord record, VitalCostReceiptRecord receipt)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.RecentCosts.Remove(receipt))
            vitals.Stamina = Math.Min(vitals.MaxStamina, vitals.Stamina + receipt.Cost);
    }

    private VitalActionResult CommitFood(CompanionRecord record, Inventory bag, int index, SObject food, int staminaGain, int healthGain)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        int stackBefore = food.Stack;
        int healthBefore = vitals.Health;
        float staminaBefore = vitals.Stamina;
        bool removed = false;
        try
        {
            food.ConsumeStack(1);
            if (food.Stack <= 0 && index < bag.Count && ReferenceEquals(bag[index], food))
            {
                bag.RemoveAt(index);
                removed = true;
            }
            vitals.Health = Math.Min(vitals.MaxHealth, healthBefore + healthGain);
            vitals.Stamina = Math.Min(vitals.MaxStamina, staminaBefore + staminaGain);
            vitals.LastFoodItemId = food.QualifiedItemId;
            vitals.LastFoodHealthRestored = vitals.Health - healthBefore;
            vitals.LastFoodStaminaRestored = vitals.Stamina - staminaBefore;
            vitals.LastFailure = null;
            if (vitals.State == CompanionVitalStates.Retreating && (float)vitals.Health / vitals.MaxHealth > RetreatThreshold)
            {
                vitals.State = CompanionVitalStates.Active;
                record.Mode = vitals.ResumeMode;
            }
            return VitalActionResult.Success("FOOD-CONSUMED", $"{record.Identity} consumed one exact {food.QualifiedItemId}; health +{vitals.LastFoodHealthRestored}, stamina +{vitals.LastFoodStaminaRestored:0.##}; Buffs ignored.");
        }
        catch (Exception ex)
        {
            vitals.Health = healthBefore;
            vitals.Stamina = staminaBefore;
            food.Stack = stackBefore;
            if (removed && !bag.Any(item => ReferenceEquals(item, food)))
                bag.Insert(Math.Min(index, bag.Count), food);
            vitals.LastFailure = $"FOOD-ROLLED-BACK: {ex.Message}";
            return VitalActionResult.Failure("FOOD-ROLLED-BACK", "Food consumption failed; the same item reference/stack and both vitals snapshots were restored.");
        }
    }

    private void ApplyMonsterDamage(CompanionRecord record, Monster monster)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        int healthBefore = vitals.Health;
        int damage = Math.Max(1, monster.DamageToFarmer);
        vitals.Health = Math.Max(0, vitals.Health - damage);
        vitals.LastDamageTaken = damage;
        vitals.LastDamageSource = $"{monster.GetType().Name}@{monster.Tile.X:0},{monster.Tile.Y:0}";
        vitals.InvulnerabilityTicksRemaining = InvulnerabilityTicks;
        this.monitor.Log($"HY-VITALS-DAMAGED: {record.Identity} took {damage} contact damage from {vitals.LastDamageSource}; health={vitals.Health}/{vitals.MaxHealth}.", LogLevel.Warn);
        if (vitals.Health == 0)
            this.EnterDowned(record, "MonsterContact");
        if (vitals.Health < healthBefore)
            this.damageObserver?.Invoke(record.Identity, monster, $"yui-{record.Identity.OwnerId}-{this.lastUpdateTick}-{Guid.NewGuid():N}");
    }

    private void EnterRetreat(CompanionRecord record)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.State == CompanionVitalStates.Retreating)
            return;
        vitals.ResumeMode = record.Mode;
        vitals.State = CompanionVitalStates.Retreating;
        this.cancelActions?.Invoke(record.Identity, "CANCELLED-BY-LOW-HEALTH");
        record.Mode = CompanionModes.Wait;
        this.bodies.Halt(record.Identity);
    }

    private void UpdateRetreat(CompanionRecord record, NPC body, ulong tick, bool emergency)
    {
        if (this.nextRetreatTick.TryGetValue(record.Identity, out ulong next) && tick < next)
            return;
        this.nextRetreatTick[record.Identity] = tick + RetreatRetryTicks;
        Monster? nearest = body.currentLocation?.characters.OfType<Monster>()
            .Where(monster => monster.Health > 0)
            .OrderBy(monster => Vector2.DistanceSquared(monster.Tile, body.Tile))
            .FirstOrDefault();
        if (nearest is null || body.currentLocation is null)
        {
            body.controller = null;
            body.Halt();
            return;
        }

        Vector2[] directions = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
        Vector2? destination = directions.Select(direction => body.Tile + direction)
            .Where(tile => body.currentLocation.isTileOnMap(tile)
                && body.currentLocation.isTileLocationOpen(tile)
                && body.currentLocation.characters.All(character => ReferenceEquals(character, body) || character.Tile != tile))
            .OrderByDescending(tile => Vector2.DistanceSquared(tile, nearest.Tile))
            .ThenBy(tile => tile.X)
            .ThenBy(tile => tile.Y)
            .Select(tile => (Vector2?)tile)
            .FirstOrDefault();
        if (destination is null)
        {
            body.controller = null;
            body.Halt();
            record.Vitals.LastFailure = emergency ? "EMERGENCY-RETREAT-BLOCKED: no open adjacent tile." : "RETREAT-BLOCKED: no open adjacent tile.";
            return;
        }
        body.controller = new PathFindController(body, body.currentLocation, destination.Value.ToPoint(), body.FacingDirection, null, RetreatPathLimit);
    }

    private void EnterDowned(CompanionRecord record, string reason)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.State == CompanionVitalStates.Downed || vitals.State == CompanionVitalStates.Recovering)
            return;
        this.recoveryDepartureTicks.Remove(record.Identity);
        if (vitals.State != CompanionVitalStates.Retreating)
            vitals.ResumeMode = record.Mode;
        vitals.Health = 0;
        vitals.Stamina = Math.Max(0f, vitals.Stamina);
        vitals.State = CompanionVitalStates.Downed;
        vitals.DownedForDay = true;
        vitals.DownedTicksRemaining = DownedDelayTicks;
        vitals.RestTicksRemaining = 0;
        vitals.RecoveryEpisodeId = Guid.NewGuid().ToString("N");
        vitals.RecoveryReason = reason;
        vitals.RecoveryDay = CurrentDay() + 1;
        this.CancelFood(record);
        this.cancelActions?.Invoke(record.Identity, "CANCELLED-BY-DOWNED");
        record.Mode = CompanionModes.Wait;
        this.bodies.Halt(record.Identity);
        this.monitor.Log($"HY-VITALS-DOWNED: {record.Identity} entered episode {vitals.RecoveryEpisodeId}; recovery day={vitals.RecoveryDay}.", LogLevel.Error);
    }

    private void EnterRecovery(CompanionRecord record, string reason)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        if (vitals.State == CompanionVitalStates.Recovering)
            return;
        string previousState = vitals.State;
        if (string.IsNullOrWhiteSpace(vitals.RecoveryEpisodeId))
            vitals.RecoveryEpisodeId = Guid.NewGuid().ToString("N");
        vitals.RecoveryReason = reason;
        vitals.RecoveryDay = CurrentDay() + 1;
        vitals.State = CompanionVitalStates.Recovering;
        vitals.RestTicksRemaining = 0;
        vitals.DownedTicksRemaining = 0;
        if (previousState != CompanionVitalStates.Retreating)
            vitals.ResumeMode = record.Mode;
        this.CancelFood(record);
        this.cancelActions?.Invoke(record.Identity, "CANCELLED-BY-RECOVERY");
        record.Mode = CompanionModes.Wait;
        if (record.WantsBody && this.bodies.TryGetBody(record.Identity, out NPC body) && body.currentLocation is not null)
        {
            this.bodies.Halt(record.Identity);
            this.recoveryDepartureTicks[record.Identity] = ExhaustionDepartureTicks;
            this.monitor.Log($"HY-VITALS-RECOVERY-ANNOUNCED: {record.Identity} exhausted their stamina and will remain visible briefly before recovery.", LogLevel.Warn);
        }
        else
            this.FinalizeRecoveryDeparture(record);
    }

    private void EnterOffscreenRecovery(CompanionRecord record, string reason)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        this.recoveryDepartureTicks.Remove(record.Identity);
        vitals.State = CompanionVitalStates.Recovering;
        vitals.DownedTicksRemaining = 0;
        vitals.LastFailure = reason;
        record.WantsBody = false;
        this.CancelFood(record);
        this.cancelActions?.Invoke(record.Identity, "CANCELLED-BY-OFFSCREEN-RECOVERY");
        record.Mode = CompanionModes.Wait;
        this.bodies.Unbind(record.Identity);
    }

    private void FinalizeRecoveryDeparture(CompanionRecord record)
    {
        this.recoveryDepartureTicks.Remove(record.Identity);
        record.WantsBody = false;
        this.bodies.Unbind(record.Identity);
        this.monitor.Log($"HY-VITALS-RECOVERING: {record.Identity} entered safe offscreen recovery until day {record.Vitals.RecoveryDay}; no items or mail changed.", LogLevel.Warn);
    }

    private void NormalizeNewDay(CompanionRecord record, int day)
    {
        CompanionVitalsRecord vitals = record.Vitals;
        this.recoveryDepartureTicks.Remove(record.Identity);
        string? episode = vitals.RecoveryEpisodeId;
        bool wasRecovering = vitals.State is CompanionVitalStates.Downed or CompanionVitalStates.Recovering;
        vitals.Health = vitals.MaxHealth;
        vitals.Stamina = vitals.MaxStamina;
        vitals.LastNormalizedDay = day;
        vitals.State = CompanionVitalStates.Active;
        vitals.InvulnerabilityTicksRemaining = 0;
        vitals.DownedTicksRemaining = 0;
        vitals.RestTicksRemaining = 0;
        vitals.DownedForDay = false;
        vitals.LastSettledRecoveryId = episode;
        vitals.RecoveryEpisodeId = null;
        vitals.RecoveryReason = null;
        vitals.RecoveryDay = -1;
        vitals.LastFailure = null;
        record.Mode = vitals.ResumeMode;
        if (wasRecovering)
            record.WantsBody = false;
        this.monitor.Log($"HY-VITALS-NEW-DAY: {record.Identity} normalized once for day {day}; settled episode={episode ?? "none"}.", LogLevel.Info);
    }

    private void EndRest(CompanionRecord record, string code)
    {
        record.Vitals.State = CompanionVitalStates.Active;
        record.Vitals.RestTicksRemaining = 0;
        record.Mode = record.Vitals.ResumeMode;
        this.monitor.Log($"HY-VITALS-{code}: {record.Identity} left bounded rest without numeric recovery.", LogLevel.Trace);
    }

    private void FinishFood(CompanionIdentity identity, string operationId, CompanionRecord record)
    {
        if (this.foodTransactions.TryGetValue(identity, out string? current) && current == operationId)
            this.foodTransactions.Remove(identity);
        if (record.ActiveTransactionId == operationId)
            record.ActiveTransactionId = null;
    }

    private void CancelFood(CompanionRecord record)
    {
        if (!this.foodTransactions.TryGetValue(record.Identity, out string? operationId))
            return;
        this.foodTransactions.Remove(record.Identity);
        if (record.ActiveTransactionId == operationId)
            record.ActiveTransactionId = null;
    }

    private bool IsFoodCurrent(CompanionIdentity identity, string operationId, CompanionRecord record) =>
        this.foodTransactions.TryGetValue(identity, out string? current)
        && current == operationId
        && record.ActiveTransactionId == operationId
        && record.Vitals.State is not (CompanionVitalStates.Downed or CompanionVitalStates.Recovering)
        && record.Vitals.Health > 0
        && this.CanMutate(identity, record);

    private bool TryGetWritable(CompanionIdentity identity, out CompanionRecord record, out VitalActionResult result)
    {
        if (!this.registry.TryGet(identity, out record!))
        {
            result = VitalActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist.");
            return false;
        }
        if (!identity.IsCanonical)
        {
            result = VitalActionResult.Failure("SINGLE-COMPANION-PER-OWNER", "Only the Owner's current Yui identity can mutate vitals.");
            return false;
        }
        if (!this.CanMutate(identity, record))
        {
            result = VitalActionResult.Failure("VITALS-WRITE-GATE", "A loaded host-authoritative save with free player control is required.");
            return false;
        }
        result = VitalActionResult.Success("VITALS-WRITABLE", $"{identity} passed the vitals write gate.");
        return true;
    }

    private bool CanMutate(CompanionIdentity identity, CompanionRecord record) =>
        Context.IsWorldReady
        && Context.IsMainPlayer
        && Context.IsPlayerFree
        && Game1.activeClickableMenu is null
        && this.getLifecycleState() == LifecycleState.SaveReady
        && this.canMutateSave()
        && record.OwnerId == identity.OwnerId
        && Game1.GetPlayer(identity.OwnerId, onlyOnline: true) is not null;

    private static int FindFoodIndex(IList<Item> bag)
    {
        for (int index = 0; index < bag.Count; index++)
        {
            if (bag[index] is SObject food && TryReadFood(food, out _, out _))
                return index;
        }
        return -1;
    }

    private static bool TryReadFood(SObject food, out int staminaGain, out int healthGain)
    {
        staminaGain = 0;
        healthGain = 0;
        if (food.Stack <= 0
            || food.bigCraftable.Value
            || food.Edibility == -300
            || food.modData.ContainsKey(StorageTags.ResponsibilityId)
            || food.modData.ContainsKey(StorageTags.ReturnPending))
            return false;
        staminaGain = food.staminaRecoveredOnConsumption();
        healthGain = food.healthRecoveredOnConsumption();
        return staminaGain > 0 || healthGain > 0;
    }

    private static bool HasNearbyMonster(NPC body, float tiles) => body.currentLocation?.characters.OfType<Monster>()
        .Any(monster => monster.Health > 0 && Vector2.DistanceSquared(monster.Tile, body.Tile) <= tiles * tiles) == true;

    private static int CurrentDay() => (int)Game1.Date.TotalDays;
}
