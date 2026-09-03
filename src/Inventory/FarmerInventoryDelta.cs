using StardewValley;

namespace YuiToIssho;

/// <summary>Captures only positive inventory deltas created by one synchronous engine call.</summary>
internal sealed class FarmerInventoryDelta
{
    private readonly Farmer farmer;
    private readonly Dictionary<Item, int> originalStacks;

    private FarmerInventoryDelta(Farmer farmer)
    {
        this.farmer = farmer;
        this.originalStacks = farmer.Items
            .OfType<Item>()
            .ToDictionary(item => item, item => item.Stack, ItemReferenceComparer.Instance);
    }

    public static FarmerInventoryDelta Capture(Farmer farmer) => new(farmer);

    public IReadOnlyList<Item> ExtractPositiveDeltas()
    {
        List<Item> outputs = new();
        for (int index = 0; index < this.farmer.Items.Count; index++)
        {
            Item? current = this.farmer.Items[index];
            if (current is null)
                continue;

            if (!this.originalStacks.TryGetValue(current, out int originalStack))
            {
                this.farmer.Items[index] = null;
                outputs.Add(current);
                continue;
            }

            int added = current.Stack - originalStack;
            if (added <= 0)
                continue;

            Item split = current.getOne();
            split.Stack = added;
            current.Stack = originalStack;
            outputs.Add(split);
        }
        return outputs;
    }

    private sealed class ItemReferenceComparer : IEqualityComparer<Item>
    {
        public static readonly ItemReferenceComparer Instance = new();

        public bool Equals(Item? left, Item? right) => ReferenceEquals(left, right);

        public int GetHashCode(Item item) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item);
    }
}

/// <summary>Temporarily isolates a Farmer's real inventory so engine outputs can be identified as exact instances.</summary>
internal sealed class FarmerInventoryIsolationLease : IDisposable
{
    private readonly Farmer farmer;
    private readonly Item?[] originalSlots;
    private bool disposed;

    private FarmerInventoryIsolationLease(Farmer farmer)
    {
        this.farmer = farmer;
        this.originalSlots = farmer.Items.ToArray();
        for (int index = 0; index < farmer.Items.Count; index++)
            farmer.Items[index] = null;
    }

    public static FarmerInventoryIsolationLease Begin(Farmer farmer) => new(farmer);

    public IReadOnlyList<Item> ExtractOutputs()
    {
        if (this.disposed)
            throw new ObjectDisposedException(nameof(FarmerInventoryIsolationLease));

        List<Item> outputs = this.farmer.Items.OfType<Item>().ToList();
        for (int index = 0; index < this.farmer.Items.Count; index++)
            this.farmer.Items[index] = null;
        return outputs;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        if (this.farmer.Items.Any(item => item is not null))
            throw new InvalidOperationException("Engine outputs must be extracted before restoring the Farmer inventory context.");

        for (int index = 0; index < this.originalSlots.Length && index < this.farmer.Items.Count; index++)
            this.farmer.Items[index] = this.originalSlots[index];
    }
}
