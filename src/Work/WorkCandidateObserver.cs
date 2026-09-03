using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace YuiToIssho;

internal readonly record struct WorkCandidate(string StableId, Vector2 Tile, int KindPriority);

internal enum WorkSweepAxis
{
    Horizontal,
    Vertical,
}

internal readonly record struct WorkSweepHint(
    WorkSweepAxis Axis,
    int Direction,
    int LaneCoordinate,
    int LaneStep,
    Vector2 LastTargetTile,
    int LastFacing);

internal readonly record struct WorkSweepPlan(
    WorkSweepAxis Axis,
    int Direction,
    int LaneCoordinate,
    int LaneStep,
    Vector2 ReferenceTile,
    bool IsInitial,
    string? EntryTargetId,
    string Reason);

internal readonly record struct WorkSweepProposal(
    WorkSweepAxis Axis,
    int Direction,
    int LaneCoordinate,
    int LaneStep,
    bool IsFallback,
    string Reason);

internal readonly record struct WorkSweepCandidateRank(int Bucket, int LaneDistance, int AxisDistance);

internal readonly record struct WorkObservation(
    IReadOnlyList<WorkCandidate> Candidates,
    int MatchingCount,
    bool HasMore,
    int ExcludedCount,
    WorkSweepPlan? SweepPlan
);

internal static class WorkCandidateObserver
{
    public const int MaximumCandidates = 64;

    public static WorkObservation Observe(
        WorkDirectiveRecord directive,
        GameLocation location,
        Vector2 bodyTile,
        IReadOnlySet<string>? excludedCandidateIds = null,
        int preferredFacing = 2,
        WorkSweepHint? sweepHint = null)
    {
        IReadOnlyList<WorldTargetFact> facts = WorldTargetClassifier.Observe(location);
        IEnumerable<WorkCandidate> raw = directive.Kind switch
        {
            WorkKinds.Water or WorkKinds.Harvest or WorkKinds.Mow or WorkKinds.Chop or WorkKinds.Mine or WorkKinds.Forage => facts
                .Where(fact => fact.Disposition == WorldTargetDispositions.Candidate && fact.SuggestedWorkKind == directive.Kind)
                .Select(At),
            WorkKinds.Till => EnumerateTillTargets(directive, location),
            WorkKinds.Pet => location.animals.Values
                .Where(animal => !animal.wasPet.Value && Game1.timeOfDay < 1900)
                .Select(animal => new WorkCandidate(animal.myID.Value.ToString(), animal.Tile, 0)),
            WorkKinds.Milk or WorkKinds.Shear => location.animals.Values
                .Where(animal => animal.isAdult() && animal.currentProduce.Value is not null)
                .Select(animal => new WorkCandidate(animal.myID.Value.ToString(), animal.Tile, 0)),
            _ => Array.Empty<WorkCandidate>(),
        };

        WorkCandidate[] matching = raw
            .Where(candidate => WorkScopeContracts.ContainsTile(directive, (int)candidate.Tile.X, (int)candidate.Tile.Y))
            .DistinctBy(candidate => candidate.StableId)
            .ToArray();
        WorkCandidate[] eligible = matching
            .Where(candidate => excludedCandidateIds is null || !excludedCandidateIds.Contains(candidate.StableId))
            .ToArray();
        WorkSweepPlan? sweepPlan = CreateSweepPlan(directive.Kind, eligible, bodyTile, preferredFacing, sweepHint);
        IOrderedEnumerable<WorkCandidate> ordered = sweepPlan is WorkSweepPlan activePlan
            ? eligible
                .OrderBy(candidate => GetSweepRank(activePlan, candidate).Bucket)
                .ThenBy(candidate => GetSweepRank(activePlan, candidate).LaneDistance)
                .ThenBy(candidate => GetSweepRank(activePlan, candidate).AxisDistance)
                .ThenBy(candidate => candidate.KindPriority)
                .ThenBy(candidate => candidate.StableId, StringComparer.Ordinal)
            : eligible
                .OrderBy(candidate => Math.Abs(candidate.Tile.X - bodyTile.X) + Math.Abs(candidate.Tile.Y - bodyTile.Y))
                .ThenBy(candidate => candidate.KindPriority)
                .ThenBy(candidate => DirectionCost(bodyTile, candidate.Tile, preferredFacing))
                .ThenBy(candidate => candidate.StableId, StringComparer.Ordinal);
        WorkCandidate[] candidates = ordered
            .Take(MaximumCandidates)
            .ToArray();
        return new WorkObservation(candidates, matching.Length, eligible.Length > candidates.Length, matching.Length - eligible.Length, sweepPlan);
    }

