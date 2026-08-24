using System.Diagnostics;
using Praxis.Link.Bluetooth;

internal static class BluetoothOutgoingSchedulerSelfTests
{
    public static Task BulkWritePacingAdaptsToFrameCountAsync()
    {
        Assert(BluetoothFramePacingPolicy.DelayAfterFrame(
                BluetoothWriteMode.WithResponse,
                60,
                0) == TimeSpan.Zero,
            "Reliable GATT writes received redundant pacing.");
        Assert(BluetoothFramePacingPolicy.DelayAfterFrame(
                BluetoothWriteMode.WithoutResponse,
                8,
                0) == TimeSpan.Zero,
            "Small bulk messages should not be delayed.");
        var mediumDelay = BluetoothFramePacingPolicy.DelayAfterFrame(
            BluetoothWriteMode.WithoutResponse,
            16,
            0);
        var largeDelay = BluetoothFramePacingPolicy.DelayAfterFrame(
            BluetoothWriteMode.WithoutResponse,
            51,
            0);
        Assert(mediumDelay > TimeSpan.Zero && largeDelay > mediumDelay,
            "Bulk write pacing did not increase for large fragmented messages.");
        Assert(BluetoothFramePacingPolicy.DelayAfterFrame(
                BluetoothWriteMode.WithoutResponse,
                51,
                50) == TimeSpan.Zero,
            "The final frame should not add an unnecessary delivery delay.");
        return Task.CompletedTask;
    }

    public static async Task BulkWriteModeReachesTransportAsync()
    {
        using var scheduler = new BluetoothOutgoingScheduler(4);
        var delivery = scheduler.EnqueueAsync(
            new byte[200],
            new BluetoothOutgoingMetadata(
                "app.icon",
                WriteMode: BluetoothWriteMode.WithoutResponse)).AsTask();
        var observed = new List<BluetoothWriteMode>();
        var report = await scheduler.SendNextAsync(
            40,
            33,
            (_, writeMode, _) =>
            {
                observed.Add(writeMode);
                return Task.CompletedTask;
            });
        await delivery;

        Assert(observed.Count == report.FrameCount &&
               observed.All(mode => mode == BluetoothWriteMode.WithoutResponse),
            "Bulk icon frames did not retain their write mode.");
        Assert(report.WriteMode == BluetoothWriteMode.WithoutResponse,
            "The send report lost the bulk write mode.");
    }

