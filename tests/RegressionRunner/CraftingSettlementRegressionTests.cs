using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using Netcode;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.GameData.Objects;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Network;
using YuiToIssho;

internal static class CraftingSettlementRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        FieldInfo playerField = typeof(Game1).GetField("_player", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousPlayer = playerField.GetValue(null);
        IDictionary<string, ObjectData> previousObjectData = Game1.objectData;
        IItemDataDefinition[] previousTypes = ItemRegistry.ItemTypes.ToArray();
        var identifierLookup = (IDictionary)typeof(ItemRegistry).GetField("IdentifierLookup", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            playerField.SetValue(null, CreateInventoryPlayer());
            // Cross-category ingredient checks use the game's real object definition.
            // Literal local data avoids loading content or a graphics device.
            Game1.objectData = new[] { "wood", "stone", "-12", "-14" }.ToDictionary(id => id,
                id => new ObjectData { Name = id, DisplayName = id, Description = "", Type = "Basic" });
            ItemRegistry.ItemTypes.Clear();
            identifierLookup.Clear();
            ItemRegistry.AddTypeDefinition(new ObjectDataDefinition());
            PartialReturnCanBeSavedAndRetried();
            ReturnRetriesRespectAnotherTask();
            QueuedReturnsRecheckTaskOwnership();
            ConsumptionUpdatesEscrowLedger();
            EqualMaterialStacksRemainAccountedFor();
            LegacyReturnLedgerRecoveryIsBounded();
        }
        finally
        {
            playerField.SetValue(null, previousPlayer);
            Game1.objectData = previousObjectData;
            ItemRegistry.ItemTypes.Clear();
            identifierLookup.Clear();
            foreach (IItemDataDefinition definition in previousTypes)
                ItemRegistry.AddTypeDefinition(definition);
            ItemRegistry.ResetCache();
        }
        Console.WriteLine("Crafting settlement regressions passed (partial returns, overlapping tasks, queued returns, material consumption, and retained output responsibility).");
    }

    internal static Farmer CreateInventoryPlayer()
    {
        // FarmerTeam's constructor loads UI textures. These tests only need its real
        // global inventory dictionary and network root, without a graphics device.
        var team = (FarmerTeam)FormatterServices.GetUninitializedObject(typeof(FarmerTeam));
        var inventories = new NetStringDictionary<Inventory, NetRef<Inventory>>();
        var mutexes = new NetStringDictionary<NetMutex, NetRef<NetMutex>>();
        typeof(FarmerTeam).GetField("globalInventories")!.SetValue(team, inventories);
        typeof(FarmerTeam).GetField("globalInventoryMutexes")!.SetValue(team, mutexes);
        typeof(FarmerTeam).GetField("<NetFields>k__BackingField", Private)!
            .SetValue(team, new NetFields("FarmerTeam").SetOwner(team)
                .AddField(inventories, "globalInventories").AddField(mutexes, "globalInventoryMutexes"));
        var farmer = (Farmer)FormatterServices.GetUninitializedObject(typeof(Farmer));
        typeof(Farmer).GetField("uniqueMultiplayerID")!.SetValue(farmer, new NetLong(900));
        farmer.teamRoot = new NetRoot<FarmerTeam>(team);
        return farmer;
    }

    private static (CompanionRecord Record, CraftTransactionRecord Transaction, CompanionInventoryStore Inventories, CraftingCoordinator Coordinator, NetMutex Mutex, Material Stone) ReturnScenario(long ownerId)
    {
        var registry = new CompanionRegistry();
        var record = new CompanionRecord { OwnerId = ownerId, Slot = 1, Mode = CompanionModes.Wait };
        ((IDictionary)typeof(CompanionRegistry).GetField("records", Private)!.GetValue(registry)!).Add(record.Identity, record);
        var inventories = new CompanionInventoryStore();
        var coordinator = new CraftingCoordinator(registry, inventories, null!, null!, null!, null!);
        CraftTransactionRecord transaction = NewReturnTransaction(ownerId);
        record.CraftTransaction = transaction;
        record.ActiveTransactionId = $"craft:{transaction.CraftId}";
        Material stone = Reserve(inventories.GetCraftEscrow(record.Identity), transaction, "stone", 3);
        NetMutex mutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(CompanionInventoryStore.GetNamespace(record.Identity));
        return (record, transaction, inventories, coordinator, mutex, stone);
    }

    private static void PumpReturn(NetMutex mutex)
    {
        // Poll the actual game lock. RequestTransfer releases it in its finally block,
        // so NetMutex never reaches the online-farmer lookup in this content-free test.
        mutex.Update((FarmerCollection)null!);
    }

    private static void ReturnRetriesRespectAnotherTask()
    {
        var (record, transaction, inventories, coordinator, mutex, stone) = ReturnScenario(920);
        Inventory bag = inventories.Get(record.Identity);
        for (int i = 0; i < CompanionInventoryStore.Capacity; i++)
            bag.Add(new Material($"filler-{i}", 1));
        Assert.Equal("CRAFT-CANCEL-QUEUED", coordinator.Cancel(record.Identity).Code);
        PumpReturn(mutex);
        Assert.True(record.ActiveTransactionId is null, "A blocked return must release its own craft gate.");
        Assert.Equal(CraftPhases.ReturningMaterials, transaction.Phase);

        var registry = (CompanionRegistry)typeof(CraftingCoordinator).GetField("registry", Private)!.GetValue(coordinator)!;
        var tasks = new TaskExecutionService(registry, new CompanionBodyBinder(null!), new NoopMonitor());
        record.Mode = CompanionModes.Follow;
        TaskBeginResult started = tasks.TryBegin(record.Identity, "storage:new-task", "Storage",
            new TaskTargetKey("Farm", "Chest", "return-overlap"));
        Assert.True(started.Started, started.Result.Message);
        TaskSession session = started.Session!;
        record.Mode = CompanionModes.Wait;
        string? lastFailure = transaction.LastFailure;
        foreach (bool freeSlot in new[] { false, true })
        {
            if (freeSlot)
                bag.RemoveAt(bag.Count - 1);
            Assert.Equal("COMPANION-BUSY", coordinator.Cancel(record.Identity).Code);
            coordinator.RestoreAfterLoad();
            Assert.True(!mutex.IsLocked(), "A conflicting task must be rejected before requesting the bag lock.");
            AssertReturnUnchanged(record, transaction, inventories, stone, "storage:new-task");
            Assert.Equal(lastFailure, transaction.LastFailure);
            Assert.True(tasks.IsCurrent(session), "Rejected material returns must leave the running task current.");
        }
        Assert.True(tasks.Complete(session, true, "STORAGE-COMPLETED", "Storage completed after rejected craft return.").IsSuccess);
        Assert.True(record.ActiveTransactionId is null && record.Mode == CompanionModes.Follow,
            "The overlapping task must still release its own gate and restore its captured follow mode.");
        Assert.Equal("CRAFT-CANCEL-QUEUED", coordinator.Cancel(record.Identity).Code);
        PumpReturn(mutex);
        Assert.True(record.CraftTransaction is null && record.ActiveTransactionId is null);
        Assert.True(inventories.GetCraftEscrow(record.Identity).Count == 0 && bag.Any(item => ReferenceEquals(item, stone)));
        Assert.True(!transaction.SourcePlan.Single().Acquired && stone.Stack == 3);
        Assert.True(inventories.Validate(new[] { record }).IsSuccess);
        record.ActiveTransactionId = "storage:after-return";
        InventoryActionResult stale = (InventoryActionResult)typeof(CraftingCoordinator).GetMethod("ReturnEscrowToBag", Private)!
            .Invoke(coordinator, new object[] { record, transaction, "PLAYER-CANCELLED" })!;
        Assert.Equal("CRAFT-RETURN-STALE", stale.Code);
        Assert.Equal("storage:after-return", record.ActiveTransactionId);
        Assert.Equal(3, stone.Stack);
    }

    private static void QueuedReturnsRecheckTaskOwnership()
    {
        int variant = 0;
        foreach (string entry in new[] { "cancel", "restore", "save" })
        foreach (bool replaceGate in new[] { false, true })
        {
            var (record, transaction, inventories, coordinator, mutex, stone) = ReturnScenario(930 + variant++);
            if (entry == "cancel")
                Assert.Equal("CRAFT-CANCEL-QUEUED", coordinator.Cancel(record.Identity).Code);
            else if (entry == "restore")
                coordinator.RestoreAfterLoad();
            else
            {
                // SuspendAll uses the same FailChestSession path as an acquisition
                // failure or chest-craft cancellation, including its queued return.
                Type sessionType = typeof(CraftingCoordinator).GetNestedType("ChestCraftSession", BindingFlags.NonPublic)!;
                Type sourceType = typeof(CraftingCoordinator).GetNestedType("PlannedCraftSource", BindingFlags.NonPublic)!;
                object sources = Activator.CreateInstance(typeof(List<>).MakeGenericType(sourceType))!;
                object session = Activator.CreateInstance(sessionType, new object?[] { record, Game1.player, null, transaction, sources })!;
                ((IDictionary)typeof(CraftingCoordinator).GetField("chestSessions", Private)!.GetValue(coordinator)!).Add(record.Identity, session);
                coordinator.SuspendAll("SAVE");
                Assert.True(record.ActiveTransactionId is null, "Stopping a chest session must release only its own craft gate.");
            }
            if (replaceGate)
                record.ActiveTransactionId = "world:new-task";
            PumpReturn(mutex);
            if (replaceGate)
            {
                AssertReturnUnchanged(record, transaction, inventories, stone, "world:new-task");
                record.ActiveTransactionId = null;
                Assert.Equal("CRAFT-CANCEL-QUEUED", coordinator.Cancel(record.Identity).Code);
                PumpReturn(mutex);
            }
            Assert.True(record.CraftTransaction is null && record.ActiveTransactionId is null);
            Assert.True(inventories.GetCraftEscrow(record.Identity).Count == 0);
            Assert.True(inventories.Get(record.Identity).Count == 1 && ReferenceEquals(stone, inventories.Get(record.Identity)[0]));
            Assert.True(!transaction.SourcePlan.Single().Acquired && stone.Stack == 3);
            Assert.True(inventories.Validate(new[] { record }).IsSuccess);
        }
    }

    private static void AssertReturnUnchanged(CompanionRecord record, CraftTransactionRecord transaction, CompanionInventoryStore inventories, Material stone, string gate)
    {
        Assert.Equal(gate, record.ActiveTransactionId);
        Assert.Equal(CompanionModes.Wait, record.Mode);
        Assert.True(ReferenceEquals(transaction, record.CraftTransaction));
        Assert.True(inventories.GetCraftEscrow(record.Identity).Count == 1 && ReferenceEquals(stone, inventories.GetCraftEscrow(record.Identity)[0]));
        Assert.True(transaction.SourcePlan.Single().Acquired && stone.Stack == 3);
        Assert.True(stone.modData.ContainsKey(CompanionInventoryStore.CraftIdTag) && stone.modData.ContainsKey(CompanionInventoryStore.CraftSourceTag));
        Assert.True(inventories.Validate(new[] { record }).IsSuccess);
    }

    private static void EqualMaterialStacksRemainAccountedFor()
    {
        foreach (bool reverse in new[] { false, true })
        {
            var identity = CompanionIdentity.ForOwner(reverse ? 904 : 903);
            var inventories = new CompanionInventoryStore();
            Inventory escrow = inventories.GetCraftEscrow(identity);
            var transaction = new CraftTransactionRecord
            {
                CraftId = "equal-material-craft",
                CompletedCount = 1,
                RecipeSnapshot = new CraftRecipeSnapshot
                {
                    Ingredients = new List<CraftIngredientRecord> { new() { IngredientId = "-12", RequiredPerCraft = 7 } },
                },
            };
            Reserve(escrow, transaction, "wood", reverse ? 5 : 10);
            Reserve(escrow, transaction, "wood", reverse ? 10 : 5);
            MethodInfo consume = typeof(CraftingCoordinator).GetMethod("ConsumeMaterials", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.True((bool)consume.Invoke(null, new object[] { escrow, transaction })!);
            Assert.Equal(8, escrow.Sum(item => item.Stack));
            var coordinator = new CraftingCoordinator(null!, inventories, null!, null!, null!, null!);
            Assert.True((bool)typeof(CraftingCoordinator).GetMethod("MoveEscrowToBag", Private)!
                .Invoke(coordinator, new object[] { identity, transaction })!);
            Assert.True(transaction.SourcePlan.All(source => !source.Acquired));
            Assert.Equal(0, escrow.Count);
            Assert.Equal(8, inventories.Get(identity).Sum(item => item.Stack), "Same-fingerprint sources must conserve materials regardless of stack order.");
        }
    }

    private static void PartialReturnCanBeSavedAndRetried()
    {
        var registry = new CompanionRegistry();
        var record = new CompanionRecord { OwnerId = 901, Slot = 1, DisplayName = CompanionGenerationPolicy.DisplayName };
        ((IDictionary)typeof(CompanionRegistry).GetField("records", Private)!.GetValue(registry)!).Add(record.Identity, record);
        var inventories = new CompanionInventoryStore();
        var coordinator = new CraftingCoordinator(registry, inventories, null!, null!, null!, null!);
        CraftTransactionRecord transaction = NewReturnTransaction(record.OwnerId);
        record.CraftTransaction = transaction;
        record.ActiveTransactionId = $"craft:{transaction.CraftId}";
        Inventory bag = inventories.Get(record.Identity);
        Inventory escrow = inventories.GetCraftEscrow(record.Identity);
        var bagWood = new Material("wood", 10);
        bag.Add(bagWood);
        for (int i = 1; i < CompanionInventoryStore.Capacity; i++)
            bag.Add(new Material($"filler-{i}", 1));
        Material wood = Reserve(escrow, transaction, "wood", 5);
        Material stone = Reserve(escrow, transaction, "stone", 3);

        InventoryActionResult Return() => (InventoryActionResult)typeof(CraftingCoordinator)
            .GetMethod("ReturnEscrowToBag", Private)!.Invoke(coordinator, new object[] { record, transaction, "PLAYER-CANCELLED" })!;

        Assert.Equal("CRAFT-RETURN-BAG-FULL", Return().Code);
        Assert.Equal(15, bagWood.Stack, "The mergeable material must return despite a full bag.");
        Assert.Equal(1, escrow.Count);
        Assert.True(ReferenceEquals(stone, escrow[0]), "The exact unreturned material must remain in escrow.");
        Assert.True(!transaction.SourcePlan[0].Acquired && transaction.SourcePlan[1].Acquired);
        Assert.Equal(CraftPhases.ReturningMaterials, transaction.Phase);
        Assert.True(CraftPhases.CanCancel(transaction.Phase), "A full bag must allow a cancellation retry.");
        Assert.True(record.ActiveTransactionId is null);
        Assert.True(inventories.Validate(new[] { record }).IsSuccess, "A partially returned craft must pass the same inventory validation used after loading.");
        CompanionRegistry restored = Reload(registry);
        Assert.True(inventories.Validate(restored.All).IsSuccess, "A partial return must survive snapshot serialization and both read-time validators.");
        Assert.Equal(CraftPhases.ReturningMaterials, restored.All.Single().CraftTransaction!.Phase);

        Assert.Equal("CRAFT-RETURN-BAG-FULL", Return().Code);
        Assert.Equal(15, bagWood.Stack, "A repeated cancellation must not return the merged material twice.");
        bag.RemoveAt(bag.Count - 1);
        Assert.Equal("CRAFT-CANCELLED", Return().Code);
        Assert.True(record.CraftTransaction is null && escrow.Count == 0);
        Assert.True(bag.Any(item => ReferenceEquals(item, stone)), "The unmerged material must retain its exact instance.");
        Assert.True(!stone.modData.ContainsKey(CompanionInventoryStore.CraftIdTag)
            && !stone.modData.ContainsKey(CompanionInventoryStore.CraftSourceTag));
        Assert.Equal(15, bagWood.Stack);
        Assert.Equal(3, stone.Stack);
        Assert.True(inventories.Validate(new[] { record }).IsSuccess);
        Assert.Equal("CRAFT-RETURN-STALE", Return().Code, "An old return callback must remain harmless after completion.");
    }

    private static CraftTransactionRecord NewReturnTransaction(long ownerId) => new()
    {
        CraftId = Guid.NewGuid().ToString("N"),
        OperationId = "return-op",
        OwnerId = ownerId,
        RecipeKey = "TestRecipe",
        RecipePolicyVersion = CraftingRecipePolicy.Version,
        CraftCount = 1,
        Phase = CraftPhases.ReturningMaterials,
        RecipeSnapshot = new CraftRecipeSnapshot
        {
            OutputQualifiedItemId = "(O)TestOutput",
            OutputPerCraft = 1,
            Ingredients = new List<CraftIngredientRecord> { new() { IngredientId = "-12", RequiredPerCraft = 1 } },
        },
    };

    private static CompanionRegistry Reload(CompanionRegistry registry)
    {
        YuiToIsshoSaveData snapshot = JsonSerializer.Deserialize<YuiToIsshoSaveData>(JsonSerializer.Serialize(registry.CreateSnapshot()))!;
        var restored = new CompanionRegistry();
        RegistryLoadResult loaded = restored.Load(snapshot, snapshot.Companions.Select(record => record.OwnerId).ToHashSet());
        Assert.True(loaded.IsSuccess, loaded.Message);
        return restored;
    }

    private static void LegacyReturnLedgerRecoveryIsBounded()
    {
        int variant = 0;
        foreach (string change in new[] { "none", "output-token", "output-tokens", "completed", "quantity", "fingerprint", "reason" })
        {
            var registry = new CompanionRegistry();
            var record = new CompanionRecord { OwnerId = 910 + variant++, Slot = 1, DisplayName = CompanionGenerationPolicy.DisplayName };
            ((IDictionary)typeof(CompanionRegistry).GetField("records", Private)!.GetValue(registry)!).Add(record.Identity, record);
            CraftTransactionRecord transaction = NewReturnTransaction(record.OwnerId);
            record.CraftTransaction = transaction;
            transaction.Phase = CraftPhases.Faulted;
            transaction.LastFailure = "PLAYER-CANCELLED:RETURN-BAG-FULL";
            var inventories = new CompanionInventoryStore();
            Inventory escrow = inventories.GetCraftEscrow(record.Identity);
            Material returned = Reserve(escrow, transaction, "wood", 5);
            Material remaining = Reserve(escrow, transaction, "stone", 3);
            escrow.Remove(returned);
            returned.modData.Clear();
            inventories.Get(record.Identity).Add(returned);
            switch (change)
            {
                case "output-token": transaction.OutputToken = Guid.NewGuid().ToString("N"); break;
                case "output-tokens": transaction.OutputTokens.Add(Guid.NewGuid().ToString("N")); break;
                case "completed": transaction.CompletedCount = 1; break;
                case "quantity": transaction.SourcePlan[1].Quantity++; break;
                case "fingerprint": transaction.SourcePlan[1].ItemFingerprint = "changed"; break;
                case "reason": transaction.LastFailure = "OTHER-FAULT"; break;
            }
            CompanionRegistry restored = Reload(registry);
            CraftTransactionRecord restoredTransaction = restored.All.Single().CraftTransaction!;
            InventoryValidationResult result = inventories.Validate(restored.All);
            if (change == "none")
            {
                Assert.True(result.IsSuccess, result.Message);
                Assert.Equal(CraftPhases.ReturningMaterials, restoredTransaction.Phase);
                Assert.True(!restoredTransaction.SourcePlan[0].Acquired && restoredTransaction.SourcePlan[1].Acquired);
                Assert.True(inventories.Validate(Reload(restored).All).IsSuccess, "Repaired legacy ledger must remain valid after another save/load.");
            }
            else
            {
                Assert.Equal("CRAFT-ESCROW-MISMATCH", result.Code, $"Legacy recovery must reject {change}.");
                Assert.True(restoredTransaction.SourcePlan.All(source => source.Acquired), "Failed legacy validation must leave the entire ledger unchanged.");
                Assert.Equal(CraftPhases.Faulted, restoredTransaction.Phase);
            }
            Assert.True(escrow.Count == 1 && ReferenceEquals(remaining, escrow[0]));
            Assert.True(inventories.Get(record.Identity).Count == 1 && ReferenceEquals(returned, inventories.Get(record.Identity)[0]));
            Assert.Equal(5, returned.Stack);
            Assert.Equal(3, remaining.Stack);
        }
    }

    private static void ConsumptionUpdatesEscrowLedger()
    {
        var record = new CompanionRecord { OwnerId = 902, Slot = 1 };
        var inventories = new CompanionInventoryStore();
        Inventory escrow = inventories.GetCraftEscrow(record.Identity);
        var transaction = new CraftTransactionRecord
        {
            CraftId = "consume-craft",
            CompletedCount = 1,
            Phase = CraftPhases.Faulted,
            RecipeSnapshot = new CraftRecipeSnapshot
            {
                Ingredients = new List<CraftIngredientRecord>
                {
                    new() { IngredientId = "-12", RequiredPerCraft = 2 },
                    new() { IngredientId = "-14", RequiredPerCraft = 2 },
                },
            },
        };
        record.CraftTransaction = transaction;
        Reserve(escrow, transaction, "wood", 2);
        Material stone = Reserve(escrow, transaction, "stone", 3);
        MethodInfo consume = typeof(CraftingCoordinator).GetMethod("ConsumeMaterials", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True((bool)consume.Invoke(null, new object[] { escrow, transaction })!);
        Assert.True(!transaction.SourcePlan[0].Acquired && transaction.SourcePlan[1].Acquired);
        Assert.Equal(1, stone.Stack, "Partially consumed source stacks must remain represented in escrow.");
        Assert.True(inventories.Validate(new[] { record }).IsSuccess);

        // Complete-output reconciliation can retain the transaction while its output
        // tags are being released. Empty escrow must still have an empty acquired ledger.
        typeof(CraftingCoordinator).GetMethod("ConsumeAllEscrow", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { escrow, transaction });
        transaction.Phase = CraftPhases.Reconciling;
        Assert.Equal(0, escrow.Count);
        Assert.True(transaction.SourcePlan.All(source => !source.Acquired));
        Assert.True(inventories.Validate(new[] { record }).IsSuccess);
    }

    private static Material Reserve(Inventory escrow, CraftTransactionRecord transaction, string id, int quantity)
    {
        var material = new Material(id, quantity);
        material.modData[CompanionInventoryStore.CraftIdTag] = transaction.CraftId;
        material.modData[CompanionInventoryStore.CraftSourceTag] = Guid.NewGuid().ToString("N");
        string fingerprint = (string)typeof(CraftingCoordinator).GetMethod("Fingerprint", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { material })!;
        transaction.SourcePlan.Add(new CraftSourceRecord
        {
            QualifiedItemId = material.QualifiedItemId,
            ItemFingerprint = fingerprint,
            Quantity = quantity,
            Acquired = true,
        });
        escrow.Add(material);
        return material;
    }

    private sealed class Material : StardewValley.Object
    {
        private readonly string materialId;

        internal Material(string materialId, int quantity)
        {
            this.materialId = materialId;
            this.ItemId = materialId;
            this.Category = materialId == "wood" ? -12 : materialId == "stone" ? -14 : 0;
            this.Stack = quantity;
        }

        public override bool canStackWith(ISalable other) => other is Material material && material.materialId == this.materialId;
        public override int maximumStackSize() => 999;
        protected override Item GetOneNew() => new Material(this.materialId, 1);
    }

    private sealed class NoopMonitor : IMonitor
    {
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level = LogLevel.Trace) { }
        public void LogOnce(string message, LogLevel level = LogLevel.Trace) { }
        public void VerboseLog(string message) { }
        public void VerboseLog(ref VerboseLogStringHandler message) { }
    }
}
