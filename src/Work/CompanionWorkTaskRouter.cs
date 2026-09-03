using Microsoft.Xna.Framework;
using StardewValley;

namespace YuiToIssho;

internal readonly record struct WorkStepGeometry(Vector2 TargetTile, Vector2 ApproachTile, int Facing);
internal readonly record struct WorkStepStartResult(bool IsSuccess, string Code, string Message, WorkStepGeometry? Geometry = null);
internal readonly record struct SingleWorkStartResult(bool IsSuccess, string Code, string Message, string Kind);

internal sealed class CompanionWorkTaskRouter
{
    private readonly WorkActionRegistry actions;
    private readonly AnimalCareCoordinator animalCare;
    private readonly CompanionVitalsCoordinator vitals;
    private readonly CompanionStorageCoordinator storage;

    public CompanionWorkTaskRouter(
        WorkActionRegistry actions,
        AnimalCareCoordinator animalCare,
        CompanionVitalsCoordinator vitals,
        CompanionStorageCoordinator storage)
    {
        this.actions = actions;
        this.animalCare = animalCare;
        this.vitals = vitals;
        this.storage = storage;
    }

    public WorkStepStartResult TryStart(CompanionRecord record, WorkDirectiveRecord directive, WorkCandidate candidate, string operationId)
    {
        WorkActionStartOutcome result = this.actions.TryStart(new WorkActionRequest(record.Identity, directive, candidate, operationId));
        if (result.IsSuccess)
        {
            this.storage.BindTask(record.Identity, operationId, result.ReservedTool);
        }
        return new WorkStepStartResult(result.IsSuccess, result.Code, result.Message, result.Geometry);
    }

    public SingleWorkStartResult TryStartSingle(CompanionRecord record, Farmer owner, NormalizedWorkScope scope, string operationId)
    {
        if (owner.currentLocation is not GameLocation location || location.NameOrUniqueName != scope.LocationKey)
            return new SingleWorkStartResult(false, "WORK-LOCATION-MISMATCH", "The normalized target location is no longer the Owner's current location.", scope.Kind);

        string kind = scope.Kind;
        var candidate = new WorkCandidate($"{scope.AnchorX},{scope.AnchorY}", new Vector2(scope.AnchorX, scope.AnchorY), 0);
        if (kind == CursorRequestedKinds.Auto
            && !this.TryResolveAuto(record.Identity, owner, location, scope.AnchorX, scope.AnchorY, out kind, out candidate))
            return new SingleWorkStartResult(false, "SINGLE-TARGET-NOT-SUPPORTED", "The Host found no supported work target at the requested tile.", kind);

        if (kind is WorkKinds.Pet or WorkKinds.Milk or WorkKinds.Shear
            && !this.TryResolveAnimalAction(record.Identity, owner, location, scope.AnchorX, scope.AnchorY, kind, out _, out candidate))
            return new SingleWorkStartResult(false, "ANIMAL-ACTION-NOT-AVAILABLE", $"No farm animal at the requested tile is currently eligible for {kind}.", kind);

        string vitalActionKind = ToVitalActionKind(kind);
        if (vitalActionKind.Length == 0)
            return new SingleWorkStartResult(false, "WORK-KIND-NOT-ROUTED", $"No vital-action policy exists for {kind}.", kind);
        if (!this.vitals.CanStartAction(record.Identity, vitalActionKind, out VitalActionResult vitalGate))
            return new SingleWorkStartResult(false, vitalGate.Code, vitalGate.Message, kind);

        var directive = new WorkDirectiveRecord
        {
            Kind = kind,
            LocationKey = scope.LocationKey,
            AnchorX = scope.AnchorX,
            AnchorY = scope.AnchorY,
            EndX = scope.AnchorX,
            EndY = scope.AnchorY,
            Shape = WorkScopeShapes.SingleTarget,
            Radius = 0,
            CompletionPolicy = WorkCompletionPolicies.Single,
        };
        WorkStepStartResult result = this.TryStart(record, directive, candidate, operationId);
        return new SingleWorkStartResult(result.IsSuccess, result.Code, result.Message, kind);
    }

