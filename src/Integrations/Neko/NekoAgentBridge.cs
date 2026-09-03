using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;

namespace YuiToIssho;

internal sealed class NekoAgentBridge
{
    private const int ProtocolVersion = 2;
    private const string GatewayPath = "/yuitoissho/agent/v2";
    private const int MaximumHandshakeBytes = 8 * 1024;
    private const int MaximumRequestBytes = 8 * 1024;
    private const int MaximumResponseBytes = 32 * 1024;
    private const int MaximumPendingRequests = 32;
    private const int MaximumConcurrentConnections = 8;
    private const int MaximumRequestsPerTick = 4;
    private const int ReceiptCapacity = 64;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly IMonitor monitor;
    private readonly string token;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, NetworkCommandResult> executeAction;
    private readonly Func<bool, NekoBridgeState> captureState;
    private readonly ConcurrentQueue<PendingRequest> pending = new();
    private readonly SemaphoreSlim connectionSlots = new(MaximumConcurrentConnections, MaximumConcurrentConnections);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<string, NekoBridgeResponse> receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> receiptOrder = new();
    private TcpListener? listener;
    private int pendingCount;
    private int stopped;

    public NekoAgentBridge(
        IMonitor monitor,
        string token,
        Func<string, string, IReadOnlyDictionary<string, string>, NetworkCommandResult> executeAction,
        Func<bool, NekoBridgeState> captureState)
    {
        this.monitor = monitor;
        this.token = token;
        this.executeAction = executeAction;
        this.captureState = captureState;
        AppDomain.CurrentDomain.ProcessExit += this.OnProcessExit;
    }

    public string Endpoint { get; private set; } = string.Empty;

