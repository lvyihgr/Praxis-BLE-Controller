using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Praxis.BluetoothCentralProbe;

internal static class Program
{
    private static readonly Guid ServiceUuid = Guid.Parse("7a51f000-5e8f-4c7a-9a1d-707261786973");
    private static readonly Guid CommandToCoreUuid = Guid.Parse("7a51f001-5e8f-4c7a-9a1d-707261786973");
    private static readonly Guid EventFromCoreUuid = Guid.Parse("7a51f002-5e8f-4c7a-9a1d-707261786973");
    private static readonly Guid PeerInfoUuid = Guid.Parse("7a51f003-5e8f-4c7a-9a1d-707261786973");

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = ProbeOptions.Parse(args);

        try
        {
            PrintHeader(options);

            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null)
            {
                return Fail(2, "No default Bluetooth adapter was found.");
            }

            Console.WriteLine($"adapter.address=0x{adapter.BluetoothAddress:X12}");
            Console.WriteLine($"adapter.central_role_supported={adapter.IsCentralRoleSupported}");
            Console.WriteLine($"adapter.peripheral_role_supported={adapter.IsPeripheralRoleSupported}");

            if (!adapter.IsCentralRoleSupported)
            {
                return Fail(3, "The default adapter does not support the Bluetooth LE central role.");
            }

            if (options.CapabilitiesOnly)
            {
                return Pass("BLE central role is supported by the default adapter.");
            }

            var discovered = await DiscoverPraxisPeripheralAsync(options);
            if (discovered is null)
            {
                return Fail(4, $"No Praxis peripheral was discovered within {options.ScanTimeout.TotalSeconds:0} seconds.");
            }

            Console.WriteLine($"scan.match.address=0x{discovered.BluetoothAddress:X12}");
            Console.WriteLine($"scan.match.address_type={discovered.AddressType}");
            Console.WriteLine($"scan.match.local_name={Escape(discovered.LocalName)}");
            Console.WriteLine($"scan.match.rssi_dbm={discovered.Rssi}");
            Console.WriteLine($"scan.match.connectable={discovered.IsConnectable}");
            Console.WriteLine($"scan.match.advertisement_type={discovered.AdvertisementType}");

            if (options.ScanOnly)
            {
                return Pass("A Praxis BLE peripheral advertisement was discovered.");
            }

            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(
                discovered.BluetoothAddress,
                discovered.AddressType);

            if (device is null)
            {
                return Fail(5, "Windows could not create a BluetoothLEDevice for the discovered iPad.");
            }

            Console.WriteLine($"device.name={Escape(device.Name)}");
            Console.WriteLine($"device.connection_status={device.ConnectionStatus}");

            var serviceResult = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
            Console.WriteLine($"gatt.service_query_status={serviceResult.Status}");
            Console.WriteLine($"gatt.service_count={serviceResult.Services.Count}");
            if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            {
                return Fail(6, $"Praxis GATT service discovery failed: {serviceResult.Status}.");
            }

            using var service = serviceResult.Services[0];
            var commandToCore = await GetRequiredCharacteristicAsync(service, CommandToCoreUuid, "command_to_core");
            var eventFromCore = await GetRequiredCharacteristicAsync(service, EventFromCoreUuid, "event_from_core");
            if (commandToCore is null || eventFromCore is null)
            {
                return Fail(7, "The iPad did not expose both required Praxis data channels.");
            }

            var peerInfo = await GetOptionalCharacteristicAsync(service, PeerInfoUuid, "peer_info");
            if (peerInfo is not null)
            {
                await ReadPeerInfoAsync(peerInfo);
            }

            if (options.ConnectOnly)
            {
                return Pass("The Praxis GATT service and required characteristics were discovered.");
            }

            if (!commandToCore.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                return Fail(8, "Command-to-Core characteristic does not support notifications.");
            }

            if (!eventFromCore.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) &&
                !eventFromCore.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
            {
                return Fail(9, "Event-from-Core characteristic does not support writes.");
            }

            var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> valueChanged = (_, eventArgs) =>
            {
                var bytes = ReadBytes(eventArgs.CharacteristicValue);
                Console.WriteLine($"gatt.command_to_core.notification_bytes={bytes.Length}");
                Console.WriteLine($"gatt.command_to_core.notification_utf8={Escape(Encoding.UTF8.GetString(bytes))}");
                Console.WriteLine($"gatt.command_to_core.notification_hex={Convert.ToHexString(bytes)}");
                reply.TrySetResult(bytes);
            };

