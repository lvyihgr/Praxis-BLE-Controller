using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;
namespace Praxis.Link.Bluetooth;

public sealed class BluetoothCentralTransport : BackgroundService
{
    private readonly BluetoothCentralOptions _options;
    private readonly ILogger<BluetoothCentralTransport> _logger;
    private readonly BluetoothOutgoingScheduler _outgoing;
    private BluetoothConnectionSnapshot _snapshot = BluetoothConnectionSnapshot.Initial;
    private CancellationTokenSource? _activeConnectionCancellation;
    private int _nextMessageId;

    public BluetoothCentralTransport(
        BluetoothCentralOptions options,
        ILogger<BluetoothCentralTransport> logger)
    {
        _options = options;
        _logger = logger;
        _outgoing = new BluetoothOutgoingScheduler(options.OutgoingQueueCapacity);
        _outgoing.Suspend(new InvalidOperationException("Praxis Bluetooth transport is not connected."));
        _outgoing.ItemQueued += item => _logger.LogDebug(
            "Praxis Bluetooth message queued: type={MessageType}; priority={Priority}; bytes={Bytes}; depth={Depth}; coalesced={Coalesced}",
            item.MessageType,
            item.Priority,
            item.PayloadBytes,
            item.QueueDepth,
            item.CoalescedMessages);
    }

    public event Func<ReadOnlyMemory<byte>, CancellationToken, Task>? MessageReceived;
    public event Action<BluetoothConnectionSnapshot>? ConnectionChanged;

    public BluetoothConnectionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsReady => Snapshot.State == BluetoothCentralState.Ready;

    public void RequestReconnect() => Volatile.Read(ref _activeConnectionCancellation)?.Cancel();

    public async ValueTask<BluetoothDeliveryResult> SendAsync(
        ReadOnlyMemory<byte> message,
        BluetoothOutgoingMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (message.Length == 0 || message.Length > _options.MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"Bluetooth message must contain 1 to {_options.MaximumMessageBytes} bytes.");
        }

        if (!IsReady)
        {
            throw new InvalidOperationException("Praxis Bluetooth transport is not connected.");
        }

        return await _outgoing.EnqueueAsync(
            message,
            metadata ?? new BluetoothOutgoingMetadata("unknown"),
            cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Publish(BluetoothCentralState.Initializing);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var adapter = await BluetoothAdapter.GetDefaultAsync();
                if (adapter is null || !adapter.IsCentralRoleSupported)
                {
                    Publish(
                        BluetoothCentralState.Faulted,
                        error: "No Bluetooth adapter with Central role support is available.");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                var discovered = await DiscoverAsync(stoppingToken);
                if (discovered is null)
                {
                    Publish(BluetoothCentralState.Reconnecting, error: "No advertising Praxis Panel was found.");
                    await Task.Delay(_options.ReconnectDelay, stoppingToken);
                    continue;
                }

                await RunConnectionAsync(discovered, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Praxis Bluetooth connection cycle failed.");
                Publish(BluetoothCentralState.Reconnecting, error: exception.Message);
                await Task.Delay(_options.ReconnectDelay, stoppingToken);
            }
        }

        _outgoing.Complete(new OperationCanceledException("Praxis Bluetooth transport stopped."));
        Publish(BluetoothCentralState.Stopped);
    }

    private async Task<DiscoveredPeripheral?> DiscoverAsync(CancellationToken cancellationToken)
    {
        Publish(BluetoothCentralState.Scanning);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ScanTimeout);

        var result = new TaskCompletionSource<DiscoveredPeripheral?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> received = (_, eventArgs) =>
        {
            if (!eventArgs.IsConnectable ||
                !eventArgs.Advertisement.ServiceUuids.Contains(BluetoothProtocol.ServiceUuid))
            {
                return;
            }

            result.TrySetResult(new DiscoveredPeripheral(
                eventArgs.BluetoothAddress,
                eventArgs.BluetoothAddressType,
                eventArgs.Advertisement.LocalName,
                eventArgs.RawSignalStrengthInDBm));
        };

        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> stopped = (_, eventArgs) =>
        {
            if (eventArgs.Error != BluetoothError.Success)
            {
                result.TrySetException(new IOException($"Bluetooth scan stopped: {eventArgs.Error}."));
            }
        };

        watcher.Received += received;
        watcher.Stopped += stopped;
        using var registration = timeout.Token.Register(() => result.TrySetResult(null));
        watcher.Start();