    public bool Start()
    {
        if (this.listener is not null)
            return true;
        try
        {
            this.listener = new TcpListener(IPAddress.Loopback, 0);
            this.listener.Start(MaximumConcurrentConnections);
            int port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
            this.Endpoint = $"ws://127.0.0.1:{port}{GatewayPath}";
            _ = Task.Run(() => this.AcceptLoopAsync(this.shutdown.Token));
            this.monitor.Log($"Yui to Issho! Agent Gateway is listening at {this.Endpoint}.", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            this.listener = null;
            this.monitor.Log($"HY-NEKO-BRIDGE-START: Bridge remained disabled after {ex.GetType().Name}.", LogLevel.Error);
            return false;
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref this.stopped, 1) != 0)
            return;
        AppDomain.CurrentDomain.ProcessExit -= this.OnProcessExit;
        this.shutdown.Cancel();
        this.listener?.Stop();
        this.listener = null;
        this.Endpoint = string.Empty;
        while (this.pending.TryDequeue(out PendingRequest? request))
        {
            Interlocked.Decrement(ref this.pendingCount);
            request.Cancel();
            request.Completion.TrySetCanceled();
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => this.Stop();

    public void ProcessPending()
    {
        for (int processed = 0; processed < MaximumRequestsPerTick && this.pending.TryDequeue(out PendingRequest? pendingRequest); processed++)
        {
            Interlocked.Decrement(ref this.pendingCount);
            if (pendingRequest.IsCancelled)
                continue;
            NekoBridgeRequest request = pendingRequest.Request;
            if (!this.receipts.TryGetValue(request.RequestId, out NekoBridgeResponse? response))
            {
                if (request.Action is "status" or "view")
                    response = request.Arguments.Count == 0
                        ? NekoBridgeResponse.Create(
                            request.RequestId,
                            true,
                            request.Action == "view" ? "VIEW" : "STATUS",
                            request.Action == "view" ? "Current bounded Yui to Issho! world view." : "Current Yui to Issho! bridge state.",
                            this.captureState(request.Action == "view"))
                        : NekoBridgeResponse.Create(request.RequestId, false, "FIELD-NOT-ALLOWED", $"{request.Action} does not accept arguments.", this.captureState(false));
                else
                {
                    NetworkCommandResult result = this.executeAction(request.RequestId, request.Action, request.Arguments);
                    response = NekoBridgeResponse.Create(request.RequestId, result.IsSuccess, result.Code, Bound(result.Message, 256), this.captureState(false), result.Planting, result.Combat);
                }
                this.CacheReceipt(request.RequestId, response);
                this.monitor.Log($"HY-NEKO-{response.Code}: request={request.RequestId[..8]} action={request.Action} ok={response.Ok}.", response.Ok ? LogLevel.Debug : LogLevel.Warn);
            }
            pendingRequest.Completion.TrySetResult(response);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && this.listener is not null)
        {
            try
            {
                TcpClient client = await this.listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                if (!this.connectionSlots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await this.ServeClientAsync(client, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        client.Dispose();
                        this.connectionSlots.Release();
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                this.monitor.Log($"HY-NEKO-BRIDGE-ACCEPT: Connection acceptance recovered after {ex.GetType().Name}.", LogLevel.Warn);
            }
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken shutdownToken)
    {
        try
        {
            client.NoDelay = true;
            using NetworkStream stream = client.GetStream();
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            handshakeTimeout.CancelAfter(IoTimeout);
            if (!await AcceptWebSocketUpgradeAsync(stream, handshakeTimeout.Token).ConfigureAwait(false))
                return;

            using WebSocket socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, KeepAliveInterval);
            while (socket.State == WebSocketState.Open && !shutdownToken.IsCancellationRequested)
            {
                byte[]? message;
                using (var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken))
                {
                    idleTimeout.CancelAfter(IdleTimeout);
                    message = await ReceiveMessageAsync(socket, idleTimeout.Token).ConfigureAwait(false);
                }
                if (message is null)
                    return;
                await this.HandleRequestAsync(socket, message, shutdownToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.monitor.Log($"HY-NEKO-BRIDGE-CLIENT: Connection closed after {ex.GetType().Name}.", LogLevel.Debug);
        }
    }

    private async Task HandleRequestAsync(WebSocket socket, byte[] message, CancellationToken shutdownToken)
    {
        NekoBridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<NekoBridgeRequest>(StrictUtf8.GetString(message), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            await SendResponseAsync(socket, NekoBridgeResponse.Failure(string.Empty, "INVALID-JSON", "The request is not valid bounded UTF-8 JSON."), shutdownToken).ConfigureAwait(false);
            return;
        }

        string validationError = ValidateEnvelope(request);
        if (validationError.Length > 0)
        {
            await SendResponseAsync(socket, NekoBridgeResponse.Failure(request?.RequestId ?? string.Empty, "INVALID-REQUEST", validationError), shutdownToken).ConfigureAwait(false);
            return;
        }
        if (!TokensMatch(this.token, request!.Token))
        {
            await SendResponseAsync(socket, NekoBridgeResponse.Failure(request.RequestId, "AUTH-FAILED", "Gateway authentication failed."), shutdownToken).ConfigureAwait(false);
            return;
        }
        if (Interlocked.Increment(ref this.pendingCount) > MaximumPendingRequests)
        {
            Interlocked.Decrement(ref this.pendingCount);
            await SendResponseAsync(socket, NekoBridgeResponse.Failure(request.RequestId, "BRIDGE-BUSY", "The bounded gateway queue is full."), shutdownToken).ConfigureAwait(false);
            return;
        }

        var pendingRequest = new PendingRequest(request);
        this.pending.Enqueue(pendingRequest);
        try
        {
            NekoBridgeResponse response = await pendingRequest.Completion.Task.WaitAsync(IoTimeout, shutdownToken).ConfigureAwait(false);
            await SendResponseAsync(socket, response, shutdownToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            pendingRequest.Cancel();
            await SendResponseAsync(socket, NekoBridgeResponse.Failure(request.RequestId, "BRIDGE-TIMEOUT", "The request expired before main-thread execution."), shutdownToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> AcceptWebSocketUpgradeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] requestBytes = await ReadHttpHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        string request = StrictUtf8.GetString(requestBytes);
        string[] lines = request.Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 2 || !string.Equals(lines[0], $"GET {GatewayPath} HTTP/1.1", StringComparison.Ordinal))
        {
            await WriteHttpErrorAsync(stream, "404 Not Found", cancellationToken).ConfigureAwait(false);
            return false;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0)
                break;
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                await WriteHttpErrorAsync(stream, "400 Bad Request", cancellationToken).ConfigureAwait(false);
                return false;
            }
            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? existing) ? $"{existing},{value}" : value;
        }

        headers.TryGetValue("Sec-WebSocket-Key", out string? key);
        bool valid = headers.TryGetValue("Upgrade", out string? upgrade)
            && string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
            && headers.TryGetValue("Connection", out string? connection)
            && connection.Split(',').Any(value => string.Equals(value.Trim(), "upgrade", StringComparison.OrdinalIgnoreCase))
            && headers.TryGetValue("Sec-WebSocket-Version", out string? version)
            && version == "13"
            && key is not null
            && IsValidWebSocketKey(key);
        if (!valid)
        {
            await WriteHttpErrorAsync(stream, "400 Bad Request", cancellationToken).ConfigureAwait(false);
            return false;
        }

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key! + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n"
        );
        await stream.WriteAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<byte[]> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] single = new byte[1];
        int matched = 0;
        byte[] terminator = "\r\n\r\n"u8.ToArray();
        while (buffer.Length < MaximumHandshakeBytes)
        {
            int read = await stream.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            buffer.WriteByte(single[0]);
            matched = single[0] == terminator[matched] ? matched + 1 : single[0] == terminator[0] ? 1 : 0;
            if (matched == terminator.Length)
                return buffer.ToArray();
        }
        throw new InvalidDataException("WebSocket handshake exceeds the byte budget.");
    }

    private static bool IsValidWebSocketKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == 16;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task WriteHttpErrorAsync(NetworkStream stream, string status, CancellationToken cancellationToken)
    {
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        byte[] chunk = new byte[2048];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                await socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Text messages only", CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            if (message.Length + result.Count > MaximumRequestBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Request exceeds 8 KiB", CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            message.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
                return message.ToArray();
        }
    }