    public static WorkSweepCandidateRank GetSweepRank(WorkSweepPlan plan, WorkCandidate candidate)
    {
        if (plan.IsInitial && candidate.StableId == plan.EntryTargetId)
            return new WorkSweepCandidateRank(0, 0, 0);

        int candidateLane = LaneCoordinate(plan.Axis, candidate.Tile);
        int candidateAxis = AxisCoordinate(plan.Axis, candidate.Tile);
        int referenceAxis = AxisCoordinate(plan.Axis, plan.ReferenceTile);
        int progress = (candidateAxis - referenceAxis) * plan.Direction;
        if (candidateLane == plan.LaneCoordinate && progress > 0)
            return new WorkSweepCandidateRank(plan.IsInitial ? 1 : 0, 0, progress);

        int laneProgress = (candidateLane - plan.LaneCoordinate) * plan.LaneStep;
        if (laneProgress > 0)
            return new WorkSweepCandidateRank(plan.IsInitial ? 2 : 1, laneProgress, Math.Abs(candidateAxis - referenceAxis));

        return new WorkSweepCandidateRank(plan.IsInitial ? 3 : 2, Math.Abs(candidateLane - plan.LaneCoordinate), Math.Abs(candidateAxis - referenceAxis));
    }

    public static WorkSweepProposal? CreateSweepProposal(WorkSweepPlan? plan, WorkCandidate candidate)
    {
        if (plan is not WorkSweepPlan activePlan)
            return null;
        WorkSweepCandidateRank rank = GetSweepRank(activePlan, candidate);
        if (activePlan.IsInitial && candidate.StableId == activePlan.EntryTargetId)
            return new WorkSweepProposal(activePlan.Axis, activePlan.Direction, activePlan.LaneCoordinate, activePlan.LaneStep, false, "SWEEP-INITIALIZED");
        if (rank.Bucket == 0 || (activePlan.IsInitial && rank.Bucket == 1))
            return new WorkSweepProposal(activePlan.Axis, activePlan.Direction, activePlan.LaneCoordinate, activePlan.LaneStep, false, "SWEEP-ADVANCE");
        if (rank.Bucket == (activePlan.IsInitial ? 2 : 1))
        {
            int nextLane = LaneCoordinate(activePlan.Axis, candidate.Tile);
            return new WorkSweepProposal(activePlan.Axis, -activePlan.Direction, nextLane, activePlan.LaneStep, false, "SWEEP-LANE-TURN-PROPOSED");
        }
        return new WorkSweepProposal(activePlan.Axis, activePlan.Direction, activePlan.LaneCoordinate, activePlan.LaneStep, true, "SWEEP-FALLBACK-BLOCKED");
    }

    private static IEnumerable<WorkCandidate> EnumerateTillTargets(WorkDirectiveRecord directive, GameLocation location)
    {
        foreach ((Vector2 tile, SObject value) in location.Objects.Pairs)
            if (IsDigSpot(value))
                yield return At(tile, -1);

        GetBounds(directive, location, out int minimumX, out int maximumX, out int minimumY, out int maximumY);
        for (int y = minimumY; y <= maximumY; y++)
        {
            for (int x = minimumX; x <= maximumX; x++)
            {
                Vector2 tile = new(x, y);
                if (!location.Objects.ContainsKey(tile)
                    && !location.terrainFeatures.ContainsKey(tile)
                    && location.GetHoeDirtAtTile(tile) is null
                    && location.doesTileHaveProperty(x, y, "Diggable", "Back") is not null)
                    yield return At(tile);
            }
        }
    }

    private static WorkCandidate At(Vector2 tile, int priority = 0) => new($"{(int)tile.X},{(int)tile.Y}", tile, priority);
    private static WorkCandidate At(WorldTargetFact fact) => new(fact.StableId, fact.Tile, fact.Category.EndsWith("Clump", StringComparison.Ordinal) ? -1 : 0);

    private static int DirectionCost(Vector2 bodyTile, Vector2 targetTile, int preferredFacing)
    {
        if (bodyTile == targetTile)
            return 0;
        int targetFacing = TaskNavigationService.FacingToward(bodyTile, targetTile);
        return TaskNavigationService.TurnDistance(preferredFacing, targetFacing);
    }

