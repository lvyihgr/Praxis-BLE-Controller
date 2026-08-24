using System.Diagnostics;

namespace Praxis.Link.Bluetooth;

public enum BluetoothMessagePriority
{
    Normal,
    High
}

public enum BluetoothDeliveryStatus
{
    Delivered,
    Superseded
}

public enum BluetoothWriteMode
{
    WithResponse,
    WithoutResponse
}

public static class BluetoothFramePacingPolicy
{
    private static readonly TimeSpan MediumMessageDelay = TimeSpan.FromMilliseconds(4);
    private static readonly TimeSpan LargeMessageDelay = TimeSpan.FromMilliseconds(12);

    public static TimeSpan DelayAfterFrame(
        BluetoothWriteMode writeMode,
        int frameCount,
        int completedFrameIndex)
    {
        if (writeMode != BluetoothWriteMode.WithoutResponse ||
            frameCount <= 1 ||
            completedFrameIndex < 0 ||
            completedFrameIndex >= frameCount - 1)
        {
            return TimeSpan.Zero;
        }

        return frameCount switch
        {
            <= 8 => TimeSpan.Zero,
            <= 24 => MediumMessageDelay,
            _ => LargeMessageDelay
        };
    }
}

public sealed record BluetoothOutgoingMetadata(
    string MessageType,
    BluetoothMessagePriority Priority = BluetoothMessagePriority.Normal,
    string? CoalescingKey = null,
    string? CommandId = null,
    BluetoothWriteMode WriteMode = BluetoothWriteMode.WithResponse);

public sealed record BluetoothDeliveryResult(
    BluetoothDeliveryStatus Status,
    TimeSpan Elapsed,
    int PayloadBytes,
    int FrameCount,
    int CoalescedMessages);

public sealed record BluetoothOutgoingQueueEvent(
    string MessageType,
    BluetoothMessagePriority Priority,
    int PayloadBytes,
    int QueueDepth,
    int CoalescedMessages);

public sealed record BluetoothOutgoingSendReport(
    string MessageType,
    BluetoothMessagePriority Priority,
    BluetoothWriteMode WriteMode,
    string? CommandId,
    int PayloadBytes,
    int FrameCount,
    int QueueDepth,
    int CoalescedMessages,
    TimeSpan Elapsed);

