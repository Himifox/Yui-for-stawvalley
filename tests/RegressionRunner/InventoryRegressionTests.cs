using System.Collections;
using System.Reflection;
using YuiToIssho;

internal static class InventoryRegressionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        var registry = new CompanionRegistry();
        var record = new CompanionRecord { OwnerId = 1, Slot = 1 };
        var records = (IDictionary)typeof(CompanionRegistry).GetField("records", Private)!.GetValue(registry)!;
        records.Add(record.Identity, record);
        var coordinator = new CraftingCoordinator(registry, new CompanionInventoryStore(), null!, null!, null!, null!);
        Type coordinatorType = typeof(CraftingCoordinator);
        Type sessionType = coordinatorType.GetNestedType("ChestCraftSession", BindingFlags.NonPublic)!;
        Type sourceType = coordinatorType.GetNestedType("PlannedCraftSource", BindingFlags.NonPublic)!;
        var descriptor = new CraftRecipeDescriptor("Chest", null!, Array.Empty<CraftIngredientRecord>(), "(BC)130", 1);
        object Session(CraftTransactionRecord transaction) => Activator.CreateInstance(sessionType,
            new object[] { record, null!, descriptor, transaction, Activator.CreateInstance(typeof(List<>).MakeGenericType(sourceType))! })!;
        object Invoke(string method, params object[] arguments) => coordinatorType.GetMethod(method, Private)!.Invoke(coordinator, arguments)!;
        var sessions = (IDictionary)coordinatorType.GetField("chestSessions", Private)!.GetValue(coordinator)!;
        var cancelled = new CraftTransactionRecord { CraftId = "old", Phase = CraftPhases.Planned };
        object oldSession = Session(cancelled);

        // This is the queued production action after cancellation removed its session and transaction.
        var acquisition = (InventoryActionResult)Invoke("AcquireBagSources", oldSession);
        Assert.Equal("CRAFT-SESSION-STALE", acquisition.Code);
        Assert.True(!acquisition.IsSuccess, "Cancelled acquisition must stop before inventory access.");
        var commit = (CraftActionResult)Invoke("CommitEscrowed", oldSession);
        Assert.Equal("CRAFT-SESSION-STALE", commit.Code);

        var replacement = new CraftTransactionRecord { CraftId = "replacement", Phase = CraftPhases.Planned };
        record.CraftTransaction = replacement;
        record.ActiveTransactionId = "craft:replacement";
        object currentSession = Session(replacement);
        sessions.Add(record.Identity, currentSession);
        Invoke("FailChestSession", oldSession, "OLD-LOCK-FAILED");
        Assert.True(ReferenceEquals(sessions[record.Identity], currentSession), "An old lock failure must not cancel the replacement craft.");
        Assert.True(ReferenceEquals(record.CraftTransaction, replacement));
        Assert.Equal("CRAFT-SESSION-STALE", ((InventoryActionResult)Invoke("AcquireBagSources", oldSession)).Code);

        var returned = (InventoryActionResult)Invoke("ReturnEscrowToBag", record, cancelled, "OLD-RETURN");
        Assert.Equal("CRAFT-RETURN-STALE", returned.Code);
        Assert.True(ReferenceEquals(record.CraftTransaction, replacement), "An old return must not touch new craft materials.");
        Assert.True((bool)Invoke("IsCurrentChestSession", currentSession));
        records[record.Identity] = new CompanionRecord { OwnerId = 1, Slot = 1 };
        Assert.Equal("CRAFT-SESSION-STALE", ((InventoryActionResult)Invoke("AcquireBagSources", currentSession)).Code);
        Console.WriteLine("Inventory regressions passed (cancelled, replaced, and unloaded crafting callbacks).");
    }
}
