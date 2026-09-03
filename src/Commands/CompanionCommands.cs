using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Monsters;
using StardewValley.Tools;

namespace YuiToIssho;

internal sealed class CompanionCommands
{
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionInventoryStore inventories;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly CompanionStorageCoordinator storage;
    private readonly WateringCoordinator watering;
    private readonly ChoppingCoordinator chopping;
    private readonly MiningCoordinator mining;
    private readonly HarvestCoordinator harvesting;
    private readonly ForageCoordinator foraging;
    private readonly MowingCoordinator mowing;
    private readonly DiggingCoordinator digging;
    private readonly AnimalCareCoordinator animalCare;
    private readonly FishingCoordinator fishing;
    private readonly CombatCoordinator combat;
    private readonly DeliveryCoordinator delivery;
    private readonly CraftingCoordinator crafting;
    private readonly PlantingCoordinator planting;
    private readonly CompanionWorkTaskRouter workTasks;
    private readonly CompanionWorkCoordinator work;
    private readonly CompanionOwnerWorkAssistCoordinator assist;
    private readonly TaskExecutionService taskExecution;
    private readonly IMonitor monitor;
    private readonly Func<LifecycleState> getLifecycleState;
    private readonly Func<bool> canMutateSave;
    private readonly CompanionMultiplayerCoordinator multiplayer;
    private readonly bool experimentalFeaturesEnabled;
    private readonly bool naturalWorkAssistEnabled;
    private Action<CompanionIdentity, string>? interruptAgent;
    private bool captureResult;
    private NetworkCommandResult capturedResult;

    public CompanionCommands(
        CompanionRegistry registry,
        CompanionBodyBinder bodies,
        CompanionInventoryStore inventories,
        CompanionVitalsCoordinator vitals,
        CompanionAppearanceCoordinator appearance,
        CompanionStorageCoordinator storage,
        WateringCoordinator watering,
        ChoppingCoordinator chopping,
        MiningCoordinator mining,
        HarvestCoordinator harvesting,
        ForageCoordinator foraging,
        MowingCoordinator mowing,
        DiggingCoordinator digging,
        AnimalCareCoordinator animalCare,
        FishingCoordinator fishing,
        CombatCoordinator combat,
        DeliveryCoordinator delivery,
        CraftingCoordinator crafting,
        PlantingCoordinator planting,
        CompanionWorkTaskRouter workTasks,
        CompanionWorkCoordinator work,
        CompanionOwnerWorkAssistCoordinator assist,
        TaskExecutionService taskExecution,
        CompanionMultiplayerCoordinator multiplayer,
        bool experimentalFeaturesEnabled,
        bool naturalWorkAssistEnabled,
        IMonitor monitor,
        Func<LifecycleState> getLifecycleState,
        Func<bool> canMutateSave)
    {
        this.registry = registry;
        this.bodies = bodies;
        this.inventories = inventories;
        this.vitals = vitals;
        this.appearance = appearance;
        this.storage = storage;
        this.watering = watering;
        this.chopping = chopping;
        this.mining = mining;
        this.harvesting = harvesting;
        this.foraging = foraging;
        this.mowing = mowing;
        this.digging = digging;
        this.animalCare = animalCare;
        this.fishing = fishing;
        this.combat = combat;
        this.delivery = delivery;
        this.crafting = crafting;
        this.planting = planting;
        this.workTasks = workTasks;
        this.work = work;
        this.assist = assist;
        this.taskExecution = taskExecution;
        this.multiplayer = multiplayer;
        this.experimentalFeaturesEnabled = experimentalFeaturesEnabled;
        this.naturalWorkAssistEnabled = naturalWorkAssistEnabled;
        this.monitor = monitor;
        this.getLifecycleState = getLifecycleState;
        this.canMutateSave = canMutateSave;
    }

    public void Register(ICommandHelper commands)
    {
        commands.Add(
            "yui",
            "Manage Yui. Usage: yui [help|status|summon|dismiss|follow|stay|assist <on|off|status>]",
            this.OnCommand
        );
    }

    public void AttachAgentInterrupt(Action<CompanionIdentity, string> handler)
    {
        this.interruptAgent = handler;
    }

