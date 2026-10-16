using System.Text.Json;
using YuiToIssho;

internal static class SaveRegressionTests
{
    public static void Run()
    {
        RemovedOwnerRoundTrip();
        RemovedAuthorizationWithoutCompanion();
        HistoricalOwnersDoNotCountAgainstCurrentLimit();
        InvalidRecordsStillFail();
        Console.WriteLine("Save regressions passed (removed owners, retained data, rejoining, and validation guards).");
    }

    private static CompanionRecord Companion(long ownerId) => new()
    {
        OwnerId = ownerId,
        Slot = CompanionIdentity.CanonicalSlot,
        DisplayName = CompanionGenerationPolicy.DisplayName,
        WantsBody = true,
    };

    private static AuthorizedChestRecord Authorization(long ownerId) => new()
    {
        OwnerId = ownerId,
        LocationKey = "Farm",
        TileX = (int)ownerId,
        TileY = 4,
        ChestToken = Guid.NewGuid().ToString("N"),
    };

    private static void RemovedOwnerRoundTrip()
    {
        CompanionRecord current = Companion(1);
        CompanionRecord removed = Companion(2);
        removed.Bond.Points = 1250;
        removed.RecentOperations.Add(new OperationReceiptRecord
        {
            OperationId = "completed-before-demolition",
            IsSuccess = true,
            Code = "COMMITTED",
            Message = "Retained receipt",
        });
        AuthorizedChestRecord currentChest = Authorization(1);
        AuthorizedChestRecord removedChest = Authorization(2);
        var data = new YuiToIsshoSaveData
        {
            Companions = new() { current, removed },
            AuthorizedChests = new() { currentChest, removedChest },
        };
        var registry = new CompanionRegistry();
        RegistryLoadResult result = registry.Load(data, new HashSet<long> { 1 });
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.All.Single() == current && registry.Active.Single() == current);
        Assert.True(registry.TryGet(current.Identity, out _));
        Assert.True(!registry.TryGet(removed.Identity, out _), "Removed owners must not enter live commands or recovery.");
        Assert.True(registry.AuthorizedChests.Single() == currentChest);
        Assert.True(!registry.Delete(removed.Identity).IsSuccess, "Active deletion must not discard retained data.");

        // Exercise the persisted snapshot boundary, not just the filtered runtime view.
        YuiToIsshoSaveData snapshot = JsonSerializer.Deserialize<YuiToIsshoSaveData>(JsonSerializer.Serialize(registry.CreateSnapshot()))!;
        Assert.Equal(2, snapshot.Companions.Count);
        Assert.Equal(2, snapshot.AuthorizedChests.Count);
        CompanionRecord retained = snapshot.Companions.Single(record => record.OwnerId == 2);
        Assert.Equal(1250, retained.Bond.Points);
        Assert.True(retained.WantsBody);
        Assert.Equal("completed-before-demolition", retained.RecentOperations.Single().OperationId);
        Assert.Equal(removedChest.ChestToken, snapshot.AuthorizedChests.Single(chest => chest.OwnerId == 2).ChestToken);

        var reloaded = new CompanionRegistry();
        Assert.True(reloaded.Load(snapshot, new HashSet<long> { 1 }).IsSuccess);
        Assert.Equal(1, reloaded.Count);
        Assert.Equal(2, reloaded.CreateSnapshot().Companions.Count);
        Assert.True(reloaded.Load(reloaded.CreateSnapshot(), new HashSet<long> { 1, 2 }).IsSuccess);
        Assert.Equal(2, reloaded.Count, "Known offline/rejoining members must remain available.");
        Assert.True(reloaded.TryGet(removed.Identity, out CompanionRecord restored));
        Assert.Equal(1250, restored.Bond.Points);
        Assert.Equal(2, reloaded.AuthorizedChests.Count);
        reloaded.Clear();
        Assert.Equal(0, reloaded.CreateSnapshot().Companions.Count);
        Assert.Equal(0, reloaded.CreateSnapshot().AuthorizedChests.Count);
    }

    private static void RemovedAuthorizationWithoutCompanion()
    {
        var registry = new CompanionRegistry();
        var data = new YuiToIsshoSaveData { AuthorizedChests = new() { Authorization(2) } };
        Assert.True(registry.Load(data, new HashSet<long> { 1 }).IsSuccess);
        Assert.Equal(0, registry.AuthorizedChests.Count);
        Assert.Equal(1, registry.CreateSnapshot().AuthorizedChests.Count);
    }

    private static void HistoricalOwnersDoNotCountAgainstCurrentLimit()
    {
        var registry = new CompanionRegistry();
        var data = new YuiToIsshoSaveData
        {
            Companions = Enumerable.Range(1, CompanionGenerationPolicy.MaximumCompanions + 1)
                .Select(ownerId => Companion(ownerId)).ToList(),
        };
        Assert.True(registry.Load(data, new HashSet<long> { 1 }).IsSuccess);
        Assert.Equal(1, registry.Count);
        Assert.Equal(CompanionGenerationPolicy.MaximumCompanions + 1, registry.CreateSnapshot().Companions.Count);
        Assert.Equal("INVALID-COMPANION-COUNT", registry.Load(data, data.Companions.Select(record => record.OwnerId).ToHashSet()).Code);
    }

    private static void InvalidRecordsStillFail()
    {
        var registry = new CompanionRegistry();
        var known = new HashSet<long> { 1 };
        Assert.Equal("INVALID-IDENTITY", registry.Load(new YuiToIsshoSaveData
        {
            Companions = new() { Companion(0) },
        }, known).Code);
        Assert.Equal("DUPLICATE-IDENTITY", registry.Load(new YuiToIsshoSaveData
        {
            Companions = new() { Companion(2), Companion(2) },
        }, known).Code);
        AuthorizedChestRecord invalid = Authorization(2);
        invalid.ChestToken = "invalid";
        Assert.Equal("INVALID-CHEST-AUTHORIZATION", registry.Load(new YuiToIsshoSaveData
        {
            AuthorizedChests = new() { invalid },
        }, known).Code);
        Assert.Equal(0, registry.Count);
    }
}
