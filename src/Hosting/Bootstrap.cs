using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Security.Cryptography;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace YuiToIssho;

internal enum LifecycleState
{
    Cold,
    ReadyWithoutSave,
    SaveReady,
    Saving,
}

internal sealed class Bootstrap
{
    private const string SaveDataKey = "schema-v9";

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly CompanionRegistry registry = new();
    private readonly CompanionInventoryStore inventories = new();
    private readonly CompanionBodyBinder bodies;
    private readonly FollowCoordinator following;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionAppearanceCoordinator appearance;
    private readonly CompanionNetworkBodyResolver networkBodies;
    private readonly CompanionProjectionCoordinator projection;
    private readonly TaskExecutionService taskExecution;
    private readonly TaskNavigationService taskNavigation;
    private readonly CompanionLeisureCoordinator leisure;
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
    private readonly WorkRuntimeModule workRuntime;
    private readonly CompanionStorageCoordinator storage;
    private readonly PlantingPreviewService plantingPreview;
    private readonly PlantingCoordinator planting;
    private readonly CraftingCoordinator crafting;
    private readonly WorkActionRegistry workActions;
    private readonly CompanionWorkTaskRouter workTasks;
    private readonly CompanionWorkCoordinator work;
    private readonly CompanionOwnerWorkAssistCoordinator assist;
    private readonly AgentRuntimeCoordinator agents;
    private readonly CompanionCommands commands;
    private readonly CompanionMultiplayerCoordinator multiplayer;
    private readonly CompanionSpeechCoordinator speech;
    private readonly CompanionDiagnosticsPanel diagnostics;
    private readonly CommandCursorCoordinator commandCursor;
    private readonly CompanionCraftingMenuCoordinator craftingMenu;
    private readonly CompanionPlantingMenuCoordinator plantingMenu;
    private readonly CompanionSocialMenuCoordinator socialMenu;
    private readonly CompanionSocialEntryCoordinator socialEntry;
    private readonly CompanionWorldInteractionCoordinator worldInteraction;
    private readonly NekoAgentBridge? nekoBridge;
    private readonly NekoBridgeDiscoveryPublisher? nekoBridgeDiscovery;
    private readonly bool experimentalFeaturesEnabled;
    private readonly bool naturalWorkAssistEnabled;
    private readonly bool autoSummonOnFirstLoad;
    private readonly bool diagnosticsEnabled;
    private readonly HashSet<CompanionIdentity> pendingOutputDrains = new();
    private readonly HashSet<long> pendingOwnerReconnects = new();
    private bool attached;
    private bool saveDataWritable;