    public NetworkCommandResult ExecuteNekoAction(string bridgeRequestId, string action, IReadOnlyDictionary<string, string> arguments)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || this.getLifecycleState() != LifecycleState.SaveReady)
            return NetworkCommandResult.Failure("LIFECYCLE-GATE", "A loaded authoritative host world is required.");
        if (!this.canMutateSave())
            return NetworkCommandResult.Failure("SAVE-DATA-READ-ONLY", "The save did not pass authoritative schema validation.");
        if (!this.experimentalFeaturesEnabled && IsExperimentalNekoAction(action))
            return NetworkCommandResult.Failure("EXPERIMENTAL-FEATURE-DISABLED", "This N.E.K.O action is outside the focused companion experience. Enable experimental features to use it.");

        string command;
        bool acceptsArguments = false;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (action)
        {
            case "summon": command = "summon"; break;
            case "recall": command = "recall"; break;
            case "follow": command = "follow"; break;
            case "wait": command = "wait"; break;
            case "stop": command = "stop"; break;
            case "work_stop": command = "work-stop"; break;
            case "work_resume": command = "work-resume"; break;
            case "assist_start": command = "assist-start"; break;
            case "assist_status": command = "assist-status"; break;
            case "assist_stop": command = "assist-stop"; break;
            case "work":
                acceptsArguments = true;
                if (arguments.Keys.Any(key => key is not ("kind" or "radius" or "completion_policy")))
                    return NetworkCommandResult.Failure("FIELD-NOT-ALLOWED", "Work accepts only kind, radius, and completion_policy.");
                if (!arguments.TryGetValue("kind", out string? requestedKind) || !WorkKinds.TryNormalize(requestedKind, out string kind))
                    return NetworkCommandResult.Failure("WORK-KIND-NOT-ALLOWED", "Work kind is not in the continuous-work allowlist.");
                string radiusText = arguments.GetValueOrDefault("radius", WorkScopeContracts.DefaultRadius.ToString());
                if (!int.TryParse(radiusText, out int radius) || radius is < WorkScopeContracts.MinimumRadius or > WorkScopeContracts.MaximumRadius)
                    return NetworkCommandResult.Failure("WORK-RADIUS-OUT-OF-RANGE", $"Radius must be {WorkScopeContracts.MinimumRadius} through {WorkScopeContracts.MaximumRadius}.");
                string policyText = arguments.GetValueOrDefault("completion_policy", "until-clear");
                if (!WorkCompletionPolicies.TryNormalizeContinuous(policyText, out string policy))
                    return NetworkCommandResult.Failure("WORK-POLICY-NOT-ALLOWED", "Completion policy must be until-clear or until-stopped.");
                Farmer owner = Game1.player;
                if (owner.currentLocation is null)
                    return NetworkCommandResult.Failure("OWNER-LOCATION-MISSING", "The host player has no current location.");
                command = "work-start";
                fields["locationKey"] = owner.currentLocation.NameOrUniqueName;
                fields["anchorX"] = owner.TilePoint.X.ToString();
                fields["anchorY"] = owner.TilePoint.Y.ToString();
                fields["shape"] = WorkScopeShapes.Radius;
                fields["radius"] = radius.ToString();
                fields["kind"] = kind;
                fields["policy"] = policy;
                break;
            case "plant_options":
                acceptsArguments = true;
                if (arguments.Keys.Any(key => key != "query"))
                    return NetworkCommandResult.Failure("FIELD-NOT-ALLOWED", "Plant options accepts only query.");
                command = "plant-options";
                if (arguments.TryGetValue("query", out string? query))
                    fields["query"] = query;
                break;
            case "plant_action":
                acceptsArguments = true;
                if (!arguments.TryGetValue("mode", out string? mode) || mode is not ("preview" or "start" or "status" or "resume" or "cancel"))
                    return NetworkCommandResult.Failure("PLANT-MODE-NOT-ALLOWED", "Plant action mode must be preview, start, status, resume, or cancel.");
                if (mode is "status" or "resume" or "cancel")
                {
                    if (arguments.Keys.Any(key => key != "mode"))
                        return NetworkCommandResult.Failure("FIELD-NOT-ALLOWED", $"Plant {mode} accepts no fields besides mode.");
                    command = $"plant-{mode}";
                    break;
                }
                if (arguments.Keys.Any(key => key is not ("mode" or "seed_option_id" or "count" or "radius")))
                    return NetworkCommandResult.Failure("FIELD-NOT-ALLOWED", $"Plant {mode} accepts only seed_option_id, count, and radius.");
                if (!arguments.TryGetValue("seed_option_id", out string? seedOptionId)
                    || !arguments.TryGetValue("count", out string? countText)
                    || !arguments.TryGetValue("radius", out string? plantRadius))
                    return NetworkCommandResult.Failure("PLANT-FIELDS-MISSING", $"Plant {mode} requires seed_option_id, count, and radius.");
                command = $"plant-{mode}";
                fields["seedOptionId"] = seedOptionId;
                fields["count"] = countText;
                fields["radius"] = plantRadius;
                if (mode == "start")
                    fields["operationId"] = $"neko-plant-{Guid.NewGuid():N}";
                break;
            case "combat_options":
                acceptsArguments = true;
                if (arguments.Keys.Any(key => key != "radius"))
                    return NetworkCommandResult.Failure("FIELD-NOT-ALLOWED", "Combat options accepts only radius.");
                command = "combat-options";
                if (arguments.TryGetValue("radius", out string? optionRadius))
                    fields["radius"] = optionRadius;
                break;
            case "combat_action":
                acceptsArguments = true;
                if (arguments.Keys.Any(key => key is not ("action" or "combat_option_id" or "radius" or "duration_seconds" or "maximum_swings")))
                    return NetworkCommandResult.Failure("FIELD-NOT-ALLOWED", "Combat action contains a field outside the bounded allowlist.");
                if (!arguments.TryGetValue("action", out string? combatAction) || combatAction is not ("strike" or "guard" or "status" or "stop"))
                    return NetworkCommandResult.Failure("COMBAT-ACTION-NOT-ALLOWED", "Combat action must be strike, guard, status, or stop.");
                command = $"combat-{combatAction}";
                if (combatAction == "strike")
                {
                    if (!arguments.TryGetValue("combat_option_id", out string? combatOptionId)
                        || arguments.Keys.Any(key => key is not ("action" or "combat_option_id")))
                        return NetworkCommandResult.Failure("COMBAT-FIELDS-MISSING", "Combat strike requires only action and combat_option_id.");
                    fields["combatOptionId"] = combatOptionId;
                    fields["operationId"] = $"neko-combat-{bridgeRequestId}";
                }
                else if (combatAction == "guard")
                {
                    if (!arguments.TryGetValue("radius", out string? guardRadius)
                        || !arguments.TryGetValue("duration_seconds", out string? durationSeconds)
                        || !arguments.TryGetValue("maximum_swings", out string? maximumSwings)
                        || arguments.Keys.Any(key => key is not ("action" or "radius" or "duration_seconds" or "maximum_swings")))
                        return NetworkCommandResult.Failure("COMBAT-FIELDS-MISSING", "Combat guard requires only action, radius, duration_seconds, and maximum_swings.");
                    fields["radius"] = guardRadius;
                    fields["seconds"] = durationSeconds;
                    fields["maximumSwings"] = maximumSwings;
                    fields["operationId"] = $"neko-combat-{bridgeRequestId}";
                }
                else if (arguments.Keys.Any(key => key != "action"))
                    return NetworkCommandResult.Failure("FIELD-NOT-ALLOWED", $"Combat {combatAction} accepts only action.");
                break;
            default:
                return NetworkCommandResult.Failure("UNKNOWN-ACTION", "The requested N.E.K.O action is not in the bridge allowlist.");
        }

        if (!acceptsArguments && arguments.Count != 0)
            return NetworkCommandResult.Failure("FIELD-NOT-ALLOWED", $"Action {action} does not accept arguments.");
        CompanionIdentity identity = CompanionIdentity.ForOwner(Game1.player.UniqueMultiplayerID);
        return this.multiplayer.Submit(identity, command, fields);
    }

    private static bool IsExperimentalNekoAction(string action) => action is
        "work" or "work_stop" or "work_resume" or
        "plant_options" or "plant_action" or
        "combat_options" or "combat_action";

    private void OnCommand(string command, string[] args)
    {
        string action = NormalizeConsoleAliases(args.FirstOrDefault()?.ToLowerInvariant() ?? "help", ref args);
        if (action == "help")
        {
            this.ShowHelp(args.Length >= 2 && args[1].Equals("advanced", StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (!Context.IsWorldReady || this.getLifecycleState() != LifecycleState.SaveReady)
        {
            this.Log(false, "LIFECYCLE-GATE", "A loaded, non-saving world is required.");
            return;
        }

        if (action == "list")
        {
            if (Context.IsMainPlayer)
                this.ListOwned();
            else
                this.Log(false, "CLIENT-QUERY-DEFERRED", "Farmhand state is supplied by the authoritative snapshot projection.");
            return;
        }

        if (!this.experimentalFeaturesEnabled && IsExperimentalConsoleAction(action))
        {
            this.Log(false, "EXPERIMENTAL-FEATURE-DISABLED", "This command is available only when EnableExperimentalFeatures is true. Use 'yui help' for everyday commands.");
            return;
        }

        args = InjectCanonicalSlot(action, args);

        CompanionIdentity identity = CompanionIdentity.ForOwner(Game1.player.UniqueMultiplayerID);
        if (IsLocalQuery(action, args))
        {
            if (!Context.IsMainPlayer)
            {
                this.Log(false, "CLIENT-QUERY-DEFERRED", "Farmhand state is supplied by the authoritative snapshot projection.");
                return;
            }
            if (!this.canMutateSave())
            {
                this.Log(false, "SAVE-DATA-READ-ONLY", "Companion state is unavailable because this save did not pass validation.");
                return;
            }
            this.RunLocalQuery(identity, action, args);
            return;
        }

        if (!TryBuildMutation(action, args, out string routedCommand, out Dictionary<string, string> fields, out string error))
        {
            this.Log(false, "INVALID-COMMAND", error);
            return;
        }
        NetworkCommandResult result = this.multiplayer.Submit(identity, routedCommand, fields);
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    public NetworkCommandResult ExecuteAuthoritative(ValidatedCommandRequest request)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || this.getLifecycleState() != LifecycleState.SaveReady)
            return NetworkCommandResult.Failure("HOST-LIFECYCLE-GATE", "A loaded, non-saving authoritative host is required.");
        if (!this.canMutateSave())
            return NetworkCommandResult.Failure("SAVE-DATA-READ-ONLY", "The save did not pass authoritative schema validation.");
        if (!this.experimentalFeaturesEnabled && !IsEverydayRoutedCommand(request.Command))
            return NetworkCommandResult.Failure("EXPERIMENTAL-FEATURE-DISABLED", "The host has not enabled experimental commands.");
        bool readOnlyRequest = request.Command is "assist-status" or "work-status" or "craft-list" or "craft-preview" or "craft-status" or "plant-options" or "plant-preview" or "plant-status" or "operation-status";
        bool craftMenuRequest = request.Command.StartsWith("craft-", StringComparison.Ordinal) && Game1.activeClickableMenu is CompanionCraftingMenu;
        if ((!Context.IsPlayerFree || Game1.activeClickableMenu is not null) && !craftMenuRequest && !readOnlyRequest)
            return NetworkCommandResult.Failure("PLAYER-BUSY", "The host lifecycle is not free for a new authoritative request.");
        if (request.Identity.OwnerId != request.SenderPlayerId)
            return NetworkCommandResult.Failure("NOT-OWNER", "The sender does not own the requested companion.");
        if (!request.Identity.IsCanonical)
            return NetworkCommandResult.Failure("SINGLE-COMPANION-PER-OWNER", "The request identity is not the Owner's current Yui.");
        Farmer? owner = Game1.GetPlayer(request.SenderPlayerId, onlyOnline: true);
        if (owner is null || owner.UniqueMultiplayerID != request.Identity.OwnerId)
            return NetworkCommandResult.Failure("OWNER-OFFLINE", "The exact Owner Farmer is not online on the host.");

        string[] coordinatorArgs = BuildCoordinatorArgs(request);
        if (IsAgentMutation(request.Command))
            this.interruptAgent?.Invoke(request.Identity, $"MANUAL-{request.Command.ToUpperInvariant()}");
        this.captureResult = true;
        this.capturedResult = default;
        try
        {
            switch (request.Command)
            {
                case "summon": this.Summon(request.Identity, owner); break;
                case "recall": this.Recall(request.Identity); break;
                case "delete": this.Delete(request.Identity); break;
                case "follow": this.SetMode(request.Identity, CompanionModes.Follow, "FOLLOWING"); break;
                case "wait": this.SetMode(request.Identity, CompanionModes.Wait, "WAITING"); break;
                case "stop": this.SetMode(request.Identity, CompanionModes.Wait, "STOPPED"); break;
                case "assist-start": this.AssistStart(request.Identity, owner); break;
                case "assist-status": this.LogAssist(this.assist.Status(request.Identity)); break;
                case "assist-stop": this.AssistStop(request.Identity); break;
                case "work-start": this.WorkStart(request, owner); break;
                case "work-status": this.LogWork(this.work.Status(request.Identity)); break;
                case "work-resume": this.WorkResume(request.Identity); break;
                case "work-stop": this.WorkStop(request.Identity, "PLAYER-STOPPED"); break;
                case "cursor-single": this.CursorSingle(request, owner); break;
                case "bag-give":
                case "bag-take": this.Bag(request.Identity, coordinatorArgs, owner); break;
                case "storage-authorize":
                case "storage-unauthorize":
                case "storage-borrow":
                case "storage-take-material":
                case "storage-return": this.Storage(request.Identity, coordinatorArgs, owner); break;
                case "delivery-create":
                case "delivery-offer":
                case "delivery-return": this.Delivery(request.Identity, coordinatorArgs); break;
                case "craft-list": this.LogCraft(this.crafting.List(request.Identity, owner)); break;
                case "craft-preview": this.LogCraft(this.crafting.Preview(request.Identity, owner, request.Fields["recipeKey"], request.Fields.TryGetValue("craftCount", out string? count) ? int.Parse(count) : 1)); break;
                case "craft-status": this.LogCraft(this.crafting.Status(request.Identity)); break;
                case "operation-status": this.LogTask(this.taskExecution.GetOperationStatus(request.Identity, request.Fields["operationId"])); break;
                case "craft-start": this.LogCraft(this.crafting.Start(request.Identity, owner, request.Fields["recipeKey"], int.Parse(request.Fields["craftCount"]), request.Fields["operationId"])); break;
                case "craft-cancel": this.LogCraft(this.crafting.Cancel(request.Identity)); break;
                case "plant-options": this.PlantOptions(request.Identity, owner, request.Fields.GetValueOrDefault("query")); break;
                case "plant-preview": this.PlantPreview(request.Identity, owner, request); break;
                case "plant-status": this.LogPlant(this.planting.Status(request.Identity)); break;
                case "plant-start": this.PlantStart(request.Identity, owner, request); break;
                case "plant-resume": this.LogPlant(this.planting.Resume(request.Identity, owner)); break;
                case "plant-cancel": this.LogPlant(this.planting.Cancel(request.Identity, owner)); break;
                case "vitals-eat":
                case "vitals-rest": this.Vitals(request.Identity, coordinatorArgs); break;
                case "water": this.Water(request.Identity, coordinatorArgs); break;
                case "chop": this.Chop(request.Identity, coordinatorArgs); break;
                case "mine": this.Mine(request.Identity, coordinatorArgs); break;
                case "harvest": this.Harvest(request.Identity, coordinatorArgs); break;
                case "forage": this.Forage(request.Identity, coordinatorArgs); break;
                case "mow": this.Mow(request.Identity, coordinatorArgs); break;
                case "dig": this.Dig(request.Identity, coordinatorArgs); break;
                case "care": this.Care(request.Identity, coordinatorArgs); break;
                case "fish": this.Fish(request.Identity, coordinatorArgs); break;
                case "fight": this.Fight(request.Identity, coordinatorArgs); break;
                case "combat-options":
                case "combat-strike":
                case "combat-guard":
                case "combat-status":
                case "combat-stop": this.Combat(request.Identity, coordinatorArgs); break;
                default: return NetworkCommandResult.Failure("UNKNOWN-COMMAND", "The validated command has no domain route.");
            }
            return string.IsNullOrEmpty(this.capturedResult.Code)
                ? NetworkCommandResult.Success("REQUEST-ACCEPTED", $"The host accepted {request.Command} for {request.Identity}.")
                : this.capturedResult;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"HY-CMD-ROUTE-FAILED: {request.Identity} {request.Command} stopped with {ex.GetType().Name}.", LogLevel.Error);
            return NetworkCommandResult.Failure("COMMAND-ROUTE-FAILED", "The host command route stopped without an automatic retry.");
        }
        finally
        {
            this.captureResult = false;
        }
    }

    private void Summon(CompanionIdentity identity, Farmer owner)
    {
        if (!this.vitals.CanSummon(identity, out VitalActionResult gate))
        {
            this.Log(false, gate.Code, gate.Message);
            return;
        }
        CompanionRecord record = this.registry.GetOrCreate(identity);
        InventoryValidationResult starterResult = this.inventories.EnsureStarterTools(identity);
        if (!starterResult.IsSuccess)
        {
            this.Log(false, starterResult.Code, starterResult.Message);
            return;
        }
        BodyBindResult result = this.bodies.Bind(record, owner);
        if (result.IsSuccess)
        {
            record.WantsBody = true;
            if (this.naturalWorkAssistEnabled)
                this.assist.ArmNatural(identity);
        }
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Recall(CompanionIdentity identity)
    {
        this.planting.Pause(identity, "RECALLED");
        this.WorkStop(identity, "RECALLED", logResult: false);
        this.appearance.Clear(identity, "RECALLED");
        this.vitals.HandleRecall(identity);
        this.watering.Cancel(identity, "CANCELLED-BY-RECALL");
        this.chopping.Cancel(identity, "CANCELLED-BY-RECALL");
        this.mining.Cancel(identity, "CANCELLED-BY-RECALL");
        this.harvesting.Cancel(identity, "CANCELLED-BY-RECALL");
        this.foraging.Cancel(identity, "CANCELLED-BY-RECALL");
        this.mowing.Cancel(identity, "CANCELLED-BY-RECALL");
        this.digging.Cancel(identity, "CANCELLED-BY-RECALL");
        this.animalCare.Cancel(identity, "CANCELLED-BY-RECALL");
        this.fishing.Cancel(identity, "CANCELLED-BY-RECALL");
        this.combat.Cancel(identity, "CANCELLED-BY-RECALL");
        this.delivery.Cancel(identity, "CANCELLED-BY-RECALL");
        this.storage.Cancel(identity, "CANCELLED-BY-RECALL");
        if (this.registry.TryGet(identity, out CompanionRecord record))
            record.WantsBody = false;

        BodyBindResult result = this.bodies.Unbind(identity);
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Delete(CompanionIdentity identity)
    {
        if (this.inventories.HasOutstandingOutputs(identity))
        {
            this.Log(false, "OUTPUT-RESPONSIBILITY", $"{identity} still owns {this.inventories.PendingOutputCount(identity)} Pending Output, {this.inventories.RecoveryVaultCount(identity)} Recovery Vault, {this.inventories.EscrowCount(identity)} Delivery Escrow, {this.inventories.CraftEscrowCount(identity)} Craft Escrow, and {this.inventories.PlantEscrowCount(identity)} Plant Escrow stack(s).");
            return;
        }

        if (this.inventories.HasItems(identity))
        {
            this.Log(false, "INVENTORY-RESPONSIBILITY", $"{identity} still owns {this.inventories.Count(identity)} real item stack(s); take them before deletion.");
            return;
        }

        if (this.registry.TryGet(identity, out CompanionRecord record)
            && (record.Inventory.Count > 0
                || record.PendingResponsibilities.Count > 0
                || record.StorageLiabilities.Count > 0
                || record.Deliveries.Any(delivery => DeliveryPhases.OwnsEscrow(delivery.Phase))
                || !string.IsNullOrWhiteSpace(record.Vitals.RecoveryEpisodeId)
                || !string.IsNullOrWhiteSpace(record.ActiveTransactionId)))
        {
            DeleteResult guarded = this.registry.Delete(identity);
            this.Log(guarded.IsSuccess, guarded.Code, guarded.Message);
            return;
        }

        this.appearance.Clear(identity, "DELETED");
        this.bodies.Unbind(identity);
        DeleteResult result = this.registry.Delete(identity);
        if (result.IsSuccess)
            this.inventories.RemoveStarterTools(identity);
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Bag(CompanionIdentity identity, string[] args, Farmer owner)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
        {
            this.Log(false, "IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
            return;
        }

        string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : "list";
        if (operation == "list")
        {
            IReadOnlyList<string> lines = this.inventories.Describe(identity);
            this.Log(true, "BAG", $"{identity} bag has {lines.Count}/{CompanionInventoryStore.Capacity} real stack(s), namespace={CompanionInventoryStore.GetNamespace(identity)}.");
            foreach (string line in lines)
                this.Log(true, "BAG-ITEM", line);
            return;
        }

        if (operation is not ("give" or "take") || args.Length < 4 || !int.TryParse(args[3], out int oneBasedSlot))
        {
            this.Log(false, "INVALID-BAG-COMMAND", "Usage: yui bag <list|give <playerSlot>|take <bagSlot>>.");
            return;
        }

        this.inventories.RequestTransfer(
            identity,
            transfer: () =>
            {
                if (!Context.IsWorldReady || !Context.IsMainPlayer || this.getLifecycleState() != LifecycleState.SaveReady || !this.canMutateSave())
                    return InventoryActionResult.Failure("TRANSFER-GATE-CLOSED", "Lifecycle, authority, or save validation changed before the bag lock was acquired.");
                if (!ReferenceEquals(Game1.GetPlayer(identity.OwnerId, onlyOnline: true), owner))
                    return InventoryActionResult.Failure("OWNER-OFFLINE", "The exact Owner disconnected before the bag lock was acquired.");
                if (!this.registry.TryGet(identity, out CompanionRecord currentRecord))
                    return InventoryActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} no longer exists.");
                if (!string.IsNullOrWhiteSpace(currentRecord.ActiveTransactionId))
                    return InventoryActionResult.Failure("COMPANION-BUSY", $"{identity} must finish or stop transaction {currentRecord.ActiveTransactionId} before moving items.");
                if (Game1.activeClickableMenu is not null || !Context.IsPlayerFree)
                    return InventoryActionResult.Failure("PLAYER-BUSY", "Close menus and return player control before moving real items.");

                return operation == "give"
                    ? this.inventories.TryGive(identity, owner, oneBasedSlot)
                    : this.inventories.TryTake(identity, owner, oneBasedSlot);
            },
            completed: result => this.Log(result.IsSuccess, result.Code, result.Message)
        );
    }

    private void Storage(CompanionIdentity identity, string[] args, Farmer owner)
    {
        string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : "status";
        switch (operation)
        {
            case "status":
                foreach (string line in this.storage.Describe(identity))
                    this.Log(true, "STORAGE-STATUS", line);
                return;
            case "authorize":
            case "unauthorize":
                if (args.Length < 5 || !int.TryParse(args[3], out int tileX) || !int.TryParse(args[4], out int tileY))
                {
                    this.Log(false, "INVALID-STORAGE-CHEST", "Usage: yui storage <authorize|unauthorize> <tileX> <tileY>.");
                    return;
                }
                this.storage.SetAuthorization(identity, owner, tileX, tileY, operation == "authorize", result => this.Log(result.IsSuccess, result.Code, result.Message));
                return;
            case "borrow":
                if (args.Length < 4)
                {
                    this.Log(false, "INVALID-STORAGE-BORROW", "Usage: yui storage borrow <qualifiedItemId>.");
                    return;
                }
                if (this.CheckVitals(identity, VitalActionKinds.Foraging))
                    this.LogStorage(this.storage.TryBorrowTool(identity, args[3]));
                return;
            case "take-material":
                if (args.Length < 5 || !int.TryParse(args[4], out int count))
                {
                    this.Log(false, "INVALID-MATERIAL-REQUEST", "Usage: yui storage take-material <qualifiedItemId> <count>.");
                    return;
                }
                if (this.CheckVitals(identity, VitalActionKinds.Foraging))
                    this.LogStorage(this.storage.TryTakeMaterial(identity, args[3], count));
                return;
            case "return":
                if (args.Length < 4)
                {
                    this.Log(false, "INVALID-STORAGE-RETURN", "Usage: yui storage return <responsibilityId>.");
                    return;
                }
                this.LogStorage(this.storage.RequestReturn(identity, args[3]));
                return;
            default:
                this.Log(false, "INVALID-STORAGE-COMMAND", "Use storage status, authorize, unauthorize, borrow, take-material, or return.");
                return;
        }
    }

    private void Vitals(CompanionIdentity identity, string[] args)
    {
        string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : "status";
        switch (operation)
        {
            case "status":
                foreach (string line in this.vitals.Describe(identity))
                    this.Log(true, "VITALS-STATUS", line);
                return;
            case "eat":
                int? slot = null;
                if (args.Length >= 4)
                {
                    if (!int.TryParse(args[3], out int parsed) || parsed <= 0)
                    {
                        this.Log(false, "INVALID-FOOD-SLOT", "Usage: yui vitals eat [oneBasedBagSlot].");
                        return;
                    }
                    slot = parsed;
                }
                VitalActionResult scheduled = this.vitals.RequestEat(identity, slot, result => this.Log(result.IsSuccess, result.Code, result.Message));
                this.Log(scheduled.IsSuccess, scheduled.Code, scheduled.Message);
                return;
            case "rest":
                int seconds = 4;
                if (args.Length >= 4 && !int.TryParse(args[3], out seconds))
                {
                    this.Log(false, "INVALID-REST-DURATION", "Usage: yui vitals rest [seconds 2..8].");
                    return;
                }
                VitalActionResult rest = this.vitals.TryStartRest(identity, seconds);
                this.Log(rest.IsSuccess, rest.Code, rest.Message);
                return;
            default:
                this.Log(false, "INVALID-VITALS-COMMAND", "Use vitals status, eat [bagSlot], or rest [seconds].");
                return;
        }
    }

    public NetworkCommandResult RunNearestDiagnostic(CompanionIdentity identity, string action)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || this.getLifecycleState() != LifecycleState.SaveReady || !this.canMutateSave())
            return NetworkCommandResult.Failure("DIAGNOSTIC-GATE-CLOSED", "A writable host world is required for test actions.");
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return NetworkCommandResult.Failure("IDENTITY-NOT-FOUND", $"{identity} does not exist.");
        if (action == "plant-preview")
        {
            Farmer? owner = Game1.GetPlayer(identity.OwnerId, onlyOnline: true);
            if (owner?.currentLocation is null)
                return NetworkCommandResult.Failure("OWNER-OFFLINE", "The exact Owner must be online for a planting preview.");
            PlantSeedOptionsResult options = this.planting.GetOptions(identity, owner, null);
            PlantSeedOption selected = options.Options.FirstOrDefault(option => option.PlantableHere);
            if (!options.IsSuccess || string.IsNullOrEmpty(selected.SeedOptionId))
                return NetworkCommandResult.Failure(options.Code, options.Message);
            PlantingPreviewResult preview = this.planting.Preview(identity, owner, selected.SeedOptionId, 1, RadiusPlantingScope(owner, WorkScopeContracts.DefaultRadius));
            return new NetworkCommandResult(preview.IsSuccess, preview.Code, $"{selected.CropDisplayName}: {preview.Message}", preview.IsSuccess ? new PlantingCommandPayload
            {
                Preview = new PlantingPreviewDto
                {
                    SeedOptionId = preview.SeedOptionId,
                    SeedDisplayName = preview.SeedDisplayName,
                    CropDisplayName = preview.CropDisplayName,
                    RequestedCount = preview.RequestedCount,
                    AvailableSeedCount = preview.AvailableSeedCount,
                    MatchingSlotCount = preview.MatchingSlotCount,
                },
            } : null);
        }
        if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
            return NetworkCommandResult.Failure("COMPANION-BUSY", $"{identity} is already executing {record.ActiveTransactionId}.");
        if (this.appearance.IsPresenting(identity))
            return NetworkCommandResult.Failure("COMPANION-PRESENTING", "Wait for the current Yui action animation to finish before starting another diagnostic test.");
        if (!this.bodies.TryGetBody(identity, out NPC body) || body.currentLocation is null)
            return NetworkCommandResult.Failure("BODY-UNAVAILABLE", $"{identity} must be summoned before a nearest-target test.");

        string vitalKind = action switch
        {
            "mow" => VitalActionKinds.Mowing,
            "dig" => VitalActionKinds.Digging,
            "chop" => VitalActionKinds.Chopping,
            "mine" => VitalActionKinds.Mining,
            "water" => VitalActionKinds.Watering,
            "harvest" => VitalActionKinds.Harvesting,
            "forage" => VitalActionKinds.Foraging,
            "fish" => VitalActionKinds.Fishing,
            "pet" => VitalActionKinds.Petting,
            "milk" => VitalActionKinds.Milking,
            "shear" => VitalActionKinds.Shearing,
            "fight" => VitalActionKinds.Combat,
            _ => string.Empty,
        };
        if (vitalKind.Length == 0)
            return NetworkCommandResult.Failure("INVALID-DIAGNOSTIC-ACTION", "The requested nearest-target test is not supported.");
        if (!this.vitals.CanStartAction(identity, vitalKind, out VitalActionResult vitalGate))
            return NetworkCommandResult.Failure(vitalGate.Code, vitalGate.Message);

        if (action is "pet" or "milk" or "shear")
            return this.RunNearestAnimalDiagnostic(identity, body, action);

        string? classifiedWorkKind = action switch
        {
            "mow" => WorkKinds.Mow,
            "chop" => WorkKinds.Chop,
            "mine" => WorkKinds.Mine,
            "water" => WorkKinds.Water,
            "harvest" => WorkKinds.Harvest,
            "forage" => WorkKinds.Forage,
            _ => null,
        };
        IReadOnlyList<WorldTargetFact> classified = WorldTargetClassifier.Observe(body.currentLocation);
        IEnumerable<Vector2> candidates = action switch
        {
            "mow" or "chop" or "mine" or "water" or "harvest" or "forage" => classified
                .Where(fact => fact.Disposition == WorldTargetDispositions.Candidate && fact.SuggestedWorkKind == classifiedWorkKind)
                .Select(fact => fact.Tile),
            "fish" => FindWaterTiles(body.currentLocation, body.TilePoint, 16),
            "fight" => body.currentLocation.characters.OfType<Monster>().Where(monster => monster.Health > 0 && !monster.IsInvisible).Select(monster => monster.Tile),
            _ => FindDiggableTiles(body.currentLocation, body.TilePoint, 12),
        };
        Vector2[] orderedCandidates = candidates
            .Distinct()
            .OrderBy(tile => Math.Abs((int)tile.X - body.TilePoint.X) + Math.Abs((int)tile.Y - body.TilePoint.Y))
            .ThenBy(tile => tile.Y)
            .ThenBy(tile => tile.X)
            .ToArray();
        if (orderedCandidates.Length == 0)
            return NetworkCommandResult.Failure("NO-NEAREST-TARGET", $"No valid nearby {action} target was found.");

        NetworkCommandResult? lastLocalFailure = null;
        foreach (Vector2 target in orderedCandidates)
        {
            NetworkCommandResult result = this.TryRunDiagnosticCandidate(identity, action, target);
            if (result.IsSuccess)
                return result;
            if (!IsLocalCandidateFailure(result.Code))
                return result;
            lastLocalFailure = result;
        }

        return lastLocalFailure is NetworkCommandResult failure
            ? NetworkCommandResult.Failure("NO-REACHABLE-TARGET", $"No reachable {action} target passed its coordinator preflight. Last rejection: {failure.Code}: {failure.Message}")
            : NetworkCommandResult.Failure("NO-NEAREST-TARGET", $"No valid nearby {action} target was found.");
    }

    private NetworkCommandResult TryRunDiagnosticCandidate(CompanionIdentity identity, string action, Vector2 target)
    {
        int x = (int)target.X;
        int y = (int)target.Y;
        string operationId = $"diag-{action}-{Game1.ticks}-{x}-{y}";
        bool success;
        string code;
        string message;
        Item? reservedTool;
        switch (action)
        {
            case "mow":
                MowCommandResult mow = this.mowing.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (mow.IsSuccess, mow.Code, mow.Message, this.mowing.GetReservedTool(identity));
                break;
            case "dig":
                DigCommandResult dig = this.digging.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (dig.IsSuccess, dig.Code, dig.Message, this.digging.GetReservedTool(identity));
                break;
            case "chop":
                ChopCommandResult chop = this.chopping.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (chop.IsSuccess, chop.Code, chop.Message, this.chopping.GetReservedTool(identity));
                break;
            case "mine":
                MineCommandResult mine = this.mining.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (mine.IsSuccess, mine.Code, mine.Message, this.mining.GetReservedTool(identity));
                break;
            case "water":
                WaterCommandResult water = this.watering.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (water.IsSuccess, water.Code, water.Message, this.watering.GetReservedTool(identity));
                break;
            case "harvest":
                HarvestCommandResult harvest = this.harvesting.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (harvest.IsSuccess, harvest.Code, harvest.Message, this.harvesting.GetReservedTool(identity));
                break;
            case "forage":
                ForageCommandResult forage = this.foraging.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (forage.IsSuccess, forage.Code, forage.Message, null);
                break;
            case "fish":
                FishingCommandResult fish = this.fishing.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (fish.IsSuccess, fish.Code, fish.Message, this.fishing.GetReservedTool(identity));
                break;
            default:
                CombatCommandResult fight = this.combat.TryStart(identity, x, y, operationId);
                (success, code, message, reservedTool) = (fight.IsSuccess, fight.Code, fight.Message, this.combat.GetReservedTool(identity));
                break;
        }
        if (success)
            this.storage.BindTask(identity, operationId, reservedTool);
        return success ? NetworkCommandResult.Success(code, $"Nearest target ({x},{y}): {message}") : NetworkCommandResult.Failure(code, message);
    }

    private static bool IsLocalCandidateFailure(string code) => code is
        "TARGET-UNREACHABLE"
        or "TARGET-OUTSIDE-VANILLA-SWING"
        or "NO-GRASS-IN-SWING"
        or "TARGET-NOT-DIGGABLE"
        or "TARGET-NOT-TREE"
        or "TARGET-NOT-CHOPPABLE"
        or "TARGET-NOT-MOWABLE"
        or "TARGET-NOT-MINEABLE"
        or "TARGET-NOT-WATERABLE"
        or "TARGET-NOT-HARVESTABLE"
        or "TARGET-NOT-FORAGE"
        or "TARGET-NOT-WATER"
        or "NO-FISHING-APPROACH"
        or "TARGET-NOT-MONSTER";

    private NetworkCommandResult RunNearestAnimalDiagnostic(CompanionIdentity identity, NPC body, string action)
    {
        Tool? tool = action switch
        {
            "milk" => this.inventories.FindFirst<MilkPail>(identity),
            "shear" => this.inventories.FindFirst<Shears>(identity),
            _ => null,
        };
        FarmAnimal? animal = body.currentLocation!.animals.Values
            .Where(candidate => action == "pet"
                ? !candidate.wasPet.Value && Game1.timeOfDay < 1900
                : tool is not null && candidate.isAdult() && candidate.currentProduce.Value is not null && candidate.CanGetProduceWithTool(tool))
            .OrderBy(candidate => Math.Abs(candidate.TilePoint.X - body.TilePoint.X) + Math.Abs(candidate.TilePoint.Y - body.TilePoint.Y))
            .FirstOrDefault();
        if (animal is null)
            return NetworkCommandResult.Failure("NO-NEAREST-ANIMAL", $"No valid nearby animal supports {action}.");
        string operationId = $"diag-{action}-{Game1.ticks}-{animal.myID.Value}";
        CareCommandResult result = this.animalCare.TryStart(identity, "animal", animal.myID.Value.ToString(), action, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.animalCare.GetReservedTool(identity));
        return result.IsSuccess ? NetworkCommandResult.Success(result.Code, result.Message) : NetworkCommandResult.Failure(result.Code, result.Message);
    }

    private static IEnumerable<Vector2> FindDiggableTiles(GameLocation location, Point origin, int radius)
    {
        for (int distance = 0; distance <= radius; distance++)
        {
            for (int x = origin.X - distance; x <= origin.X + distance; x++)
            {
                for (int y = origin.Y - distance; y <= origin.Y + distance; y++)
                {
                    if (Math.Abs(x - origin.X) + Math.Abs(y - origin.Y) != distance || x < 0 || y < 0)
                        continue;
                    Vector2 tile = new(x, y);
                    if (!location.Objects.ContainsKey(tile)
                        && !location.terrainFeatures.ContainsKey(tile)
                        && location.GetHoeDirtAtTile(tile) is null
                        && location.doesTileHaveProperty(x, y, "Diggable", "Back") is not null)
                        yield return tile;
                }
            }
        }
    }

    private static IEnumerable<Vector2> FindWaterTiles(GameLocation location, Point origin, int radius)
    {
        if (!location.canFishHere())
            yield break;
        for (int distance = 1; distance <= radius; distance++)
        {
            for (int x = origin.X - distance; x <= origin.X + distance; x++)
            {
                for (int y = origin.Y - distance; y <= origin.Y + distance; y++)
                {
                    if (Math.Abs(x - origin.X) + Math.Abs(y - origin.Y) == distance && x >= 0 && y >= 0 && location.isWaterTile(x, y))
                        yield return new Vector2(x, y);
                }
            }
        }
    }

    private void Delivery(CompanionIdentity identity, string[] args)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
        {
            this.Log(false, "IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
            return;
        }
        string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : "status";
        if (operation == "status")
        {
            DeliveryRecord[] active = record.Deliveries.Where(delivery => DeliveryPhases.OwnsEscrow(delivery.Phase)).ToArray();
            this.Log(true, "DELIVERY-STATUS", $"{identity} has {active.Length} active delivery cargo stack(s).");
            foreach (DeliveryRecord delivery in active)
                this.Log(true, "DELIVERY-CARGO", $"id={delivery.DeliveryId}, recipient={delivery.RecipientPlayerId}, item={delivery.QualifiedItemId}, quantity={delivery.Quantity}, phase={delivery.Phase}.");
            return;
        }

        this.inventories.RequestTransfer(
            identity,
            () =>
            {
                if (!Context.IsWorldReady || !Context.IsMainPlayer || this.getLifecycleState() != LifecycleState.SaveReady || !this.canMutateSave())
                    return InventoryActionResult.Failure("DELIVERY-GATE-CLOSED", "Authority, lifecycle, or save validation changed before the bag lock was acquired.");
                if (!this.registry.TryGet(identity, out CompanionRecord current) || !ReferenceEquals(current, record))
                    return InventoryActionResult.Failure("IDENTITY-NOT-FOUND", $"{identity} no longer exists.");
                if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId))
                    return InventoryActionResult.Failure("COMPANION-BUSY", $"{identity} must finish transaction {record.ActiveTransactionId} first.");

                if (operation == "send" && args.Length == 7
                    && int.TryParse(args[3], out int bagSlot)
                    && int.TryParse(args[4], out int quantity)
                    && long.TryParse(args[5], out long recipientId))
                {
                    InventoryActionResult escrowed = this.inventories.CreateDeliveryLocked(record, args[6], recipientId, bagSlot, quantity, unchecked((ulong)Game1.ticks));
                    if (!escrowed.IsSuccess || escrowed.Code is "ALREADY-COMPLETED" or "ALREADY-RETURNED")
                        return escrowed;
                    InventoryActionResult offered = this.TryOfferDeliveryLocked(record, args[6]);
                    return offered.IsSuccess || offered.Code is not ("RECIPIENT-NOT-NEARBY" or "RECIPIENT-TOO-FAR" or "RECIPIENT-INVENTORY-FULL")
                        ? offered
                        : InventoryActionResult.Success("DELIVERY-ESCROWED", $"Cargo is safely escrowed; immediate handoff is pending. {offered.Message}");
                }
                if (operation == "offer" && args.Length == 4)
                    return this.TryOfferDeliveryLocked(record, args[3]);
                if (operation == "return" && args.Length == 4)
                    return this.inventories.ReturnDeliveryLocked(record, args[3]);
                return InventoryActionResult.Failure("INVALID-DELIVERY-COMMAND", "Use delivery send <bagSlot> <count> <recipientPlayerId> <deliveryId>, offer <deliveryId>, or return <deliveryId>.");
            },
            completed: result => this.Log(result.IsSuccess, result.Code, result.Message)
        );
    }

    private InventoryActionResult TryOfferDeliveryLocked(CompanionRecord record, string deliveryId)
    {
        DeliveryRecord? delivery = record.Deliveries.FirstOrDefault(candidate => string.Equals(candidate.DeliveryId, deliveryId, StringComparison.Ordinal));
        if (delivery is null)
            return InventoryActionResult.Failure("DELIVERY-NOT-FOUND", $"Delivery {deliveryId} does not exist.");
        if (delivery.Phase == DeliveryPhases.Completed)
            return InventoryActionResult.Success("ALREADY-COMPLETED", $"Delivery {deliveryId} was already completed.");
        Farmer? recipient = Game1.GetPlayer(delivery.RecipientPlayerId, onlyOnline: true);
        if (recipient?.currentLocation is null || !this.bodies.TryGetBody(record.Identity, out NPC body) || body.currentLocation is null
            || !ReferenceEquals(recipient.currentLocation, body.currentLocation))
        {
            delivery.Phase = DeliveryPhases.Escrowed;
            delivery.LastFailure = "Recipient is offline or on another map.";
            return InventoryActionResult.Failure("RECIPIENT-NOT-NEARBY", "Recipient must be online on Yui's current map; cargo remains in Escrow.");
        }
        int distance = Math.Abs(body.TilePoint.X - recipient.TilePoint.X) + Math.Abs(body.TilePoint.Y - recipient.TilePoint.Y);
        if (distance > 12)
        {
            delivery.Phase = DeliveryPhases.Escrowed;
            delivery.LastFailure = $"Recipient is {distance} tiles away.";
            return InventoryActionResult.Failure("RECIPIENT-TOO-FAR", "Recipient must be within 12 tiles; cargo remains in Escrow.");
        }

        Item? cargo = this.inventories.GetEscrow(record.Identity).OfType<Item>().FirstOrDefault(item =>
            item.modData.TryGetValue(CompanionInventoryStore.DeliveryCargoTag, out string? taggedId)
            && string.Equals(taggedId, deliveryId, StringComparison.Ordinal));
        if (cargo is null)
            return InventoryActionResult.Failure("DELIVERY-CARGO-MISSING", "The delivery record has no exact Escrow cargo.");
        int facing = FacingToward(body.Tile, recipient.Tile);
        body.faceDirection(facing);
        delivery.Phase = DeliveryPhases.Offering;
        this.appearance.Prepare(record.Identity, $"delivery:{deliveryId}", AppearanceActionKinds.Handoff, cargo, facing);
        InventoryActionResult result = this.inventories.CompleteDeliveryLocked(record, deliveryId, recipient);
        if (result.IsSuccess)
            this.appearance.Commit(record.Identity, $"delivery:{deliveryId}");
        else
        {
            delivery.Phase = DeliveryPhases.Escrowed;
            delivery.LastFailure = result.Message;
            this.appearance.Fail(record.Identity, $"delivery:{deliveryId}", result.Code);
        }
        return result;
    }

    private void SetMode(CompanionIdentity identity, string mode, string code)
    {
        if (!this.registry.TryGet(identity, out CompanionRecord record))
        {
            this.Log(false, "IDENTITY-NOT-FOUND", $"{identity} does not exist; summon it first.");
            return;
        }

        VitalActionResult vitalsGate = this.vitals.CanChangeMode(identity);
        if (!vitalsGate.IsSuccess)
        {
            this.Log(false, vitalsGate.Code, vitalsGate.Message);
            return;
        }

        if (record.WorkDirective is not null)
        {
            this.CancelSessions(identity, $"CANCELLED-BY-{code}");
            this.work.Stop(identity, code, useReturnMode: false);
        }
        this.assist.DisableForOverride(identity, code);

        this.planting.Pause(identity, $"MODE-{code}");
        PlantingTransactionRecord? plantingTransaction = record.PlantingTransaction;
        bool pausedPlanting = plantingTransaction is not null && PlantingPhases.OwnsResponsibility(plantingTransaction.Phase);
        if (pausedPlanting)
            plantingTransaction!.ReturnMode = mode;

        if (mode == CompanionModes.Wait)
        {
            this.CancelSessions(identity, $"CANCELLED-BY-{code}");
            this.bodies.Halt(identity);
        }
        else if (!string.IsNullOrWhiteSpace(record.ActiveTransactionId) && !pausedPlanting)
        {
            this.Log(false, "COMPANION-BUSY", $"{identity} must finish or stop transaction {record.ActiveTransactionId} first.");
            return;
        }

        record.Mode = mode;
        if (mode == CompanionModes.Follow && this.naturalWorkAssistEnabled)
            this.assist.ArmNatural(identity);
        this.Log(true, code, $"{identity} mode is {mode}.");
    }

    private void WorkStart(ValidatedCommandRequest request, Farmer owner)
    {
        VitalActionResult vitalsGate = this.vitals.CanChangeMode(request.Identity);
        if (!vitalsGate.IsSuccess)
        {
            this.Log(false, vitalsGate.Code, vitalsGate.Message);
            return;
        }

        IReadOnlyDictionary<string, string> fields = request.Fields;
        var scope = new WorkScopeRequest(
            fields["locationKey"],
            int.Parse(fields["anchorX"]),
            int.Parse(fields["anchorY"]),
            fields["shape"],
            int.Parse(fields["radius"]),
            fields["kind"],
            fields["policy"]
        )
        {
            EndX = fields.TryGetValue("endX", out string? endX) ? int.Parse(endX) : int.Parse(fields["anchorX"]),
            EndY = fields.TryGetValue("endY", out string? endY) ? int.Parse(endY) : int.Parse(fields["anchorY"]),
        };
        WorkDirectiveResult startGate = this.work.CanStart(request.Identity, owner, scope);
        if (!startGate.IsSuccess)
        {
            this.LogWork(startGate);
            return;
        }

        if (this.registry.TryGet(request.Identity, out CompanionRecord record) && record.WorkDirective is not null)
        {
            this.CancelSessions(request.Identity, "CANCELLED-BY-WORK-REPLACED");
        }
        this.assist.DisableForOverride(request.Identity, "WORK-REPLACED");
        this.LogWork(this.work.Start(request.Identity, owner, request.RequestId, scope));
    }

    public NetworkCommandResult SubmitCursorScope(CompanionIdentity identity, WorkScopeRequest scope)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["locationKey"] = scope.LocationKey,
            ["anchorX"] = scope.AnchorX.ToString(),
            ["anchorY"] = scope.AnchorY.ToString(),
            ["shape"] = scope.Shape,
            ["radius"] = scope.Radius.ToString(),
            ["kind"] = scope.RequestedKind,
            ["policy"] = scope.CompletionPolicy,
        };
        if (scope.Shape == WorkScopeShapes.Rectangle)
        {
            fields["endX"] = scope.EndX.ToString();
            fields["endY"] = scope.EndY.ToString();
        }
        return this.multiplayer.Submit(identity, scope.Shape == WorkScopeShapes.SingleTarget ? "cursor-single" : "work-start", fields);
    }

    public NetworkCommandResult SubmitOperationStatus(CompanionIdentity identity, string operationId) =>
        this.multiplayer.Submit(identity, "operation-status", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["operationId"] = operationId,
        });

    public NetworkCommandResult SubmitCraftStart(CompanionIdentity identity, string recipeKey, int craftCount)
    {
        return this.multiplayer.Submit(identity, "craft-start", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["recipeKey"] = recipeKey,
            ["craftCount"] = craftCount.ToString(),
            ["operationId"] = $"craft-{Guid.NewGuid():N}",
        });
    }

    public NetworkCommandResult SubmitCraftCancel(CompanionIdentity identity) =>
        this.multiplayer.Submit(identity, "craft-cancel", new Dictionary<string, string>(StringComparer.Ordinal));

    public NetworkCommandResult SubmitPlantOptions(CompanionIdentity identity, string? query = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(query))
            fields["query"] = query.Trim();
        return this.multiplayer.Submit(identity, "plant-options", fields);
    }

    public NetworkCommandResult SubmitPlantPreview(CompanionIdentity identity, string seedOptionId, int count, int radius) =>
        this.multiplayer.Submit(identity, "plant-preview", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["seedOptionId"] = seedOptionId,
            ["count"] = count.ToString(),
            ["radius"] = radius.ToString(),
        });

    public NetworkCommandResult SubmitPlantStart(CompanionIdentity identity, string seedOptionId, int count, int radius) =>
        this.multiplayer.Submit(identity, "plant-start", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["seedOptionId"] = seedOptionId,
            ["count"] = count.ToString(),
            ["radius"] = radius.ToString(),
            ["operationId"] = $"menu-plant-{Guid.NewGuid():N}",
        });

    public NetworkCommandResult SubmitPlantControl(CompanionIdentity identity, string operation) =>
        this.multiplayer.Submit(identity, $"plant-{operation}", new Dictionary<string, string>(StringComparer.Ordinal));

    private void CursorSingle(ValidatedCommandRequest request, Farmer owner)
    {
        IReadOnlyDictionary<string, string> fields = request.Fields;
        var scope = new WorkScopeRequest(fields["locationKey"], int.Parse(fields["anchorX"]), int.Parse(fields["anchorY"]), fields["shape"], int.Parse(fields["radius"]), fields["kind"], fields["policy"]);
        WorkScopeValidationResult validation = WorkScopeNormalizer.NormalizeSingle(owner, scope);
        if (!validation.IsSuccess)
        {
            this.Log(false, validation.Code, validation.Message);
            return;
        }
        if (!this.registry.TryGet(request.Identity, out CompanionRecord record))
        {
            this.Log(false, "IDENTITY-NOT-FOUND", $"{request.Identity} does not exist; summon it first.");
            return;
        }
        NormalizedWorkScope normalized = validation.Scope;
        string operationId = $"cursor-{request.RequestId}";
        SingleWorkStartResult result = this.workTasks.TryStartSingle(record, owner, normalized, operationId);
        if (result.IsSuccess)
        {
            if (record.WorkDirective is not null)
                this.work.TrackManualRequest(request.Identity, operationId);
        }
        this.Log(result.IsSuccess, result.Code, result.IsSuccess
            ? $"Accepted {result.Kind} at {normalized.LocationKey} ({normalized.AnchorX},{normalized.AnchorY}); operation={operationId}."
            : result.Message);
    }

    private void WorkResume(CompanionIdentity identity)
    {
        VitalActionResult vitalsGate = this.vitals.CanChangeMode(identity);
        if (!vitalsGate.IsSuccess)
        {
            this.Log(false, vitalsGate.Code, vitalsGate.Message);
            return;
        }
        this.LogWork(this.work.Resume(identity));
    }

    private void WorkStop(CompanionIdentity identity, string reason, bool logResult = true)
    {
        this.CancelSessions(identity, $"CANCELLED-BY-{reason}");
        this.assist.DisableForOverride(identity, reason);
        WorkDirectiveResult result = this.work.Stop(identity, reason, useReturnMode: true);
        if (logResult)
            this.LogWork(result);
    }

    private void CancelSessions(CompanionIdentity identity, string code)
    {
        this.watering.Cancel(identity, code);
        this.chopping.Cancel(identity, code);
        this.mining.Cancel(identity, code);
        this.harvesting.Cancel(identity, code);
        this.foraging.Cancel(identity, code);
        this.mowing.Cancel(identity, code);
        this.digging.Cancel(identity, code);
        this.animalCare.Cancel(identity, code);
        this.fishing.Cancel(identity, code);
        this.combat.Cancel(identity, code);
        this.delivery.Cancel(identity, code);
        this.storage.Cancel(identity, code);
        this.crafting.Cancel(identity);
    }

    private void LogWork(WorkDirectiveResult result) => this.Log(result.IsSuccess, result.Code, result.Message);

    private void AssistStart(CompanionIdentity identity, Farmer owner)
    {
        VitalActionResult vitalsGate = this.vitals.CanChangeMode(identity);
        if (!vitalsGate.IsSuccess)
        {
            this.Log(false, vitalsGate.Code, vitalsGate.Message);
            return;
        }
        this.LogAssist(this.assist.Start(identity, owner));
    }

    private void AssistStop(CompanionIdentity identity)
    {
        this.CancelSessions(identity, "CANCELLED-BY-ASSIST-STOPPED");
        this.LogAssist(this.assist.Stop(identity, "PLAYER-STOPPED", stopOwnedDirective: true));
    }

    private void LogAssist(OwnerWorkAssistResult result) => this.Log(result.IsSuccess, result.Code, result.Message);

    private void Water(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-WATER-TARGET", "Usage: yui water <tileX> <tileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"water-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Watering))
            return;

        WaterCommandResult result = this.watering.TryStart(identity, tileX, tileY, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.watering.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Chop(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-CHOP-TARGET", "Usage: yui chop <tileX> <tileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"chop-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Chopping))
            return;

        ChopCommandResult result = this.chopping.TryStart(identity, tileX, tileY, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.chopping.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Mine(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-MINE-TARGET", "Usage: yui mine <tileX> <tileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"mine-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Mining))
            return;

        MineCommandResult result = this.mining.TryStart(identity, tileX, tileY, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.mining.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Harvest(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-HARVEST-TARGET", "Usage: yui harvest <tileX> <tileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"harvest-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Harvesting))
            return;

        HarvestCommandResult result = this.harvesting.TryStart(identity, tileX, tileY, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.harvesting.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Forage(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-FORAGE-TARGET", "Usage: yui forage <tileX> <tileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"forage-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Foraging))
            return;

        ForageCommandResult result = this.foraging.TryStart(identity, tileX, tileY, operationId);
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Mow(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-MOW-TARGET", "Usage: yui mow <tileX> <tileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"mow-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Mowing))
            return;

        MowCommandResult result = this.mowing.TryStart(identity, tileX, tileY, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.mowing.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Dig(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-DIG-TARGET", "Usage: yui dig <tileX> <tileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"dig-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Digging))
            return;

        DigCommandResult result = this.digging.TryStart(identity, tileX, tileY, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.digging.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Care(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 5)
        {
            this.Log(false, "INVALID-CARE-TARGET", "Usage: yui care <animal|pet> <animalId|petGuid> <pet|milk|shear> [operationId].");
            return;
        }

        string operationId = args.Length >= 6
            ? args[5]
            : $"care-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{args[2]}-{args[3]}-{args[4]}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        string careKind = args[4].ToLowerInvariant() switch
        {
            "milk" => VitalActionKinds.Milking,
            "shear" => VitalActionKinds.Shearing,
            _ => VitalActionKinds.Petting,
        };
        if (!this.CheckVitals(identity, careKind))
            return;

        CareCommandResult result = this.animalCare.TryStart(identity, args[2], args[3], args[4], operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.animalCare.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Fish(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-FISH-TARGET", "Usage: yui fish <waterTileX> <waterTileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"fish-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Fishing))
            return;

        FishingCommandResult result = this.fishing.TryStart(identity, tileX, tileY, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.fishing.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Fight(CompanionIdentity identity, string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[2], out int tileX) || !int.TryParse(args[3], out int tileY))
        {
            this.Log(false, "INVALID-MONSTER-TARGET", "Usage: yui fight <monsterTileX> <monsterTileY> [operationId].");
            return;
        }

        string operationId = args.Length >= 5
            ? args[4]
            : $"fight-{Game1.uniqueIDForThisGame}-{Game1.Date.TotalDays}-{Game1.currentLocation.NameOrUniqueName}-{tileX}-{tileY}";
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
        {
            this.Log(false, "INVALID-OPERATION-ID", "Operation ID must contain 1 to 128 non-whitespace characters.");
            return;
        }

        if (!this.CheckVitals(identity, VitalActionKinds.Combat))
            return;

        CombatCommandResult result = this.combat.TryStart(identity, tileX, tileY, operationId);
        if (result.IsSuccess)
            this.storage.BindTask(identity, operationId, this.combat.GetReservedTool(identity));
        this.Log(result.IsSuccess, result.Code, result.Message);
    }

    private void Combat(CompanionIdentity identity, string[] args)
    {
        string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;
        if (operation == "options")
        {
            int radius = 6;
            if (args.Length >= 4 && !int.TryParse(args[3], out radius))
            {
                this.Log(false, "COMBAT-RADIUS-INVALID", "Usage: yui combat options [radius 1..8].");
                return;
            }
            CombatOptionsResult result = this.combat.GetOptions(identity, radius);
            string details = result.Options.Count == 0
                ? result.Message
                : string.Join("; ", result.Options.Select(option => $"{option.CombatOptionId}:{option.MonsterKind}:{option.DistanceBand}:{option.ThreatBand}:isolate={option.CanIsolate}:ttl={option.ExpiresInSeconds}s"));
            this.CaptureOrLog(new NetworkCommandResult(result.IsSuccess, result.Code, details, Combat: new CombatCommandPayload
            {
                Options = result.Options.Select(ToDto).ToList(),
                Phase = "options",
            }));
            return;
        }
        if (operation == "strike" && args.Length is 4 or 5)
        {
            string operationId = args.Length == 5 ? args[4] : $"combat-strike-{Guid.NewGuid():N}";
            if (!this.CheckVitals(identity, VitalActionKinds.Combat))
                return;
            CombatCommandResult result = this.combat.TryStartOption(identity, args[3], operationId);
            if (result.IsSuccess)
                this.storage.BindTask(identity, operationId, this.combat.GetReservedTool(identity));
            this.Log(result.IsSuccess, result.Code, result.Message);
            return;
        }
        if (operation == "guard" && args.Length is >= 5 and <= 7
            && int.TryParse(args[3], out int guardRadius)
            && int.TryParse(args[4], out int seconds))
        {
            int maximumSwings = args.Length >= 6 && int.TryParse(args[5], out int parsedSwings) ? parsedSwings : 10;
            string directiveId = args.Length == 7 ? args[6] : $"combat-guard-{Guid.NewGuid():N}";
            if (!this.CheckVitals(identity, VitalActionKinds.Combat))
                return;
            CombatCommandResult result = this.combat.TryStartGuard(identity, guardRadius, seconds, maximumSwings, directiveId);
            this.Log(result.IsSuccess, result.Code, result.Message);
            return;
        }
        if (operation == "status" && args.Length == 3)
        {
            CombatCommandResult result = this.combat.Status(identity);
            this.CaptureOrLog(new NetworkCommandResult(result.IsSuccess, result.Code, result.Message, Combat: new CombatCommandPayload { Phase = result.Code }));
            return;
        }
        if (operation == "stop" && args.Length == 3)
        {
            CombatCommandResult result = this.combat.Cancel(identity, "COMBAT-CANCELLED");
            this.Log(result.IsSuccess, result.Code, result.Message);
            return;
        }
        this.Log(false, "INVALID-COMBAT-COMMAND", "Usage: yui combat <options [radius 1..8]|strike <combatOptionId> [operationId]|guard <radius> <seconds> [maximumSwings] [directiveId]|status|stop>.");
    }

    private void PlantOptions(CompanionIdentity identity, Farmer owner, string? query)
    {
        PlantSeedOptionsResult result = this.planting.GetOptions(identity, owner, query);
        if (!result.IsSuccess)
        {
            this.Log(false, result.Code, result.Message);
            return;
        }
        string summary = string.Join(" | ", result.Options.Select(option =>
            $"{option.SeedOptionId}:{option.SeedDisplayName}->{option.CropDisplayName} x{option.AvailableCount} {option.ReasonCode} ttl={option.ExpiresInSeconds}s"));
        this.CaptureOrLog(new NetworkCommandResult(true, result.Code, Bound(summary, 1800), new PlantingCommandPayload
        {
            Options = result.Options.Select(ToDto).ToList(),
        }));
    }

    private void PlantPreview(CompanionIdentity identity, Farmer owner, ValidatedCommandRequest request)
    {
        PlantingScope scope = RadiusPlantingScope(owner, int.Parse(request.Fields["radius"]));
        PlantingPreviewResult result = this.planting.Preview(identity, owner, request.Fields["seedOptionId"], int.Parse(request.Fields["count"]), scope);
        this.CaptureOrLog(new NetworkCommandResult(result.IsSuccess, result.Code, result.Message, result.IsSuccess ? new PlantingCommandPayload
        {
            Preview = new PlantingPreviewDto
            {
                SeedOptionId = result.SeedOptionId,
                SeedDisplayName = result.SeedDisplayName,
                CropDisplayName = result.CropDisplayName,
                RequestedCount = result.RequestedCount,
                AvailableSeedCount = result.AvailableSeedCount,
                MatchingSlotCount = result.MatchingSlotCount,
            },
        } : null));
    }

    private void PlantStart(CompanionIdentity identity, Farmer owner, ValidatedCommandRequest request)
    {
        PlantingScope scope = RadiusPlantingScope(owner, int.Parse(request.Fields["radius"]));
        string operationId = request.Fields.GetValueOrDefault("operationId") ?? $"r9-{request.RequestId}";
        this.LogPlant(this.planting.Start(identity, owner, request.Fields["seedOptionId"], int.Parse(request.Fields["count"]), scope, operationId));
    }

    private static PlantingScope RadiusPlantingScope(Farmer owner, int radius) => new(
        owner.currentLocation!.NameOrUniqueName,
        owner.TilePoint.X,
        owner.TilePoint.Y,
        owner.TilePoint.X,
        owner.TilePoint.Y,
        WorkScopeShapes.Radius,
        radius);

    private void RunLocalQuery(CompanionIdentity identity, string action, string[] args)
    {
        if (action == "bag")
            this.Bag(identity, args, Game1.player);
        else if (action == "storage")
            this.Storage(identity, args, Game1.player);
        else if (action == "delivery")
            this.Delivery(identity, args);
        else
            this.Vitals(identity, args);
    }

    private static bool IsLocalQuery(string action, string[] args)
    {
        string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;
        return (action == "bag" && (operation.Length == 0 || operation == "list"))
            || (action == "storage" && (operation.Length == 0 || operation == "status"))
            || (action == "delivery" && (operation.Length == 0 || operation == "status"))
            || (action == "vitals" && (operation.Length == 0 || operation == "status"));
    }

    private void ShowHelp(bool advanced)
    {
        this.Log(
            true,
            "HELP",
            "Everyday commands: yui status | summon | dismiss | follow | stay | assist <on|off|status>. Use 'yui help advanced' for experimental command groups.");
        if (!advanced)
            return;

        if (!this.experimentalFeaturesEnabled)
        {
            this.Log(false, "EXPERIMENTAL-FEATURE-DISABLED", "Set EnableExperimentalFeatures to true before using advanced commands.");
            return;
        }

        this.Log(
            true,
            "ADVANCED-HELP",
            "Advanced groups: work, plant, combat, bag, storage, delivery, craft, vitals, water, chop, mine, harvest, forage, mow, dig, care, fish, fight, and delete.");
    }

    private static string NormalizeConsoleAliases(string action, ref string[] args)
    {
        string normalizedAction = action switch
        {
            "status" => "list",
            "dismiss" => "recall",
            "stay" => "wait",
            _ => action,
        };
        if (args.Length > 0 && normalizedAction != action)
        {
            args = args.ToArray();
            args[0] = normalizedAction;
        }

        if (normalizedAction == "assist" && args.Length >= 2)
        {
            string operation = args[1].ToLowerInvariant() switch
            {
                "on" => "start",
                "off" => "stop",
                _ => args[1],
            };
            if (operation != args[1])
            {
                args = args.ToArray();
                args[1] = operation;
            }
        }
        return normalizedAction;
    }

    private static bool IsExperimentalConsoleAction(string action) => action is
        "work" or "plant" or "combat" or "bag" or "storage" or "delivery" or "craft" or "vitals" or
        "water" or "chop" or "mine" or "harvest" or "forage" or "mow" or "dig" or "care" or "fish" or "fight" or "delete";

    private static bool IsEverydayRoutedCommand(string command) => command is
        "summon" or "recall" or "follow" or "wait" or "stop" or
        "assist-start" or "assist-status" or "assist-stop";

    private static bool TryBuildMutation(string action, string[] args, out string command, out Dictionary<string, string> fields, out string error)
    {
        command = action;
        fields = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;
        if (action is "summon" or "recall" or "delete" or "follow" or "wait" or "stop")
        {
            if (args.Length != 2)
                error = $"Usage: yui {action}.";
            return error.Length == 0;
        }

        if (action == "work")
        {
            string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;
            if (operation is "status" or "resume" or "stop")
            {
                if (args.Length != 3)
                {
                    error = $"Usage: yui work {operation}.";
                    return false;
                }
                command = $"work-{operation}";
                return true;
            }
            if (operation != "start" || args.Length is < 4 or > 6)
            {
                error = "Usage: yui work start <kind> [radius 1..24] [until-clear|until-stopped].";
                return false;
            }

            Farmer owner = Game1.player;
            if (owner.currentLocation is null)
            {
                error = "The Owner has no current location.";
                return false;
            }
            command = "work-start";
            fields["locationKey"] = owner.currentLocation.NameOrUniqueName;
            fields["anchorX"] = ((int)owner.Tile.X).ToString();
            fields["anchorY"] = ((int)owner.Tile.Y).ToString();
            fields["shape"] = WorkScopeShapes.Radius;
            fields["radius"] = args.Length >= 5 ? args[4] : WorkScopeContracts.DefaultRadius.ToString();
            fields["kind"] = args[3];
            fields["policy"] = args.Length >= 6 ? args[5] : "until-clear";
            return true;
        }

        if (action == "assist")
        {
            string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;
            if (operation is not ("start" or "status" or "stop") || args.Length != 3)
            {
                error = "Usage: yui assist <on|off|status>.";
                return false;
            }
            command = $"assist-{operation}";
            return true;
        }

        if (action == "plant")
        {
            string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;
            if (operation == "options")
            {
                if (args.Length is < 3 or > 4) { error = "Usage: yui plant options [query]."; return false; }
                command = "plant-options";
                if (args.Length == 4) fields["query"] = args[3];
                return true;
            }
            if (operation is "status" or "resume" or "cancel")
            {
                if (args.Length != 3) { error = $"Usage: yui plant {operation}."; return false; }
                command = $"plant-{operation}";
                return true;
            }
            if (operation == "preview" && args.Length is >= 5 and <= 6)
            {
                command = "plant-preview";
                fields["seedOptionId"] = args[3];
                fields["count"] = args[4];
                fields["radius"] = args.Length == 6 ? args[5] : WorkScopeContracts.DefaultRadius.ToString();
                return true;
            }
            if (operation == "start" && args.Length is >= 5 and <= 7)
            {
                command = "plant-start";
                fields["seedOptionId"] = args[3];
                fields["count"] = args[4];
                fields["radius"] = args.Length >= 6 ? args[5] : WorkScopeContracts.DefaultRadius.ToString();
                if (args.Length == 7) fields["operationId"] = args[6];
                return true;
            }
            error = "Usage: yui plant <options [query]|preview <seedOptionId> <count> [radius]|start <seedOptionId> <count> [radius] [operationId]|status|resume|cancel>.";
            return false;
        }

        if (action == "combat")
        {
            string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;
            if (operation == "options" && args.Length is 3 or 4)
            {
                command = "combat-options";
                if (args.Length == 4) fields["radius"] = args[3];
                return true;
            }
            if (operation == "strike" && args.Length is 4 or 5)
            {
                command = "combat-strike";
                fields["combatOptionId"] = args[3];
                if (args.Length == 5) fields["operationId"] = args[4];
                return true;
            }
            if (operation == "guard" && args.Length is >= 5 and <= 7)
            {
                command = "combat-guard";
                fields["radius"] = args[3];
                fields["seconds"] = args[4];
                if (args.Length >= 6) fields["maximumSwings"] = args[5];
                if (args.Length == 7) fields["operationId"] = args[6];
                return true;
            }
            if (operation is "status" or "stop" && args.Length == 3)
            {
                command = $"combat-{operation}";
                return true;
            }
            error = "Usage: yui combat <options [radius 1..8]|strike <combatOptionId> [operationId]|guard <radius> <seconds> [maximumSwings] [directiveId]|status|stop>.";
            return false;
        }

        if (action == "bag")
        {
            if (args.Length != 4 || args[2].ToLowerInvariant() is not ("give" or "take"))
            {
                error = "Usage: yui bag <give <playerSlot>|take <bagSlot>>.";
                return false;
            }
            command = args[2].Equals("give", StringComparison.OrdinalIgnoreCase) ? "bag-give" : "bag-take";
            fields[command == "bag-give" ? "playerSlot" : "bagSlot"] = args[3];
            return true;
        }

        if (action == "storage")
        {
            if (args.Length < 4)
            {
                error = "Storage mutation requires an operation and arguments.";
                return false;
            }
            string operation = args[2].ToLowerInvariant();
            switch (operation)
            {
                case "authorize":
                case "unauthorize":
                    if (args.Length != 5) { error = "Usage: yui storage <authorize|unauthorize> <tileX> <tileY>."; return false; }
                    command = $"storage-{operation}";
                    fields["tileX"] = args[3]; fields["tileY"] = args[4];
                    return true;
                case "borrow":
                    if (args.Length != 4) { error = "Usage: yui storage borrow <qualifiedItemId>."; return false; }
                    command = "storage-borrow"; fields["itemId"] = args[3]; return true;
                case "take-material":
                    if (args.Length != 5) { error = "Usage: yui storage take-material <qualifiedItemId> <count>."; return false; }
                    command = "storage-take-material"; fields["itemId"] = args[3]; fields["count"] = args[4]; return true;
                case "return":
                    if (args.Length != 4) { error = "Usage: yui storage return <responsibilityId>."; return false; }
                    command = "storage-return"; fields["responsibilityId"] = args[3]; return true;
                default:
                    error = "Use storage authorize, unauthorize, borrow, take-material, or return.";
                    return false;
            }
        }

        if (action == "vitals")
        {
            string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;
            if (operation == "eat" && args.Length is 3 or 4)
            {
                command = "vitals-eat";
                if (args.Length == 4) fields["bagSlot"] = args[3];
                return true;
            }
            if (operation == "rest" && args.Length is 3 or 4)
            {
                command = "vitals-rest";
                if (args.Length == 4) fields["seconds"] = args[3];
                return true;
            }
            error = "Use vitals eat [bagSlot] or rest [seconds].";
            return false;
        }

        if (action == "delivery")
        {
            string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : string.Empty;
            if (operation == "send" && args.Length == 7)
            {
                command = "delivery-create";
                fields["bagSlot"] = args[3];
                fields["count"] = args[4];
                fields["recipientId"] = args[5];
                fields["deliveryId"] = args[6];
                return true;
            }
            if (operation is "offer" or "return" && args.Length == 4)
            {
                command = $"delivery-{operation}";
                fields["deliveryId"] = args[3];
                return true;
            }
            error = "Use delivery send <bagSlot> <count> <recipientPlayerId> <deliveryId>, offer <deliveryId>, or return <deliveryId>.";
            return false;
        }

        if (action == "craft")
        {
            string operation = args.Length >= 3 ? args[2].ToLowerInvariant() : "status";
            if (operation is "list" or "status")
            {
                if (args.Length != 3) { error = $"Usage: yui craft {operation}."; return false; }
                command = $"craft-{operation}";
                return true;
            }
            if (operation == "preview" && args.Length is 4 or 5)
            {
                command = "craft-preview";
                fields["recipeKey"] = args[3];
                if (args.Length == 5) fields["craftCount"] = args[4];
                return true;
            }
            if (operation == "start" && args.Length is >= 4 and <= 6)
            {
                command = "craft-start";
                fields["recipeKey"] = args[3];
                fields["craftCount"] = args.Length >= 5 ? args[4] : "1";
                fields["operationId"] = args.Length == 6 ? args[5] : $"craft-{Guid.NewGuid():N}";
                return true;
            }
            if (operation == "cancel" && args.Length == 3)
            {
                command = "craft-cancel";
                return true;
            }
            error = "Use craft list, preview <recipeKey> [craftCount], start <recipeKey> [craftCount] [operationId], status, or cancel.";
            return false;
        }

        if (action == "care")
        {
            if (args.Length is not (5 or 6))
            {
                error = "Usage: yui care <animal|pet> <animalId|petGuid> <pet|milk|shear> [operationId].";
                return false;
            }
            fields["targetType"] = args[2].ToLowerInvariant();
            fields["targetId"] = args[3];
            fields["careAction"] = args[4].ToLowerInvariant();
            if (args.Length == 6) fields["operationId"] = args[5];
            return true;
        }

        if (action is "water" or "chop" or "mine" or "harvest" or "forage" or "mow" or "dig" or "fish" or "fight")
        {
            if (args.Length is not (4 or 5))
            {
                error = $"Usage: yui {action} <tileX> <tileY> [operationId].";
                return false;
            }
            fields["tileX"] = args[2];
            fields["tileY"] = args[3];
            if (args.Length == 5) fields["operationId"] = args[4];
            return true;
        }

        error = "Use 'yui help' to see available commands.";
        return false;
    }

    private static string[] BuildCoordinatorArgs(ValidatedCommandRequest request)
    {
        string slot = request.Identity.Slot.ToString();
        IReadOnlyDictionary<string, string> fields = request.Fields;
        string operationId = fields.GetValueOrDefault("operationId") ?? $"r9-{request.RequestId}";
        return request.Command switch
        {
            "bag-give" => new[] { "bag", slot, "give", fields["playerSlot"] },
            "bag-take" => new[] { "bag", slot, "take", fields["bagSlot"] },
            "storage-authorize" => new[] { "storage", slot, "authorize", fields["tileX"], fields["tileY"] },
            "storage-unauthorize" => new[] { "storage", slot, "unauthorize", fields["tileX"], fields["tileY"] },
            "storage-borrow" => new[] { "storage", slot, "borrow", fields["itemId"] },
            "storage-take-material" => new[] { "storage", slot, "take-material", fields["itemId"], fields["count"] },
            "storage-return" => new[] { "storage", slot, "return", fields["responsibilityId"] },
            "delivery-create" => new[] { "delivery", slot, "send", fields["bagSlot"], fields["count"], fields["recipientId"], fields["deliveryId"] },
            "delivery-offer" => new[] { "delivery", slot, "offer", fields["deliveryId"] },
            "delivery-return" => new[] { "delivery", slot, "return", fields["deliveryId"] },
            "vitals-eat" => fields.TryGetValue("bagSlot", out string? bagSlot) ? new[] { "vitals", slot, "eat", bagSlot } : new[] { "vitals", slot, "eat" },
            "vitals-rest" => fields.TryGetValue("seconds", out string? seconds) ? new[] { "vitals", slot, "rest", seconds } : new[] { "vitals", slot, "rest" },
            "combat-options" => fields.TryGetValue("radius", out string? combatRadius) ? new[] { "combat", slot, "options", combatRadius } : new[] { "combat", slot, "options" },
            "combat-strike" => new[] { "combat", slot, "strike", fields["combatOptionId"], operationId },
            "combat-guard" => new[] { "combat", slot, "guard", fields["radius"], fields["seconds"], fields.GetValueOrDefault("maximumSwings", "10"), operationId },
            "combat-status" => new[] { "combat", slot, "status" },
            "combat-stop" => new[] { "combat", slot, "stop" },
            "care" => new[] { "care", slot, fields["targetType"], fields["targetId"], fields["careAction"], operationId },
            "water" or "chop" or "mine" or "harvest" or "forage" or "mow" or "dig" or "fish" or "fight" => new[] { request.Command, slot, fields["tileX"], fields["tileY"], operationId },
            _ => new[] { request.Command, slot },
        };
    }

    private static bool IsAgentMutation(string command) => command is not ("assist-status" or "work-status" or "craft-list" or "craft-preview" or "craft-status" or "plant-options" or "plant-preview" or "plant-status" or "combat-options" or "combat-status" or "operation-status");

    private void ListOwned()
    {
        long ownerId = Game1.player.UniqueMultiplayerID;
        CompanionRecord[] records = this.registry.All.Where(record => record.OwnerId == ownerId).ToArray();
        if (records.Length == 0)
        {
            this.Log(true, "EMPTY", "Yui has not joined this player yet. Use 'yui summon' to meet her.");
            return;
        }

        foreach (CompanionRecord record in records)
        {
            string presence = record.WantsBody ? "present" : "away";
            string assistState = record.OwnerWorkAssistEnabled ? "on" : "off";
            this.Log(
                true,
                "STATUS",
                $"{record.DisplayName}: {presence}, {record.Mode.ToLowerInvariant()}, health {record.Vitals.Health}/{record.Vitals.MaxHealth}, stamina {MathF.Round(record.Vitals.Stamina)}/{MathF.Round(record.Vitals.MaxStamina)}, assist {assistState}.");
        }
    }

    private void Log(bool success, string code, string message)
    {
        if (this.captureResult)
        {
            this.capturedResult = new NetworkCommandResult(success, code, message);
            return;
        }
        this.monitor.Log($"HY-CMD-{code}: {message}", success ? LogLevel.Info : LogLevel.Warn);
    }

    private void CaptureOrLog(NetworkCommandResult result)
    {
        if (this.captureResult)
            this.capturedResult = result;
        else
            this.monitor.Log($"HY-CMD-{result.Code}: {result.Message}", result.IsSuccess ? LogLevel.Info : LogLevel.Warn);
    }

    private static PlantSeedOptionDto ToDto(PlantSeedOption option) => new()
    {
        SeedOptionId = option.SeedOptionId,
        SeedDisplayName = Bound(option.SeedDisplayName, 64),
        CropDisplayName = Bound(option.CropDisplayName, 64),
        AvailableCount = option.AvailableCount,
        PlantableHere = option.PlantableHere,
        ReasonCode = Bound(option.ReasonCode, 64),
        ExpiresInSeconds = option.ExpiresInSeconds,
    };

    private static CombatOptionDto ToDto(CombatOption option) => new()
    {
        CombatOptionId = option.CombatOptionId,
        MonsterKind = Bound(option.MonsterKind, 32),
        DistanceBand = option.DistanceBand,
        ThreatBand = option.ThreatBand,
        CanIsolate = option.CanIsolate,
        ExpiresInSeconds = option.ExpiresInSeconds,
    };

    private static int FacingToward(Microsoft.Xna.Framework.Vector2 from, Microsoft.Xna.Framework.Vector2 to)
    {
        Microsoft.Xna.Framework.Vector2 delta = to - from;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            return delta.X > 0 ? 1 : 3;
        return delta.Y > 0 ? 2 : 0;
    }

    private void LogStorage(StorageActionResult result) => this.Log(result.IsSuccess, result.Code, result.Message);

    private void LogCraft(CraftActionResult result) => this.Log(result.IsSuccess, result.Code, result.Message);

    private void LogPlant(PlantingActionResult result) => this.Log(result.IsSuccess, result.Code, result.Message);

    private void LogTask(TaskExecutionResult result) => this.Log(result.IsSuccess, result.Code, result.Message);

    private bool CheckVitals(CompanionIdentity identity, string kind)
    {
        bool allowed = this.vitals.CanStartAction(identity, kind, out VitalActionResult result);
        if (!allowed)
            this.Log(false, result.Code, result.Message);
        return allowed;
    }

    private static string[] InjectCanonicalSlot(string action, string[] args)
    {
        string[] normalized = new string[args.Length + 1];
        normalized[0] = action;
        normalized[1] = CompanionIdentity.CanonicalSlot.ToString();
        Array.Copy(args, 1, normalized, 2, args.Length - 1);
        return normalized;
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
