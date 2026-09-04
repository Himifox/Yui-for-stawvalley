namespace YuiToIssho;

internal readonly record struct CompanionIdentity(long OwnerId, int Slot)
{
    public const int CanonicalSlot = 1;

    public bool IsCanonical => this.OwnerId > 0 && this.Slot == CanonicalSlot;

    public static CompanionIdentity ForOwner(long ownerId) => new(ownerId, CanonicalSlot);

    public static bool IsValidSlot(int slot) => slot == CanonicalSlot;

    public override string ToString() => $"{this.OwnerId}:{this.Slot}";
}
