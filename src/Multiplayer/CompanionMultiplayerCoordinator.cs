using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace YuiToIssho;

internal readonly record struct CommandReceiptObservation(
    string RequestId,
    CompanionIdentity Identity,
    string Command,
    IReadOnlyDictionary<string, string> Fields,
    NetworkCommandResult Result,
    ulong SnapshotVersion);

internal sealed class CompanionMultiplayerCoordinator
{
    private const int ReceiptCacheCapacity = 256;
    private const int PendingRequestCapacity = 32;
    private const int RequestRetryTicks = 60;
    private const int MaximumSendAttempts = 3;
    private const int DeferredReceiptTimeoutTicks = 600;
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly CompanionProjectionCoordinator projection;
    private readonly CompanionBodyBinder bodies;
    private readonly Dictionary<RequestCacheKey, CommandReceiptDto> receiptCache = new();
    private readonly Dictionary<RequestCacheKey, NetworkCommandResult> earlyDeferredCompletions = new();
    private readonly Queue<RequestCacheKey> receiptOrder = new();
    private readonly Dictionary<long, ulong> lastAcceptedSequence = new();
    private readonly Dictionary<CompanionIdentity, CompanionPresentationDto> lastPresentations = new();
    private readonly Dictionary<CompanionIdentity, CompanionPresentationDto?> authoritativePresentations = new();
    private readonly Dictionary<CompanionIdentity, ulong> presentationRevisions = new();
    private readonly Dictionary<string, PendingOutboundRequest> pendingOutbound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnknownOutboundOperation> unknownOperations = new(StringComparer.Ordinal);
    private Func<ValidatedCommandRequest, NetworkCommandResult>? commandHandler;
    private Action<long>? peerConnectedHandler;
    private Action<long>? peerDisconnectedHandler;
    private Action<CommandReceiptObservation>? receiptObserver;
    private Action<CommandReceiptObservation>? settlementObserver;
    private Action<SpeechEventDto>? speechObserver;
    private Action<CompanionSnapshotDto>? speechSnapshotObserver;
    private bool attached;
    private bool suspended = true;
    private string sessionEpoch = string.Empty;
    private ulong localSequence;
    private ulong snapshotVersion;
    private ulong presentationSequence;
    private ulong currentTick;
    private ulong lastSnapshotTick;
    private ulong? lastSnapshotRequestTick;
    private ulong lastUnknownQueryTick;

