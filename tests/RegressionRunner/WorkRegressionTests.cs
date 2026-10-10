using System.Runtime.Serialization;
using System.Reflection;
using StardewValley.TerrainFeatures;
using StardewValley;
using YuiToIssho;

internal static class WorkRegressionTests
{
    internal static void Run()
    {
        Item NewItem() => (Item)FormatterServices.GetUninitializedObject(typeof(StardewValley.Object));
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        Item[] outputs = { NewItem(), NewItem(), NewItem() };
        var stored = new List<Item>();
        var restored = new List<Item>();
        int calls = 0;
        var result = GeneratedOutputSettlement.Route(outputs, item =>
        {
            calls++;
            stored.Add(item);
            return calls == 1
                ? InventoryActionResult.Failure("OUTPUT-IN-RECOVERY", "retained")
                : InventoryActionResult.Success("OUTPUT-IN-BAG", "retained");
        }, restored.Add);
        Check(calls == 3 && stored.SequenceEqual(outputs), "A recovery result must not abandon later extracted outputs.");
        Check(!result.IsSuccess && result.Code == "OUTPUT-IN-RECOVERY", "Partial failure must remain visible to the caller.");
        Check(restored.Count == 0, "An output retained in recovery must not be duplicated on the ground.");

        stored.Clear();
        restored.Clear();
        calls = 0;
        result = GeneratedOutputSettlement.Route(outputs, item =>
        {
            calls++;
            if (calls == 1)
                return InventoryActionResult.Failure("OUTPUT-RESPONSIBILITY-UNKNOWN", "not retained");
            stored.Add(item);
            return InventoryActionResult.Success("OUTPUT-IN-BAG", "retained");
        }, restored.Add);
        Check(calls == 3 && stored.SequenceEqual(outputs.Skip(1)), "A failed first output must not prevent storing subsequent outputs.");
        Check(restored.Count == 1 && ReferenceEquals(restored[0], outputs[0]), "Fallback must retain the exact failed item instance once.");
        Check(!result.IsSuccess, "Fallback retention must not hide the routing fault.");

        restored.Clear();
        calls = 0;
        result = GeneratedOutputSettlement.Route(outputs, item =>
        {
            if (++calls == 2) throw new InvalidOperationException("container unavailable");
            return InventoryActionResult.Success("OUTPUT-IN-BAG", "retained");
        }, restored.Add);
        Check(calls == 3 && restored.Count == 1 && ReferenceEquals(restored[0], outputs[1]), "A thrown routing failure must retain the failed exact item and continue remaining outputs.");
        Check(result.Code == "OUTPUT-ROUTING-FAILED", "A thrown routing fault must remain observable.");

        restored.Clear();
        result = GeneratedOutputSettlement.Route(outputs,
            item => InventoryActionResult.Failure("OUTPUT-RESPONSIBILITY-UNKNOWN", "container reported failure"),
            restored.Add, item => true);
        Check(restored.Count == 0 && !result.IsSuccess, "An exact output already held by a persistent container must never be duplicated by fallback.");

        restored.Clear();
        result = GeneratedOutputSettlement.Route(outputs,
            _ => throw new InvalidOperationException("inventory unavailable"), restored.Add,
            _ => throw new InvalidOperationException("inventory still unavailable"));
        Check(restored.SequenceEqual(outputs) && !result.IsSuccess, "A failed retention lookup must not abandon any extracted outputs.");

        // Exercise the actual capture boundary with original game collection/terrain types.
        var tree = new Tree { Location = new GameLocation() };
        tree.falling.Value = true;
        var location = tree.Location;
        Debris Drop() => new Debris { item = NewItem() };
        var unrelated = Drop();
        location.debris.Add(unrelated);
        var captured = new List<Debris>();
        ChoppingTreeDropCapture.Track(tree, drops => captured.AddRange(drops));
        MethodInfo before = typeof(ChoppingTreeDropCapture).GetMethod("BeforeUpdate", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo after = typeof(ChoppingTreeDropCapture).GetMethod("AfterUpdate", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] state = { tree, null };
        before.Invoke(null, state);
        var produced = Drop();
        location.debris.Add(produced);
        after.Invoke(null, new[] { tree, state[1] });
        Check(captured.Count == 1 && ReferenceEquals(captured[0], produced), "Only debris produced inside the exact tree update may enter its delayed output queue.");
        Check(location.debris.Contains(unrelated), "Unrelated pre-existing or between-tick drops must remain in the world.");

        state = new object?[] { tree, null };
        before.Invoke(null, state);
        var cancelledOutput = Drop();
        location.debris.Add(cancelledOutput);
        ChoppingTreeDropCapture.Release(tree);
        after.Invoke(null, new[] { tree, state[1] });
        Check(captured.Count == 1 && location.debris.Contains(cancelledOutput), "Releasing a task during capture must preserve unclaimed drops in the world.");
        state = new object?[] { tree, null };
        before.Invoke(null, state);
        Check(state[1] is null, "Untracked trees must skip capture allocation.");

        var worldOutput = new StardewValley.Object { Stack = 1 };
        var worldDrop = new Debris { item = worldOutput };
        location.debris.Add(worldDrop);
        var worldResult = WorldDebrisCapture.RouteSpecificLocked(location, new[] { worldDrop },
            _ => throw new InvalidOperationException("inventory lookup failed"), _ => false);
        Check(!worldResult.Result.IsSuccess && worldResult.Result.Code == "WORLD-OUTPUT-ROUTING-FAILED", "A world output storage exception must become a bounded routing failure.");
        Check(location.debris.Contains(worldDrop) && ReferenceEquals(worldDrop.item, worldOutput), "A storage exception must restore the exact removed debris and output item.");
        restored.Clear();
        calls = 0;
        GeneratedOutputSettlement.Route(outputs, _ =>
        {
            calls++;
            return InventoryActionResult.Success("OUTPUT-IN-BAG", "retained");
        }, restored.Add);
        Check(calls == outputs.Length, "A world routing failure must leave the caller able to preserve all directly extracted outputs.");

        worldResult = WorldDebrisCapture.RouteSpecificLocked(location, new[] { worldDrop },
            _ => throw new InvalidOperationException("container reported failure after retain"), _ => true);
        Check(!worldResult.Result.IsSuccess && !location.debris.Contains(worldDrop), "An exact world output already retained elsewhere must not be duplicated by restoring debris after an exception.");
    }
}
