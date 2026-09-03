using Microsoft.Xna.Framework;
using StardewValley;

namespace YuiToIssho;

internal static class WorkKinds
{
    public const string Water = "Water";
    public const string Harvest = "Harvest";
    public const string Mow = "Mow";
    public const string Till = "Till";
    public const string Chop = "Chop";
    public const string Mine = "Mine";
    public const string Forage = "Forage";
    public const string Pet = "Pet";
    public const string Milk = "Milk";
    public const string Shear = "Shear";

    private static readonly HashSet<string> Continuous = new(StringComparer.Ordinal)
    {
        Water, Harvest, Mow, Till, Chop, Mine, Forage, Pet, Milk, Shear,
    };

    public static bool IsContinuous(string? kind) => kind is not null && Continuous.Contains(kind);

    public static bool TryNormalize(string? value, out string kind)
    {
        kind = Continuous.FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return kind.Length > 0;
    }
}

internal static class CursorRequestedKinds
{
    public const string Auto = "Auto";
}

internal static class WorkScopeShapes
{
    public const string SingleTarget = "SingleTarget";
    public const string Radius = "Radius";

    public const string Rectangle = "Rectangle";

    public static bool IsValid(string? shape) => shape is SingleTarget or Radius or Rectangle;
}

internal static class WorkCompletionPolicies
{
    public const string Single = "Single";
    public const string UntilClear = "UntilClear";
    public const string UntilStopped = "UntilStopped";

    public static bool IsValid(string? policy) => policy is Single or UntilClear or UntilStopped;

    public static bool TryNormalizeContinuous(string? value, out string policy)
    {
        policy = value?.ToLowerInvariant() switch
        {
            "until-clear" or "untilclear" => UntilClear,
            "until-stopped" or "untilstopped" => UntilStopped,
            _ => string.Empty,
        };
        return policy.Length > 0;
    }
}

internal static class WorkRuntimePhases
{
    public const string NotObserved = "NotObserved";
    public const string Observing = "Observing";
    public const string Starting = "Starting";
    public const string Executing = "Executing";
    public const string InterStep = "InterStep";
    public const string Blocked = "Blocked";
    public const string Paused = "Paused";
    public const string Faulted = "Faulted";

    public static bool IsValid(string? phase) => phase is NotObserved or Observing or Starting or Executing or InterStep or Blocked or Paused or Faulted;
}

internal static class WorkScopeContracts
{
    public const int DefaultRadius = 8;
    public const int MinimumRadius = 1;
    public const int MaximumRadius = 24;
    public const int MaximumOwnerAnchorDistance = 24;
    public const int MaximumRectangleWidth = 25;
    public const int MaximumRectangleHeight = 25;
    public const int MaximumRectangleTiles = MaximumRectangleWidth * MaximumRectangleHeight;

    public static bool ContainsTile(int anchorX, int anchorY, int radius, int tileX, int tileY)
    {
        long dx = tileX - anchorX;
        long dy = tileY - anchorY;
        return dx * dx + dy * dy <= (long)radius * radius;
    }

    public static bool ContainsTile(WorkDirectiveRecord directive, int tileX, int tileY) => directive.Shape == WorkScopeShapes.Rectangle
        ? tileX >= Math.Min(directive.AnchorX, directive.EndX)
            && tileX <= Math.Max(directive.AnchorX, directive.EndX)
            && tileY >= Math.Min(directive.AnchorY, directive.EndY)
            && tileY <= Math.Max(directive.AnchorY, directive.EndY)
        : ContainsTile(directive.AnchorX, directive.AnchorY, directive.Radius, tileX, tileY);

    public static bool IsRectangleWithinLimit(int anchorX, int anchorY, int endX, int endY)
    {
        long width = Math.Abs((long)endX - anchorX) + 1;
        long height = Math.Abs((long)endY - anchorY) + 1;
        return width <= MaximumRectangleWidth
            && height <= MaximumRectangleHeight
            && width * height <= MaximumRectangleTiles;
    }
}

internal readonly record struct WorkScopeRequest(
    string LocationKey,
    int AnchorX,
    int AnchorY,
    string Shape,
    int Radius,
    string RequestedKind,
    string CompletionPolicy
)
{
    public int EndX { get; init; }
    public int EndY { get; init; }
}

