using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using Netcode;
using StardewValley;
using StardewValley.Network;
using YuiToIssho;

internal static class PlantingRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        FieldInfo playerField = typeof(Game1).GetField("_player", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? originalPlayer = playerField.GetValue(null);
        Game1 originalGame = Game1.game1;
        try
        {
            // A remote owner exercises the real lifecycle gate without local menus or game content.
            Game1.game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
            Farmer NewFarmer(long id)
            {
                // Farmer's constructor loads sprite assets through FarmerTeam; only these
                // real network fields are needed for the production owner lifecycle gate.
                var farmer = (Farmer)FormatterServices.GetUninitializedObject(typeof(Farmer));
                typeof(Farmer).GetField("uniqueMultiplayerID")!.SetValue(farmer, new NetLong(id));
                typeof(Farmer).GetField("usingTool")!.SetValue(farmer, new NetBool(false));
                typeof(Character).GetField("currentLocationRef", Private)!.SetValue(farmer, new NetLocationRef(new GameLocation()));
                farmer.CanMove = true;
                return farmer;
            }
            playerField.SetValue(null, NewFarmer(1));
            Farmer owner = NewFarmer(2);
            var registry = new CompanionRegistry();
            var record = new CompanionRecord { OwnerId = owner.UniqueMultiplayerID, Slot = 1 };
            var records = (IDictionary)typeof(CompanionRegistry).GetField("records", Private)!.GetValue(registry)!;
            records.Add(record.Identity, record);
            var coordinator = new PlantingCoordinator(registry, new CompanionBodyBinder(null!), null!, null!, null!, null!, null!, null!, null!);
            Type runtimeType = typeof(PlantingCoordinator).GetNestedType("PlantingRuntime", BindingFlags.NonPublic)!;
            Type sourceType = typeof(PlantingCoordinator).GetNestedType("PlantingRuntimeSource", BindingFlags.NonPublic)!;
            var runtimes = (IDictionary)typeof(PlantingCoordinator).GetField("runtimes", Private)!.GetValue(coordinator)!;

            (PlantingTransactionRecord Transaction, object Runtime) Track(params PlantingSourceRecord[] sources)
            {
                var transaction = new PlantingTransactionRecord
                {
                    PlantingId = Guid.NewGuid().ToString("N"),
                    Phase = PlantingPhases.AcquiringSeeds,
                    SourcePlan = sources.ToList(),
                };
                var runtimeSources = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sourceType))!;
                foreach (PlantingSourceRecord source in sources)
                    runtimeSources.Add(Activator.CreateInstance(sourceType, new object?[] { source, null, null, null })!);
                object runtime = Activator.CreateInstance(runtimeType, new object[] { record, owner, transaction, runtimeSources })!;
                runtimeType.GetProperty("BagAcquired")!.SetValue(runtime, true);
                record.PlantingTransaction = transaction;
                record.ActiveTransactionId = transaction.PlantingId;
                runtimes[record.Identity] = runtime;
                return (transaction, runtime);
            }
            PlantingSourceRecord Source(string kind, int acquired = 3) => new() { SourceKind = kind, Quantity = 3, AcquiredQuantity = acquired };

            foreach (string[] kinds in new[]
            {
                new[] { PlantingSourceKinds.Bag },
                new[] { PlantingSourceKinds.AuthorizedChest },
                new[] { PlantingSourceKinds.Bag, PlantingSourceKinds.AuthorizedChest },
            })
            {
                var (transaction, _) = Track(kinds.Select(kind => Source(kind)).ToArray());
                coordinator.Update(10);
                Assert.Equal(PlantingPhases.Planting, transaction.Phase, "A complete real seed plan must leave acquisition on the next tick, regardless of source kind.");
                Assert.Equal(10UL, transaction.UpdatedTick);
                Assert.True(transaction.SourcePlan.All(source => source.AcquiredQuantity == source.Quantity && source.ConsumedQuantity == 0), "The transition must not consume or reacquire any seed.");
            }

            var (waiting, pendingRuntime) = Track(Source(PlantingSourceKinds.Bag), Source(PlantingSourceKinds.AuthorizedChest, 0));
            runtimeType.GetProperty("LockPending")!.SetValue(pendingRuntime, true);
            coordinator.Update(11);
            Assert.Equal(PlantingPhases.AcquiringSeeds, waiting.Phase, "A pending final chest acquisition must finish before planting.");
            waiting.SourcePlan[1].AcquiredQuantity = waiting.SourcePlan[1].Quantity;
            coordinator.Update(12);
            Assert.Equal(PlantingPhases.AcquiringSeeds, waiting.Phase, "An in-flight lock callback still owns the acquisition boundary.");
            runtimeType.GetProperty("LockPending")!.SetValue(pendingRuntime, false);
            coordinator.Update(13);
            Assert.Equal(PlantingPhases.Planting, waiting.Phase, "The tick after the final chest callback must complete acquisition.");

            var (busy, busyRuntime) = Track(Source(PlantingSourceKinds.Bag));
            owner.CanMove = false;
            coordinator.Update(14);
            Assert.Equal(PlantingPhases.AcquiringSeeds, busy.Phase, "Owner lifecycle gating must still pause a fully acquired plan.");
            var acquire = typeof(PlantingCoordinator).GetMethod("AcquireBagSources", Private)!;
            Assert.Equal("OWNER-BUSY", ((InventoryActionResult)acquire.Invoke(coordinator, new[] { busyRuntime })!).Code);
            owner.CanMove = true;
            coordinator.Update(15);
            Assert.Equal(PlantingPhases.Planting, busy.Phase, "Acquisition must continue when the owner becomes available.");

            foreach (string phase in new[] { PlantingPhases.Cancelling, PlantingPhases.ReturningSeeds })
            {
                var (returning, runtime) = Track(Source(PlantingSourceKinds.Bag, 0));
                returning.Phase = phase;
                runtimeType.GetProperty("LockPending")!.SetValue(runtime, true);
                coordinator.Update(16);
                Assert.Equal(phase, returning.Phase, "Cancellation must retain priority over an incomplete seed plan.");
                Assert.Equal("PLANT-TRANSACTION-STALE", ((InventoryActionResult)acquire.Invoke(coordinator, new[] { runtime })!).Code, "A queued bag acquisition must not restore a cancelled transaction.");
                Assert.Equal(0, returning.SourcePlan[0].AcquiredQuantity);
            }

            var (paused, pausedRuntime) = Track(Source(PlantingSourceKinds.Bag));
            coordinator.Pause(record.Identity, "TEST-PAUSE");
            coordinator.Update(17);
            Assert.Equal(PlantingPhases.Paused, paused.Phase, "An explicit pause must remain paused despite a complete seed plan.");
            Assert.True(!runtimes.Contains(record.Identity), "Pausing must drop the active acquisition runtime.");
            Assert.Equal("PLANT-TRANSACTION-STALE", ((InventoryActionResult)acquire.Invoke(coordinator, new[] { pausedRuntime })!).Code, "A queued callback must not reacquire after pause.");
            PlantingExecutionRegressionTests.Run();
            Console.WriteLine("Planting regressions passed (bag/chest/mixed acquisition, pending locks, owner gating, cancellation and pause).");
        }
        finally
        {
            playerField.SetValue(null, originalPlayer);
            Game1.game1 = originalGame;
        }
    }
}
