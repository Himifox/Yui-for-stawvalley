namespace YuiToIssho;

internal sealed class WorkRuntimeModule
{
    private readonly RuntimeParticipant[] participants;
    private readonly RuntimeParticipant combat;

    public WorkRuntimeModule(
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
        DeliveryCoordinator delivery)
    {
        this.combat = RuntimeParticipant.From(combat.Update, combat.Cancel, combat.CancelAll);
        this.participants =
        [
            RuntimeParticipant.From(watering.Update, watering.Cancel, watering.CancelAll),
            RuntimeParticipant.From(chopping.Update, chopping.Cancel, chopping.CancelAll),
            RuntimeParticipant.From(mining.Update, mining.Cancel, mining.CancelAll),
            RuntimeParticipant.From(harvesting.Update, harvesting.Cancel, harvesting.CancelAll),
            RuntimeParticipant.From(foraging.Update, foraging.Cancel, foraging.CancelAll),
            RuntimeParticipant.From(mowing.Update, mowing.Cancel, mowing.CancelAll),
            RuntimeParticipant.From(digging.Update, digging.Cancel, digging.CancelAll),
            RuntimeParticipant.From(animalCare.Update, animalCare.Cancel, animalCare.CancelAll),
            RuntimeParticipant.From(fishing.Update, fishing.Cancel, fishing.CancelAll),
            this.combat,
            RuntimeParticipant.FromVoid(delivery.Update, delivery.Cancel, delivery.CancelAll),
        ];
    }

    public void Update(ulong sessionTick)
    {
        foreach (RuntimeParticipant participant in this.participants)
            participant.Update(sessionTick);
    }

    public void CancelAll(string code)
    {
        foreach (RuntimeParticipant participant in this.participants)
            participant.CancelAll(code);
    }

    public void Cancel(CompanionIdentity identity, string code, bool includeCombat = true)
    {
        foreach (RuntimeParticipant participant in this.participants)
        {
            if (includeCombat || !ReferenceEquals(participant, this.combat))
                participant.Cancel(identity, code);
        }
    }

    private sealed record RuntimeParticipant(
        Action<ulong> Update,
        Action<CompanionIdentity, string> Cancel,
        Action<string> CancelAll)
    {
        public static RuntimeParticipant From<TResult>(
            Action<ulong> update,
            Func<CompanionIdentity, string, TResult> cancel,
            Action<string> cancelAll) => new(
                update,
                (identity, code) => _ = cancel(identity, code),
                cancelAll);

        public static RuntimeParticipant FromVoid(
            Action<ulong> update,
            Action<CompanionIdentity, string> cancel,
            Action<string> cancelAll) => new(update, cancel, cancelAll);
    }
}