internal readonly record struct NormalizedWorkScope(
    string LocationKey,
    int AnchorX,
    int AnchorY,
    string Shape,
    int Radius,
    string Kind,
    string CompletionPolicy
)
{
    public int EndX { get; init; }
    public int EndY { get; init; }
}

internal readonly record struct WorkScopeValidationResult(bool IsSuccess, string Code, string Message, NormalizedWorkScope Scope)
{
    public static WorkScopeValidationResult Success(NormalizedWorkScope scope) => new(true, "WORK-SCOPE-ACCEPTED", "The Host normalized the work scope.", scope);
    public static WorkScopeValidationResult Failure(string code, string message) => new(false, code, message, default);
}

internal static class WorkScopeNormalizer
{
    public static WorkScopeValidationResult NormalizeSingle(Farmer owner, WorkScopeRequest request)
    {
        GameLocation? location = owner.currentLocation;
        if (location is null || request.LocationKey != location.NameOrUniqueName)
            return WorkScopeValidationResult.Failure("WORK-LOCATION-MISMATCH", "The requested LocationKey must be the Owner's current location.");
        if (request.Shape != WorkScopeShapes.SingleTarget || request.Radius != 0 || request.CompletionPolicy != WorkCompletionPolicies.Single)
            return WorkScopeValidationResult.Failure("SINGLE-SCOPE-NOT-ALLOWED", "A single target requires SingleTarget, radius 0, and Single policy.");
        string kind;
        if (string.Equals(request.RequestedKind, CursorRequestedKinds.Auto, StringComparison.OrdinalIgnoreCase))
            kind = CursorRequestedKinds.Auto;
        else if (!WorkKinds.TryNormalize(request.RequestedKind, out kind))
            return WorkScopeValidationResult.Failure("SINGLE-KIND-NOT-ALLOWED", "The requested single-target work kind is not in the command-cursor allowlist.");
        int width = location.Map.Layers[0].LayerWidth;
        int height = location.Map.Layers[0].LayerHeight;
        if (request.AnchorX < 0 || request.AnchorY < 0 || request.AnchorX >= width || request.AnchorY >= height)
            return WorkScopeValidationResult.Failure("WORK-ANCHOR-OUTSIDE-MAP", "The target is outside the current map.");
        Vector2 ownerTile = owner.Tile;
        int distance = Math.Abs((int)ownerTile.X - request.AnchorX) + Math.Abs((int)ownerTile.Y - request.AnchorY);
        if (distance > WorkScopeContracts.MaximumOwnerAnchorDistance)
            return WorkScopeValidationResult.Failure("WORK-ANCHOR-TOO-FAR", $"The target must be within {WorkScopeContracts.MaximumOwnerAnchorDistance} Manhattan tiles of the Owner.");
        return WorkScopeValidationResult.Success(new NormalizedWorkScope(location.NameOrUniqueName, request.AnchorX, request.AnchorY, WorkScopeShapes.SingleTarget, 0, kind, WorkCompletionPolicies.Single));
    }

    public static WorkScopeValidationResult NormalizeRadius(Farmer owner, WorkScopeRequest request)
    {
        GameLocation? location = owner.currentLocation;
        if (location is null || string.IsNullOrWhiteSpace(request.LocationKey) || request.LocationKey != location.NameOrUniqueName)
            return WorkScopeValidationResult.Failure("WORK-LOCATION-MISMATCH", "The requested LocationKey must be the Owner's current location.");
        if (request.Shape != WorkScopeShapes.Radius)
            return WorkScopeValidationResult.Failure("WORK-SHAPE-NOT-ALLOWED", "Continuous work requires the Radius shape.");
        if (request.Radius is < WorkScopeContracts.MinimumRadius or > WorkScopeContracts.MaximumRadius)
            return WorkScopeValidationResult.Failure("WORK-RADIUS-OUT-OF-RANGE", $"Radius must be {WorkScopeContracts.MinimumRadius} through {WorkScopeContracts.MaximumRadius}.");
        if (!WorkKinds.TryNormalize(request.RequestedKind, out string kind))
            return WorkScopeValidationResult.Failure("WORK-KIND-NOT-ALLOWED", "The requested work kind is not in the continuous-work allowlist.");
        if (!WorkCompletionPolicies.TryNormalizeContinuous(request.CompletionPolicy, out string policy))
            return WorkScopeValidationResult.Failure("WORK-POLICY-NOT-ALLOWED", "Continuous work requires UntilClear or UntilStopped.");

        int width = location.Map.Layers[0].LayerWidth;
        int height = location.Map.Layers[0].LayerHeight;
        if (request.AnchorX < 0 || request.AnchorY < 0 || request.AnchorX >= width || request.AnchorY >= height)
            return WorkScopeValidationResult.Failure("WORK-ANCHOR-OUTSIDE-MAP", "The work anchor is outside the current map.");

        Vector2 ownerTile = owner.Tile;
        int distance = Math.Abs((int)ownerTile.X - request.AnchorX) + Math.Abs((int)ownerTile.Y - request.AnchorY);
        if (distance > WorkScopeContracts.MaximumOwnerAnchorDistance)
            return WorkScopeValidationResult.Failure("WORK-ANCHOR-TOO-FAR", $"The work anchor must be within {WorkScopeContracts.MaximumOwnerAnchorDistance} Manhattan tiles of the Owner.");

        return WorkScopeValidationResult.Success(new NormalizedWorkScope(
            location.NameOrUniqueName,
            request.AnchorX,
            request.AnchorY,
            WorkScopeShapes.Radius,
            request.Radius,
            kind,
            policy
        ));
    }