    public Bootstrap(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
        ModConfig config = helper.ReadConfig<ModConfig>();
        this.experimentalFeaturesEnabled = config.EnableExperimentalFeatures;
        this.naturalWorkAssistEnabled = config.EnableNaturalWorkAssist;
        this.autoSummonOnFirstLoad = config.AutoSummonOnFirstLoad;
        this.diagnosticsEnabled = config.EnableDiagnostics;
        this.bodies = new CompanionBodyBinder(monitor);
        this.vitals = new CompanionVitalsCoordinator(this.registry, this.bodies, this.inventories, monitor, () => this.State, () => this.saveDataWritable);
        this.appearance = new CompanionAppearanceCoordinator(this.registry, this.bodies, monitor);
        this.taskNavigation = new TaskNavigationService(this.bodies);
        this.leisure = new CompanionLeisureCoordinator(this.registry, this.bodies, this.appearance, this.taskNavigation, monitor);
        this.following = new FollowCoordinator(this.bodies, this.taskNavigation, identity => this.appearance.IsPresenting(identity) || this.leisure.IsActive(identity), monitor);
        this.networkBodies = new CompanionNetworkBodyResolver();
        this.projection = new CompanionProjectionCoordinator(this.registry, this.bodies, this.inventories, this.appearance, this.networkBodies, monitor);
        this.taskExecution = new TaskExecutionService(this.registry, this.bodies, monitor);
        this.watering = new WateringCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.chopping = new ChoppingCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.mining = new MiningCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.harvesting = new HarvestCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.foraging = new ForageCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.mowing = new MowingCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.digging = new DiggingCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.animalCare = new AnimalCareCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.fishing = new FishingCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.combat = new CombatCoordinator(this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.delivery = new DeliveryCoordinator(this.registry, this.bodies, this.inventories, this.appearance, this.taskExecution, this.taskNavigation, monitor);
        this.workRuntime = new WorkRuntimeModule(
            this.watering,
            this.chopping,
            this.mining,
            this.harvesting,
            this.foraging,
            this.mowing,
            this.digging,
            this.animalCare,
            this.fishing,
            this.combat,
            this.delivery);
        this.storage = new CompanionStorageCoordinator(this.registry, this.bodies, this.inventories, this.taskNavigation, monitor, () => this.State, () => this.saveDataWritable);
        this.plantingPreview = new PlantingPreviewService(this.inventories, this.storage, this.bodies);
        this.planting = new PlantingCoordinator(this.registry, this.bodies, this.inventories, this.storage, this.plantingPreview, this.taskExecution, this.taskNavigation, this.appearance, monitor);
        this.crafting = new CraftingCoordinator(this.registry, this.inventories, this.bodies, this.storage, this.appearance, monitor);
        this.workActions = new WorkActionRegistry(this.watering, this.chopping, this.mining, this.harvesting, this.foraging, this.mowing, this.digging, this.animalCare);
        this.workTasks = new CompanionWorkTaskRouter(this.workActions, this.animalCare, this.vitals, this.storage);
        this.work = new CompanionWorkCoordinator(this.registry, this.bodies, this.inventories, this.taskNavigation, this.workTasks, monitor);
        this.assist = new CompanionOwnerWorkAssistCoordinator(this.registry, this.bodies, this.work, this.vitals, monitor);
        this.agents = new AgentRuntimeCoordinator(this.registry, this.bodies, this.taskExecution, monitor);
        this.vitals.AttachCancellation(this.CancelForVitals);
        this.vitals.AttachDamageObserver(this.ObserveYuiDamage);
        this.multiplayer = new CompanionMultiplayerCoordinator(helper, monitor, this.projection, this.bodies);
        this.speech = new CompanionSpeechCoordinator(helper, this.registry, this.bodies, this.multiplayer, monitor);
        this.assist.AttachStartObserver(this.speech.ObserveNaturalAssistStarted);
        this.commands = new CompanionCommands(
            this.registry,
            this.bodies,
            this.inventories,
            this.vitals,
            this.appearance,
            this.storage,
            this.watering,
            this.chopping,
            this.mining,
            this.harvesting,
            this.foraging,
            this.mowing,
            this.digging,
            this.animalCare,
            this.fishing,
            this.combat,
            this.delivery,
            this.crafting,
            this.planting,
            this.workTasks,
            this.work,
            this.assist,
            this.taskExecution,
            this.leisure,
            this.multiplayer,
            config.EnableExperimentalFeatures,
            config.EnableNaturalWorkAssist,
            monitor,
            () => this.State,
            () => this.saveDataWritable
        );
        this.diagnostics = new CompanionDiagnosticsPanel(helper, this.registry, this.bodies, this.inventories, this.vitals, this.appearance, this.taskExecution, this.combat, this.work, this.agents, this.multiplayer, this.commands.RunNearestDiagnostic);
        if (config.EnableNekoBridge)
        {
            if (string.IsNullOrWhiteSpace(config.NekoBridgeToken))
            {
                config.NekoBridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                helper.WriteConfig(config);
                monitor.Log("Yui to Issho! generated a private local Agent Gateway key in config.json.", LogLevel.Info);
            }
            if (string.IsNullOrWhiteSpace(config.NekoBridgeToken) || config.NekoBridgeToken.Length is < 16 or > 256)
                monitor.Log("HY-AGENT-GATEWAY-CONFIG: Gateway is disabled; token must be 16..256 characters.", LogLevel.Error);
            else
            {
                this.nekoBridge = new NekoAgentBridge(monitor, config.NekoBridgeToken, this.commands.ExecuteNekoAction, this.CaptureNekoBridgeState);
                this.nekoBridgeDiscovery = new NekoBridgeDiscoveryPublisher(helper.DirectoryPath, monitor);
            }
        }
        this.commandCursor = new CommandCursorCoordinator(
            helper,
            this.commands,
            this.registry,
            this.taskExecution,
            this.work,
            this.projection,
            this.multiplayer,
            config,
            () => this.State,
            () => this.saveDataWritable);
        this.craftingMenu = new CompanionCraftingMenuCoordinator(helper, this.registry, this.commands, config, () => this.State, () => this.saveDataWritable);
        this.plantingMenu = new CompanionPlantingMenuCoordinator(helper, this.registry, this.commands, config, () => this.State, () => this.saveDataWritable);
        this.socialMenu = new CompanionSocialMenuCoordinator(this.projection, this.speech, () => this.State);
        this.socialEntry = new CompanionSocialEntryCoordinator(helper, monitor, this.socialMenu);
        this.worldInteraction = new CompanionWorldInteractionCoordinator(helper, this.bodies, this.projection, this.socialMenu, () => this.State);
        this.multiplayer.AttachCommandHandler(this.commands.ExecuteAuthoritative);
        this.multiplayer.AttachPeerConnectedHandler(this.OnOwnerConnected);
        this.multiplayer.AttachPeerDisconnectedHandler(this.OnOwnerDisconnected);
        this.multiplayer.AttachSettlementObserver(this.speech.ObserveCommandSettlement);
        this.multiplayer.AttachSpeechObserver(this.speech.AcceptNetworkSpeech);
        this.multiplayer.AttachSpeechSnapshotObserver(this.speech.AcceptSnapshotSpeech);
        this.taskExecution.AttachCompletionObserver(this.speech.ObserveTaskCompletion);
        this.commands.AttachAgentInterrupt(this.agents.Interrupt);
        this.projection.AttachAgentSnapshotProvider(this.agents.GetSnapshot);
        this.projection.AttachWorkSnapshotProvider(this.work.GetSnapshot);
        this.projection.AttachCombatSnapshotProvider(this.combat.GetSnapshot);
        this.projection.AttachSpeechSnapshotProvider(this.speech.GetSnapshot);
    }

    public LifecycleState State { get; private set; } = LifecycleState.Cold;

    public ulong SessionTick { get; private set; }

    public bool TryRenderNetworkBody(NPC body, SpriteBatch spriteBatch, float alpha)
    {
        if (this.State != LifecycleState.SaveReady)
            return false;
        return Context.IsMainPlayer
            ? this.appearance.TryRenderNetworkBody(body, spriteBatch, alpha)
            : this.projection.TryRenderNetworkBody(body, spriteBatch, alpha);
    }

    internal bool IsSyntheticSeatOccupant(long playerId) => this.leisure.IsSyntheticOccupant(playerId);

    internal bool IsCompanionSeatedAt(MapSeat seat, NPC body) => this.leisure.IsBodySeatedAt(seat, body)
        || this.projection.IsProjectedBodySeatedAt(seat, body);

    public void Attach()
    {
        if (this.attached)
            return;

        this.attached = true;
        this.commands.Register(this.helper.ConsoleCommands);
        if (this.diagnosticsEnabled)
            this.diagnostics.Attach();
        if (this.experimentalFeaturesEnabled)
        {
            this.commandCursor.Attach();
            this.craftingMenu.Attach();
            this.plantingMenu.Attach();
            this.monitor.Log("Yui to Issho! experimental input surfaces are enabled.", LogLevel.Warn);
        }
        this.socialEntry.Attach();
        this.worldInteraction.Attach();
        this.multiplayer.Attach();
        if (this.nekoBridge?.Start() == true && this.nekoBridgeDiscovery?.Publish(this.nekoBridge.Endpoint) != true)
            this.nekoBridge.Stop();
        this.helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        this.helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        this.helper.Events.GameLoop.Saving += this.OnSaving;
        this.helper.Events.GameLoop.Saved += this.OnSaved;
        this.helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        this.helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        this.helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        this.helper.Events.GameLoop.DayEnding += this.OnDayEnding;
        this.helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
        this.helper.Events.World.ObjectListChanged += this.OnObjectListChanged;
        this.helper.Events.World.TerrainFeatureListChanged += this.OnTerrainFeatureListChanged;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.State = LifecycleState.ReadyWithoutSave;
        this.monitor.Log("Yui to Issho! lifecycle is ready.", LogLevel.Info);
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.pendingOutputDrains.Clear();
        this.pendingOwnerReconnects.Clear();
        this.speech.Clear();
        this.multiplayer.ResetSession();
        this.plantingPreview.ResetSession();
        this.workRuntime.CancelAll("CANCELLED-BY-LOAD");
        this.leisure.ClearAll("CANCELLED-BY-LOAD");
        this.taskExecution.ClearRuntime();
        this.work.ClearRuntime();
        this.assist.ClearRuntime();
        this.agents.Reset();
        this.storage.CancelRuntime("CANCELLED-BY-LOAD");
        this.planting.ClearRuntime();
        this.vitals.ClearRuntime();
        this.appearance.ClearRuntime();
        this.bodies.DetachAll(clearGenerations: true);
        this.following.Clear();
        this.registry.Clear();
        this.saveDataWritable = false;
        this.State = LifecycleState.SaveReady;

        if (!Context.IsMainPlayer)
        {
            this.multiplayer.BeginClientSession();
            this.monitor.Log("Yui to Issho! is read-only on farmhands; the host owns companion state.", LogLevel.Info);
            return;
        }

        try
        {
            YuiToIsshoSaveData data = this.helper.Data.ReadSaveData<YuiToIsshoSaveData>(SaveDataKey) ?? new YuiToIsshoSaveData();
            RegistryLoadResult result = this.registry.Load(data);
            if (!result.IsSuccess)
            {
                this.monitor.Log($"HY-SAVE-{result.Code}: {result.Message} Companion writes are disabled for this save.", LogLevel.Error);
                return;
            }
            bool introducedCompanion = false;
            if (this.autoSummonOnFirstLoad && !this.registry.CompanionIntroductionCompleted)
            {
                CompanionIdentity identity = CompanionIdentity.ForOwner(Game1.player.UniqueMultiplayerID);
                CompanionRecord introduced = this.registry.GetOrCreate(identity);
                introduced.WantsBody = true;
                introduced.Mode = CompanionModes.Follow;
                this.registry.MarkCompanionIntroductionCompleted();
                introducedCompanion = true;
                this.monitor.Log($"HY-COMPANION-INTRODUCTION: created the first companion for {identity}.", LogLevel.Info);
            }
            this.work.RestoreAfterLoad();

            InventoryValidationResult starterResult = this.inventories.EnsureStarterTools(this.registry.Active);
            if (!starterResult.IsSuccess)
            {
                this.monitor.Log($"HY-BAG-{starterResult.Code}: {starterResult.Message} Companion writes are disabled for this save.", LogLevel.Error);
                return;
            }

            InventoryValidationResult inventoryResult = this.inventories.Validate(this.registry.All);
            if (!inventoryResult.IsSuccess)
            {
                this.monitor.Log($"HY-BAG-{inventoryResult.Code}: {inventoryResult.Message} Companion writes are disabled for this save.", LogLevel.Error);
                return;
            }

            InventoryValidationResult storageResult = this.storage.Validate();
            if (!storageResult.IsSuccess)
            {
                this.monitor.Log($"HY-STORAGE-{storageResult.Code}: {storageResult.Message} Companion writes are disabled for this save.", LogLevel.Error);
                return;
            }

            InventoryValidationResult vitalsResult = this.vitals.ValidateAndInitialize();
            if (!vitalsResult.IsSuccess)
            {
                this.monitor.Log($"HY-VITALS-{vitalsResult.Code}: {vitalsResult.Message} Companion writes are disabled for this save.", LogLevel.Error);
                return;
            }

            this.planting.RestoreAfterLoad();

            InventoryValidationResult craftResult = this.crafting.Validate();
            if (!craftResult.IsSuccess)
            {
                this.monitor.Log($"HY-CRAFT-{craftResult.Code}: {craftResult.Message} Companion writes are disabled for this save.", LogLevel.Error);
                return;
            }

            InventoryValidationResult appearanceResult = this.appearance.ValidateAndInitialize();
            if (!appearanceResult.IsSuccess)
            {
                this.monitor.Log($"HY-APPEARANCE-{appearanceResult.Code}: {appearanceResult.Message} Companion writes are disabled for this save.", LogLevel.Error);
                return;
            }

            this.saveDataWritable = true;
            this.crafting.RestoreAfterLoad();
            this.bodies.RestoreDesired(this.registry.Active);
            if (this.naturalWorkAssistEnabled)
            {
                foreach (CompanionRecord record in this.registry.Active.Where(candidate => candidate.WantsBody && candidate.Mode == CompanionModes.Follow))
                    this.assist.ArmNatural(record.Identity);
            }
            if (introducedCompanion)
                this.speech.OfferFirstMeeting(CompanionIdentity.ForOwner(Game1.player.UniqueMultiplayerID));
            this.agents.BeginHostSession();
            this.multiplayer.BeginHostSession();
            this.monitor.Log($"Yui to Issho! save scope is ready. {result.Message}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"HY-SAVE-READ: Save data could not be read; companion writes are disabled. {ex.Message}", LogLevel.Error);
        }
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (this.State == LifecycleState.SaveReady)
        {
            this.State = LifecycleState.Saving;
            this.speech.Suspend();
            this.multiplayer.Suspend();
            this.work.SuspendAll("SAVING");
            this.workRuntime.CancelAll("CANCELLED-BY-SAVE");
            this.leisure.ClearAll("CANCELLED-BY-SAVE");
            this.assist.ReleaseLeases("SAVING");
            this.planting.PauseAll("SAVING");
            this.taskExecution.ClearRuntime();
            this.work.ClearRuntime();
            this.agents.SuspendAll("SAVING");
            this.storage.CancelRuntime("CANCELLED-BY-SAVE");
            this.crafting.SuspendAll("CANCELLED-BY-SAVE");
            this.vitals.OnSaving();
            this.appearance.ClearRuntime();
            this.following.PauseAll(this.registry.Active);
            this.bodies.DetachAll();

                if (Context.IsMainPlayer && this.saveDataWritable)
                {
                try
                {
                    this.helper.Data.WriteSaveData(SaveDataKey, this.registry.CreateSnapshot());
                }
                catch (Exception ex)
                {
                    this.saveDataWritable = false;
                    this.monitor.Log($"HY-SAVE-WRITE: Save data could not be written; further companion writes are disabled. {ex.Message}", LogLevel.Error);
                }
            }
        }
    }

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        if (this.State == LifecycleState.Saving)
        {
            this.State = LifecycleState.SaveReady;
            this.multiplayer.Resume();
            this.work.ResumeAfterSave();
            this.agents.ResumeAll();
            if (Context.IsMainPlayer && this.saveDataWritable)
                this.bodies.RestoreDesired(this.registry.Active);
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        this.pendingOutputDrains.Clear();
        this.pendingOwnerReconnects.Clear();
        this.speech.Clear();
        this.multiplayer.ResetSession();
        this.plantingPreview.ResetSession();
        this.workRuntime.CancelAll("CANCELLED-BY-TITLE");
        this.leisure.ClearAll("CANCELLED-BY-TITLE");
        this.planting.ClearRuntime();
        this.taskExecution.ClearRuntime();
        this.work.ClearRuntime();
        this.assist.ClearRuntime();
        this.agents.Reset();
        this.storage.CancelRuntime("CANCELLED-BY-TITLE");
        this.crafting.SuspendAll("CANCELLED-BY-TITLE");
        this.vitals.ClearRuntime();
        this.appearance.ClearRuntime();
        this.bodies.DetachAll(clearGenerations: true);
        this.following.Clear();
        this.registry.Clear();
        this.saveDataWritable = false;
        this.State = LifecycleState.ReadyWithoutSave;
        this.SessionTick = 0;
        this.monitor.Log("Yui to Issho! save scope was released.", LogLevel.Trace);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        this.nekoBridge?.ProcessPending();
        if (this.State == LifecycleState.SaveReady)
        {
            this.SessionTick++;
            if (e.IsMultipleOf(6) && this.saveDataWritable)
            {
                this.appearance.Update(this.SessionTick);
                if (Context.IsMainPlayer)
                {
                    this.ProcessOwnerReconnects();
                    this.assist.Update(this.SessionTick);
                    this.leisure.Update(this.registry.Active, this.SessionTick);
                }
                if (Context.IsPlayerFree)
                {
                    this.vitals.Update(this.SessionTick);
                    this.workRuntime.Update(this.SessionTick);
                    this.storage.Update(this.SessionTick);
                    this.crafting.Update(this.SessionTick);
                    this.planting.Update(this.SessionTick);
                    AgentScheduleDecision schedule = this.agents.Update(this.SessionTick);
                    if (schedule.AdvanceWork)
                        this.work.Update(this.SessionTick);
                    if (schedule.AdvanceFollow)
                        this.following.Update(this.registry.Active, this.SessionTick);
                    if (this.SessionTick % 60 == 0)
                        this.DrainPendingOutputs();
                }
                else
                    this.following.PauseAll(this.registry.Active);
            }
            if (e.IsMultipleOf(6))
            {
                this.speech.Update(this.SessionTick);
                this.multiplayer.Update(this.SessionTick);
            }
        }
    }

    private NekoBridgeState CaptureNekoBridgeState(bool includeNearby)
    {
        bool worldReady = Context.IsWorldReady && this.State == LifecycleState.SaveReady;
        bool hostAuthoritative = Context.IsMainPlayer;
        if (!worldReady || !hostAuthoritative)
            return new NekoBridgeState { WorldReady = worldReady, HostAuthoritative = hostAuthoritative };

        CompanionIdentity identity = CompanionIdentity.ForOwner(Game1.player.UniqueMultiplayerID);
        if (!this.registry.TryGet(identity, out CompanionRecord record))
            return new NekoBridgeState { WorldReady = true, HostAuthoritative = true };

        bool bodyPresent = this.bodies.TryGetBody(identity, out NPC body) && body.currentLocation is not null;
        AgentRuntimeSnapshot? agent = this.agents.GetSnapshot(identity);
        WorkRuntimeSnapshot? workRuntime = this.work.GetSnapshot(identity);
        OwnerWorkAssistSnapshot assist = this.assist.GetSnapshot(identity);
        bool nearbyTruncated = false;
        IReadOnlyList<NekoBridgeTargetGroup>? nearby = includeNearby && bodyPresent
            ? CaptureNekoNearby(body.currentLocation!, body.Tile, out nearbyTruncated)
            : null;
        return new NekoBridgeState
        {
            WorldReady = true,
            HostAuthoritative = true,
            CompanionExists = true,
            BodyPresent = bodyPresent,
            Mode = record.Mode,
            Location = bodyPresent ? BoundBridgeText(body.currentLocation!.NameOrUniqueName, 128) : string.Empty,
            TileX = bodyPresent ? body.TilePoint.X : 0,
            TileY = bodyPresent ? body.TilePoint.Y : 0,
            Behavior = agent?.BehaviorState ?? AgentBehaviorStates.Unavailable,
            BrainPhase = agent?.BrainPhase ?? AgentBrainPhases.Dormant,
            WorkKind = record.WorkDirective?.Kind ?? string.Empty,
            WorkState = record.WorkDirective?.SuspendedReason ?? workRuntime?.Phase ?? string.Empty,
            AssistEnabled = assist.Enabled,
            AssistKind = assist.Kind,
            AssistState = assist.State,
            VitalState = record.Vitals.State,
            Health = record.Vitals.Health,
            MaxHealth = record.Vitals.MaxHealth,
            Stamina = record.Vitals.Stamina,
            MaxStamina = record.Vitals.MaxStamina,
            StaminaRatio = record.Vitals.MaxStamina > 0f ? record.Vitals.Stamina / record.Vitals.MaxStamina : 0f,
            FatigueLevel = CompanionFatigueLevels.From(record.Vitals),
            RecoveryDay = record.Vitals.RecoveryDay,
            RecoveryReason = record.Vitals.RecoveryReason ?? string.Empty,
            Nearby = nearby,
            NearbyTruncated = nearbyTruncated,
        };
    }

    private static IReadOnlyList<NekoBridgeTargetGroup> CaptureNekoNearby(GameLocation location, Vector2 origin, out bool truncated)
    {
        const int radius = AgentPerceptionService.ScanRadius;
        const int maximumGroups = 24;
        var groups = WorldTargetClassifier.Observe(location)
            .Select(fact => new
            {
                Fact = fact,
                Distance = Math.Abs((int)fact.Tile.X - (int)origin.X) + Math.Abs((int)fact.Tile.Y - (int)origin.Y),
            })
            .Where(item => item.Distance <= radius)
            .GroupBy(item => new
            {
                item.Fact.Category,
                item.Fact.Subtype,
                item.Fact.SuggestedWorkKind,
                item.Fact.Disposition,
                item.Fact.ReasonCode,
            })
            .OrderBy(group => group.Min(item => item.Distance))
            .ThenBy(group => group.Key.Category, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Subtype, StringComparer.Ordinal)
            .ToArray();
        truncated = groups.Length > maximumGroups;
        return groups
            .Take(maximumGroups)
            .Select(group => new NekoBridgeTargetGroup
            {
                Category = BoundBridgeText(group.Key.Category, 32),
                Subtype = BoundBridgeText(group.Key.Subtype, 64),
                Count = Math.Min(group.Count(), 999),
                SuggestedWorkKind = BoundBridgeText(group.Key.SuggestedWorkKind ?? string.Empty, 32),
                Disposition = BoundBridgeText(group.Key.Disposition, 24),
                ReasonCode = BoundBridgeText(group.Key.ReasonCode, 48),
                Nearest = group
                    .OrderBy(item => item.Distance)
                    .ThenBy(item => item.Fact.Tile.Y)
                    .ThenBy(item => item.Fact.Tile.X)
                    .Take(3)
                    .Select(item => new NekoBridgeRelativeTarget
                    {
                        Direction = RelativeDirection(origin, item.Fact.Tile),
                        Distance = item.Distance,
                    })
                    .ToArray(),
            })
            .ToArray();
    }

    private static string RelativeDirection(Vector2 origin, Vector2 target)
    {
        int dx = (int)target.X - (int)origin.X;
        int dy = (int)target.Y - (int)origin.Y;
        if (dx == 0 && dy == 0)
            return "Here";
        string vertical = dy < 0 ? "North" : dy > 0 ? "South" : string.Empty;
        string horizontal = dx < 0 ? "West" : dx > 0 ? "East" : string.Empty;
        return vertical + horizontal;
    }

    private static string BoundBridgeText(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
    {
        if (this.State == LifecycleState.SaveReady && Context.IsMainPlayer)
            this.work.NotifyLocationChanged(e.Location.NameOrUniqueName, this.SessionTick);
    }

    private void OnTerrainFeatureListChanged(object? sender, TerrainFeatureListChangedEventArgs e)
    {
        if (this.State == LifecycleState.SaveReady && Context.IsMainPlayer)
            this.work.NotifyLocationChanged(e.Location.NameOrUniqueName, this.SessionTick);
    }

    private void DrainPendingOutputs()
    {
        foreach (CompanionRecord record in this.registry.Active)
        {
            CompanionIdentity identity = record.Identity;
            if (record.ActiveTransactionId is not null
                || (this.inventories.PendingOutputCount(identity) == 0 && this.inventories.RecoveryVaultCount(identity) == 0)
                || this.inventories.Count(identity) >= CompanionInventoryStore.Capacity
                || !this.pendingOutputDrains.Add(identity))
                continue;

            this.inventories.RequestTransfer(
                identity,
                () => this.State == LifecycleState.SaveReady
                    && this.saveDataWritable
                    && Context.IsMainPlayer
                    && this.registry.TryGet(identity, out CompanionRecord current)
                    && ReferenceEquals(current, record)
                    ? this.inventories.DrainPendingOutputsLocked(record)
                    : InventoryActionResult.Failure("LIFECYCLE-CLOSED", "Pending Output drain was cancelled before commit because the authoritative lifecycle closed."),
                result =>
                {
                    this.pendingOutputDrains.Remove(identity);
                    if (result.Code == "OUTPUTS-DRAINED")
                        this.monitor.Log($"HY-OUTPUT-{result.Code}: {identity} {result.Message}", LogLevel.Info);
                    else if (!result.IsSuccess)
                        this.monitor.Log($"HY-OUTPUT-{result.Code}: {identity} {result.Message}", LogLevel.Warn);
                }
            );
        }
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (this.State != LifecycleState.SaveReady)
            return;
        if (!Context.IsMainPlayer)
            this.projection.Render(e);
        this.speech.Render(e);
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (Context.IsMainPlayer && this.State == LifecycleState.SaveReady && this.saveDataWritable)
        {
            this.agents.ResumeAll();
            this.work.RequireDayConfirmation();
            this.planting.PauseAll("DAY-START-REQUIRES-RESUME");
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        this.speech.Suspend();
        if (Context.IsMainPlayer)
        {
            this.agents.SuspendAll("DAY-ENDING");
            this.leisure.ClearAll("DAY-ENDING");
            this.planting.PauseAll("DAY-ENDING");
            this.assist.ReleaseLeases("DAY-ENDING");
        }
    }

    private void CancelForVitals(CompanionIdentity identity, string code)
    {
        this.agents.Interrupt(identity, code);
        this.leisure.Stand(identity, code);
        this.work.Suspend(identity, code);
        this.workRuntime.Cancel(identity, code);
        this.storage.Cancel(identity, code);
        this.planting.Pause(identity, code);
        CraftActionResult craftCancel = this.crafting.Cancel(identity);
        if (!craftCancel.IsSuccess)
        {
            LogLevel level = craftCancel.Code == "CRAFT-COMMIT-BOUNDARY" ? LogLevel.Info : LogLevel.Warn;
            this.monitor.Log($"HY-CRAFT-{craftCancel.Code}: {identity} {craftCancel.Message}", level);
        }
    }

    internal void ObserveOwnerDamage(Farmer owner, StardewValley.Monsters.Monster attacker)
    {
        if (!this.experimentalFeaturesEnabled || !Context.IsMainPlayer || this.State != LifecycleState.SaveReady || !this.saveDataWritable)
            return;
        CompanionIdentity identity = CompanionIdentity.ForOwner(owner.UniqueMultiplayerID);
        if (!this.registry.TryGet(identity, out _)
            || owner.currentLocation is null
            || !ReferenceEquals(attacker.currentLocation, owner.currentLocation))
            return;
        this.CancelForDefensiveCombat(identity, "INTERRUPTED-BY-OWNER-DAMAGE");
        string eventId = $"owner-{owner.UniqueMultiplayerID}-{this.SessionTick}-{Guid.NewGuid():N}";
        CombatCommandResult result = this.combat.ObserveDamage(identity, attacker, eventId);
        this.monitor.Log($"HY-COMBAT-{result.Code}: {result.Message}", result.IsSuccess ? LogLevel.Info : LogLevel.Warn);
    }

    private void ObserveYuiDamage(CompanionIdentity identity, StardewValley.Monsters.Monster attacker, string eventId)
    {
        if (!this.experimentalFeaturesEnabled || !Context.IsMainPlayer || this.State != LifecycleState.SaveReady || !this.saveDataWritable)
            return;
        this.CancelForDefensiveCombat(identity, "INTERRUPTED-BY-YUI-DAMAGE");
        CombatCommandResult result = this.combat.ObserveDamage(identity, attacker, eventId);
        this.monitor.Log($"HY-COMBAT-{result.Code}: {result.Message}", result.IsSuccess ? LogLevel.Info : LogLevel.Warn);
    }

    private void CancelForDefensiveCombat(CompanionIdentity identity, string code)
    {
        this.agents.Interrupt(identity, code);
        this.leisure.Stand(identity, code);
        this.work.Suspend(identity, code);
        this.workRuntime.Cancel(identity, code, includeCombat: false);
        this.storage.Cancel(identity, code);
        this.planting.Pause(identity, code);
        this.crafting.Cancel(identity);
    }

    private void OnOwnerConnected(long ownerId)
    {
        if (ownerId != 0)
            this.pendingOwnerReconnects.Add(ownerId);
    }

    private void OnOwnerDisconnected(long ownerId)
    {
        this.pendingOwnerReconnects.Remove(ownerId);
        this.agents.RemoveOwner(ownerId, "OWNER-DISCONNECTED");
        CompanionRecord[] records = this.registry.Active.Where(record => record.OwnerId == ownerId).ToArray();
        foreach (CompanionRecord record in records)
        {
            this.CancelForVitals(record.Identity, "OWNER-DISCONNECTED");
            this.work.Suspend(record.Identity, "OWNER-DISCONNECTED");
            this.planting.Pause(record.Identity, "OWNER-DISCONNECTED");
            this.vitals.HandleOwnerDisconnected(record.Identity);
            this.appearance.Clear(record.Identity, "OWNER-DISCONNECTED");
            this.bodies.Halt(record.Identity);
        }
        this.following.PauseAll(records);
    }

    private void ProcessOwnerReconnects()
    {
        foreach (long ownerId in this.pendingOwnerReconnects.ToArray())
        {
            Farmer? owner = Game1.GetPlayer(ownerId, onlyOnline: true);
            if (owner?.currentLocation is null)
                continue;
            foreach (CompanionRecord record in this.registry.Active.Where(record => record.OwnerId == ownerId))
            {
                if (record.WantsBody && !this.bodies.TryGetBody(record.Identity, out _))
                {
                    BodyBindResult bind = this.bodies.Bind(record, owner);
                    if (!bind.IsSuccess)
                    {
                        this.monitor.Log($"HY-BODY-{bind.Code}: Reconnect restore for {record.Identity} failed: {bind.Message}", LogLevel.Warn);
                        continue;
                    }
                }
                if (record.WorkDirective?.SuspendedReason == "OWNER-DISCONNECTED"
                    && string.IsNullOrWhiteSpace(record.ActiveTransactionId))
                {
                    WorkDirectiveResult resume = this.work.Resume(record.Identity);
                    if (!resume.IsSuccess)
                        this.monitor.Log($"HY-WORK-{resume.Code}: Reconnect restore for {record.Identity} remained suspended: {resume.Message}", LogLevel.Debug);
                }
            }
            this.agents.ResumeAll();
            this.pendingOwnerReconnects.Remove(ownerId);
        }
    }
}
