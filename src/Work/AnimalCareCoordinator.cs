using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Pathfinding;
using StardewValley.Tools;

namespace YuiToIssho;

internal readonly record struct CareCommandResult(bool IsSuccess, string Code, string Message)
{
    public static CareCommandResult Success(string code, string message) => new(true, code, message);
    public static CareCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class AnimalCareCoordinator
{
    private const int PathSearchLimit = 256;
    private const int RepathDelayTicks = 20;
    private const int StuckTimeoutTicks = 300;
    private const int MaximumPathAttempts = 5;
    private const int VanillaUsePower = 1;

    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly TaskExecutionService execution;
    private readonly TaskNavigationService navigation;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, CareTask> tasks = new();

    public AnimalCareCoordinator(CompanionBodyBinder bodies, CompanionInventoryStore inventories, CompanionVitalsCoordinator vitals, CompanionAppearanceCoordinator appearance, TaskExecutionService execution, TaskNavigationService navigation, IMonitor monitor)
    {
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.execution = execution;
        this.navigation = navigation;
        this.monitor = monitor;
    }

    public CareCommandResult TryStart(
        CompanionIdentity identity,
        string targetKindRaw,
        string targetIdRaw,
        string actionRaw,
        string operationId)
    {
        if (this.execution.TryResolveExisting(identity, operationId, out TaskExecutionResult existing))
            return FromExecution(existing);

        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return CareCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before animal care.");
        Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
        if (owner?.currentLocation is null || !ReferenceEquals(owner.currentLocation, body.currentLocation))
            return CareCommandResult.Failure("OWNER-LOCATION-MISMATCH", "Owner and companion must be in the same location when care starts.");

        if (!TryParseTarget(targetKindRaw, targetIdRaw, out CareTargetKey targetKey, out string targetFailure))
            return CareCommandResult.Failure("INVALID-CARE-TARGET", targetFailure);
        if (!TryParseAction(actionRaw, out CareAction action))
            return CareCommandResult.Failure("INVALID-CARE-ACTION", "Action must be pet, milk, or shear.");
        if (targetKey.Kind == CareTargetKind.Pet && action != CareAction.Pet)
            return CareCommandResult.Failure("PET-ACTION-BOUNDARY", "Pet identities only support pet; farm tools never target Pet.");

        Character? target = ResolveTarget(owner.currentLocation, targetKey);
        if (target is null)
            return CareCommandResult.Failure("ANIMAL-NOT-FOUND", $"{targetKey} is not present in the owner's location.");
        if (!this.ValidateStart(identity, owner, target, action, out Tool? tool, out ProduceSnapshot? produce, out string stateFailure))
            return CareCommandResult.Failure("CARE-PRECONDITION", stateFailure);
        TaskTargetKey targetReservation = new(owner.currentLocation.NameOrUniqueName, targetKey.Kind.ToString(), targetKey.Id);
        TaskBeginResult begin = this.execution.TryBegin(identity, operationId, "AnimalCare", targetReservation);
        if (!begin.Started || begin.Session is null)
            return FromExecution(begin.Result);

        this.tasks.Add(identity, new CareTask(begin.Session, targetKey, action, target, owner.currentLocation, owner, tool, produce, body.Position));
        this.monitor.Log($"HY-CARE-STARTED: {identity} reserved {targetReservation} for {action} operation {operationId}.", LogLevel.Info);
        return CareCommandResult.Success("STARTED", $"Animal care {operationId} started for {targetKey} ({action}).");
    }

    public void Update(ulong tick)
    {
        foreach (CareTask task in this.tasks.Values.ToArray())
            this.UpdateOne(task, tick);
    }

    public CareCommandResult Cancel(CompanionIdentity identity, string code)
    {
        if (!this.tasks.TryGetValue(identity, out CareTask? task))
            return CareCommandResult.Success("ALREADY-IDLE", $"{identity} has no animal-care task.");
        return this.Complete(task, code, $"Operation {task.OperationId} was cancelled before interaction.", false);
    }

    public Item? GetReservedTool(CompanionIdentity identity) => this.tasks.TryGetValue(identity, out CareTask? task) ? task.Tool : null;

    public bool CanStartFarmAnimalAction(CompanionIdentity identity, Farmer owner, FarmAnimal animal, string workKind)
    {
        CareAction? action = workKind switch
        {
            WorkKinds.Pet => CareAction.Pet,
            WorkKinds.Milk => CareAction.Milk,
            WorkKinds.Shear => CareAction.Shear,
            _ => null,
        };
        return action is CareAction resolved
            && this.ValidateStart(identity, owner, animal, resolved, out _, out _, out _);
    }

    public void CancelAll(string code)
    {
        foreach (CompanionIdentity identity in this.tasks.Keys.ToArray())
            this.Cancel(identity, code);
    }

    private void UpdateOne(CareTask task, ulong tick)
    {
        if (!this.execution.IsCurrent(task.Session))
        {
            this.execution.AbandonRuntime(task.Session);
            this.tasks.Remove(task.Identity);
            return;
        }
        if (!this.bodies.TryGetBody(task.Identity, out NPC body) || body.currentLocation is null || !ReferenceEquals(body.currentLocation, task.Location))
        {
            this.Complete(task, "BODY-INVALID", "The companion body became unavailable or changed location.", false);
            return;
        }
        if (task.Owner.currentLocation is null || !ReferenceEquals(task.Owner.currentLocation, task.Location))
        {
            this.Complete(task, "OWNER-LEFT-LOCATION", "The owner left the animal-care location.", false);
            return;
        }

        Character? resolved = ResolveTarget(task.Location, task.TargetKey);
        if (resolved is null || !ReferenceEquals(resolved, task.Target))
        {
            this.Complete(task, "ANIMAL-CHANGED", "The stable animal identity disappeared or resolved to a replacement instance.", false);
            return;
        }
        if (!this.ValidateSettlement(task, out string stateFailure))
        {
            this.Complete(task, "ANIMAL-STATE-CHANGED", stateFailure, false);
            return;
        }

        Vector2 targetTile = resolved.Tile;
        if (ManhattanDistance(body.TilePoint, targetTile.ToPoint()) == 1)
        {
            this.bodies.Halt(task.Identity);
            body.faceDirection(FacingToward(body.Tile, targetTile));
            this.Settle(task);
            return;
        }

        if (targetTile != task.LastTargetTile)
        {
            task.LastTargetTile = targetTile;
            this.bodies.Halt(task.Identity);
            task.Navigation.PathIssued = false;
            task.Navigation.LastPosition = body.Position;
            task.Navigation.LastProgressTick = tick;
            task.Navigation.NextPathTick = tick;
        }
        TaskNavigationResult progress = this.navigation.Observe(task.Identity, body, task.Navigation, tick, StuckTimeoutTicks, MaximumPathAttempts, RepathDelayTicks);
        if (progress.BudgetExhausted)
        {
            this.Complete(task, "PATH-BUDGET-EXHAUSTED", "The animal stayed unreachable through the bounded retry budget.", false);
            return;
        }
        if (!progress.CanIssuePath)
            return;

        Vector2? approach = FindApproachTile(task.Location, targetTile, body, resolved);
        if (approach is null)
        {
            this.Complete(task, "NO-LEGAL-NEIGHBOR", "The moving animal had no legal neighboring interaction tile.", false);
            return;
        }

        body.controller = new PathFindController(body, task.Location, approach.Value.ToPoint(), FacingToward(approach.Value, targetTile), null, PathSearchLimit);
        task.Session.MarkTraveling();
        this.navigation.MarkPathIssued(task.Navigation, body.Position, tick, RepathDelayTicks);
    }

    private bool ValidateSettlement(CareTask task, out string failure)
    {
        if (task.Target is Pet pet)
        {
            if (task.Owner.CurrentItem is not null)
            {
                failure = "Pet interaction requires the owner to hold no item, preventing hat or powder side effects.";
                return false;
            }
            if (WasPetToday(pet, task.Owner))
            {
                failure = "This Pet was already petted by the owner today.";
                return false;
            }
            failure = string.Empty;
            return true;
        }

        FarmAnimal animal = (FarmAnimal)task.Target;
        if (task.Action == CareAction.Pet)
        {
            if (animal.wasPet.Value || task.Owner.ActiveObject is not null || Game1.timeOfDay >= 1900)
            {
                failure = "FarmAnimal petting state, held object, or late-day sleep gate changed.";
                return false;
            }
            failure = string.Empty;
            return true;
        }

        if (task.Tool is null || !this.inventories.ContainsExact(task.Identity, task.Tool))
        {
            failure = "The exact reserved animal tool left this Yui's bag.";
            return false;
        }
        if (!animal.isAdult() || !animal.CanGetProduceWithTool(task.Tool) || task.Produce is null
            || animal.currentProduce.Value != task.Produce.ProduceId
            || animal.produceQuality.Value != task.Produce.Quality
            || animal.hasEatenAnimalCracker.Value != task.Produce.Cracker)
        {
            failure = "The animal's maturity, produce, quality, cracker state, or tool compatibility changed.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private void Settle(CareTask task)
    {
        if (!OwnerContextLease.CanProject(task.Owner))
            return;
        if (!task.Session.TryEnterSettlement())
            return;

        int facing = this.bodies.TryGetBody(task.Identity, out NPC visualBody) ? visualBody.FacingDirection : 2;
        if (task.Action == CareAction.Pet)
        {
            this.appearance.Prepare(task.Identity, task.OperationId, AppearanceActionKinds.Petting, task.Tool, facing);
            CareCommandResult result = this.SettlePet(task, facing);
            if (result.IsSuccess)
                this.appearance.Commit(task.Identity, task.OperationId);
            this.Complete(task, result.Code, result.Message, result.IsSuccess);
            return;
        }

        this.inventories.RequestTransfer(
            task.Identity,
            () => this.SettleProduceLocked(task),
            result =>
            {
                if (!this.tasks.TryGetValue(task.Identity, out CareTask? current) || !ReferenceEquals(current, task))
                    return;
                if (result.IsSuccess)
                    this.appearance.Commit(task.Identity, task.OperationId);
                this.Complete(task, result.Code, result.Message, result.IsSuccess);
            }
        );
    }

    private CareCommandResult SettlePet(CareTask task, int facing)
    {
        if (!this.execution.IsCurrent(task.Session))
            return CareCommandResult.Failure("SESSION-NOT-CURRENT", "The animal-care session is no longer current.");
        if (!this.ValidateSettlement(task, out string stateFailure))
            return CareCommandResult.Failure("ANIMAL-STATE-CHANGED", stateFailure);

        string actionKind = task.Action switch
        {
            CareAction.Milk => VitalActionKinds.Milking,
            CareAction.Shear => VitalActionKinds.Shearing,
            _ => VitalActionKinds.Petting,
        };
        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, actionKind, $"{task.OperationId}:care");
        if (!cost.IsSuccess)
            return CareCommandResult.Failure(cost.Result.Code, cost.Result.Message);

        bool vanillaInvoked = false;
        try
        {
            if (!this.bodies.TryGetBody(task.Identity, out NPC body))
                return CareCommandResult.Failure("BODY-INVALID", "The companion body disappeared before petting settlement.");
            using OwnerContextLease context = OwnerContextLease.Project(task.Owner, body.Position, facing, task.Location);
            if (task.Target is Pet pet)
            {
                vanillaInvoked = true;
                pet.checkAction(task.Owner, task.Location);
                bool petCommitted = WasPetToday(pet, task.Owner);
                if (!petCommitted)
                    return CareCommandResult.Failure("VANILLA-REJECTED", "Vanilla did not record the Pet interaction; no retry occurred.");
                cost.Commit();
                return CareCommandResult.Success("COMMITTED", "Vanilla recorded one Pet interaction for the owner today.");
            }

            FarmAnimal animal = (FarmAnimal)task.Target;
            vanillaInvoked = true;
            animal.pet(task.Owner, false);
            if (!animal.wasPet.Value)
                return CareCommandResult.Failure("VANILLA-REJECTED", "Vanilla did not pet the FarmAnimal; no retry occurred.");
            cost.Commit();
            return CareCommandResult.Success("COMMITTED", "Vanilla petted the FarmAnimal once.");
        }
        catch (Exception ex)
        {
            if (vanillaInvoked)
                cost.Commit();
            return CareCommandResult.Failure("SETTLEMENT-ERROR", $"Petting stopped without retry after {ex.GetType().Name}.");
        }
    }

    private InventoryActionResult SettleProduceLocked(CareTask task)
    {
        if (!this.execution.IsCurrent(task.Session))
            return InventoryActionResult.Failure("SESSION-NOT-CURRENT", "The animal-care session is no longer current.");
        if (!this.ValidateSettlement(task, out string stateFailure))
            return InventoryActionResult.Failure("ANIMAL-STATE-CHANGED", stateFailure);
        if (!this.bodies.TryGetBody(task.Identity, out NPC body))
            return InventoryActionResult.Failure("BODY-INVALID", "The companion body disappeared while the bag lock was pending.");
        FarmAnimal animal = (FarmAnimal)task.Target;
        if (!ReferenceEquals(body.currentLocation, task.Location)
            || !ReferenceEquals(animal.currentLocation, task.Location)
            || ManhattanDistance(body.TilePoint, animal.TilePoint) != 1)
        {
            return InventoryActionResult.Failure(
                "ANIMAL-POSITION-CHANGED",
                "The companion and animal are no longer cardinally adjacent after the bag lock was acquired."
            );
        }
        if (!OwnerContextLease.CanProject(task.Owner))
            return InventoryActionResult.Failure("OWNER-BUSY", "The owner started another action while animal care was waiting for the bag lock; the animal remains unchanged.");
        if (this.inventories.Count(task.Identity) >= CompanionInventoryStore.Capacity)
            return InventoryActionResult.Failure("BAG-FULL", "Yui's bag has no complete output slot; the animal's produce remains untouched.");
        if (task.Tool is not MilkPail && task.Tool is not Shears)
            return InventoryActionResult.Failure("TOOL-CHANGED", "The exact reserved animal tool is no longer compatible.");

        string actionKind = task.Action == CareAction.Milk ? VitalActionKinds.Milking : VitalActionKinds.Shearing;
        using VitalCostLease cost = this.vitals.ReserveCost(task.Identity, actionKind, $"{task.OperationId}:care");
        if (!cost.IsSuccess)
            return InventoryActionResult.Failure(cost.Result.Code, cost.Result.Message);

        int facing = FacingToward(body.Tile, animal.Tile);
        body.faceDirection(facing);
        string visualKind = task.Action == CareAction.Milk ? AppearanceActionKinds.Milking : AppearanceActionKinds.Shearing;
        this.appearance.Prepare(task.Identity, task.OperationId, visualKind, task.Tool, facing);
        IReadOnlyList<Item> outputs;
        Exception? settlementError = null;
        using (OwnerContextLease context = OwnerContextLease.Project(task.Owner, body.Position, facing, task.Location))
        using (FarmerInventoryIsolationLease inventory = FarmerInventoryIsolationLease.Begin(task.Owner))
        {
            try
            {
                if (task.Tool is MilkPail pail)
                {
                    FarmAnimal? previous = pail.animal;
                    try { pail.animal = animal; pail.DoFunction(task.Location, animal.StandingPixel.X, animal.StandingPixel.Y, VanillaUsePower, task.Owner); }
                    finally { pail.animal = previous; }
                }
                else if (task.Tool is Shears shears)
                {
                    FarmAnimal? previous = shears.animal;
                    try { shears.animal = animal; shears.DoFunction(task.Location, animal.StandingPixel.X, animal.StandingPixel.Y, VanillaUsePower, task.Owner); }
                    finally { shears.animal = previous; }
                }
            }
            catch (Exception ex)
            {
                settlementError = ex;
            }
            outputs = inventory.ExtractOutputs();
        }

        FarmAnimal settledAnimal = (FarmAnimal)task.Target;
        bool produceCleared = settledAnimal.currentProduce.Value is null;
        if (produceCleared)
            cost.Commit();

        foreach (Item output in outputs)
        {
            InventoryActionResult routed = this.inventories.StoreGeneratedOutput(task.Identity, output);
            if (!routed.IsSuccess)
                return routed;
        }

        if (settlementError is not null)
            return InventoryActionResult.Failure("SETTLEMENT-ERROR", $"Animal care stopped without retry after {settlementError.GetType().Name}.");
        if (!produceCleared)
            return InventoryActionResult.Failure("VANILLA-REJECTED", "Vanilla left the animal's produce unchanged; no retry or fabricated item occurred.");
        if (outputs.Count == 0)
            return InventoryActionResult.Failure("OUTPUT-MISSING", "Vanilla cleared the animal's produce without producing a traceable inventory output.");
        return InventoryActionResult.Success("COMMITTED", $"Vanilla produced {outputs.Count} exact stack(s), now owned by this Yui.");
    }

    private CareCommandResult Complete(CareTask task, string code, string message, bool success)
    {
        if (!success)
            this.appearance.Fail(task.Identity, task.OperationId, code);
        TaskExecutionResult result = this.execution.Complete(task.Session, success, code, message);
        this.tasks.Remove(task.Identity);
        this.monitor.Log($"HY-CARE-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
        return FromExecution(result);
    }

    private bool ValidateStart(CompanionIdentity identity, Farmer owner, Character target, CareAction action, out Tool? tool, out ProduceSnapshot? produce, out string failure)
    {
        tool = null;
        produce = null;
        if (target is Pet pet)
        {
            if (owner.CurrentItem is not null || WasPetToday(pet, owner))
            {
                failure = "Pet requires an empty hand and must not already be petted by this owner today.";
                return false;
            }
            failure = string.Empty;
            return true;
        }
        FarmAnimal animal = (FarmAnimal)target;
        if (action == CareAction.Pet)
        {
            if (animal.wasPet.Value || owner.ActiveObject is not null || Game1.timeOfDay >= 1900)
            {
                failure = "FarmAnimal must be unpetted, the owner must hold no active object, and it must be before 19:00.";
                return false;
            }
            failure = string.Empty;
            return true;
        }
        tool = action == CareAction.Milk ? this.inventories.FindFirst<MilkPail>(identity) : this.inventories.FindFirst<Shears>(identity);
        if (tool is null || this.inventories.Count(identity) >= CompanionInventoryStore.Capacity || !animal.isAdult() || !animal.CanGetProduceWithTool(tool) || animal.currentProduce.Value is null)
        {
            failure = "A matching real tool, an adult compatible animal with produce, and one complete output slot in this Yui's bag are required.";
            return false;
        }
        produce = new ProduceSnapshot(animal.currentProduce.Value, animal.produceQuality.Value, animal.hasEatenAnimalCracker.Value);
        failure = string.Empty;
        return true;
    }

    private static Character? ResolveTarget(GameLocation location, CareTargetKey key)
    {
        if (key.Kind == CareTargetKind.FarmAnimal && long.TryParse(key.Id, out long animalId))
            return location.animals.Values.FirstOrDefault(animal => animal.myID.Value == animalId);
        if (key.Kind == CareTargetKind.Pet && Guid.TryParse(key.Id, out Guid petId))
            return location.characters.OfType<Pet>().FirstOrDefault(pet => pet.petId.Value == petId);
        return null;
    }

    private static bool WasPetToday(Pet pet, Farmer owner) =>
        pet.lastPetDay.TryGetValue(owner.UniqueMultiplayerID, out int day) && day == Game1.Date.TotalDays;

    private static bool TryParseTarget(string kindRaw, string idRaw, out CareTargetKey key, out string failure)
    {
        if (kindRaw.Equals("animal", StringComparison.OrdinalIgnoreCase) && long.TryParse(idRaw, out long animalId))
        {
            key = new(CareTargetKind.FarmAnimal, animalId.ToString()); failure = string.Empty; return true;
        }
        if (kindRaw.Equals("pet", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(idRaw, out Guid petId))
        {
            key = new(CareTargetKind.Pet, petId.ToString("D")); failure = string.Empty; return true;
        }
        key = default; failure = "Use animal with a numeric myID, or pet with a petId GUID."; return false;
    }

    private static bool TryParseAction(string raw, out CareAction action) => Enum.TryParse(raw, true, out action);

    private static Vector2? FindApproachTile(GameLocation location, Vector2 target, NPC body, Character caredFor)
    {
        Vector2[] candidates = { target + new Vector2(1, 0), target + new Vector2(-1, 0), target + new Vector2(0, 1), target + new Vector2(0, -1) };
        return candidates.Where(tile => location.isTileLocationOpen(tile)
                && location.characters.All(character => ReferenceEquals(character, body) || ReferenceEquals(character, caredFor) || character.Tile != tile)
                && location.animals.Values.All(animal => ReferenceEquals(animal, caredFor) || animal.Tile != tile))
            .OrderBy(tile => ManhattanDistance(tile.ToPoint(), body.TilePoint)).Cast<Vector2?>().FirstOrDefault();
    }

    private static int FacingToward(Vector2 from, Vector2 to)
    {
        Vector2 delta = to - from;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y)) return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }

    private static int ManhattanDistance(Point left, Point right) => Math.Abs(left.X - right.X) + Math.Abs(left.Y - right.Y);

    private static CareCommandResult FromExecution(TaskExecutionResult result) => result.IsSuccess
        ? CareCommandResult.Success(result.Code, result.Message)
        : CareCommandResult.Failure(result.Code, result.Message);

    private enum CareTargetKind { FarmAnimal, Pet }
    private enum CareAction { Pet, Milk, Shear }
    private readonly record struct CareTargetKey(CareTargetKind Kind, string Id) { public override string ToString() => $"{this.Kind}:{this.Id}"; }
    private sealed record ProduceSnapshot(string ProduceId, int Quality, bool Cracker);

    private sealed class CareTask
    {
        public CareTask(TaskSession session, CareTargetKey targetKey, CareAction action, Character target, GameLocation location, Farmer owner, Tool? tool, ProduceSnapshot? produce, Vector2 position)
        { this.Session = session; this.TargetKey = targetKey; this.Action = action; this.Target = target; this.Location = location; this.Owner = owner; this.Tool = tool; this.Produce = produce; this.Navigation = new TaskNavigationState(position, 0); this.LastTargetTile = target.Tile; }
        public TaskSession Session { get; }
        public CompanionIdentity Identity => this.Session.Identity;
        public string OperationId => this.Session.OperationId;
        public CareTargetKey TargetKey { get; }
        public CareAction Action { get; }
        public Character Target { get; }
        public GameLocation Location { get; }
        public Farmer Owner { get; }
        public Tool? Tool { get; }
        public ProduceSnapshot? Produce { get; }
        public TaskNavigationState Navigation { get; }
        public Vector2 LastTargetTile { get; set; }
    }
}
