using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxis.Core.Actions;
using Praxis.Core.Configuration;
using Praxis.Core.Hosting;
using Praxis.Core.Protocol;
using Praxis.Core.Security;
using Praxis.Core.State;
using Praxis.Link.Bluetooth;

namespace Praxis.Core.Bluetooth;

internal sealed class BluetoothPanelSession(
    BluetoothCentralTransport transport,
    BluetoothPanelMessageSink sink,
    PanelProtocolRouter router,
    ConfigurationStore configurations,
    StateStore states,
    AppIconProvider icons,
    PairingManager pairing,
    SecureSession secureSession,
    BluetoothSessionReadiness readiness,
    AuditLog audit,
    PraxisRuntimeOptions runtime,
    ILogger<BluetoothPanelSession> logger) : IHostedService
{
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IconAcknowledgementTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);
    private readonly object _bootstrapSync = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _iconAcknowledgements =
        new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<BluetoothBootstrapPreparation>? _bootstrapPreparation;
    private CancellationTokenSource? _bootstrapCancellation;
    private Task? _bootstrapTask;
    private int _ready;
    private int _authenticated;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        transport.MessageReceived += OnMessageReceivedAsync;
        transport.ConnectionChanged += OnConnectionChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        transport.MessageReceived -= OnMessageReceivedAsync;
        transport.ConnectionChanged -= OnConnectionChanged;
        await SetDisconnectedAsync(CancellationToken.None);
    }

    private void OnConnectionChanged(BluetoothConnectionSnapshot snapshot)
    {
        _ = ObserveConnectionChangeAsync(snapshot);
    }

    private async Task ObserveConnectionChangeAsync(BluetoothConnectionSnapshot snapshot)
    {
        try
        {
            await states.SetAsync(
                "bluetooth.connection",
                snapshot,
                snapshot.State == BluetoothCentralState.Ready ? "online" : "idle",
                "Bluetooth",
                CancellationToken.None);

            if (snapshot.State == BluetoothCentralState.Ready)
            {
                if (Interlocked.Exchange(ref _ready, 1) == 0)
                {
                    if (string.IsNullOrWhiteSpace(snapshot.PeerId))
                        throw new InvalidDataException("Praxis Panel did not provide a stable device identity.");
                    await sink.SendPlainAsync(
                        pairing.Begin(snapshot.PeerId, snapshot.DeviceName),
                        CancellationToken.None);
                }
            }
            else if (Interlocked.Exchange(ref _ready, 0) == 1)
            {
                await SetDisconnectedAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not process a Praxis Bluetooth connection state change.");
        }
    }

    private void BeginBootstrap(
        BluetoothConnectionSnapshot snapshot,
        string deviceId,
        CancellationToken connectionCancellationToken)
    {
        CancellationTokenSource cancellation;
        lock (_bootstrapSync)
        {
            _bootstrapCancellation?.Cancel();
            _bootstrapCancellation?.Dispose();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellationToken);
            _bootstrapCancellation = cancellation;
            _bootstrapPreparation = new TaskCompletionSource<BluetoothBootstrapPreparation>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            readiness.MarkSynchronizing();
            _bootstrapTask = ObserveBootstrapAsync(
                SendBootstrapAsync(snapshot, deviceId, cancellation.Token),
                cancellation.Token);
        }
    }

    private async Task ObserveBootstrapAsync(Task bootstrap, CancellationToken cancellationToken)
    {
        try
        {
            await bootstrap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            readiness.MarkSynchronizing();
            logger.LogError(exception, "Praxis Bluetooth bootstrap failed.");
            try
            {
                await sink.SendAsync(
                    ProtocolMessage.Create("bootstrap.failed", new { error = exception.Message }),
                    new BluetoothOutgoingMetadata(
                        "bootstrap.failed",
                        BluetoothMessagePriority.High),
                    CancellationToken.None);
            }
            catch (Exception sendException)
            {
                logger.LogDebug(sendException, "Could not deliver the Praxis bootstrap failure.");
            }
        }
    }

    private async Task SendBootstrapAsync(
        BluetoothConnectionSnapshot snapshot,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await _bootstrapGate.WaitAsync(cancellationToken);
        try
        {
            await states.SetAsync(
                "core.connectedPanels",
                1,
                "online",
                "Connected panels",
                cancellationToken);
            await audit.WriteAsync(
                "panel.bluetooth.paired",
                deviceId,
                true,
                snapshot.DeviceName,
                cancellationToken);

            var preparationTask = Volatile.Read(ref _bootstrapPreparation)?.Task;
            var preparation = await WaitForPreparationAsync(preparationTask, cancellationToken);
            await sink.SendAsync(ProtocolMessage.Create("core.hello", new
            {
                serverName = runtime.ServerName,
                version = BluetoothVersion.Value,
                protocolVersion = BluetoothProtocol.ProtocolVersion,
                bootstrapVersion = 2,
                transport = "bluetooth",
                maximumPduSize = snapshot.MaximumPduSize
            }), cancellationToken);

            var configuration = await configurations.GetAsync(cancellationToken);
            var configurationHash = ConfigurationSnapshot.ComputeHash(configuration);
            if (preparation is not null &&
                string.Equals(
                    preparation.ConfigurationHash,
                    configurationHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                await sink.SendAsync(
                    ProtocolMessage.Create("config.unchanged", new { configurationHash }),
                    cancellationToken);
            }
            else
            {
                await sink.SendAsync(
                    ProtocolMessage.Create(
                        "config.snapshot",
                        ConfigurationSnapshot.CreatePayload(configuration, configurationHash)),
                    cancellationToken);
            }

            var applicationIcons = await LoadApplicationIconsAsync(configuration, cancellationToken);
            var iconHashes = applicationIcons.ToDictionary(
                icon => icon.ActionId,
                icon => icon.Hash,
                StringComparer.OrdinalIgnoreCase);
            var missingIconIds = preparation is null
                ? iconHashes.Keys.ToArray()
                : BluetoothBootstrapCachePlanner.FindMissingIcons(
                    iconHashes,
                    preparation.IconHashes);
            var missingIconSet = missingIconIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var useCachedFastPath = BluetoothBootstrapCachePlanner.ShouldUseCachedFastPath(
                preparation is not null,
                missingIconSet.Count);

            if (preparation is not null)
            {
                await sink.SendAsync(ProtocolMessage.Create("bootstrap.plan", new
                {
                    totalIconCount = iconHashes.Count,
                    missingIconCount = missingIconSet.Count
                }), cancellationToken);
            }

            if (!useCachedFastPath)
                await SendEssentialStateSnapshotAsync(cancellationToken);

            foreach (var icon in applicationIcons.Where(icon => missingIconSet.Contains(icon.ActionId)))
            {
                await SendApplicationIconAsync(
                    icon,
                    requireAcknowledgement: preparation is not null,
                    cancellationToken);
            }

            if (preparation is not null)
            {
                await sink.SendAsync(ProtocolMessage.Create("bootstrap.completed", new
                {
                    configurationHash,
                    iconHashes,
                    transferredIconCount = missingIconSet.Count
                }), cancellationToken);
            }

            readiness.MarkReady();
            logger.LogInformation(
                "Praxis Bluetooth bootstrap completed: cachedConfiguration={CachedConfiguration}; icons={IconCount}; transferredIcons={TransferredIconCount}.",
                preparation is not null && string.Equals(
                    preparation.ConfigurationHash,
                    configurationHash,
                    StringComparison.OrdinalIgnoreCase),
                iconHashes.Count,
                missingIconSet.Count);

            if (useCachedFastPath)
            {
                logger.LogInformation(
                    "Praxis Bluetooth cache hit enabled the fast bootstrap path; ordinary state follows readiness.");
                await SendEssentialStateSnapshotAsync(cancellationToken);
            }

            var presentationState = states.Snapshot().FirstOrDefault(entry => string.Equals(
                entry.Key,
                "presentation.state",
                StringComparison.OrdinalIgnoreCase));
            if (presentationState is not null)
            {
                await sink.BroadcastAsync(
                    ProtocolMessage.Create("state.changed", presentationState),
                    cancellationToken);
            }
        }
        finally
        {
            _bootstrapGate.Release();
        }
    }

    private async Task SendEssentialStateSnapshotAsync(CancellationToken cancellationToken)
    {
        var essentialStates = states.Snapshot()
            .Where(entry => !string.Equals(
                entry.Key,
                "presentation.state",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        await sink.SendAsync(
            ProtocolMessage.Create("state.snapshot", essentialStates),
            cancellationToken);
    }

    private async Task<BluetoothBootstrapPreparation?> WaitForPreparationAsync(
        Task<BluetoothBootstrapPreparation>? preparationTask,
        CancellationToken cancellationToken)
    {
        if (preparationTask is null)
            return null;
        var timeout = Task.Delay(PreparationTimeout, cancellationToken);
        var completed = await Task.WhenAny(preparationTask, timeout);
        return completed == preparationTask
            ? await preparationTask
            : null;
    }

    private async Task<IReadOnlyList<ApplicationIconPayload>> LoadApplicationIconsAsync(
        PraxisConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var actions = configuration.Actions.Where(action =>
                string.Equals(action.Type, "appToggle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action.Id, "presentation.open", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var pending = actions.Select(async action =>
        {
            var png = await icons.GetPngAsync(action, cancellationToken);
            return png is null || png.Length == 0
                ? null
                : new ApplicationIconPayload(
                    action.Id,
                    Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),
                    png);
        });
        var loaded = await Task.WhenAll(pending);
        return loaded.OfType<ApplicationIconPayload>().ToArray();
    }

    private async Task SendApplicationIconAsync(
        ApplicationIconPayload icon,
        bool requireAcknowledgement,
        CancellationToken cancellationToken)
    {
        var message = ProtocolMessage.Create("app.icon", new
        {
            actionId = icon.ActionId,
            mediaType = "image/png",
            sha256 = icon.Hash,
            data = Convert.ToBase64String(icon.Png)
        });
        if (!requireAcknowledgement)
        {
            await sink.SendAsync(
                message,
                new BluetoothOutgoingMetadata(
                    "app.icon",
                    BluetoothMessagePriority.Normal,
                    $"app.icon:{icon.ActionId}"),
                cancellationToken);
            return;
        }

        var acknowledgementKey = IconAcknowledgementKey(icon.ActionId, icon.Hash);
        for (var attempt = 0; attempt < 2; attempt += 1)
        {
            var acknowledgement = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _iconAcknowledgements[acknowledgementKey] = acknowledgement;
            var writeMode = attempt == 0
                ? BluetoothWriteMode.WithoutResponse
                : BluetoothWriteMode.WithResponse;
            await sink.SendAsync(
                message,
                new BluetoothOutgoingMetadata(
                    "app.icon",
                    BluetoothMessagePriority.Normal,
                    $"app.icon:{icon.ActionId}",
                    WriteMode: writeMode),
                cancellationToken);

            var timeout = Task.Delay(IconAcknowledgementTimeout, cancellationToken);
            var completed = await Task.WhenAny(acknowledgement.Task, timeout);
            if (completed == acknowledgement.Task && await acknowledgement.Task)
            {
                _iconAcknowledgements.TryRemove(acknowledgementKey, out _);
                return;
            }

            _iconAcknowledgements.TryRemove(acknowledgementKey, out _);
            logger.LogWarning(
                "Praxis icon acknowledgement timed out: actionId={ActionId}; writeMode={WriteMode}; attempt={Attempt}.",
                icon.ActionId,
                writeMode,
                attempt + 1);
        }

        throw new IOException($"iPad 未确认图标 {icon.ActionId}。请重新连接后重试。");
    }

    private async Task OnMessageReceivedAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        if (secureSession.IsActive)
        {
            if (!secureSession.TryUnwrap(message.Span, out var authenticated, out var error) || authenticated is null)
            {
                logger.LogWarning("Rejected an unauthenticated Praxis Bluetooth message: {Error}", error);
                return;
            }

            if (authenticated.Type == "bootstrap.prepare")
            {
                var preparation = authenticated.Payload.Deserialize<BluetoothBootstrapPreparation>(
                    JsonDefaults.Options);
                if (preparation is not null)
                    Volatile.Read(ref _bootstrapPreparation)?.TrySetResult(preparation);
                return;
            }

            if (authenticated.Type == "app.icon.received")
            {
                var actionId = authenticated.Payload.TryGetProperty("actionId", out var actionElement)
                    ? actionElement.GetString()
                    : null;
                var hash = authenticated.Payload.TryGetProperty("sha256", out var hashElement)
                    ? hashElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(actionId) && !string.IsNullOrWhiteSpace(hash) &&
                    _iconAcknowledgements.TryGetValue(
                        IconAcknowledgementKey(actionId, hash),
                        out var acknowledgement))
                {
                    acknowledgement.TrySetResult(true);
                }
                return;
            }

            if (!readiness.IsReady && authenticated.Type is "control.invoke" or "config.refresh")
            {
                await sink.SendAsync(
                    ProtocolMessage.Create("protocol.error", new
                    {
                        error = "Praxis Bluetooth 正在同步控制配置，本次操作未执行。",
                        code = "session.notReady"
                    }, authenticated.Id),
                    cancellationToken);
                return;
            }

            var source = "bluetooth:" + (transport.Snapshot.PeerId ?? "unknown");
            await router.HandleJsonAsync(
                JsonSerializer.Serialize(authenticated, JsonDefaults.CompactOptions),
                source,
                sink.SendAsync,
                cancellationToken);
            return;
        }

        ProtocolMessage? plain;
        try
        {
            plain = JsonSerializer.Deserialize<ProtocolMessage>(message.Span, JsonDefaults.Options);
        }
        catch (JsonException)
        {
            logger.LogWarning("Rejected invalid JSON before Praxis pairing completed.");
            return;
        }
        if (plain is null || !plain.Type.StartsWith("pairing.", StringComparison.Ordinal))
        {
            await sink.SendPlainAsync(
                ProtocolMessage.Create("pairing.failed", new { error = "请先完成设备配对。", attemptsRemaining = 0 }, plain?.Id),
                cancellationToken);
            return;
        }

        logger.LogInformation("Received Praxis pairing message: type={MessageType}.", plain.Type);
        var outcome = await pairing.ProcessAsync(plain, cancellationToken);
        await sink.SendPlainAsync(outcome.Response, cancellationToken);
        logger.LogInformation(
            "Praxis pairing response delivered: type={MessageType}; activated={Activated}; failed={Failed}.",
            outcome.Response.Type,
            outcome.Activation is not null,
            outcome.IsFailure);
        if (outcome.Activation is null)
            return;

        secureSession.Activate(outcome.Activation.Key, outcome.Activation.SessionId);
        Interlocked.Exchange(ref _authenticated, 1);
        BeginBootstrap(
            transport.Snapshot,
            outcome.Activation.DeviceId,
            cancellationToken);
    }

    private async Task SetDisconnectedAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _ready, 0);
        Interlocked.Exchange(ref _authenticated, 0);
        readiness.MarkSynchronizing();
        CancellationTokenSource? bootstrapCancellation;
        TaskCompletionSource<BluetoothBootstrapPreparation>? bootstrapPreparation;
        lock (_bootstrapSync)
        {
            bootstrapCancellation = _bootstrapCancellation;
            _bootstrapCancellation = null;
            bootstrapPreparation = _bootstrapPreparation;
            _bootstrapPreparation = null;
            _bootstrapTask = null;
        }
        bootstrapCancellation?.Cancel();
        bootstrapCancellation?.Dispose();
        bootstrapPreparation?.TrySetCanceled();
        foreach (var acknowledgement in _iconAcknowledgements.Values)
            acknowledgement.TrySetCanceled();
        _iconAcknowledgements.Clear();
        secureSession.Reset();
        pairing.Abort();
        await states.SetAsync("core.connectedPanels", 0, "idle", "Connected panels", cancellationToken);
        await audit.WriteAsync(
            "panel.bluetooth.disconnected",
            transport.Snapshot.DeviceAddress,
            true,
            transport.Snapshot.Error,
            cancellationToken);
    }

    private static string IconAcknowledgementKey(string actionId, string hash) =>
        actionId + ":" + hash;

    private sealed record ApplicationIconPayload(string ActionId, string Hash, byte[] Png);
}