    private static WorkSweepPlan? CreateSweepPlan(string kind, WorkCandidate[] eligible, Vector2 bodyTile, int preferredFacing, WorkSweepHint? hint)
    {
        if (!SupportsSweep(kind) || eligible.Length < 3)
            return null;
        bool hasHorizontalLane = eligible.GroupBy(candidate => (int)candidate.Tile.Y).Any(group => group.Count() >= 2);
        bool hasVerticalLane = eligible.GroupBy(candidate => (int)candidate.Tile.X).Any(group => group.Count() >= 2);
        if (!hasHorizontalLane && !hasVerticalLane)
            return null;

        if (hint is WorkSweepHint active
            && active.Direction is -1 or 1
            && active.LaneStep is -1 or 1
            && (active.Axis == WorkSweepAxis.Horizontal ? hasHorizontalLane : hasVerticalLane))
        {
            return new WorkSweepPlan(
                active.Axis,
                active.Direction,
                active.LaneCoordinate,
                active.LaneStep,
                active.LastTargetTile,
                false,
                null,
                "SWEEP-ADVANCE");
        }

        WorkSweepAxis axis = ChooseAxis(eligible, preferredFacing, hasHorizontalLane, hasVerticalLane);
        WorkCandidate entry = EntryCandidates(eligible, axis)
            .OrderBy(candidate => Math.Abs(candidate.Tile.X - bodyTile.X) + Math.Abs(candidate.Tile.Y - bodyTile.Y))
            .ThenBy(candidate => DirectionCost(bodyTile, candidate.Tile, preferredFacing))
            .ThenBy(candidate => candidate.StableId, StringComparer.Ordinal)
            .First();
        int minimumAxis = eligible.Min(candidate => AxisCoordinate(axis, candidate.Tile));
        int maximumAxis = eligible.Max(candidate => AxisCoordinate(axis, candidate.Tile));
        int minimumLane = eligible.Min(candidate => LaneCoordinate(axis, candidate.Tile));
        int maximumLane = eligible.Max(candidate => LaneCoordinate(axis, candidate.Tile));
        int entryAxis = AxisCoordinate(axis, entry.Tile);
        int entryLane = LaneCoordinate(axis, entry.Tile);
        int direction = entryAxis - minimumAxis <= maximumAxis - entryAxis ? 1 : -1;
        int laneStep = entryLane - minimumLane <= maximumLane - entryLane ? 1 : -1;
        return new WorkSweepPlan(axis, direction, entryLane, laneStep, entry.Tile, true, entry.StableId, "SWEEP-INITIALIZED");
    }

    private static WorkSweepAxis ChooseAxis(WorkCandidate[] candidates, int preferredFacing, bool hasHorizontalLane, bool hasVerticalLane)
    {
        if (!hasHorizontalLane)
            return WorkSweepAxis.Vertical;
        if (!hasVerticalLane)
            return WorkSweepAxis.Horizontal;
        int horizontalSpan = candidates.Max(candidate => (int)candidate.Tile.X) - candidates.Min(candidate => (int)candidate.Tile.X);
        int verticalSpan = candidates.Max(candidate => (int)candidate.Tile.Y) - candidates.Min(candidate => (int)candidate.Tile.Y);
        if (horizontalSpan != verticalSpan)
            return horizontalSpan > verticalSpan ? WorkSweepAxis.Horizontal : WorkSweepAxis.Vertical;
        return preferredFacing is 1 or 3 ? WorkSweepAxis.Horizontal : WorkSweepAxis.Vertical;
    }

    private static IEnumerable<WorkCandidate> EntryCandidates(WorkCandidate[] candidates, WorkSweepAxis axis)
    {
        int minimumLane = candidates.Min(candidate => LaneCoordinate(axis, candidate.Tile));
        int maximumLane = candidates.Max(candidate => LaneCoordinate(axis, candidate.Tile));
        foreach (int lane in new[] { minimumLane, maximumLane }.Distinct())
        {
            WorkCandidate[] laneCandidates = candidates.Where(candidate => LaneCoordinate(axis, candidate.Tile) == lane).ToArray();
            yield return laneCandidates.MinBy(candidate => AxisCoordinate(axis, candidate.Tile));
            WorkCandidate maximum = laneCandidates.MaxBy(candidate => AxisCoordinate(axis, candidate.Tile));
            if (maximum.StableId != laneCandidates.MinBy(candidate => AxisCoordinate(axis, candidate.Tile)).StableId)
                yield return maximum;
        }
    }

    public static bool SupportsSweep(string kind) => kind is WorkKinds.Water or WorkKinds.Harvest or WorkKinds.Till;

    private static int AxisCoordinate(WorkSweepAxis axis, Vector2 tile) => axis == WorkSweepAxis.Horizontal ? (int)tile.X : (int)tile.Y;

    private static int LaneCoordinate(WorkSweepAxis axis, Vector2 tile) => axis == WorkSweepAxis.Horizontal ? (int)tile.Y : (int)tile.X;

    private static void GetBounds(WorkDirectiveRecord directive, GameLocation location, out int minimumX, out int maximumX, out int minimumY, out int maximumY)
    {
        minimumX = Math.Max(0, directive.Shape == WorkScopeShapes.Rectangle ? Math.Min(directive.AnchorX, directive.EndX) : directive.AnchorX - directive.Radius);
        maximumX = Math.Min(location.Map.Layers[0].LayerWidth - 1, directive.Shape == WorkScopeShapes.Rectangle ? Math.Max(directive.AnchorX, directive.EndX) : directive.AnchorX + directive.Radius);
        minimumY = Math.Max(0, directive.Shape == WorkScopeShapes.Rectangle ? Math.Min(directive.AnchorY, directive.EndY) : directive.AnchorY - directive.Radius);
        maximumY = Math.Min(location.Map.Layers[0].LayerHeight - 1, directive.Shape == WorkScopeShapes.Rectangle ? Math.Max(directive.AnchorY, directive.EndY) : directive.AnchorY + directive.Radius);
    }

    private static bool IsDigSpot(SObject value) => value.QualifiedItemId is "(O)590" or "(O)SeedSpot";

}