    public static async Task DeliveryWaitsForLastFrameAsync()
    {
        using var scheduler = new BluetoothOutgoingScheduler(16);
        var payload = Enumerable.Range(0, 400).Select(index => (byte)(index % 251)).ToArray();
        const int maximumFrameBytes = 40;
        var expectedFrames = BluetoothMessageFraming.Encode(payload, maximumFrameBytes, 1).Count;
        var lastFrameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLastFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writtenFrames = 0;

        var delivery = scheduler.EnqueueAsync(payload, new BluetoothOutgoingMetadata("large.state")).AsTask();
        var sending = scheduler.SendNextAsync(
            maximumFrameBytes,
            1,
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref writtenFrames) == expectedFrames)
                {
                    lastFrameStarted.TrySetResult();
                    await releaseLastFrame.Task.WaitAsync(cancellationToken);
                }
            });

        await lastFrameStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(!delivery.IsCompleted, "SendAsync completed before the final GATT frame completed.");
        releaseLastFrame.TrySetResult();
        var result = await delivery;
        var report = await sending;

        Assert(result.Status == BluetoothDeliveryStatus.Delivered, "Message was not marked delivered.");
        Assert(result.FrameCount == expectedFrames && report.FrameCount == expectedFrames,
            "Delivered frame count is incorrect.");
    }

    public static async Task CoalescesPendingPresentationStateAsync()
    {
        using var scheduler = new BluetoothOutgoingScheduler(8);
        var metadata = new BluetoothOutgoingMetadata(
            "state.changed",
            CoalescingKey: "state.changed:presentation.state");
        var first = scheduler.EnqueueAsync("first"u8.ToArray(), metadata).AsTask();
        var second = scheduler.EnqueueAsync("second"u8.ToArray(), metadata).AsTask();
        var third = scheduler.EnqueueAsync("latest"u8.ToArray(), metadata).AsTask();

        Assert((await first).Status == BluetoothDeliveryStatus.Superseded,
            "First presentation state was not superseded.");
        Assert((await second).Status == BluetoothDeliveryStatus.Superseded,
            "Second presentation state was not superseded.");
        Assert(scheduler.QueueDepth == 1, "Coalesced presentation states used more than one queue slot.");

        var received = new List<byte[]>();
        await scheduler.SendNextAsync(64, 2, (frame, _) =>
        {
            received.Add(frame.ToArray());
            return Task.CompletedTask;
        });
        Assert((await third).Status == BluetoothDeliveryStatus.Delivered,
            "Latest presentation state was not delivered.");
        Assert(Reassemble(received).SequenceEqual("latest"u8.ToArray()),
            "The coalesced queue did not retain the latest payload.");
    }

    public static async Task HighPrioritySkipsNormalBacklogAsync()
    {
        using var scheduler = new BluetoothOutgoingScheduler(16);
        var normalOne = scheduler.EnqueueAsync(
            "normal-one"u8.ToArray(),
            new BluetoothOutgoingMetadata("state.changed", CoalescingKey: "state.changed:one")).AsTask();
        var normalTwo = scheduler.EnqueueAsync(
            "normal-two"u8.ToArray(),
            new BluetoothOutgoingMetadata("state.changed", CoalescingKey: "state.changed:two")).AsTask();
        var critical = scheduler.EnqueueAsync(
            "critical"u8.ToArray(),
            new BluetoothOutgoingMetadata("state.changed", BluetoothMessagePriority.High, CommandId: "command-1")).AsTask();

        var report = await scheduler.SendNextAsync(64, 3, (_, _) => Task.CompletedTask);
        Assert(report.MessageType == "state.changed" && report.Priority == BluetoothMessagePriority.High,
            "Critical state did not skip normal state backlog.");
        Assert((await critical).Status == BluetoothDeliveryStatus.Delivered,
            "Critical state was not delivered.");
        Assert(!normalOne.IsCompleted && !normalTwo.IsCompleted,
            "Normal backlog was unexpectedly sent before the critical state.");
        scheduler.CancelPending();
        await IgnoreCancellationAsync(normalOne);
        await IgnoreCancellationAsync(normalTwo);
    }

    public static async Task CommandStatePrecedesCompletionAsync()
    {
        using var scheduler = new BluetoothOutgoingScheduler(8);
        var state = scheduler.EnqueueAsync(
            "state"u8.ToArray(),
            new BluetoothOutgoingMetadata(
                "state.changed",
                BluetoothMessagePriority.High,
                CommandId: "same-command")).AsTask();
        var completed = scheduler.EnqueueAsync(
            "completed"u8.ToArray(),
            new BluetoothOutgoingMetadata(
                "action.completed",
                BluetoothMessagePriority.High,
                CommandId: "same-command")).AsTask();

        var first = await scheduler.SendNextAsync(64, 4, (_, _) => Task.CompletedTask);
        var second = await scheduler.SendNextAsync(64, 5, (_, _) => Task.CompletedTask);
        await Task.WhenAll(state, completed);
        Assert(first.MessageType == "state.changed" && second.MessageType == "action.completed",
            "Associated presentation state was not delivered before action.completed.");
    }

    public static async Task StartedMessageIsNotInterruptedAsync()
    {
        using var scheduler = new BluetoothOutgoingScheduler(8);
        var payload = new byte[600];
        var firstFrameWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameWrites = 0;
        var normal = scheduler.EnqueueAsync(
            payload,
            new BluetoothOutgoingMetadata("large.normal", CoalescingKey: "state.changed:presentation.state")).AsTask();
        var normalSend = scheduler.SendNextAsync(
            40,
            6,
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref frameWrites) == 1)
                {
                    firstFrameWritten.TrySetResult();
                    await continueFrames.Task.WaitAsync(cancellationToken);
                }
            });

        await firstFrameWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var critical = scheduler.EnqueueAsync(
            "critical"u8.ToArray(),
            new BluetoothOutgoingMetadata("action.failed", BluetoothMessagePriority.High, CommandId: "command-2")).AsTask();
        Assert(!critical.IsCompleted, "Critical message interrupted an already-started fragmented message.");
        continueFrames.TrySetResult();
        var normalReport = await normalSend;
        await normal;
        Assert(normalReport.FrameCount > 1 && frameWrites == normalReport.FrameCount,
            "Started fragmented message did not finish all frames.");

        var criticalReport = await scheduler.SendNextAsync(64, 7, (_, _) => Task.CompletedTask);
        await critical;
        Assert(criticalReport.MessageType == "action.failed", "Critical message was not sent after the active message.");
    }

    public static async Task DisconnectCompletesAllWaitersAsync()
    {
        using var scheduler = new BluetoothOutgoingScheduler(8);
        var first = scheduler.EnqueueAsync(
            "one"u8.ToArray(),
            new BluetoothOutgoingMetadata("state.changed", CoalescingKey: "state.changed:one")).AsTask();
        var second = scheduler.EnqueueAsync(
            "two"u8.ToArray(),
            new BluetoothOutgoingMetadata("action.completed", BluetoothMessagePriority.High)).AsTask();

        scheduler.Suspend(new IOException("Disconnected for test."));
        await AssertThrowsAsync<IOException>(() => first);
        await AssertThrowsAsync<IOException>(() => second);
        Assert(first.IsCompleted && second.IsCompleted && scheduler.QueueDepth == 0,
            "Disconnect left outgoing waiters or queue entries pending.");

        scheduler.Resume();
        using var sessionCancellation = new CancellationTokenSource();
        var frameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = scheduler.EnqueueAsync(
            new byte[200],
            new BluetoothOutgoingMetadata("active.message")).AsTask();
        var activeSend = scheduler.SendNextAsync(
            40,
            10,
            async (_, cancellationToken) =>
            {
                frameStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            sessionCancellation.Token);
        await frameStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        sessionCancellation.Cancel();
        scheduler.Suspend(new IOException("Disconnected during active write."));
        await AssertThrowsAsync<OperationCanceledException>(() => active);
        await AssertThrowsAsync<OperationCanceledException>(() => activeSend);
        Assert(active.IsCompleted, "Disconnect left the active message waiter pending.");
    }

    public static async Task LifecycleMessagesStayResponsiveDuringLargeUpdatesAsync()
    {
        using var scheduler = new BluetoothOutgoingScheduler(128);
        var ordinary = new List<Task<BluetoothDeliveryResult>>();
        var largePayload = new byte[12_000];
        for (var index = 0; index < 100; index += 1)
        {
            largePayload[0] = (byte)index;
            ordinary.Add(scheduler.EnqueueAsync(
                largePayload,
                new BluetoothOutgoingMetadata(
                    "state.changed",
                    CoalescingKey: "state.changed:presentation.state")).AsTask());
        }

        Assert(scheduler.QueueDepth == 1, "Large periodic presentation states accumulated instead of coalescing.");
        var stopwatch = Stopwatch.StartNew();
        var state = scheduler.EnqueueAsync(
            "command-state"u8.ToArray(),
            new BluetoothOutgoingMetadata(
                "state.changed",
                BluetoothMessagePriority.High,
                CommandId: "lifecycle-command")).AsTask();
        var stateReport = await scheduler.SendNextAsync(40, 8, (_, _) => Task.CompletedTask);
        await state;
        var completed = scheduler.EnqueueAsync(
            "completed"u8.ToArray(),
            new BluetoothOutgoingMetadata(
                "action.completed",
                BluetoothMessagePriority.High,
                CommandId: "lifecycle-command")).AsTask();
        var completedReport = await scheduler.SendNextAsync(40, 9, (_, _) => Task.CompletedTask);
        await completed;
        stopwatch.Stop();

        Assert(stateReport.MessageType == "state.changed" && completedReport.MessageType == "action.completed",
            "Lifecycle delivery order changed under large state updates.");
        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            "Lifecycle messages were delayed by coalescible large-state backlog.");
        Assert(scheduler.QueueDepth == 1, "Ordinary state backlog grew while lifecycle messages were delivered.");
        scheduler.CancelPending();
        foreach (var task in ordinary)
        {
            if (!task.IsCompletedSuccessfully)
                await IgnoreCancellationAsync(task);
        }
    }

    private static byte[] Reassemble(IEnumerable<byte[]> frames)
    {
        using var reassembler = new BluetoothMessageReassembler();
        byte[]? completed = null;
        foreach (var frame in frames)
            completed = reassembler.Accept(frame) ?? completed;
        return completed ?? throw new InvalidOperationException("Message did not complete.");
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