        try
        {
            return await result.Task;
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= received;
            watcher.Stopped -= stopped;
        }
    }

    private async Task RunConnectionAsync(
        DiscoveredPeripheral discovered,
        CancellationToken stoppingToken)
    {
        var address = discovered.BluetoothAddress.ToString("X12");
        Publish(
            BluetoothCentralState.Connecting,
            discovered.LocalName,
            address,
            discovered.SignalStrengthDbm);

        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(
            discovered.BluetoothAddress,
            discovered.AddressType)
            ?? throw new IOException("Windows could not create the Praxis Panel Bluetooth device.");

        Publish(
            BluetoothCentralState.DiscoveringServices,
            device.Name,
            address,
            discovered.SignalStrengthDbm);

        var serviceResult = await device.GetGattServicesForUuidAsync(
            BluetoothProtocol.ServiceUuid,
            BluetoothCacheMode.Uncached);
        if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
        {
            DisposeServices(serviceResult.Services);
            throw new IOException($"Praxis service discovery failed: {serviceResult.Status}.");
        }

        using var service = serviceResult.Services[0];
        foreach (var unused in serviceResult.Services.Skip(1))
        {
            unused.Dispose();
        }

        var commandToCore = await GetCharacteristicAsync(
            service,
            BluetoothProtocol.CommandToCoreUuid,
            GattCharacteristicProperties.Notify,
            "Command to Core");
        var eventFromCore = await GetWritableCharacteristicAsync(service);
        var peerInfoCharacteristic = await GetCharacteristicAsync(
            service,
            BluetoothProtocol.PeerInfoUuid,
            GattCharacteristicProperties.Read,
            "Peer info");
        var peerInfo = await ReadPeerInfoAsync(peerInfoCharacteristic, stoppingToken);

        var session = service.Session;
        if (session.CanMaintainConnection)
        {
            session.MaintainConnection = true;
        }

        var incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        using var reassembler = new BluetoothMessageReassembler(_options.MaximumMessageBytes);
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Interlocked.Exchange(ref _activeConnectionCancellation, sessionCancellation)?.Cancel();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> valueChanged = (_, eventArgs) =>
        {
            try
            {
                var completed = reassembler.Accept(ReadBytes(eventArgs.CharacteristicValue));
                if (completed is not null)
                {
                    incoming.Writer.TryWrite(completed);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Discarding an invalid Praxis Bluetooth frame.");
            }
        };

        TypedEventHandler<BluetoothLEDevice, object> connectionChanged = (sender, _) =>
        {
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            {
                disconnected.TrySetResult();
            }
        };

        commandToCore.ValueChanged += valueChanged;
        device.ConnectionStatusChanged += connectionChanged;
        Task? sender = null;
        Task? receiver = null;

        try
        {
            var subscribeStatus = await commandToCore.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (subscribeStatus != GattCommunicationStatus.Success)
            {
                throw new IOException($"Praxis command subscription failed: {subscribeStatus}.");
            }

            _outgoing.Resume();
            var maximumPduSize = service.Session.MaxPduSize;
            var maximumFrameBytes = Math.Max(
                BluetoothMessageFraming.HeaderBytes + 1,
                maximumPduSize - 3);
            Publish(
                BluetoothCentralState.Ready,
                device.Name,
                address,
                discovered.SignalStrengthDbm,
                maximumPduSize,
                peerId: peerInfo.DeviceId,
                peerVersion: peerInfo.Version);
            sender = SendLoopAsync(eventFromCore, maximumFrameBytes, sessionCancellation.Token);
            receiver = DispatchIncomingAsync(incoming.Reader, sessionCancellation.Token);
            var stopped = Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            var completed = await Task.WhenAny(sender, receiver, disconnected.Task, stopped);

            if (completed == sender)
            {
                await sender;
            }

            if (completed == receiver)
            {
                await receiver;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeConnectionCancellation, null, sessionCancellation);
            sessionCancellation.Cancel();
            _outgoing.Suspend(new IOException("Praxis Panel disconnected before the message was delivered."));
            incoming.Writer.TryComplete();
            await ObserveSessionTaskAsync(sender);
            await ObserveSessionTaskAsync(receiver);
            commandToCore.ValueChanged -= valueChanged;
            device.ConnectionStatusChanged -= connectionChanged;
            try
            {
                await commandToCore.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
                // The remote device may already be gone.
            }

            if (session.CanMaintainConnection)
            {
                session.MaintainConnection = false;
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                Publish(
                    BluetoothCentralState.Reconnecting,
                    device.Name,
                    address,
                    discovered.SignalStrengthDbm,
                    error: "Praxis Panel disconnected.");
                await Task.Delay(_options.ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task SendLoopAsync(
        GattCharacteristic characteristic,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var report = await _outgoing.SendNextAsync(
                maximumFrameBytes,
                NextMessageId(),
                async (frame, writeMode, frameCancellationToken) =>
                {
                    var gattWriteOption = writeMode == BluetoothWriteMode.WithoutResponse &&
                                          characteristic.CharacteristicProperties.HasFlag(
                                              GattCharacteristicProperties.WriteWithoutResponse)
                        ? GattWriteOption.WriteWithoutResponse
                        : GattWriteOption.WriteWithResponse;
                    var result = await characteristic.WriteValueWithResultAsync(
                        ToBuffer(frame.ToArray()),
                        gattWriteOption)
                        .AsTask(frameCancellationToken);
                    if (result.Status != GattCommunicationStatus.Success)
                    {
                        throw new IOException(
                            $"Bluetooth write failed: {result.Status}; protocol={result.ProtocolError?.ToString() ?? "none"}.");
                    }
                },
                cancellationToken);
            _logger.LogInformation(
                "Praxis Bluetooth message delivered: type={MessageType}; priority={Priority}; writeMode={WriteMode}; elapsedMs={ElapsedMs}; bytes={Bytes}; frames={Frames}; depth={Depth}; coalesced={Coalesced}",
                report.MessageType,
                report.Priority,
                report.WriteMode,
                report.Elapsed.TotalMilliseconds,
                report.PayloadBytes,
                report.FrameCount,
                report.QueueDepth,
                report.CoalescedMessages);
        }
    }

    private async Task DispatchIncomingAsync(
        ChannelReader<byte[]> incoming,
        CancellationToken cancellationToken)
    {
        await foreach (var message in incoming.ReadAllAsync(cancellationToken))
        {
            var handlers = MessageReceived;
            if (handlers is null)
            {
                continue;
            }

            foreach (Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler in handlers.GetInvocationList())
            {
                await handler(message, cancellationToken);
            }
        }
    }

    private async Task ObserveSessionTaskAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Praxis Bluetooth session task stopped during disconnect cleanup.");
        }
    }

    private static async Task<GattCharacteristic> GetCharacteristicAsync(
        GattDeviceService service,
        Guid uuid,
        GattCharacteristicProperties requiredProperty,
        string label)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            throw new IOException($"{label} characteristic discovery failed: {result.Status}.");
        }

        var characteristic = result.Characteristics[0];
        if (!characteristic.CharacteristicProperties.HasFlag(requiredProperty))
        {
            throw new IOException($"{label} characteristic does not support {requiredProperty}.");
        }

        return characteristic;
    }

    private static async Task<BluetoothPeerInfo> ReadPeerInfoAsync(
        GattCharacteristic characteristic,
        CancellationToken cancellationToken)
    {
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException($"Praxis peer information could not be read: {result.Status}.");

        return BluetoothPeerInfoCodec.Decode(ReadBytes(result.Value));
    }

    private static async Task<GattCharacteristic> GetWritableCharacteristicAsync(GattDeviceService service)
    {
        var result = await service.GetCharacteristicsForUuidAsync(
            BluetoothProtocol.EventFromCoreUuid,
            BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            throw new IOException($"Event from Core characteristic discovery failed: {result.Status}.");
        }

        var characteristic = result.Characteristics[0];
        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write))
        {
            throw new IOException("Event from Core characteristic must support Write with response.");
        }

        return characteristic;
    }

    private ushort NextMessageId()
    {
        var next = unchecked((ushort)Interlocked.Increment(ref _nextMessageId));
        return next == 0
            ? unchecked((ushort)Interlocked.Increment(ref _nextMessageId))
            : next;
    }

    private void Publish(
        BluetoothCentralState state,
        string? deviceName = null,
        string? deviceAddress = null,
        short? signalStrengthDbm = null,
        int? maximumPduSize = null,
        string? error = null,
        string? peerId = null,
        string? peerVersion = null)
    {
        var snapshot = new BluetoothConnectionSnapshot(
            state,
            deviceName,
            deviceAddress,
            signalStrengthDbm,
            maximumPduSize,
            error,
            DateTimeOffset.UtcNow,
            peerId,
            peerVersion);
        Volatile.Write(ref _snapshot, snapshot);
        ConnectionChanged?.Invoke(snapshot);
        _logger.LogInformation(
            "Praxis Bluetooth state: {State}; device={Device}; address={Address}; mtu={Mtu}; error={Error}",
            state,
            deviceName,
            deviceAddress,
            maximumPduSize,
            error);
    }

    private static byte[] ReadBytes(IBuffer buffer)
    {
        var bytes = new byte[checked((int)buffer.Length)];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static IBuffer ToBuffer(byte[] bytes)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static void DisposeServices(IReadOnlyList<GattDeviceService> services)
    {
        foreach (var service in services)
        {
            service.Dispose();
        }
    }

    private sealed record DiscoveredPeripheral(
        ulong BluetoothAddress,
        BluetoothAddressType AddressType,
        string? LocalName,
        short SignalStrengthDbm);

    public override void Dispose()
    {
        _outgoing.Dispose();
        base.Dispose();
    }
}
