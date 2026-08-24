using System.Text.Json;
using Praxis.Link.Bluetooth;
using Praxis.Core.Bluetooth;
using Praxis.Core.Presentation;
using Praxis.Core.Protocol;
using Praxis.Core.State;

var tests = new List<(string Name, Action Run)>
{
    ("small message uses one frame", SmallMessageUsesOneFrame),
    ("large message round trips through fragments", LargeMessageRoundTrips),
    ("every frame respects negotiated size", FramesRespectMaximumSize),
    ("out-of-order frame is rejected", OutOfOrderFrameIsRejected),
    ("invalid frame does not poison the next message", InvalidFrameDoesNotPoisonNextMessage),
    ("oversized message is rejected", OversizedMessageIsRejected),
    ("protocol messages receive correct delivery metadata", ProtocolMessagesReceiveCorrectDeliveryMetadata),
    ("camel-case peer information is accepted", CamelCasePeerInformationIsAccepted)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

var schedulerTests = new List<(string Name, Func<Task> Run)>
{
    ("bulk icon pacing adapts to message size", BluetoothOutgoingSchedulerSelfTests.BulkWritePacingAdaptsToFrameCountAsync),
    ("bulk icon write mode reaches transport", BluetoothOutgoingSchedulerSelfTests.BulkWriteModeReachesTransportAsync),
    ("delivery waits for final frame", BluetoothOutgoingSchedulerSelfTests.DeliveryWaitsForLastFrameAsync),
    ("pending presentation states coalesce", BluetoothOutgoingSchedulerSelfTests.CoalescesPendingPresentationStateAsync),
    ("critical state skips normal backlog", BluetoothOutgoingSchedulerSelfTests.HighPrioritySkipsNormalBacklogAsync),
    ("command state precedes completion", BluetoothOutgoingSchedulerSelfTests.CommandStatePrecedesCompletionAsync),
    ("started fragmented message is not interrupted", BluetoothOutgoingSchedulerSelfTests.StartedMessageIsNotInterruptedAsync),
    ("disconnect completes all waiters", BluetoothOutgoingSchedulerSelfTests.DisconnectCompletesAllWaitersAsync),
    ("lifecycle stays responsive during large updates", BluetoothOutgoingSchedulerSelfTests.LifecycleMessagesStayResponsiveDuringLargeUpdatesAsync)
};
foreach (var test in schedulerTests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine();
var totalTests = tests.Count + schedulerTests.Count;
Console.WriteLine($"{totalTests - failures.Count}/{totalTests} checks passed.");
if (failures.Count > 0)
{
    Console.WriteLine(string.Join(Environment.NewLine, failures));
    Environment.ExitCode = 1;
}

static void SmallMessageUsesOneFrame()
{
    var message = "praxis"u8.ToArray();
    var frames = BluetoothMessageFraming.Encode(message, 64, 7);
    Assert(frames.Count == 1, "Expected one frame.");

    using var reassembler = new BluetoothMessageReassembler();
    var completed = reassembler.Accept(frames[0]);
    Assert(completed is not null && completed.SequenceEqual(message), "Single-frame payload changed.");
}

static void LargeMessageRoundTrips()
{
    var message = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
    var frames = BluetoothMessageFraming.Encode(message, 47, 19);
    Assert(frames.Count > 1, "Expected a fragmented message.");

    using var reassembler = new BluetoothMessageReassembler();
    byte[]? completed = null;
    foreach (var frame in frames)
    {
        var current = reassembler.Accept(frame);
        if (current is not null)
        {
            Assert(completed is null, "Message completed more than once.");
            completed = current;
        }
    }

    Assert(completed is not null && completed.SequenceEqual(message), "Fragmented payload changed.");
}

static void FramesRespectMaximumSize()
{
    const int maximumFrameBytes = 23;
    var frames = BluetoothMessageFraming.Encode(new byte[800], maximumFrameBytes, 31);
    Assert(frames.All(frame => frame.Length <= maximumFrameBytes), "A frame exceeded the negotiated limit.");
}

static void OutOfOrderFrameIsRejected()
{
    var frames = BluetoothMessageFraming.Encode(new byte[200], 32, 41);
    using var reassembler = new BluetoothMessageReassembler();
    AssertThrows<InvalidDataException>(() => reassembler.Accept(frames[1]));
}

static void InvalidFrameDoesNotPoisonNextMessage()
{
    using var reassembler = new BluetoothMessageReassembler();
    AssertThrows<InvalidDataException>(() => reassembler.Accept(new byte[BluetoothMessageFraming.HeaderBytes]));

    var expected = "recovered"u8.ToArray();
    var frame = BluetoothMessageFraming.Encode(expected, 64, 52).Single();
    var completed = reassembler.Accept(frame);
    Assert(completed is not null && completed.SequenceEqual(expected), "Reassembler did not recover after an invalid frame.");
}

static void OversizedMessageIsRejected()
{
    var message = new byte[BluetoothProtocol.MaximumMessageBytes + 1];
    AssertThrows<ArgumentOutOfRangeException>(() => BluetoothMessageFraming.Encode(message, 64, 61));
}

static void ProtocolMessagesReceiveCorrectDeliveryMetadata()
{
    var commandId = "presentation-command";
    var commandState = new PresentationPanelState(
        "ready", true, true, true, false, "Deck.pptx", 1, 3, "notes", 0, null, [], 8, commandId);
    var commandEntry = new StateEntry(
        "presentation.state",
        JsonSerializer.SerializeToElement(commandState, JsonDefaults.Options),
        "ready",
        "PowerPoint",
        DateTimeOffset.UtcNow);
    var commandMetadata = BluetoothPanelMessageSink.Classify(
        ProtocolMessage.Create("state.changed", commandEntry));
    Assert(commandMetadata.Priority == BluetoothMessagePriority.High &&
        commandMetadata.CommandId == commandId && commandMetadata.CoalescingKey is null,
        "Command-associated presentation state was not classified as high priority.");

    var ordinaryEntry = commandEntry with
    {
        Value = JsonSerializer.SerializeToElement(commandState with { CommandId = null }, JsonDefaults.Options)
    };
    var ordinaryMetadata = BluetoothPanelMessageSink.Classify(
        ProtocolMessage.Create("state.changed", ordinaryEntry));
    Assert(ordinaryMetadata.Priority == BluetoothMessagePriority.Normal &&
        ordinaryMetadata.CoalescingKey == "state.changed:presentation.state",
        "Ordinary presentation state was not assigned its coalescing key.");

    var controlEntry = new StateEntry(
        "control:presentation-end",
        JsonSerializer.SerializeToElement("running", JsonDefaults.Options),
        "running",
        null,
        DateTimeOffset.UtcNow);
    var controlMetadata = BluetoothPanelMessageSink.Classify(
        ProtocolMessage.Create("state.changed", controlEntry));
    Assert(controlMetadata.Priority == BluetoothMessagePriority.Normal &&
        controlMetadata.CoalescingKey == "state.changed:control:presentation-end",
        "Control state was not classified as ordinary coalescible state.");

    foreach (var type in new[] { "action.completed", "action.failed", "protocol.error", "pong" })
    {
        var metadata = BluetoothPanelMessageSink.Classify(ProtocolMessage.Create(type, new { }, commandId));
        Assert(metadata.Priority == BluetoothMessagePriority.High,
            $"{type} was not classified as high priority.");
    }
}

static void CamelCasePeerInformationIsAccepted()
{
    var peer = BluetoothPeerInfoCodec.Decode(
        "{\"deviceId\":\"ipad-mini-2\",\"version\":\"1.2.0\",\"name\":\"Praxis iPad\"}"u8);
    Assert(peer.DeviceId == "ipad-mini-2", "camelCase deviceId was not decoded.");
    Assert(peer.Version == "1.2.0", "peer version was not decoded.");
    Assert(peer.Name == "Praxis iPad", "peer name was not decoded.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
