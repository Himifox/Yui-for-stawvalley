using StardewModdingAPI.Events;

namespace YuiToIssho;

internal static class MultiplayerProtocol
{
    public const int Version = 9;
    public const string ModId = "Himifox.YuiToIssho";
    public const int MaxRequestCharacters = 2048;
    public const int MaxFieldCount = 10;
    public const int MaxFieldKeyLength = 32;
    public const int MaxFieldValueLength = 256;
    public const int MaxCompanionsPerSnapshot = 64;

    public static class MessageTypes
    {
        public const string CommandRequest = "r9.command-request.v9";
        public const string CommandReceipt = "r9.command-receipt.v9";
        public const string SnapshotRequest = "r9.snapshot-request.v9";
        public const string RuntimeSnapshot = "r9.runtime-snapshot.v9";
        public const string PresentationEvent = "r9.presentation-event.v9";
        public const string SpeechEvent = "r9.speech-event.v9";

        public static bool IsKnown(string type) => type is CommandRequest or CommandReceipt or SnapshotRequest or RuntimeSnapshot or PresentationEvent or SpeechEvent;
    }
}

internal sealed class CommandRequestDto
{
    public int ProtocolVersion { get; set; }
    public string SessionEpoch { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public long OwnerId { get; set; }
    public int Slot { get; set; }
    public long SenderPlayerId { get; set; }
    public ulong Sequence { get; set; }
    public string Command { get; set; } = string.Empty;
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class CommandReceiptDto
{
    public int ProtocolVersion { get; set; } = MultiplayerProtocol.Version;
    public string SessionEpoch { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public long OwnerId { get; set; }
    public int Slot { get; set; }
    public long SenderPlayerId { get; set; }
    public ulong Sequence { get; set; }
    public bool IsSuccess { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public ulong SnapshotVersion { get; set; }
    public PlantingCommandPayload? Planting { get; set; }
    public CombatCommandPayload? Combat { get; set; }
}

internal sealed class SnapshotRequestDto
{
    public int ProtocolVersion { get; set; }
    public string SessionEpoch { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public long SenderPlayerId { get; set; }
    public ulong LastSnapshotVersion { get; set; }
}

internal sealed class RuntimeSnapshotDto
{
    public int ProtocolVersion { get; set; } = MultiplayerProtocol.Version;
    public string SessionEpoch { get; set; } = string.Empty;
    public ulong SnapshotVersion { get; set; }
    public long HostPlayerId { get; set; }
    public ulong GeneratedTick { get; set; }
    public List<CompanionSnapshotDto> Companions { get; set; } = new();
}

internal sealed class CompanionSnapshotDto
{
    public long OwnerId { get; set; }
    public int Slot { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool OwnerOnline { get; set; }
    public bool WantsBody { get; set; }
    public string Mode { get; set; } = string.Empty;
    public bool BodyPresent { get; set; }
    public ulong BodyGeneration { get; set; }
    public string LocationKey { get; set; } = string.Empty;
    public int PixelX { get; set; }
    public int PixelY { get; set; }
    public int Facing { get; set; }
    public int BagCount { get; set; }
    public int LiabilityCount { get; set; }
    public string ActiveTransactionId { get; set; } = string.Empty;
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public float Stamina { get; set; }
    public float MaxStamina { get; set; }
    public string VitalState { get; set; } = string.Empty;
    public string WorkKind { get; set; } = string.Empty;
    public string WorkLocationKey { get; set; } = string.Empty;
    public int WorkAnchorX { get; set; }
    public int WorkAnchorY { get; set; }
    public int WorkEndX { get; set; }
    public int WorkEndY { get; set; }
    public int WorkRadius { get; set; }
    public string WorkShape { get; set; } = string.Empty;
    public string WorkPolicy { get; set; } = string.Empty;
    public string WorkState { get; set; } = string.Empty;
    public string WorkPhase { get; set; } = string.Empty;
    public int WorkMatchingCount { get; set; }
    public int WorkCandidateCount { get; set; }
    public int WorkBlockedCount { get; set; }
    public string WorkOperationId { get; set; } = string.Empty;
    public string WorkLastReason { get; set; } = string.Empty;
    public ulong WorkObservationRevision { get; set; }
    public string CombatMode { get; set; } = string.Empty;
    public string CombatPhase { get; set; } = string.Empty;
    public int CombatRemainingSeconds { get; set; }
    public int CombatCommittedSwings { get; set; }
    public int CombatMaximumSwings { get; set; }
    public string CombatTargetKind { get; set; } = string.Empty;
    public string CombatTargetDistanceBand { get; set; } = string.Empty;
    public string CombatLastOutcome { get; set; } = string.Empty;
    public string AgentBehaviorState { get; set; } = AgentBehaviorStates.Unavailable;
    public string AgentBrainPhase { get; set; } = AgentBrainPhases.Dormant;
    public long AgentPlanGeneration { get; set; }
    public long AgentSnapshotVersion { get; set; }
    public string AgentIntentId { get; set; } = string.Empty;
    public string AgentStepKind { get; set; } = string.Empty;
    public string AgentStepState { get; set; } = string.Empty;
    public string CraftRecipeKey { get; set; } = string.Empty;
    public string CraftPhase { get; set; } = string.Empty;
    public int CraftCompletedCount { get; set; }
    public int CraftCount { get; set; }
    public int CraftEscrowCount { get; set; }
    public string PlantingPhase { get; set; } = string.Empty;
    public int PlantingRequestedCount { get; set; }
    public int PlantingPlantedCount { get; set; }
    public int PlantingRemainingCount { get; set; }
    public string PlantingScopeSummary { get; set; } = string.Empty;
    public string PlantingCurrentStepSummary { get; set; } = string.Empty;
    public string PlantingLastReason { get; set; } = string.Empty;
    public int PlantEscrowStackCount { get; set; }
    public CompanionAppearanceDto Appearance { get; set; } = new();
    public ulong PresentationRevision { get; set; }
    public CompanionPresentationDto? Presentation { get; set; }
    public ulong SpeechSequence { get; set; }
    public ulong SpeechBodyGeneration { get; set; }
    public string SpeechId { get; set; } = string.Empty;
    public string SpeechTopicKey { get; set; } = string.Empty;
    public string SpeechText { get; set; } = string.Empty;
    public int SpeechPriority { get; set; }
    public int SpeechRemainingTicks { get; set; }

    internal CompanionIdentity Identity => new(this.OwnerId, this.Slot);
}

internal sealed class CompanionAppearanceDto
{
    public int ProfileSchemaVersion { get; set; }
    public int Generation { get; set; }
    public string BodyType { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public int HairStyle { get; set; }
    public int Skin { get; set; }
    public string ShirtId { get; set; } = string.Empty;
    public string PantsId { get; set; } = string.Empty;
    public string ShoeColorId { get; set; } = string.Empty;
    public uint HairColor { get; set; }
    public uint EyeColor { get; set; }
    public uint PantsColor { get; set; }
    public int AccessoryId { get; set; } = -1;
    public string HatQualifiedItemId { get; set; } = string.Empty;
}

internal sealed class CompanionPresentationDto
{
    public ulong Revision { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public int Facing { get; set; }
    public int Frame { get; set; }
    public int RemainingTicks { get; set; }
    public ulong StartedAtHostTick { get; set; }
    public ulong EndsAtHostTick { get; set; }
}

internal sealed class PresentationEventDto
{
    public int ProtocolVersion { get; set; } = MultiplayerProtocol.Version;
    public string SessionEpoch { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public ulong Sequence { get; set; }
    public long OwnerId { get; set; }
    public int Slot { get; set; }
    public ulong BodyGeneration { get; set; }
    public ulong PresentationRevision { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public int Facing { get; set; }
    public int Frame { get; set; }
    public ulong StartedAtHostTick { get; set; }
    public ulong EndsAtHostTick { get; set; }
}

internal sealed class SpeechEventDto
{
    public int ProtocolVersion { get; set; } = MultiplayerProtocol.Version;
    public string SessionEpoch { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public ulong SpeechSequence { get; set; }
    public long OwnerId { get; set; }
    public int Slot { get; set; }
    public ulong BodyGeneration { get; set; }
    public string SpeechId { get; set; } = string.Empty;
    public string TopicKey { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int Priority { get; set; }
    public ulong StartedAtHostTick { get; set; }
    public ulong ExpiresAtHostTick { get; set; }
}

internal readonly record struct ValidatedCommandRequest(
    string SessionEpoch,
    string RequestId,
    CompanionIdentity Identity,
    long SenderPlayerId,
    ulong Sequence,
    string Command,
    IReadOnlyDictionary<string, string> Fields);

internal readonly record struct ProtocolValidationResult(bool IsSuccess, string Code, string Message, ValidatedCommandRequest Request)
{
    public static ProtocolValidationResult Success(ValidatedCommandRequest request) => new(true, "OK", "Validated.", request);
    public static ProtocolValidationResult Failure(string code, string message) => new(false, code, message, default);
}

internal readonly record struct NetworkCommandResult(
    bool IsSuccess,
    string Code,
    string Message,
    PlantingCommandPayload? Planting = null,
    CombatCommandPayload? Combat = null,
    string RequestId = "")
{
    public static NetworkCommandResult Success(string code, string message) => new(true, code, message);
    public static NetworkCommandResult Failure(string code, string message) => new(false, code, message);
}

internal sealed class CombatCommandPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("options")]
    public List<CombatOptionDto>? Options { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("phase")]
    public string? Phase { get; init; }
}

internal sealed class CombatOptionDto
{
    [System.Text.Json.Serialization.JsonPropertyName("combat_option_id")]
    public string CombatOptionId { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("monster_kind")]
    public string MonsterKind { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("distance_band")]
    public string DistanceBand { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("threat_band")]
    public string ThreatBand { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("can_isolate")]
    public bool CanIsolate { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("expires_in_seconds")]
    public int ExpiresInSeconds { get; init; }
}

internal sealed class PlantingCommandPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("options")]
    public List<PlantSeedOptionDto>? Options { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("preview")]
    public PlantingPreviewDto? Preview { get; init; }
}

internal sealed class PlantSeedOptionDto
{
    [System.Text.Json.Serialization.JsonPropertyName("seed_option_id")]
    public string SeedOptionId { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("seed_display_name")]
    public string SeedDisplayName { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("crop_display_name")]
    public string CropDisplayName { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("available_count")]
    public int AvailableCount { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("plantable_here")]
    public bool PlantableHere { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("reason_code")]
    public string ReasonCode { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("expires_in_seconds")]
    public int ExpiresInSeconds { get; init; }
}

internal sealed class PlantingPreviewDto
{
    [System.Text.Json.Serialization.JsonPropertyName("seed_option_id")]
    public string SeedOptionId { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("seed_display_name")]
    public string SeedDisplayName { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("crop_display_name")]
    public string CropDisplayName { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("requested_count")]
    public int RequestedCount { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("available_seed_count")]
    public int AvailableSeedCount { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("matching_slot_count")]
    public int MatchingSlotCount { get; init; }
}

internal static class MultiplayerRequestValidator
{
    private static readonly IReadOnlyDictionary<string, CommandShape> Shapes = new Dictionary<string, CommandShape>(StringComparer.Ordinal)
    {
        ["summon"] = Shape(),
        ["recall"] = Shape(),
        ["delete"] = Shape(),
        ["follow"] = Shape(),
        ["wait"] = Shape(),
        ["sit"] = Shape(),
        ["stand"] = Shape(),
        ["stop"] = Shape(),
        ["assist-start"] = Shape(),
        ["assist-status"] = Shape(),
        ["assist-stop"] = Shape(),
        ["work-start"] = Shape(new[] { "locationKey", "anchorX", "anchorY", "shape", "radius", "kind", "policy" }, new[] { "endX", "endY" }),
        ["work-status"] = Shape(),
        ["work-resume"] = Shape(),
        ["work-stop"] = Shape(),
        ["cursor-single"] = Shape("locationKey", "anchorX", "anchorY", "shape", "radius", "kind", "policy"),
        ["bag-give"] = Shape("playerSlot"),
        ["bag-take"] = Shape("bagSlot"),
        ["storage-authorize"] = Shape("tileX", "tileY"),
        ["storage-unauthorize"] = Shape("tileX", "tileY"),
        ["storage-borrow"] = Shape("itemId"),
        ["storage-take-material"] = Shape("itemId", "count"),
        ["storage-return"] = Shape("responsibilityId"),
        ["vitals-eat"] = Shape(optional: new[] { "bagSlot" }),
        ["vitals-rest"] = Shape(optional: new[] { "seconds" }),
        ["water"] = TargetShape(),
        ["chop"] = TargetShape(),
        ["mine"] = TargetShape(),
        ["harvest"] = TargetShape(),
        ["forage"] = TargetShape(),
        ["mow"] = TargetShape(),
        ["dig"] = TargetShape(),
        ["fish"] = TargetShape(),
        ["fight"] = TargetShape(),
        ["combat-options"] = Shape(Array.Empty<string>(), new[] { "radius" }),
        ["combat-strike"] = Shape(new[] { "combatOptionId" }, new[] { "operationId" }),
        ["combat-guard"] = Shape(new[] { "radius", "seconds" }, new[] { "maximumSwings", "operationId" }),
        ["combat-status"] = Shape(),
        ["combat-stop"] = Shape(),
        ["care"] = Shape(new[] { "targetType", "targetId", "careAction" }, new[] { "operationId" }),
        ["delivery-create"] = Shape("bagSlot", "count", "recipientId", "deliveryId"),
        ["delivery-offer"] = Shape("deliveryId"),
        ["delivery-return"] = Shape("deliveryId"),
        ["craft-list"] = Shape(),
        ["craft-preview"] = Shape(new[] { "recipeKey" }, new[] { "craftCount" }),
        ["craft-status"] = Shape(),
        ["operation-status"] = Shape("operationId"),
        ["craft-start"] = Shape(new[] { "recipeKey", "craftCount", "operationId" }),
        ["craft-cancel"] = Shape(),
        ["plant-options"] = Shape(optional: new[] { "query" }),
        ["plant-preview"] = Shape("seedOptionId", "count", "radius"),
        ["plant-status"] = Shape(),
        ["plant-start"] = Shape(new[] { "seedOptionId", "count", "radius" }, new[] { "operationId" }),
        ["plant-resume"] = Shape(),
        ["plant-cancel"] = Shape(),
    };

    public static ProtocolValidationResult ValidateCommand(CommandRequestDto? dto, ModMessageReceivedEventArgs transport, string currentEpoch) =>
        ValidateCommand(dto, transport.FromPlayerID, currentEpoch);

    public static ProtocolValidationResult ValidateCommand(CommandRequestDto? dto, long transportSenderId, string currentEpoch)
    {
        if (dto is null)
            return ProtocolValidationResult.Failure("EMPTY-REQUEST", "The request body is missing.");
        if (dto.ProtocolVersion != MultiplayerProtocol.Version)
            return ProtocolValidationResult.Failure("UNSUPPORTED-PROTOCOL", "The request protocol version is not supported.");
        if (!Guid.TryParseExact(dto.SessionEpoch, "N", out _) || dto.SessionEpoch != currentEpoch)
            return ProtocolValidationResult.Failure("STALE-EPOCH", "The request belongs to another save session.");
        if (!Guid.TryParseExact(dto.RequestId, "N", out _))
            return ProtocolValidationResult.Failure("INVALID-REQUEST-ID", "RequestId must be one compact GUID.");
        if (dto.SenderPlayerId == 0 || dto.SenderPlayerId != transportSenderId)
            return ProtocolValidationResult.Failure("SENDER-MISMATCH", "The transport sender does not match SenderPlayerId.");
        if (dto.OwnerId == 0 || dto.OwnerId != dto.SenderPlayerId)
            return ProtocolValidationResult.Failure("NOT-OWNER", "A farmhand may request only its own companion.");
        if (!CompanionIdentity.IsValidSlot(dto.Slot))
            return ProtocolValidationResult.Failure("SINGLE-COMPANION-PER-OWNER", "Each player may request only the canonical Slot 1 Yui.");
        if (dto.Sequence == 0)
            return ProtocolValidationResult.Failure("INVALID-SEQUENCE", "Sequence must be greater than zero.");
        if (string.IsNullOrWhiteSpace(dto.Command) || dto.Command.Length > 32 || !Shapes.TryGetValue(dto.Command, out CommandShape? shape))
            return ProtocolValidationResult.Failure("UNKNOWN-COMMAND", "The command is not in the network allowlist.");
        if (dto.Fields is null || dto.Fields.Count > MultiplayerProtocol.MaxFieldCount)
            return ProtocolValidationResult.Failure("INVALID-FIELDS", "The command field collection is missing or oversized.");

        int size = dto.SessionEpoch.Length + dto.RequestId.Length + dto.Command.Length;
        foreach ((string key, string value) in dto.Fields)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MultiplayerProtocol.MaxFieldKeyLength || value is null || value.Length > MultiplayerProtocol.MaxFieldValueLength)
                return ProtocolValidationResult.Failure("INVALID-FIELD-SIZE", "A command field is empty or oversized.");
            if (!shape.Allowed.Contains(key))
                return ProtocolValidationResult.Failure("FIELD-NOT-ALLOWED", $"Field {key} is not allowed for {dto.Command}.");
            size += key.Length + value.Length;
        }
        if (size > MultiplayerProtocol.MaxRequestCharacters)
            return ProtocolValidationResult.Failure("REQUEST-TOO-LARGE", "The request exceeds the protocol character budget.");
        if (shape.Required.Any(key => !dto.Fields.ContainsKey(key)))
            return ProtocolValidationResult.Failure("MISSING-FIELD", "The command is missing a required field.");

        string? fieldError = ValidateFieldValues(dto.Command, dto.Fields);
        if (fieldError is not null)
            return ProtocolValidationResult.Failure("INVALID-FIELD-VALUE", fieldError);

        var fields = new Dictionary<string, string>(dto.Fields, StringComparer.Ordinal);
        return ProtocolValidationResult.Success(new ValidatedCommandRequest(dto.SessionEpoch, dto.RequestId, new CompanionIdentity(dto.OwnerId, dto.Slot), dto.SenderPlayerId, dto.Sequence, dto.Command, fields));
    }

    public static ProtocolValidationResult ValidateSnapshotRequest(SnapshotRequestDto? dto, ModMessageReceivedEventArgs transport, string currentEpoch)
    {
        if (dto is null || dto.ProtocolVersion != MultiplayerProtocol.Version)
            return ProtocolValidationResult.Failure("UNSUPPORTED-PROTOCOL", "The snapshot request protocol is invalid.");
        if (dto.SenderPlayerId == 0 || dto.SenderPlayerId != transport.FromPlayerID)
            return ProtocolValidationResult.Failure("SENDER-MISMATCH", "The snapshot transport sender is invalid.");
        if (!Guid.TryParseExact(dto.RequestId, "N", out _))
            return ProtocolValidationResult.Failure("INVALID-REQUEST-ID", "The snapshot RequestId is invalid.");
        if (dto.SessionEpoch.Length > 32 || (dto.SessionEpoch.Length > 0 && dto.SessionEpoch != currentEpoch))
            return ProtocolValidationResult.Failure("STALE-EPOCH", "The snapshot request carries an old epoch.");
        return ProtocolValidationResult.Success(default);
    }

    private static string? ValidateFieldValues(string command, IReadOnlyDictionary<string, string> fields)
    {
        foreach ((string key, string value) in fields)
        {
            switch (key)
            {
                case "tileX":
                case "tileY":
                case "anchorX":
                case "anchorY":
                case "endX":
                case "endY":
                    if (!int.TryParse(value, out int tile) || tile is < 0 or > 9999)
                        return $"{key} must be an integer from 0 through 9999.";
                    break;
                case "radius":
                    if (!int.TryParse(value, out int radius) || radius is < 0 or > WorkScopeContracts.MaximumRadius)
                        return $"radius must be 0 through {WorkScopeContracts.MaximumRadius}.";
                    break;
                case "locationKey":
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
                        return "locationKey is empty or oversized.";
                    break;
                case "shape":
                    if (!WorkScopeShapes.IsValid(value))
                        return "shape must be SingleTarget or Radius.";
                    break;
                case "kind":
                    if (value != CursorRequestedKinds.Auto && !WorkKinds.TryNormalize(value, out _))
                        return "kind is not in the continuous-work allowlist.";
                    break;
                case "policy":
                    if (value != WorkCompletionPolicies.Single && !WorkCompletionPolicies.TryNormalizeContinuous(value, out _))
                        return "policy must be until-clear or until-stopped.";
                    break;
                case "playerSlot":
                    if (!int.TryParse(value, out int playerSlot) || playerSlot is < 1 or > 36)
                        return "playerSlot must be from 1 through 36.";
                    break;
                case "bagSlot":
                    if (!int.TryParse(value, out int bagSlot) || bagSlot is < 1 or > CompanionInventoryStore.Capacity)
                        return $"bagSlot must identify a regular Yui item from 1 through {CompanionInventoryStore.Capacity}; protected starter tools have no public slot.";
                    break;
                case "count":
                    if (!int.TryParse(value, out int count) || count is < 1 or > 999)
                        return "count must be from 1 through 999.";
                    break;
                case "seconds":
                    if (!int.TryParse(value, out int seconds) || seconds is < 2 or > 8)
                        return "seconds must be from 2 through 8.";
                    break;
                case "operationId":
                    if (!IsBoundedToken(value, 96))
                        return "operationId contains unsupported characters or length.";
                    break;
                case "responsibilityId":
                    if (!Guid.TryParseExact(value, "N", out _))
                        return "responsibilityId must be one compact GUID.";
                    break;
                case "craftCount":
                    if (!int.TryParse(value, out int craftCount) || craftCount is < 1 or > 25)
                        return "craftCount must be from 1 through 25.";
                    break;
                case "recipeKey":
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
                        return "recipeKey is empty, oversized, or contains control characters.";
                    break;
                case "seedOptionId":
                    if (!Guid.TryParseExact(value, "N", out _))
                        return "seedOptionId must be one compact lower-level selection GUID.";
                    break;
                case "query":
                    if (value.Length > PlantingConstants.MaximumQueryLength || value.Any(char.IsControl))
                        return $"query must be at most {PlantingConstants.MaximumQueryLength} non-control characters.";
                    break;
                case "deliveryId":
                    if (!IsBoundedToken(value, 96))
                        return "deliveryId contains unsupported characters or length.";
                    break;
                case "recipientId":
                    if (!long.TryParse(value, out long recipientId) || recipientId == 0)
                        return "recipientId must be a non-zero Int64 player ID.";
                    break;
                case "itemId":
                    if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
                        return "itemId is empty or oversized.";
                    break;
                case "targetType":
                    if (value is not ("animal" or "pet"))
                        return "targetType must be animal or pet.";
                    break;
                case "careAction":
                    if (value is not ("pet" or "milk" or "shear"))
                        return "careAction must be pet, milk, or shear.";
                    break;
                case "targetId":
                    if (fields.GetValueOrDefault("targetType") == "animal")
                    {
                        if (!long.TryParse(value, out _))
                            return "Animal targetId must be Int64.";
                    }
                    else if (!Guid.TryParse(value, out _))
                    {
                        return "Pet targetId must be a GUID.";
                    }
                    break;
            }
        }

        if (command == "care" && fields["targetType"] == "pet" && fields["careAction"] != "pet")
            return "Pets support only the pet action.";
        if (command is "plant-preview" or "plant-start")
        {
            if (!int.TryParse(fields["count"], out int plantCount) || plantCount is < 1 or > PlantingConstants.MaximumCount)
                return $"plant count must be 1 through {PlantingConstants.MaximumCount}.";
            if (!int.TryParse(fields["radius"], out int plantRadius) || plantRadius is < WorkScopeContracts.MinimumRadius or > WorkScopeContracts.MaximumRadius)
                return $"plant radius must be {WorkScopeContracts.MinimumRadius} through {WorkScopeContracts.MaximumRadius}.";
        }
        if (command == "work-start" && fields["shape"] != WorkScopeShapes.Radius)
        {
            if (fields["shape"] != WorkScopeShapes.Rectangle)
                return "Continuous work requires Radius or Rectangle shape.";
            if (!fields.ContainsKey("endX") || !fields.ContainsKey("endY") || fields["radius"] != "0")
                return "Rectangle work requires both endpoint fields and radius 0.";
        }
        else if (command == "work-start" && (fields.ContainsKey("endX") || fields.ContainsKey("endY")))
            return "Radius work must not include rectangle endpoint fields.";
        if (command == "cursor-single" && (fields["shape"] != WorkScopeShapes.SingleTarget || fields["radius"] != "0" || fields["policy"] != WorkCompletionPolicies.Single))
            return "A cursor single request requires SingleTarget, radius 0, and Single policy.";
        return null;
    }

    private static bool IsBoundedToken(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ':' or '.');

    private static CommandShape TargetShape() => Shape(new[] { "tileX", "tileY" }, new[] { "operationId" });
    private static CommandShape Shape(params string[] required) => Shape(required, Array.Empty<string>());
    private static CommandShape Shape(string[]? required = null, string[]? optional = null) => new(required ?? Array.Empty<string>(), optional ?? Array.Empty<string>());

    private sealed class CommandShape
    {
        public CommandShape(IEnumerable<string> required, IEnumerable<string> optional)
        {
            this.Required = required.ToHashSet(StringComparer.Ordinal);
            this.Allowed = this.Required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        }

        public HashSet<string> Required { get; }
        public HashSet<string> Allowed { get; }
    }
}

internal static class MultiplayerDtoCodec
{
    public static bool TryRead<T>(ModMessageReceivedEventArgs e, out T? dto, out string error) where T : class
    {
        try
        {
            dto = e.ReadAs<T>();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            dto = null;
            error = ex.GetType().Name;
            return false;
        }
    }

    public static string Bounded(string? value, int maximum) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, maximum)];
}
