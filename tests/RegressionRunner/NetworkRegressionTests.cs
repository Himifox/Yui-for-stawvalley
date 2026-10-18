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
        SignedOwnerRequests();
        CursorTimeoutOutcomes();
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

    private static void SignedOwnerRequests()
    {
        string epoch = Guid.NewGuid().ToString("N");
        foreach (long ownerId in new[] { -4899310317827733102L, -1L, long.MinValue, long.MaxValue })
        {
            var request = new CommandRequestDto
            {
                ProtocolVersion = MultiplayerProtocol.Version,
                SessionEpoch = epoch,
                RequestId = Guid.NewGuid().ToString("N"),
                SenderPlayerId = ownerId,
                OwnerId = ownerId,
                Slot = CompanionIdentity.CanonicalSlot,
                Sequence = 1,
                Command = "follow",
                Fields = new Dictionary<string, string>(),
            };
            Require(MultiplayerRequestValidator.ValidateCommand(request, ownerId, epoch).IsSuccess,
                "Signed nonzero farmhand IDs must match their exact transport identity.");
            Require(MultiplayerRequestValidator.ValidateCommand(request, 1, epoch).Code == "SENDER-MISMATCH",
                "Accepting negative IDs must not allow transport sender spoofing.");
            request.OwnerId = 1;
            Require(MultiplayerRequestValidator.ValidateCommand(request, ownerId, epoch).Code == "NOT-OWNER",
                "A negative-ID sender still may not control another owner's Yui.");
            request.OwnerId = 0;
            Require(MultiplayerRequestValidator.ValidateCommand(request, ownerId, epoch).Code == "NOT-OWNER",
                "An unassigned owner ID must remain invalid.");
            request.SenderPlayerId = 0;
            Require(MultiplayerRequestValidator.ValidateCommand(request, 0, epoch).Code == "SENDER-MISMATCH",
                "An unassigned sender ID must remain invalid.");
        }
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

    private static void CursorTimeoutOutcomes()
    {
        var cursor = new CommandCursorCoordinator(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        Type type = typeof(CommandCursorCoordinator);
        Type markerType = type.GetNestedType("CursorMarker", BindingFlags.NonPublic)!;
        object Marker(string shape) => Activator.CreateInstance(markerType, new object[]
        {
            CompanionIdentity.ForOwner(1), "Farm", new Microsoft.Xna.Framework.Point(1, 1),
            new Microsoft.Xna.Framework.Point(2, 2), WorkKinds.Water, shape, Guid.NewGuid().ToString("N"),
        })!;
        object? Read(object marker, string property) => markerType.GetProperty(property)!.GetValue(marker);
        void Apply(object marker, NetworkCommandResult result) =>
            type.GetMethod("ApplySubmissionResult", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(cursor, new object[] { marker, result, 42UL });

        foreach (string code in new[] { "RECEIPT-TIMEOUT", "DEFERRED-TIMEOUT" })
        {
            object area = Marker(WorkScopeShapes.Rectangle);
            Apply(area, NetworkCommandResult.Failure(code, "Host result unknown."));
            Require((string)Read(area, "Phase")! == "Unknown", "An unconfirmed area request must not imply completion or rejection.");
            Require((bool)Read(area, "IsTerminal")!, "Unknown area results must stop snapshot-based completion tracking.");
            Require(!(bool)Read(area, "ReceiptAccepted")!, "An area receipt timeout must not count as host acceptance.");
            Require((string)Read(area, "Summary")! == code, "The unknown marker should preserve the timeout reason.");

            object single = Marker(WorkScopeShapes.SingleTarget);
            Apply(single, NetworkCommandResult.Failure(code, "Host result unknown."));
            Require((string)Read(single, "Phase")! == "Paused" && !(bool)Read(single, "IsTerminal")!,
                "Single tasks must retain operation-status recovery after a receipt timeout.");
            Require((bool)Read(single, "ReceiptAccepted")!, "Single-task recovery must remain enabled.");
        }
        object accepted = Marker(WorkScopeShapes.Rectangle);
        Apply(accepted, NetworkCommandResult.Success("WORK-STARTED", "Accepted."));
        Require((string)Read(accepted, "Phase")! == "Accepted" && !(bool)Read(accepted, "IsTerminal")!,
            "An acknowledged area request must continue normal snapshot tracking.");
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