    private static async Task SendResponseAsync(WebSocket socket, NekoBridgeResponse response, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        if (payload.Length > MaximumResponseBytes)
            payload = JsonSerializer.SerializeToUtf8Bytes(NekoBridgeResponse.Failure(response.RequestId, "RESPONSE-TOO-LARGE", "The response exceeds the 32 KiB byte budget."), JsonOptions);
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateEnvelope(NekoBridgeRequest? request)
    {
        if (request is null || request.ProtocolVersion != ProtocolVersion)
            return $"protocol_version must be {ProtocolVersion}.";
        if (!Guid.TryParseExact(request.RequestId, "N", out _))
            return "request_id must be one compact GUID.";
        if (string.IsNullOrEmpty(request.Token) || request.Token.Length > 256)
            return "token is missing or oversized.";
        if (string.IsNullOrWhiteSpace(request.Action) || request.Action.Length > 32 || request.Action.Any(character => !char.IsLower(character) && character != '_'))
            return "action is empty or invalid.";
        if (request.Arguments is null || request.Arguments.Count > MultiplayerProtocol.MaxFieldCount)
            return "arguments is missing or oversized.";
        foreach ((string key, string value) in request.Arguments)
            if (string.IsNullOrWhiteSpace(key) || key.Length > MultiplayerProtocol.MaxFieldKeyLength || value is null || value.Length > MultiplayerProtocol.MaxFieldValueLength || value.Any(char.IsControl))
                return "an argument key or value is invalid.";
        return string.Empty;
    }

    private static bool TokensMatch(string expected, string actual)
    {
        byte[] left = Encoding.UTF8.GetBytes(expected);
        byte[] right = Encoding.UTF8.GetBytes(actual);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private void CacheReceipt(string requestId, NekoBridgeResponse response)
    {
        this.receipts[requestId] = response;
        this.receiptOrder.Enqueue(requestId);
        while (this.receiptOrder.Count > ReceiptCapacity)
            this.receipts.Remove(this.receiptOrder.Dequeue());
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private sealed class PendingRequest
    {
        private int cancelled;

        public PendingRequest(NekoBridgeRequest request)
        {
            this.Request = request;
        }

        public NekoBridgeRequest Request { get; }
        public TaskCompletionSource<NekoBridgeResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsCancelled => Volatile.Read(ref this.cancelled) != 0;
        public void Cancel() => Interlocked.Exchange(ref this.cancelled, 1);
    }
}

internal sealed class NekoBridgeRequest
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class NekoBridgeResponse
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; } = 2;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public NekoBridgeState? State { get; set; }

    [JsonPropertyName("planting")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlantingCommandPayload? Planting { get; set; }

    [JsonPropertyName("combat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CombatCommandPayload? Combat { get; set; }

    public static NekoBridgeResponse Create(string requestId, bool ok, string code, string message, NekoBridgeState? state, PlantingCommandPayload? planting = null, CombatCommandPayload? combat = null) => new()
    {
        RequestId = requestId,
        Ok = ok,
        Code = code,
        Message = message,
        State = state,
        Planting = planting,
        Combat = combat,
    };

    public static NekoBridgeResponse Failure(string requestId, string code, string message) => Create(requestId, false, code, message, null);
}

internal sealed class NekoBridgeState
{
    [JsonPropertyName("world_ready")]
    public bool WorldReady { get; init; }

    [JsonPropertyName("host_authoritative")]
    public bool HostAuthoritative { get; init; }

    [JsonPropertyName("companion_exists")]
    public bool CompanionExists { get; init; }

    [JsonPropertyName("body_present")]
    public bool BodyPresent { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    [JsonPropertyName("tile_x")]
    public int TileX { get; init; }

    [JsonPropertyName("tile_y")]
    public int TileY { get; init; }

    [JsonPropertyName("behavior")]
    public string Behavior { get; init; } = string.Empty;

    [JsonPropertyName("brain_phase")]
    public string BrainPhase { get; init; } = string.Empty;

    [JsonPropertyName("work_kind")]
    public string WorkKind { get; init; } = string.Empty;

    [JsonPropertyName("work_state")]
    public string WorkState { get; init; } = string.Empty;

    [JsonPropertyName("assist_enabled")]
    public bool AssistEnabled { get; init; }

    [JsonPropertyName("assist_kind")]
    public string AssistKind { get; init; } = string.Empty;

    [JsonPropertyName("assist_state")]
    public string AssistState { get; init; } = string.Empty;

    [JsonPropertyName("vital_state")]
    public string VitalState { get; init; } = string.Empty;

    [JsonPropertyName("health")]
    public int Health { get; init; }

    [JsonPropertyName("max_health")]
    public int MaxHealth { get; init; }

    [JsonPropertyName("stamina")]
    public float Stamina { get; init; }

    [JsonPropertyName("max_stamina")]
    public float MaxStamina { get; init; }

    [JsonPropertyName("stamina_ratio")]
    public float StaminaRatio { get; init; }

    [JsonPropertyName("fatigue_level")]
    public string FatigueLevel { get; init; } = string.Empty;

    [JsonPropertyName("recovery_day")]
    public int RecoveryDay { get; init; } = -1;

    [JsonPropertyName("recovery_reason")]
    public string RecoveryReason { get; init; } = string.Empty;

    [JsonPropertyName("nearby")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<NekoBridgeTargetGroup>? Nearby { get; init; }

    [JsonPropertyName("nearby_truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NearbyTruncated { get; init; }
}

internal sealed class NekoBridgeTargetGroup
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("subtype")]
    public string Subtype { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("suggested_work_kind")]
    public string SuggestedWorkKind { get; init; } = string.Empty;

    [JsonPropertyName("disposition")]
    public string Disposition { get; init; } = string.Empty;

    [JsonPropertyName("reason_code")]
    public string ReasonCode { get; init; } = string.Empty;

    [JsonPropertyName("nearest")]
    public IReadOnlyList<NekoBridgeRelativeTarget> Nearest { get; init; } = Array.Empty<NekoBridgeRelativeTarget>();
}

internal sealed class NekoBridgeRelativeTarget
{
    [JsonPropertyName("direction")]
    public string Direction { get; init; } = string.Empty;

    [JsonPropertyName("distance")]
    public int Distance { get; init; }
}