            commandToCore.ValueChanged += valueChanged;
            try
            {
                var subscribeStatus = await commandToCore.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                Console.WriteLine($"gatt.command_to_core.subscribe_status={subscribeStatus}");
                if (subscribeStatus != GattCommunicationStatus.Success)
                {
                    return Fail(10, $"Subscribing to the iPad command channel failed: {subscribeStatus}.");
                }

                var requestId = options.RequestId ?? Guid.NewGuid().ToString("N");
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    v = 1,
                    id = requestId,
                    type = "probe.hello"
                });

                var writeOption = eventFromCore.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
                    ? GattWriteOption.WriteWithResponse
                    : GattWriteOption.WriteWithoutResponse;
                var writeResult = await eventFromCore.WriteValueWithResultAsync(ToBuffer(payload), writeOption);
                Console.WriteLine($"gatt.event_from_core.write_status={writeResult.Status}");
                Console.WriteLine($"gatt.event_from_core.protocol_error={writeResult.ProtocolError?.ToString() ?? "none"}");
                Console.WriteLine($"gatt.event_from_core.bytes={payload.Length}");
                Console.WriteLine($"gatt.probe_request_id={requestId}");
                if (writeResult.Status != GattCommunicationStatus.Success)
                {
                    return Fail(11, $"Writing the probe message to the iPad failed: {writeResult.Status}.");
                }

                var completed = await Task.WhenAny(reply.Task, Task.Delay(options.ReplyTimeout));
                if (completed != reply.Task)
                {
                    return Fail(12, $"The iPad did not notify a reply within {options.ReplyTimeout.TotalSeconds:0} seconds.");
                }

                var response = await reply.Task;
                if (!ContainsProbeAcknowledgement(response, requestId))
                {
                    return Fail(13, "A notification arrived, but it was not the expected probe acknowledgement.");
                }

                return Pass("PC Central discovery, GATT connection, write, and notification paths all succeeded.");
            }
            finally
            {
                commandToCore.ValueChanged -= valueChanged;
                _ = await commandToCore.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"probe.exception_type={exception.GetType().FullName}");
            Console.Error.WriteLine($"probe.exception_hresult=0x{exception.HResult:X8}");
            Console.Error.WriteLine($"probe.exception_message={exception.Message}");
            Console.Error.WriteLine($"probe.exception_stack={exception.StackTrace}");
            return Fail(20, "An unexpected exception stopped the Bluetooth Central probe.");
        }
    }

    private static async Task<DiscoveredPeripheral?> DiscoverPraxisPeripheralAsync(ProbeOptions options)
    {
        using var cancellation = new CancellationTokenSource(options.ScanTimeout);
        var match = new TaskCompletionSource<DiscoveredPeripheral?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> received = (_, eventArgs) =>
        {
            if (!eventArgs.Advertisement.ServiceUuids.Contains(ServiceUuid))
            {
                return;
            }

            var localName = eventArgs.Advertisement.LocalName ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(options.DeviceName) &&
                !localName.Contains(options.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            match.TrySetResult(new DiscoveredPeripheral(
                eventArgs.BluetoothAddress,
                eventArgs.BluetoothAddressType,
                localName,
                eventArgs.RawSignalStrengthInDBm,
                eventArgs.IsConnectable,
                eventArgs.AdvertisementType));
        };

        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> stopped = (_, eventArgs) =>
        {
            Console.WriteLine($"scan.stopped_error={eventArgs.Error}");
            if (eventArgs.Error != BluetoothError.Success)
            {
                match.TrySetResult(null);
            }
        };

        watcher.Received += received;
        watcher.Stopped += stopped;
        using var registration = cancellation.Token.Register(() => match.TrySetResult(null));

        Console.WriteLine($"scan.service_uuid={ServiceUuid}");
        Console.WriteLine($"scan.device_name_filter={Escape(options.DeviceName)}");
        Console.WriteLine($"scan.timeout_seconds={options.ScanTimeout.TotalSeconds:0}");
        Console.WriteLine("scan.status=starting");
        watcher.Start();

        try
        {
            return await match.Task;
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= received;
            watcher.Stopped -= stopped;
            Console.WriteLine("scan.status=stopped");
        }
    }

    private static async Task<GattCharacteristic?> GetRequiredCharacteristicAsync(
        GattDeviceService service,
        Guid uuid,
        string label)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        Console.WriteLine($"gatt.{label}.query_status={result.Status}");
        Console.WriteLine($"gatt.{label}.count={result.Characteristics.Count}");
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            return null;
        }

        var characteristic = result.Characteristics[0];
        Console.WriteLine($"gatt.{label}.properties={characteristic.CharacteristicProperties}");
        return characteristic;
    }

    private static async Task<GattCharacteristic?> GetOptionalCharacteristicAsync(
        GattDeviceService service,
        Guid uuid,
        string label)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        Console.WriteLine($"gatt.{label}.query_status={result.Status}");
        Console.WriteLine($"gatt.{label}.count={result.Characteristics.Count}");
        return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0
            ? result.Characteristics[0]
            : null;
    }

    private static async Task ReadPeerInfoAsync(GattCharacteristic characteristic)
    {
        if (!characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
        {
            Console.WriteLine("gatt.peer_info.read_status=unsupported");
            return;
        }

        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        Console.WriteLine($"gatt.peer_info.read_status={result.Status}");
        if (result.Status != GattCommunicationStatus.Success)
        {
            return;
        }

        var bytes = ReadBytes(result.Value);
        Console.WriteLine($"gatt.peer_info.bytes={bytes.Length}");
        Console.WriteLine($"gatt.peer_info.utf8={Escape(Encoding.UTF8.GetString(bytes))}");
    }

    private static bool ContainsProbeAcknowledgement(byte[] response, string requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "probe.ack")
            {
                return false;
            }

            return root.TryGetProperty("payload", out var payload) &&
                   payload.TryGetProperty("requestId", out var responseRequestId) &&
                   responseRequestId.GetString() == requestId;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value) ? "none" : value.Replace("\r", "\\r").Replace("\n", "\\n");

    private static void PrintHeader(ProbeOptions options)
    {
        Console.WriteLine("probe.name=Praxis Bluetooth Central Probe");
        Console.WriteLine("probe.version=1");
        Console.WriteLine($"probe.os={Environment.OSVersion}");
        Console.WriteLine($"probe.process_architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"probe.mode={options.Mode}");
    }

    private static int Pass(string message)
    {
        Console.WriteLine("PROBE_RESULT=PASS");
        Console.WriteLine("probe.message=" + message);
        return 0;
    }

    private static int Fail(int exitCode, string message)
    {
        Console.Error.WriteLine("PROBE_RESULT=FAIL");
        Console.Error.WriteLine("probe.message=" + message);
        return exitCode;
    }

    private sealed record DiscoveredPeripheral(
        ulong BluetoothAddress,
        BluetoothAddressType AddressType,
        string LocalName,
        short Rssi,
        bool IsConnectable,
        BluetoothLEAdvertisementType AdvertisementType);

    private sealed record ProbeOptions(
        bool CapabilitiesOnly,
        bool ScanOnly,
        bool ConnectOnly,
        string? DeviceName,
        string? RequestId,
        TimeSpan ScanTimeout,
        TimeSpan ReplyTimeout)
    {
        public string Mode => CapabilitiesOnly ? "capabilities" : ScanOnly ? "scan" : ConnectOnly ? "connect" : "round_trip";

        public static ProbeOptions Parse(string[] args) => new(
            HasFlag(args, "--capabilities-only"),
            HasFlag(args, "--scan-only"),
            HasFlag(args, "--connect-only"),
            GetValue(args, "--name"),
            GetValue(args, "--request-id"),
            TimeSpan.FromSeconds(GetInteger(args, "--scan-timeout", 15, 1, 300)),
            TimeSpan.FromSeconds(GetInteger(args, "--reply-timeout", 10, 1, 120)));

        private static bool HasFlag(string[] args, string flag) =>
            args.Contains(flag, StringComparer.OrdinalIgnoreCase);

        private static string? GetValue(string[] args, string option)
        {
            var index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static int GetInteger(string[] args, string option, int fallback, int minimum, int maximum)
        {
            var value = GetValue(args, option);
            return int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
                ? parsed
                : fallback;
        }
    }
}