    public CompanionMultiplayerCoordinator(IModHelper helper, IMonitor monitor, CompanionProjectionCoordinator projection, CompanionBodyBinder bodies)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.projection = projection;
        this.bodies = bodies;
    }

    public string SessionEpoch => this.sessionEpoch;
    public bool IsSessionReady => !this.suspended && Guid.TryParseExact(this.sessionEpoch, "N", out _);
    public int UnknownOperationCount => this.unknownOperations.Count;

    public string DescribeSession()
    {
        string epoch = this.sessionEpoch.Length >= 8 ? this.sessionEpoch[..8] : "none";
        return $"protocol={MultiplayerProtocol.Version} epoch={epoch} ready={this.IsSessionReady} snapshot={this.projection.SnapshotVersion} unknownOps={this.unknownOperations.Count}";
    }

    public string DescribeCompanion(CompanionIdentity identity)
    {
        this.presentationRevisions.TryGetValue(identity, out ulong hostRevision);
        this.bodies.TryGetBodyGeneration(identity, out ulong hostGeneration);
        return Context.IsMainPlayer
            ? $"bodySource={BodyReplicationModes.NativeNpc} bodyGen={hostGeneration} presentationRev={hostRevision} {this.DescribeSession()}"
            : $"{this.projection.DescribeNetworkState(identity)} {this.DescribeSession()}";
    }

    public void Attach()
    {
        if (this.attached)
            return;
        this.attached = true;
        this.helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
        this.helper.Events.Multiplayer.PeerConnected += this.OnPeerConnected;
        this.helper.Events.Multiplayer.PeerDisconnected += this.OnPeerDisconnected;
    }

    public void AttachCommandHandler(Func<ValidatedCommandRequest, NetworkCommandResult> handler) => this.commandHandler = handler;
    public void AttachPeerConnectedHandler(Action<long> handler) => this.peerConnectedHandler = handler;
    public void AttachPeerDisconnectedHandler(Action<long> handler) => this.peerDisconnectedHandler = handler;
    public void AttachReceiptObserver(Action<CommandReceiptObservation> observer) => this.receiptObserver = observer;
    public void AttachSettlementObserver(Action<CommandReceiptObservation> observer) => this.settlementObserver = observer;
    public void AttachSpeechObserver(Action<SpeechEventDto> observer) => this.speechObserver = observer;
    public void AttachSpeechSnapshotObserver(Action<CompanionSnapshotDto> observer) => this.speechSnapshotObserver = observer;

    public void BroadcastSpeech(SpeechEventDto speech)
    {
        if (!Context.IsMainPlayer || !this.IsSessionReady)
            return;
        speech.ProtocolVersion = MultiplayerProtocol.Version;
        speech.SessionEpoch = this.sessionEpoch;
        if (!SpeechEventContracts.IsValid(speech, this.sessionEpoch))
            throw new InvalidOperationException("The authoritative speech event failed its bounded contract.");
        this.helper.Multiplayer.SendMessage(speech, MultiplayerProtocol.MessageTypes.SpeechEvent, new[] { MultiplayerProtocol.ModId });
    }

    public void BeginHostSession()
    {
        this.ClearProjectionState();
        this.ClearRequestState();
        this.sessionEpoch = Guid.NewGuid().ToString("N");
        this.suspended = false;
    }

    public void BeginClientSession()
    {
        this.ClearProjectionState();
        this.ClearRequestState();
        this.sessionEpoch = string.Empty;
        this.suspended = false;
        this.RequestSnapshot();
    }

    public void AcceptHostEpoch(string epoch)
    {
        if (!Context.IsMainPlayer && Guid.TryParseExact(epoch, "N", out _))
        {
            bool changed = this.sessionEpoch != epoch;
            this.sessionEpoch = epoch;
            if (changed)
                this.QueryUnknownOperations();
        }
    }

    public void Suspend()
    {
        this.suspended = true;
        if (!Context.IsMainPlayer)
        {
            foreach (PendingOutboundRequest pending in this.pendingOutbound.Values)
                this.RememberUnknownOperation(pending.Dto);
            this.pendingOutbound.Clear();
        }
    }
    public void Resume()
    {
        this.suspended = false;
        if (!Context.IsMainPlayer)
            this.QueryUnknownOperations();
    }

    public void ResetSession(bool preserveUnknownOperations = false)
    {
        this.suspended = true;
        this.sessionEpoch = string.Empty;
        if (preserveUnknownOperations)
        {
            foreach (PendingOutboundRequest pending in this.pendingOutbound.Values)
                this.RememberUnknownOperation(pending.Dto);
        }
        this.ClearRequestState(clearUnknownOperations: !preserveUnknownOperations);
        this.ClearProjectionState();
    }

    public void Update(ulong tick)
    {
        this.currentTick = tick;
        if (Context.IsMainPlayer)
        {
            if (this.IsSessionReady)
            {
                this.PublishPresentationChanges(this.projection.BuildHostPresentationView());
                if (this.lastSnapshotTick == 0 || tick - this.lastSnapshotTick >= 30)
                    this.PublishSnapshot(null);
            }
        }
        else
        {
            this.projection.Update(6);
            this.RetryPendingRequests(tick);
            if (this.IsSessionReady
                && this.unknownOperations.Count > 0
                && (this.lastUnknownQueryTick == 0 || tick - this.lastUnknownQueryTick >= 120))
                this.QueryUnknownOperations();
            if (!this.suspended
                && this.sessionEpoch.Length == 0
                && (this.lastSnapshotRequestTick is null || tick - this.lastSnapshotRequestTick.Value >= 120))
                this.RequestSnapshot();
        }
    }

    public NetworkCommandResult Submit(CompanionIdentity identity, string command, IReadOnlyDictionary<string, string> fields)
    {
        if (!this.IsSessionReady || !Context.IsWorldReady)
            return NetworkCommandResult.Failure("NETWORK-SESSION-NOT-READY", "A current host session epoch is required before submitting companion work.");

        long senderId = Game1.player.UniqueMultiplayerID;
        var dto = new CommandRequestDto
        {
            ProtocolVersion = MultiplayerProtocol.Version,
            SessionEpoch = this.sessionEpoch,
            RequestId = Guid.NewGuid().ToString("N"),
            OwnerId = identity.OwnerId,
            Slot = identity.Slot,
            SenderPlayerId = senderId,
            Sequence = ++this.localSequence,
            Command = command,
            Fields = new Dictionary<string, string>(fields, StringComparer.Ordinal),
        };
        ProtocolValidationResult validation = MultiplayerRequestValidator.ValidateCommand(dto, senderId, this.sessionEpoch);
        if (!validation.IsSuccess)
            return NetworkCommandResult.Failure(validation.Code, validation.Message);

        if (Context.IsMainPlayer)
        {
            CommandReceiptDto receipt = this.Settle(validation.Request);
            return new NetworkCommandResult(receipt.IsSuccess, receipt.Code, receipt.Message, receipt.Planting, receipt.Combat, dto.RequestId, receipt.IsFinal);
        }

        if (this.pendingOutbound.Count >= PendingRequestCapacity)
            return NetworkCommandResult.Failure("REQUEST-QUEUE-FULL", "Wait for a host Receipt before sending more companion requests.");
        this.pendingOutbound.Add(dto.RequestId, new PendingOutboundRequest(dto, this.currentTick));
        this.SendCommand(dto);
        return new NetworkCommandResult(true, "REQUEST-SENT", $"Sent request {dto.RequestId} to the host for {identity}.", RequestId: dto.RequestId);
    }

    public void CompleteDeferred(ValidatedCommandRequest request, NetworkCommandResult result)
    {
        if (!Context.IsMainPlayer
            || !this.IsSessionReady
            || request.SessionEpoch != this.sessionEpoch
            || !result.IsFinal)
            return;

        RequestCacheKey key = new(request.SenderPlayerId, request.RequestId);
        if (!this.receiptCache.TryGetValue(key, out CommandReceiptDto? pending))
        {
            this.earlyDeferredCompletions[key] = result;
            return;
        }
        if (pending.IsFinal)
            return;

        CommandReceiptDto receipt = this.CreateReceipt(request, result);
        this.receiptCache[key] = receipt;
        this.ObserveSettlement(request, result, receipt.SnapshotVersion);
        if (request.SenderPlayerId != Game1.player.UniqueMultiplayerID)
            this.SendReceipt(request.SenderPlayerId, receipt);
        else
            this.receiptObserver?.Invoke(new CommandReceiptObservation(request.RequestId, request.Identity, request.Command, request.Fields, result, receipt.SnapshotVersion));
        this.monitor.Log($"HY-NET-DEFERRED-{MultiplayerDtoCodec.Bounded(result.Code, 64)}: {MultiplayerDtoCodec.Bounded(result.Message, 256)}", result.IsSuccess ? LogLevel.Info : LogLevel.Warn);
    }

    public void RequestSnapshot()
    {
        if (Context.IsMainPlayer || !Context.IsWorldReady)
            return;
        var dto = new SnapshotRequestDto
        {
            ProtocolVersion = MultiplayerProtocol.Version,
            SessionEpoch = this.sessionEpoch,
            RequestId = Guid.NewGuid().ToString("N"),
            SenderPlayerId = Game1.player.UniqueMultiplayerID,
            LastSnapshotVersion = this.projection.SnapshotVersion,
        };
        this.helper.Multiplayer.SendMessage(dto, MultiplayerProtocol.MessageTypes.SnapshotRequest, new[] { MultiplayerProtocol.ModId });
        this.lastSnapshotRequestTick = this.currentTick;
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != MultiplayerProtocol.ModId || !MultiplayerProtocol.MessageTypes.IsKnown(e.Type))
            return;

        if (e.Type == MultiplayerProtocol.MessageTypes.CommandRequest)
            this.ReceiveCommand(e);
        else if (e.Type == MultiplayerProtocol.MessageTypes.CommandReceipt)
            this.ReceiveReceipt(e);
        else if (e.Type == MultiplayerProtocol.MessageTypes.SnapshotRequest)
            this.ReceiveSnapshotRequest(e);
        else if (e.Type == MultiplayerProtocol.MessageTypes.RuntimeSnapshot)
            this.ReceiveSnapshot(e);
        else if (e.Type == MultiplayerProtocol.MessageTypes.PresentationEvent)
            this.ReceivePresentation(e);
        else if (e.Type == MultiplayerProtocol.MessageTypes.SpeechEvent)
            this.ReceiveSpeech(e);
    }

    private void ReceiveCommand(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer || !this.IsSessionReady)
            return;
        if (!MultiplayerDtoCodec.TryRead(e, out CommandRequestDto? dto, out string readError))
        {
            this.monitor.Log($"HY-NET-MALFORMED: Rejected command payload from {e.FromPlayerID}: {readError}.", LogLevel.Warn);
            return;
        }

        ProtocolValidationResult validation = MultiplayerRequestValidator.ValidateCommand(dto, e, this.sessionEpoch);
        if (!validation.IsSuccess)
        {
            this.SendReceipt(e.FromPlayerID, dto, NetworkCommandResult.Failure(validation.Code, validation.Message));
            return;
        }

        CommandReceiptDto receipt = this.Settle(validation.Request);
        this.SendReceipt(e.FromPlayerID, receipt);
    }

    private void ReceiveReceipt(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer || e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID)
            return;
        if (!MultiplayerDtoCodec.TryRead(e, out CommandReceiptDto? dto, out string readError) || dto is null)
        {
            this.monitor.Log($"HY-NET-RECEIPT-MALFORMED: {readError}.", LogLevel.Warn);
            return;
        }
        if (dto.ProtocolVersion != MultiplayerProtocol.Version
            || dto.SessionEpoch != this.sessionEpoch
            || dto.SenderPlayerId != Game1.player.UniqueMultiplayerID
            || !Guid.TryParseExact(dto.RequestId, "N", out _)
            || (!dto.IsFinal && (!dto.IsSuccess || dto.Code != "REQUEST-PENDING"))
            || (dto.IsFinal && dto.Code == "REQUEST-PENDING"))
            return;
        if (this.pendingOutbound.TryGetValue(dto.RequestId, out PendingOutboundRequest? pending)
            && pending.Dto.Sequence == dto.Sequence
            && pending.Dto.OwnerId == dto.OwnerId
            && pending.Dto.Slot == dto.Slot)
        {
            pending.LastSentTick = this.currentTick;
            pending.IsDeferred = !dto.IsFinal;
            if (dto.IsFinal)
            {
                this.pendingOutbound.Remove(dto.RequestId);
                this.receiptObserver?.Invoke(new CommandReceiptObservation(
                    dto.RequestId,
                    new CompanionIdentity(pending.Dto.OwnerId, pending.Dto.Slot),
                    pending.Dto.Command,
                    pending.Dto.Fields,
                    new NetworkCommandResult(dto.IsSuccess, dto.Code, dto.Message, dto.Planting, dto.Combat, dto.RequestId),
                    dto.SnapshotVersion));
                if (pending.Dto.Command == "operation-status"
                    && pending.Dto.Fields.TryGetValue("operationId", out string? operationId)
                    && IsDefinitiveOperationStatus(dto.Code))
                    this.unknownOperations.Remove(operationId);
            }
        }
        this.monitor.Log($"HY-NET-{MultiplayerDtoCodec.Bounded(dto.Code, 64)}: {MultiplayerDtoCodec.Bounded(dto.Message, 256)}", dto.IsFinal ? (dto.IsSuccess ? LogLevel.Info : LogLevel.Warn) : LogLevel.Trace);
    }

    private void ReceiveSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer || !this.IsSessionReady)
            return;
        if (!MultiplayerDtoCodec.TryRead(e, out SnapshotRequestDto? dto, out string readError))
        {
            this.monitor.Log($"HY-NET-SNAPSHOT-MALFORMED: Rejected snapshot request from {e.FromPlayerID}: {readError}.", LogLevel.Warn);
            return;
        }
        ProtocolValidationResult validation = MultiplayerRequestValidator.ValidateSnapshotRequest(dto, e, this.sessionEpoch);
        if (!validation.IsSuccess || Game1.GetPlayer(e.FromPlayerID, onlyOnline: true) is null)
            return;
        this.PublishSnapshot(e.FromPlayerID);
    }

    private void ReceiveSnapshot(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer || e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID)
            return;
        if (!MultiplayerDtoCodec.TryRead(e, out RuntimeSnapshotDto? snapshot, out string readError) || snapshot is null)
        {
            this.monitor.Log($"HY-NET-SNAPSHOT-MALFORMED: {readError}.", LogLevel.Warn);
            return;
        }
        if (snapshot.HostPlayerId != e.FromPlayerID
            || (this.sessionEpoch.Length > 0 && snapshot.SessionEpoch != this.sessionEpoch))
            return;

        ProjectionApplyResult applied = this.projection.ApplySnapshot(snapshot);
        if (!applied.IsSuccess)
        {
            if (applied.Code != "STALE-SNAPSHOT")
                this.monitor.Log($"HY-NET-{applied.Code}: {applied.Message}", LogLevel.Warn);
            return;
        }
        this.AcceptHostEpoch(snapshot.SessionEpoch);
        foreach (CompanionSnapshotDto companion in snapshot.Companions)
            this.NotifySpeechSnapshot(companion);
    }

    private void ReceivePresentation(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer || e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID)
            return;
        if (!MultiplayerDtoCodec.TryRead(e, out PresentationEventDto? presentation, out string readError) || presentation is null)
        {
            this.monitor.Log($"HY-NET-PRESENTATION-MALFORMED: {readError}.", LogLevel.Warn);
            return;
        }
        if (presentation.ProtocolVersion != MultiplayerProtocol.Version
            || presentation.SessionEpoch != this.sessionEpoch)
            return;
        ProjectionApplyResult applied = this.projection.ApplyPresentation(presentation);
        if (!applied.IsSuccess && applied.Code is not ("STALE-PRESENTATION" or "PRESENTATION-IDENTITY-UNKNOWN"))
            this.monitor.Log($"HY-NET-{applied.Code}: {applied.Message}", LogLevel.Warn);
    }

    private void ReceiveSpeech(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer || e.FromPlayerID != Game1.MasterPlayer.UniqueMultiplayerID)
            return;
        if (!MultiplayerDtoCodec.TryRead(e, out SpeechEventDto? speech, out string readError) || speech is null)
        {
            this.monitor.Log($"HY-NET-SPEECH-MALFORMED: {readError}.", LogLevel.Warn);
            return;
        }
        if (!SpeechEventContracts.IsValid(speech, this.sessionEpoch))
            return;
        try
        {
            this.speechObserver?.Invoke(speech);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"HY-NET-SPEECH-OBSERVER-FAILED: {ex.GetType().Name}.", LogLevel.Warn);
        }
    }

    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (Context.IsMainPlayer && this.IsSessionReady)
        {
            this.lastAcceptedSequence.Remove(e.Peer.PlayerID);
            this.peerConnectedHandler?.Invoke(e.Peer.PlayerID);
            this.PublishSnapshot(e.Peer.PlayerID);
        }
        else if (!Context.IsMainPlayer && e.Peer.IsHost)
        {
            this.suspended = false;
            this.RequestSnapshot();
        }
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        if (Context.IsMainPlayer)
            this.peerDisconnectedHandler?.Invoke(e.Peer.PlayerID);
        else if (e.Peer.IsHost)
            this.ResetSession(preserveUnknownOperations: true);
    }

    private CommandReceiptDto Settle(ValidatedCommandRequest request)
    {
        RequestCacheKey key = new(request.SenderPlayerId, request.RequestId);
        if (this.receiptCache.TryGetValue(key, out CommandReceiptDto? cached))
            return cached;

        NetworkCommandResult result;
        if (this.suspended || !Context.IsMainPlayer || !Context.IsWorldReady)
        {
            result = NetworkCommandResult.Failure("HOST-LIFECYCLE-GATE", "The authoritative host session is not accepting requests.");
        }
        else if (Game1.GetPlayer(request.SenderPlayerId, onlyOnline: true) is null)
        {
            result = NetworkCommandResult.Failure("SENDER-OFFLINE", "The sender is not an online Farmer on the host.");
        }
        else if (this.lastAcceptedSequence.TryGetValue(request.SenderPlayerId, out ulong previous) && request.Sequence <= previous)
        {
            result = NetworkCommandResult.Failure("STALE-SEQUENCE", $"Sequence {request.Sequence} is not newer than {previous}.");
        }
        else
        {
            this.lastAcceptedSequence[request.SenderPlayerId] = request.Sequence;
            result = this.commandHandler?.Invoke(request) ?? NetworkCommandResult.Failure("COMMAND-NOT-ROUTED", "The host command route is not active yet.");
            if (!result.IsFinal && this.earlyDeferredCompletions.Remove(key, out NetworkCommandResult completed))
                result = completed;
        }

        CommandReceiptDto receipt = this.CreateReceipt(request, result);
        this.CacheReceipt(key, receipt);
        if (receipt.IsFinal)
            this.ObserveSettlement(request, result, receipt.SnapshotVersion);
        return receipt;
    }

    private CommandReceiptDto CreateReceipt(ValidatedCommandRequest request, NetworkCommandResult result) => new()
    {
        SessionEpoch = this.sessionEpoch,
        RequestId = request.RequestId,
        OwnerId = request.Identity.OwnerId,
        Slot = request.Identity.Slot,
        SenderPlayerId = request.SenderPlayerId,
        Sequence = request.Sequence,
        IsSuccess = result.IsSuccess,
        IsFinal = result.IsFinal,
        Code = MultiplayerDtoCodec.Bounded(result.Code, 64),
        Message = MultiplayerDtoCodec.Bounded(result.Message, 256),
        SnapshotVersion = this.snapshotVersion,
        Planting = result.Planting,
        Combat = result.Combat,
    };

    private void ObserveSettlement(ValidatedCommandRequest request, NetworkCommandResult result, ulong settledSnapshotVersion)
    {
        try
        {
            this.settlementObserver?.Invoke(new CommandReceiptObservation(
                request.RequestId,
                request.Identity,
                request.Command,
                request.Fields,
                result,
                settledSnapshotVersion));
        }
        catch (Exception ex)
        {
            this.monitor.Log($"HY-NET-SETTLEMENT-OBSERVER-FAILED: {request.Identity} {ex.GetType().Name}.", LogLevel.Warn);
        }
    }

    private void SendReceipt(long recipient, CommandRequestDto? request, NetworkCommandResult result)
    {
        var receipt = new CommandReceiptDto
        {
            SessionEpoch = this.sessionEpoch,
            RequestId = MultiplayerDtoCodec.Bounded(request?.RequestId, 64),
            OwnerId = request?.OwnerId ?? 0,
            Slot = request?.Slot ?? 0,
            SenderPlayerId = recipient,
            Sequence = request?.Sequence ?? 0,
            IsSuccess = result.IsSuccess,
            IsFinal = result.IsFinal,
            Code = MultiplayerDtoCodec.Bounded(result.Code, 64),
            Message = MultiplayerDtoCodec.Bounded(result.Message, 256),
            SnapshotVersion = this.snapshotVersion,
            Planting = result.Planting,
            Combat = result.Combat,
        };
        this.SendReceipt(recipient, receipt);
    }

    private void SendReceipt(long recipient, CommandReceiptDto receipt) =>
        this.helper.Multiplayer.SendMessage(receipt, MultiplayerProtocol.MessageTypes.CommandReceipt, new[] { MultiplayerProtocol.ModId }, new[] { recipient });

    private void CacheReceipt(RequestCacheKey key, CommandReceiptDto receipt)
    {
        this.receiptCache[key] = receipt;
        this.receiptOrder.Enqueue(key);
        while (this.receiptOrder.Count > ReceiptCacheCapacity)
        {
            RequestCacheKey oldest = this.receiptOrder.Dequeue();
            this.receiptCache.Remove(oldest);
        }
    }

    private void ClearRequestState(bool clearUnknownOperations = true)
    {
        this.receiptCache.Clear();
        this.earlyDeferredCompletions.Clear();
        this.receiptOrder.Clear();
        this.lastAcceptedSequence.Clear();
        this.pendingOutbound.Clear();
        if (clearUnknownOperations)
            this.unknownOperations.Clear();
        this.localSequence = 0;
    }

    private void RetryPendingRequests(ulong tick)
    {
        if (this.suspended || this.sessionEpoch.Length == 0)
            return;
        foreach (PendingOutboundRequest pending in this.pendingOutbound.Values.ToArray())
        {
            if (tick - pending.LastSentTick < RequestRetryTicks)
                continue;
            if (pending.IsDeferred)
            {
                if (tick - pending.FirstSentTick >= DeferredReceiptTimeoutTicks)
                {
                    this.pendingOutbound.Remove(pending.Dto.RequestId);
                    this.monitor.Log($"HY-NET-DEFERRED-TIMEOUT: Host did not finish deferred request {pending.Dto.RequestId} within the bounded wait.", LogLevel.Warn);
                    continue;
                }
                pending.LastSentTick = tick;
                this.SendCommand(pending.Dto);
                continue;
            }
            if (pending.Attempts >= MaximumSendAttempts)
            {
                this.pendingOutbound.Remove(pending.Dto.RequestId);
                this.RememberUnknownOperation(pending.Dto);
                this.monitor.Log($"HY-NET-RECEIPT-TIMEOUT: Host did not settle request {pending.Dto.RequestId} after {pending.Attempts} send attempts; recoverable operations will be queried after the next authoritative snapshot.", LogLevel.Warn);
                continue;
            }
            pending.Attempts++;
            pending.LastSentTick = tick;
            this.SendCommand(pending.Dto);
        }
    }

    private void SendCommand(CommandRequestDto dto) =>
        this.helper.Multiplayer.SendMessage(
            dto,
            MultiplayerProtocol.MessageTypes.CommandRequest,
            new[] { MultiplayerProtocol.ModId },
            new[] { Game1.MasterPlayer.UniqueMultiplayerID });

    private void PublishSnapshot(long? recipient)
    {
        if (!Context.IsMainPlayer || !this.IsSessionReady)
            return;
        this.PublishPresentationChanges(this.projection.BuildHostPresentationView());
        ulong version = ++this.snapshotVersion;
        long[] recipients = recipient is null
            ? this.helper.Multiplayer.GetConnectedPlayers().Where(peer => !peer.IsHost).Select(peer => peer.PlayerID).ToArray()
            : new[] { recipient.Value };
        foreach (long viewerId in recipients)
        {
            try
            {
                RuntimeSnapshotDto snapshot = this.projection.BuildHostSnapshot(
                    this.sessionEpoch,
                    version,
                    this.currentTick,
                    viewerId,
                    this.authoritativePresentations,
                    this.presentationRevisions);
                this.helper.Multiplayer.SendMessage(snapshot, MultiplayerProtocol.MessageTypes.RuntimeSnapshot, new[] { MultiplayerProtocol.ModId }, new[] { viewerId });
            }
            catch (Exception ex)
            {
                this.monitor.Log($"HY-NET-SNAPSHOT-BUILD-FAILED: Viewer {viewerId} stopped with {ex.GetType().Name}.", LogLevel.Warn);
            }
        }
        this.lastSnapshotTick = this.currentTick;
    }

    private void PublishPresentationChanges(IReadOnlyDictionary<CompanionIdentity, CompanionPresentationDto?> presentations)
    {
        HashSet<CompanionIdentity> current = presentations.Keys.ToHashSet();
        foreach ((CompanionIdentity identity, CompanionPresentationDto? next) in presentations)
        {
            if (next is null)
            {
                if (this.lastPresentations.Remove(identity, out CompanionPresentationDto? previous))
                {
                    ulong revision = this.NextPresentationRevision(identity);
                    this.authoritativePresentations[identity] = null;
                    this.SendPresentation(identity, previous, "Clear", revision, this.currentTick, this.currentTick);
                }
                continue;
            }
            bool hasExisting = this.lastPresentations.TryGetValue(identity, out CompanionPresentationDto? existing);
            if (hasExisting && SamePresentation(existing!, next))
            {
                if (existing!.EndsAtHostTick == 0 && existing.RemainingTicks != next.RemainingTicks)
                {
                    existing.RemainingTicks = next.RemainingTicks;
                    this.authoritativePresentations[identity] = ClonePresentation(existing);
                }
                continue;
            }
            ulong nextRevision = this.NextPresentationRevision(identity);
            bool continuesTimeline = hasExisting && SamePresentationTimeline(existing!, next);
            ulong startedAtHostTick = continuesTimeline ? existing!.StartedAtHostTick : this.currentTick;
            ulong endsAtHostTick = continuesTimeline
                ? existing!.EndsAtHostTick
                : next.RemainingTicks <= 0 ? 0 : this.currentTick + (ulong)next.RemainingTicks;
            next.Revision = nextRevision;
            next.StartedAtHostTick = startedAtHostTick;
            next.EndsAtHostTick = endsAtHostTick;
            this.lastPresentations[identity] = next;
            this.authoritativePresentations[identity] = ClonePresentation(next);
            this.SendPresentation(identity, next, next.Phase, nextRevision, startedAtHostTick, endsAtHostTick);
        }
        foreach (CompanionIdentity stale in this.lastPresentations.Keys.Where(identity => !current.Contains(identity)).ToArray())
        {
            CompanionPresentationDto previous = this.lastPresentations[stale];
            ulong revision = this.NextPresentationRevision(stale);
            this.SendPresentation(stale, previous, "Clear", revision, this.currentTick, this.currentTick);
            this.lastPresentations.Remove(stale);
            this.authoritativePresentations.Remove(stale);
        }
    }

    private void SendPresentation(CompanionIdentity identity, CompanionPresentationDto source, string phase, ulong revision, ulong startedAtHostTick, ulong endsAtHostTick)
    {
        this.bodies.TryGetBodyGeneration(identity, out ulong bodyGeneration);
        var dto = new PresentationEventDto
        {
            SessionEpoch = this.sessionEpoch,
            EventId = Guid.NewGuid().ToString("N"),
            Sequence = ++this.presentationSequence,
            OwnerId = identity.OwnerId,
            Slot = identity.Slot,
            BodyGeneration = bodyGeneration,
            PresentationRevision = revision,
            OperationId = source.OperationId,
            Kind = source.Kind,
            Phase = phase,
            ToolId = source.ToolId,
            Facing = source.Facing,
            Frame = source.Frame,
            StartedAtHostTick = startedAtHostTick,
            EndsAtHostTick = endsAtHostTick,
        };
        this.helper.Multiplayer.SendMessage(dto, MultiplayerProtocol.MessageTypes.PresentationEvent, new[] { MultiplayerProtocol.ModId });
    }

    private void ClearProjectionState()
    {
        this.projection.Clear();
        this.lastPresentations.Clear();
        this.authoritativePresentations.Clear();
        this.presentationRevisions.Clear();
        this.snapshotVersion = 0;
        this.presentationSequence = 0;
        this.currentTick = 0;
        this.lastSnapshotTick = 0;
        this.lastSnapshotRequestTick = null;
        this.lastUnknownQueryTick = 0;
    }

    private static bool SamePresentation(CompanionPresentationDto left, CompanionPresentationDto right) =>
        SamePresentationTimeline(left, right)
        && left.Frame == right.Frame;

    private static bool SamePresentationTimeline(CompanionPresentationDto left, CompanionPresentationDto right) =>
        left.OperationId == right.OperationId
        && left.Kind == right.Kind
        && left.Phase == right.Phase
        && left.ToolId == right.ToolId
        && left.Facing == right.Facing;

    private ulong NextPresentationRevision(CompanionIdentity identity)
    {
        ulong next = this.presentationRevisions.TryGetValue(identity, out ulong previous) && previous < ulong.MaxValue ? previous + 1 : 1;
        this.presentationRevisions[identity] = next;
        return next;
    }

    private static CompanionPresentationDto ClonePresentation(CompanionPresentationDto source) => new()
    {
        Revision = source.Revision,
        OperationId = source.OperationId,
        Kind = source.Kind,
        Phase = source.Phase,
        ToolId = source.ToolId,
        Facing = source.Facing,
        Frame = source.Frame,
        RemainingTicks = source.RemainingTicks,
        StartedAtHostTick = source.StartedAtHostTick,
        EndsAtHostTick = source.EndsAtHostTick,
    };

    private void NotifySpeechSnapshot(CompanionSnapshotDto companion)
    {
        try
        {
            this.speechSnapshotObserver?.Invoke(companion);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"HY-NET-SPEECH-SNAPSHOT-OBSERVER-FAILED: {companion.Identity} {ex.GetType().Name}.", LogLevel.Warn);
        }
    }

    private void RememberUnknownOperation(CommandRequestDto dto)
    {
        if (!IsRecoverableOperation(dto.Command))
            return;
        string operationId = dto.Fields.TryGetValue("operationId", out string? supplied) ? supplied : $"r11-{dto.RequestId}";
        if (operationId.Length is > 0 and <= 128)
            this.unknownOperations[operationId] = new UnknownOutboundOperation(new CompanionIdentity(dto.OwnerId, dto.Slot), operationId, dto.Command);
    }

    private void QueryUnknownOperations()
    {
        if (!this.IsSessionReady || Context.IsMainPlayer)
            return;
        this.lastUnknownQueryTick = this.currentTick;
        foreach (UnknownOutboundOperation unknown in this.unknownOperations.Values.ToArray())
        {
            if (this.pendingOutbound.Count >= PendingRequestCapacity)
                break;
            this.Submit(unknown.Identity, "operation-status", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["operationId"] = unknown.OperationId,
            });
        }
    }

    private static bool IsRecoverableOperation(string command) => command is
        "water" or "chop" or "mine" or "harvest" or "forage" or "mow" or "dig" or "fish" or "fight" or "care" or "craft-start";

    private static bool IsDefinitiveOperationStatus(string code) => code is not
        ("HOST-LIFECYCLE-GATE" or "OWNER-OFFLINE" or "PLAYER-BUSY" or "SAVE-DATA-READ-ONLY" or "COMMAND-ROUTE-FAILED" or "NETWORK-SESSION-NOT-READY");

    private readonly record struct RequestCacheKey(long SenderPlayerId, string RequestId);

    private sealed class PendingOutboundRequest
    {
        public PendingOutboundRequest(CommandRequestDto dto, ulong sentTick) { this.Dto = dto; this.FirstSentTick = sentTick; this.LastSentTick = sentTick; }
        public CommandRequestDto Dto { get; }
        public ulong FirstSentTick { get; }
        public ulong LastSentTick { get; set; }
        public int Attempts { get; set; } = 1;
        public bool IsDeferred { get; set; }
    }

    private sealed record UnknownOutboundOperation(CompanionIdentity Identity, string OperationId, string Command);
}