    public static WorkScopeValidationResult NormalizeRectangle(Farmer owner, WorkScopeRequest request)
    {
        GameLocation? location = owner.currentLocation;
        if (location is null || request.LocationKey != location.NameOrUniqueName)
            return WorkScopeValidationResult.Failure("WORK-LOCATION-MISMATCH", "The requested LocationKey must be the Owner's current location.");
        if (request.Shape != WorkScopeShapes.Rectangle || request.Radius != 0)
            return WorkScopeValidationResult.Failure("WORK-SHAPE-NOT-ALLOWED", "Two-point work requires Rectangle shape and radius 0.");
        if (!WorkKinds.TryNormalize(request.RequestedKind, out string kind))
            return WorkScopeValidationResult.Failure("WORK-KIND-NOT-ALLOWED", "The requested work kind is not in the continuous-work allowlist.");
        if (!WorkCompletionPolicies.TryNormalizeContinuous(request.CompletionPolicy, out string policy))
            return WorkScopeValidationResult.Failure("WORK-POLICY-NOT-ALLOWED", "Rectangle work requires UntilClear or UntilStopped.");
        int width = location.Map.Layers[0].LayerWidth;
        int height = location.Map.Layers[0].LayerHeight;
        if (request.AnchorX < 0 || request.AnchorY < 0 || request.EndX < 0 || request.EndY < 0
            || request.AnchorX >= width || request.EndX >= width || request.AnchorY >= height || request.EndY >= height)
            return WorkScopeValidationResult.Failure("WORK-ANCHOR-OUTSIDE-MAP", "A rectangle corner is outside the current map.");
        int ownerX = (int)owner.Tile.X;
        int ownerY = (int)owner.Tile.Y;
        if (Math.Abs(ownerX - request.AnchorX) + Math.Abs(ownerY - request.AnchorY) > WorkScopeContracts.MaximumOwnerAnchorDistance
            || Math.Abs(ownerX - request.EndX) + Math.Abs(ownerY - request.EndY) > WorkScopeContracts.MaximumOwnerAnchorDistance)
            return WorkScopeValidationResult.Failure("WORK-ANCHOR-TOO-FAR", $"Both rectangle corners must be within {WorkScopeContracts.MaximumOwnerAnchorDistance} Manhattan tiles of the Owner.");
        if (!WorkScopeContracts.IsRectangleWithinLimit(request.AnchorX, request.AnchorY, request.EndX, request.EndY))
            return WorkScopeValidationResult.Failure("WORK-RECTANGLE-TOO-LARGE", $"A rectangle may be at most {WorkScopeContracts.MaximumRectangleWidth} by {WorkScopeContracts.MaximumRectangleHeight} tiles ({WorkScopeContracts.MaximumRectangleTiles} total).");
        return WorkScopeValidationResult.Success(new NormalizedWorkScope(location.NameOrUniqueName, request.AnchorX, request.AnchorY, WorkScopeShapes.Rectangle, 0, kind, policy)
        {
            EndX = request.EndX,
            EndY = request.EndY,
        });
    }
}
