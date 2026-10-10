using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using YuiToIssho;

internal static class NetworkRegressionTests
{
    public static void Run()
    {
        foreach (int seconds in new[] { 5, 8, 9, 30, 60 })
            Require(Validate("combat-guard", seconds).IsSuccess, $"Guard should accept {seconds}s.");
        foreach (int seconds in new[] { 2, 4, 61 })
            Require(!Validate("combat-guard", seconds).IsSuccess, $"Guard should reject {seconds}s.");
        foreach (int seconds in new[] { 2, 8 })
            Require(Validate("vitals-rest", seconds).IsSuccess, $"Rest should accept {seconds}s.");
        foreach (int seconds in new[] { 1, 9, 30 })
            Require(!Validate("vitals-rest", seconds).IsSuccess, $"Rest should reject {seconds}s.");

        TimeoutRecovery();
        RejectInvalidMessageAsync(WebSocketMessageType.Binary, 1).GetAwaiter().GetResult();
        RejectInvalidMessageAsync(WebSocketMessageType.Text, 8193).GetAwaiter().GetResult();
        Console.WriteLine("Network regressions passed (duration contracts and real WebSocket rejection).");
    }

    private static ProtocolValidationResult Validate(string command, int seconds)
    {
        string epoch = Guid.NewGuid().ToString("N");
        var request = new CommandRequestDto
        {
            ProtocolVersion = MultiplayerProtocol.Version,
            SessionEpoch = epoch,
            RequestId = Guid.NewGuid().ToString("N"),
            OwnerId = 1,
            SenderPlayerId = 1,
            Slot = CompanionIdentity.CanonicalSlot,
            Sequence = 1,
            Command = command,
            Fields = new Dictionary<string, string> { ["seconds"] = seconds.ToString() },
        };
        if (command == "combat-guard")
            request.Fields["radius"] = "6";
        return MultiplayerRequestValidator.ValidateCommand(request, 1, epoch);
    }

    private static void TimeoutRecovery()
    {
        var projection = new CompanionProjectionCoordinator(null!, null!, null!, null!, null!, null!);
        var coordinator = new CompanionMultiplayerCoordinator(null!, null!, projection, null!);
        string requestId = Guid.NewGuid().ToString("N");
        var request = new CommandRequestDto
        {
            RequestId = requestId,
            OwnerId = 1,
            Slot = CompanionIdentity.CanonicalSlot,
            Command = "cursor-single",
        };
        Type type = typeof(CompanionMultiplayerCoordinator);
        type.GetMethod("RememberUnknownOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object[] { request });
        var unknown = (System.Collections.IDictionary)type.GetField("unknownOperations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        Require(unknown.Contains($"cursor-{requestId}"), "Cursor recovery must query the exact operation ID used by the host.");

        CommandReceiptObservation? observed = null;
        coordinator.AttachReceiptObserver(receipt => observed = receipt);
        type.GetMethod("NotifyRequestTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object[] { request, "RECEIPT-TIMEOUT" });
        Require(observed is CommandReceiptObservation receipt
            && receipt.RequestId == requestId
            && receipt.Command == "cursor-single"
            && receipt.Result.Code == "RECEIPT-TIMEOUT"
            && !receipt.Result.IsSuccess,
            "Receipt timeout must notify the originating UI with its request identity.");
    }

    private static async Task RejectInvalidMessageAsync(WebSocketMessageType messageType, int payloadSize)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var clientTransport = new TcpClient();
            Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
            await clientTransport.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using TcpClient serverTransport = await accepting;
            using WebSocket client = WebSocket.CreateFromStream(clientTransport.GetStream(), false, null, TimeSpan.Zero);
            using WebSocket server = WebSocket.CreateFromStream(serverTransport.GetStream(), true, null, TimeSpan.Zero);
            MethodInfo receive = typeof(NekoAgentBridge).GetMethod("ReceiveMessageAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var receiving = (Task<byte[]?>)receive.Invoke(null, new object[] { server, cancellation.Token })!;
            await client.SendAsync(new ArraySegment<byte>(new byte[payloadSize]), messageType, true, cancellation.Token);
            // Deliberately do not receive or acknowledge the server's close frame.
            // Rejection must return without waiting for a cooperative peer.
            byte[]? result = await receiving.WaitAsync(TimeSpan.FromSeconds(2));
            Require(result is null, $"Invalid {messageType} message should be rejected.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
