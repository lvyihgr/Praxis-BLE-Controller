using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxis.Core.Hosting;
using Praxis.Link.Bluetooth;
using Praxis.Core.Security;

namespace Praxis.Core.Bluetooth;

internal sealed class BluetoothTrayIconService(
    BluetoothCentralTransport transport,
    PraxisRuntimeOptions runtime,
    PairingManager pairing,
    SecureSession secureSession,
    IHostApplicationLifetime lifetime,
    ILogger<BluetoothTrayIconService> logger) : IHostedService
{
    private readonly object _gate = new();
    private Thread? _thread;
    private SynchronizationContext? _trayContext;
    private ApplicationContext? _applicationContext;
    private NotifyIcon? _icon;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _pairingCodeItem;
    private Icon? _applicationIcon;
    private BluetoothCentralState _lastRenderedState = BluetoothCentralState.Stopped;
    private int _stopRequested;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!runtime.EnableTrayIcon || !Environment.UserInteractive || !OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        transport.ConnectionChanged += OnConnectionChanged;
        pairing.PairingCodeChanged += OnPairingCodeChanged;
        secureSession.StateChanged += OnSecureStateChanged;
        _thread = new Thread(RunTray)
        {
            IsBackground = true,
            Name = "Praxis Bluetooth Tray"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        transport.ConnectionChanged -= OnConnectionChanged;
        pairing.PairingCodeChanged -= OnPairingCodeChanged;
        secureSession.StateChanged -= OnSecureStateChanged;
        Interlocked.Exchange(ref _stopRequested, 1);
        PostToTray(ExitTray);

        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        return Task.CompletedTask;
    }

    private void RunTray()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            lock (_gate)
            {
                _trayContext = SynchronizationContext.Current;
            }

            _applicationContext = new ApplicationContext();
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Praxis Bluetooth " + BluetoothVersion.Value) { Enabled = false });
            _statusItem = new ToolStripMenuItem("状态：正在启动") { Enabled = false };
            menu.Items.Add(_statusItem);
            _pairingCodeItem = new ToolStripMenuItem("配对码：" + pairing.FormattedPairingCode);
            _pairingCodeItem.Click += (_, _) => CopyPairingCode();
            menu.Items.Add(_pairingCodeItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("显示连接状态", null, (_, _) => ShowCurrentStatus());
            menu.Items.Add("更新配对码", null, (_, _) => RotatePairingCode());
            menu.Items.Add("忘记所有配对设备", null, async (_, _) => await ForgetAllDevicesAsync());
            menu.Items.Add("退出 Praxis Bluetooth", null, (_, _) =>
            {
                if (_icon is not null)
                {
                    _icon.Visible = false;
                }

                lifetime.StopApplication();
            });

            _applicationIcon = LoadApplicationIcon();
            _icon = new NotifyIcon
            {
                Icon = _applicationIcon ?? SystemIcons.Application,
                Text = "Praxis Bluetooth",
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.DoubleClick += (_, _) => ShowCurrentStatus();
            logger.LogInformation("Praxis Bluetooth notification-area icon started.");

            UpdateTray(transport.Snapshot, showTransitionNotification: false);
            _icon.ShowBalloonTip(
                3000,
                "Praxis Bluetooth 已启动",
                "蓝牙服务正在运行。右键图标可以查看状态或退出。",
                ToolTipIcon.Info);

            if (Volatile.Read(ref _stopRequested) == 1)
            {
                ExitTray();
            }

            Application.Run(_applicationContext);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Praxis Bluetooth tray icon could not be started.");
        }
        finally
        {
            lock (_gate)
            {
                _trayContext = null;
            }

            _icon?.Dispose();
            _applicationIcon?.Dispose();
            _applicationContext?.Dispose();
            _icon = null;
            _applicationIcon = null;
            _applicationContext = null;
        }
    }

    private void OnConnectionChanged(BluetoothConnectionSnapshot snapshot) =>
        PostToTray(() => UpdateTray(snapshot, showTransitionNotification: true));

    private void OnPairingCodeChanged() => PostToTray(UpdatePairingCode);
    private void OnSecureStateChanged(bool _) => PostToTray(() => UpdateTray(transport.Snapshot, showTransitionNotification: false));

    private void UpdateTray(
        BluetoothConnectionSnapshot snapshot,
        bool showTransitionNotification)
    {
        if (_icon is null || _statusItem is null)
        {
            return;
        }

        var status = Describe(snapshot);
        _statusItem.Text = "状态：" + status;
        _icon.Text = Truncate("Praxis Bluetooth · " + status, 63);

        var previous = _lastRenderedState;
        _lastRenderedState = snapshot.State;
        if (!showTransitionNotification || previous == snapshot.State)
        {
            return;
        }

        if (snapshot.State == BluetoothCentralState.Ready)
        {
            _icon.ShowBalloonTip(
                2500,
                secureSession.IsActive ? "Praxis Panel 已连接" : "Praxis Panel 等待配对",
                secureSession.IsActive
                    ? "已建立经过认证的 iPad 蓝牙会话。"
                    : $"请在 iPad 输入配对码 {pairing.FormattedPairingCode}。",
                ToolTipIcon.Info);
        }
        else if (previous == BluetoothCentralState.Ready)
        {
            _icon.ShowBalloonTip(
                2500,
                "Praxis Panel 已断开",
                "正在自动扫描并重新连接 iPad。",
                ToolTipIcon.Warning);
        }
        else if (snapshot.State == BluetoothCentralState.Faulted)
        {
            _icon.ShowBalloonTip(
                3000,
                "Praxis 蓝牙不可用",
                snapshot.Error ?? "请检查 Windows 蓝牙适配器。",
                ToolTipIcon.Error);
        }
    }

    private void ShowCurrentStatus()
    {
        var snapshot = transport.Snapshot;
        var detail = snapshot.State == BluetoothCentralState.Ready
            ? $"{Describe(snapshot)}\nMTU：{snapshot.MaximumPduSize?.ToString() ?? "未知"}"
            : snapshot.Error ?? Describe(snapshot);
        _icon?.ShowBalloonTip(3000, "Praxis Bluetooth", detail, ToolTipIcon.Info);
    }

    private void CopyPairingCode()
    {
        try
        {
            Clipboard.SetText(pairing.PairingCode);
            _icon?.ShowBalloonTip(1800, "配对码已复制", pairing.FormattedPairingCode, ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not copy the Praxis pairing code.");
        }
    }

    private void UpdatePairingCode()
    {
        if (_pairingCodeItem is not null)
            _pairingCodeItem.Text = "配对码：" + pairing.FormattedPairingCode;
    }

    private void RotatePairingCode()
    {
        pairing.RotateCode();
        secureSession.Reset();
        transport.RequestReconnect();
        _icon?.ShowBalloonTip(1800, "配对码已更新", pairing.FormattedPairingCode, ToolTipIcon.Info);
    }

    private async Task ForgetAllDevicesAsync()
    {
        var result = MessageBox.Show(
            "确定要忘记所有已配对的 iPad 吗？下次连接需要重新输入配对码。",
            "Praxis Bluetooth",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;
        await pairing.ForgetAllAsync();
        secureSession.Reset();
        transport.RequestReconnect();
        UpdatePairingCode();
        _icon?.ShowBalloonTip(2200, "已清除配对", "所有 iPad 需要重新配对。", ToolTipIcon.Info);
    }

    private void ExitTray()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
        }

        _applicationContext?.ExitThread();
    }

    private void PostToTray(Action action)
    {
        SynchronizationContext? context;
        lock (_gate)
        {
            context = _trayContext;
        }

        context?.Post(_ => action(), null);
    }

    private string Describe(BluetoothConnectionSnapshot snapshot) => snapshot.State switch
    {
        BluetoothCentralState.Stopped => "已停止",
        BluetoothCentralState.Initializing => "正在初始化",
        BluetoothCentralState.Scanning => "正在扫描 iPad",
        BluetoothCentralState.Connecting => "正在连接 iPad",
        BluetoothCentralState.DiscoveringServices => "正在建立蓝牙服务",
        BluetoothCentralState.Ready when !secureSession.IsActive => "等待 iPad 完成配对",
        BluetoothCentralState.Ready => string.IsNullOrWhiteSpace(snapshot.DeviceName)
            ? "已安全连接 iPad"
            : $"已安全连接 {snapshot.DeviceName}",
        BluetoothCentralState.Reconnecting => "正在重新连接",
        BluetoothCentralState.Faulted => "蓝牙不可用",
        _ => snapshot.State.ToString()
    };

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static Icon? LoadApplicationIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "praxis.ico");
        return File.Exists(iconPath) ? new Icon(iconPath, 32, 32) : null;
    }
}