public sealed class BluetoothOutgoingScheduler : IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<OutgoingItem> _highPriority = new();
    private readonly LinkedList<OutgoingItem> _normalPriority = new();
    private readonly Dictionary<string, LinkedListNode<OutgoingItem>> _coalescible =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _available = new(0);
    private readonly SemaphoreSlim _slots;
    private bool _disposed;
    private Exception? _terminalError;
    private Exception? _suspensionError;
    private int _depth;

    public BluetoothOutgoingScheduler(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _slots = new SemaphoreSlim(capacity, capacity);
    }

    public event Action<BluetoothOutgoingQueueEvent>? ItemQueued;

    public int QueueDepth
    {
        get
        {
            lock (_sync)
                return _depth;
        }
    }

    public async ValueTask<BluetoothDeliveryResult> EnqueueAsync(
        ReadOnlyMemory<byte> payload,
        BluetoothOutgoingMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (payload.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(payload));

        var item = new OutgoingItem(payload.ToArray(), metadata);
        if (!TryReplaceCoalescible(item, out var queueEvent))
        {
            await _slots.WaitAsync(cancellationToken);
            try
            {
                if (TryReplaceCoalescible(item, out queueEvent))
                {
                    _slots.Release();
                }
                else
                {
                    lock (_sync)
                    {
                        ThrowIfUnavailableLocked();
                        EnqueueLocked(item);
                        queueEvent = CreateQueueEventLocked(item);
                    }
                    _available.Release();
                }
            }
            catch
            {
                _slots.Release();
                throw;
            }
        }

        ItemQueued?.Invoke(queueEvent!);
        return await item.Completion.Task.WaitAsync(cancellationToken);
    }

    public async Task<BluetoothOutgoingSendReport> SendNextAsync(
        int maximumFrameBytes,
        ushort messageId,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writeFrameAsync,
        CancellationToken cancellationToken = default) =>
        await SendNextAsync(
            maximumFrameBytes,
            messageId,
            (frame, _, frameCancellationToken) => writeFrameAsync(frame, frameCancellationToken),
            cancellationToken);

    public async Task<BluetoothOutgoingSendReport> SendNextAsync(
        int maximumFrameBytes,
        ushort messageId,
        Func<ReadOnlyMemory<byte>, BluetoothWriteMode, CancellationToken, Task> writeFrameAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeFrameAsync);
        var item = await DequeueAsync(cancellationToken);
        var frames = BluetoothMessageFraming.Encode(item.Payload, maximumFrameBytes, messageId);
        try
        {
            for (var index = 0; index < frames.Count; index += 1)
            {
                await writeFrameAsync(frames[index], item.Metadata.WriteMode, cancellationToken);
                var pacingDelay = BluetoothFramePacingPolicy.DelayAfterFrame(
                    item.Metadata.WriteMode,
                    frames.Count,
                    index);
                if (pacingDelay > TimeSpan.Zero)
                    await Task.Delay(pacingDelay, cancellationToken);
            }

            var elapsed = item.Stopwatch.Elapsed;
            item.Completion.TrySetResult(new BluetoothDeliveryResult(
                BluetoothDeliveryStatus.Delivered,
                elapsed,
                item.Payload.Length,
                frames.Count,
                item.CoalescedMessages));
            return new BluetoothOutgoingSendReport(
                item.Metadata.MessageType,
                item.Metadata.Priority,
                item.Metadata.WriteMode,
                item.Metadata.CommandId,
                item.Payload.Length,
                frames.Count,
                QueueDepth,
                item.CoalescedMessages,
                elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.Completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            item.Completion.TrySetException(exception);
            throw;
        }
    }

    public void FailPending(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<OutgoingItem> pending;
        lock (_sync)
            pending = DrainLocked();
        foreach (var item in pending)
            item.Completion.TrySetException(exception);
    }

    public void Suspend(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<OutgoingItem> pending;
        lock (_sync)
        {
            _suspensionError = exception;
            pending = DrainLocked();
        }
        foreach (var item in pending)
            item.Completion.TrySetException(exception);
    }

    public void Resume()
    {
        lock (_sync)
        {
            ThrowIfTerminalLocked();
            _suspensionError = null;
        }
    }

    public void CancelPending(CancellationToken cancellationToken = default)
    {
        List<OutgoingItem> pending;
        lock (_sync)
            pending = DrainLocked();
        foreach (var item in pending)
            item.Completion.TrySetCanceled(cancellationToken);
    }

    public void Complete(Exception? error = null)
    {
        List<OutgoingItem> pending;
        lock (_sync)
        {
            if (_terminalError is not null || _disposed)
                return;
            _terminalError = error ?? new ObjectDisposedException(nameof(BluetoothOutgoingScheduler));
            pending = DrainLocked();
        }
        foreach (var item in pending)
            item.Completion.TrySetException(_terminalError);
        _available.Release();
    }

    private bool TryReplaceCoalescible(
        OutgoingItem replacement,
        out BluetoothOutgoingQueueEvent? queueEvent)
    {
        OutgoingItem? superseded = null;
        lock (_sync)
        {
            ThrowIfUnavailableLocked();
            if (replacement.Metadata.Priority != BluetoothMessagePriority.Normal ||
                string.IsNullOrWhiteSpace(replacement.Metadata.CoalescingKey) ||
                !_coalescible.TryGetValue(replacement.Metadata.CoalescingKey, out var node))
            {
                queueEvent = null;
                return false;
            }

            superseded = node.Value;
            replacement.CoalescedMessages = superseded.CoalescedMessages + 1;
            node.Value = replacement;
            queueEvent = CreateQueueEventLocked(replacement);
        }

        superseded.Completion.TrySetResult(new BluetoothDeliveryResult(
            BluetoothDeliveryStatus.Superseded,
            superseded.Stopwatch.Elapsed,
            superseded.Payload.Length,
            0,
            superseded.CoalescedMessages + 1));
        return true;
    }

    private void EnqueueLocked(OutgoingItem item)
    {
        var queue = item.Metadata.Priority == BluetoothMessagePriority.High
            ? _highPriority
            : _normalPriority;
        var node = queue.AddLast(item);
        if (item.Metadata.Priority == BluetoothMessagePriority.Normal &&
            !string.IsNullOrWhiteSpace(item.Metadata.CoalescingKey))
        {
            _coalescible[item.Metadata.CoalescingKey] = node;
        }
        _depth += 1;
    }

    private async Task<OutgoingItem> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken);
            lock (_sync)
            {
                var node = _highPriority.First ?? _normalPriority.First;
                if (node is null)
                {
                    ThrowIfUnavailableLocked();
                    continue;
                }

                if (node.List == _highPriority)
                    _highPriority.Remove(node);
                else
                    _normalPriority.Remove(node);
                if (!string.IsNullOrWhiteSpace(node.Value.Metadata.CoalescingKey))
                    _coalescible.Remove(node.Value.Metadata.CoalescingKey);
                _depth -= 1;
                _slots.Release();
                return node.Value;
            }
        }
    }

    private List<OutgoingItem> DrainLocked()
    {
        var pending = _highPriority.Concat(_normalPriority).ToList();
        _highPriority.Clear();
        _normalPriority.Clear();
        _coalescible.Clear();
        var releasedSlots = _depth;
        _depth = 0;
        for (var index = 0; index < releasedSlots; index += 1)
            _slots.Release();
        return pending;
    }

    private BluetoothOutgoingQueueEvent CreateQueueEventLocked(OutgoingItem item) => new(
        item.Metadata.MessageType,
        item.Metadata.Priority,
        item.Payload.Length,
        _depth,
        item.CoalescedMessages);

    private void ThrowIfUnavailableLocked()
    {
        ThrowIfTerminalLocked();
        if (_suspensionError is not null)
            throw new InvalidOperationException("Praxis Bluetooth outgoing scheduler is disconnected.", _suspensionError);
    }

    private void ThrowIfTerminalLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_terminalError is not null)
            throw new InvalidOperationException("Praxis Bluetooth outgoing scheduler is stopped.", _terminalError);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Complete();
        _disposed = true;
        _available.Dispose();
        _slots.Dispose();
    }

    private sealed class OutgoingItem(byte[] payload, BluetoothOutgoingMetadata metadata)
    {
        public byte[] Payload { get; } = payload;
        public BluetoothOutgoingMetadata Metadata { get; } = metadata;
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
        public TaskCompletionSource<BluetoothDeliveryResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CoalescedMessages { get; set; }
    }
}