    private bool TryResolveAuto(CompanionIdentity identity, Farmer owner, GameLocation location, int tileX, int tileY, out string kind, out WorkCandidate candidate)
    {
        List<(string Kind, WorkCandidate Candidate)> candidates = WorldTargetClassifier.Observe(location)
            .Where(fact => (int)fact.Tile.X == tileX
                && (int)fact.Tile.Y == tileY
                && fact.Disposition == WorldTargetDispositions.Candidate
                && WorkKinds.IsContinuous(fact.SuggestedWorkKind))
            .Select(fact => (
                fact.SuggestedWorkKind!,
                new WorkCandidate(fact.StableId, fact.Tile, AutoPriority(fact.SuggestedWorkKind!))))
            .ToList();
        var tillDirective = new WorkDirectiveRecord
        {
            Kind = WorkKinds.Till,
            LocationKey = location.NameOrUniqueName,
            AnchorX = tileX,
            AnchorY = tileY,
            EndX = tileX,
            EndY = tileY,
            Shape = WorkScopeShapes.SingleTarget,
            Radius = 0,
            CompletionPolicy = WorkCompletionPolicies.Single,
        };
        WorkObservation till = WorkCandidateObserver.Observe(tillDirective, location, new Vector2(tileX, tileY));
        if (!candidates.Any(entry => entry.Kind == WorkKinds.Till) && till.Candidates.Count > 0)
        {
            WorkCandidate tillCandidate = till.Candidates[0];
            candidates.Add((WorkKinds.Till, tillCandidate with { KindPriority = AutoPriority(WorkKinds.Till) }));
        }
        if (this.TryResolveAnimalAction(
            identity,
            owner,
            location,
            tileX,
            tileY,
            CursorRequestedKinds.Auto,
            out string animalKind,
            out WorkCandidate animalCandidate))
            candidates.Add((animalKind, animalCandidate));
        if (candidates.Count == 0)
        {
            kind = CursorRequestedKinds.Auto;
            candidate = default;
            return false;
        }
        (kind, candidate) = candidates
            .OrderBy(entry => entry.Candidate.KindPriority)
            .ThenBy(entry => entry.Candidate.StableId, StringComparer.Ordinal)
            .First();
        return true;
    }

    private bool TryResolveAnimalAction(
        CompanionIdentity identity,
        Farmer owner,
        GameLocation location,
        int tileX,
        int tileY,
        string requestedKind,
        out string resolvedKind,
        out WorkCandidate candidate)
    {
        string[] kinds = requestedKind == CursorRequestedKinds.Auto
            ? new[] { WorkKinds.Pet, WorkKinds.Milk, WorkKinds.Shear }
            : new[] { requestedKind };
        foreach (FarmAnimal animal in location.animals.Values
            .Where(value => (int)value.Tile.X == tileX && (int)value.Tile.Y == tileY)
            .OrderBy(value => value.myID.Value))
        {
            foreach (string kind in kinds)
            {
                if (!this.animalCare.CanStartFarmAnimalAction(identity, owner, animal, kind))
                    continue;
                resolvedKind = kind;
                candidate = new WorkCandidate(animal.myID.Value.ToString(), animal.Tile, AutoPriority(kind));
                return true;
            }
        }
        resolvedKind = requestedKind;
        candidate = default;
        return false;
    }

    private static string ToVitalActionKind(string kind) => kind switch
    {
        WorkKinds.Water => VitalActionKinds.Watering,
        WorkKinds.Harvest => VitalActionKinds.Harvesting,
        WorkKinds.Mow => VitalActionKinds.Mowing,
        WorkKinds.Till => VitalActionKinds.Digging,
        WorkKinds.Chop => VitalActionKinds.Chopping,
        WorkKinds.Mine => VitalActionKinds.Mining,
        WorkKinds.Forage => VitalActionKinds.Foraging,
        WorkKinds.Pet => VitalActionKinds.Petting,
        WorkKinds.Milk => VitalActionKinds.Milking,
        WorkKinds.Shear => VitalActionKinds.Shearing,
        _ => string.Empty,
    };

    private static int AutoPriority(string kind) => kind switch
    {
        WorkKinds.Harvest => 0,
        WorkKinds.Mow => 1,
        WorkKinds.Till => 2,
        WorkKinds.Mine => 3,
        WorkKinds.Chop => 4,
        WorkKinds.Forage => 5,
        WorkKinds.Pet or WorkKinds.Milk or WorkKinds.Shear => 6,
        WorkKinds.Water => 7,
        _ => int.MaxValue,
    };
}
