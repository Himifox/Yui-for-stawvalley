using StardewValley;

namespace YuiToIssho;

internal readonly record struct WorkActionRequest(
    CompanionIdentity Identity,
    WorkDirectiveRecord Directive,
    WorkCandidate Candidate,
    string OperationId);

internal readonly record struct WorkActionStartOutcome(
    bool IsSuccess,
    string Code,
    string Message,
    Item? ReservedTool,
    WorkStepGeometry? Geometry = null);

internal sealed class WorkActionRegistry
{
    private readonly Dictionary<string, Func<WorkActionRequest, WorkActionStartOutcome>> handlers = new(StringComparer.Ordinal);

    public WorkActionRegistry(
        WateringCoordinator watering,
        ChoppingCoordinator chopping,
        MiningCoordinator mining,
        HarvestCoordinator harvesting,
        ForageCoordinator foraging,
        MowingCoordinator mowing,
        DiggingCoordinator digging,
        AnimalCareCoordinator animalCare)
    {
        this.Register(WorkKinds.Water, request =>
        {
            WaterCommandResult result = watering.TryStart(
                request.Identity,
                (int)request.Candidate.Tile.X,
                (int)request.Candidate.Tile.Y,
                request.OperationId);
            WorkStepGeometry? geometry = result.IsSuccess && watering.TryGetReservedGeometry(request.Identity, out WorkStepGeometry frozen)
                ? frozen
                : null;
            return new WorkActionStartOutcome(result.IsSuccess, result.Code, result.Message, watering.GetReservedTool(request.Identity), geometry);
        });
        this.Register(WorkKinds.Harvest, request =>
        {
            HarvestCommandResult result = harvesting.TryStart(
                request.Identity,
                (int)request.Candidate.Tile.X,
                (int)request.Candidate.Tile.Y,
                request.OperationId);
            WorkStepGeometry? geometry = result.IsSuccess && harvesting.TryGetReservedGeometry(request.Identity, out WorkStepGeometry frozen)
                ? frozen
                : null;
            return new WorkActionStartOutcome(result.IsSuccess, result.Code, result.Message, harvesting.GetReservedTool(request.Identity), geometry);
        });
        this.Register(WorkKinds.Mow, request =>
        {
            MowCommandResult result = mowing.TryStart(
                request.Identity,
                (int)request.Candidate.Tile.X,
                (int)request.Candidate.Tile.Y,
                request.OperationId,
                tile => WorkScopeContracts.ContainsTile(request.Directive, (int)tile.X, (int)tile.Y));
            return new WorkActionStartOutcome(result.IsSuccess, result.Code, result.Message, mowing.GetReservedTool(request.Identity));
        });
        this.Register(WorkKinds.Till, request =>
        {
            DigCommandResult result = digging.TryStart(
                request.Identity,
                (int)request.Candidate.Tile.X,
                (int)request.Candidate.Tile.Y,
                request.OperationId);
            WorkStepGeometry? geometry = result.IsSuccess && digging.TryGetReservedGeometry(request.Identity, out WorkStepGeometry frozen)
                ? frozen
                : null;
            return new WorkActionStartOutcome(result.IsSuccess, result.Code, result.Message, digging.GetReservedTool(request.Identity), geometry);
        });
        this.Register(WorkKinds.Chop, request =>
        {
            ChopCommandResult result = chopping.TryStart(
                request.Identity,
                (int)request.Candidate.Tile.X,
                (int)request.Candidate.Tile.Y,
                request.OperationId);
            return new WorkActionStartOutcome(result.IsSuccess, result.Code, result.Message, chopping.GetReservedTool(request.Identity));
        });
        this.Register(WorkKinds.Mine, request =>
        {
            MineCommandResult result = mining.TryStart(
                request.Identity,
                (int)request.Candidate.Tile.X,
                (int)request.Candidate.Tile.Y,
                request.OperationId);
            return new WorkActionStartOutcome(result.IsSuccess, result.Code, result.Message, mining.GetReservedTool(request.Identity));
        });
        this.Register(WorkKinds.Forage, request =>
        {
            ForageCommandResult result = foraging.TryStart(
                request.Identity,
                (int)request.Candidate.Tile.X,
                (int)request.Candidate.Tile.Y,
                request.OperationId);
            return new WorkActionStartOutcome(result.IsSuccess, result.Code, result.Message, null);
        });

        foreach (string kind in new[] { WorkKinds.Pet, WorkKinds.Milk, WorkKinds.Shear })
        {
            this.Register(kind, request =>
            {
                CareCommandResult result = animalCare.TryStart(
                    request.Identity,
                    "animal",
                    request.Candidate.StableId,
                    request.Directive.Kind.ToLowerInvariant(),
                    request.OperationId);
                return new WorkActionStartOutcome(result.IsSuccess, result.Code, result.Message, animalCare.GetReservedTool(request.Identity));
            });
        }
    }

    public WorkActionStartOutcome TryStart(WorkActionRequest request)
    {
        return this.handlers.TryGetValue(request.Directive.Kind, out Func<WorkActionRequest, WorkActionStartOutcome>? handler)
            ? handler(request)
            : new WorkActionStartOutcome(
                false,
                "WORK-KIND-NOT-ROUTED",
                $"No continuous task route exists for {request.Directive.Kind}.",
                null);
    }

    private void Register(string kind, Func<WorkActionRequest, WorkActionStartOutcome> handler)
    {
        if (!this.handlers.TryAdd(kind, handler))
            throw new InvalidOperationException($"A work action handler is already registered for {kind}.");
    }
}
