using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxis.Core.Actions;
using Praxis.Core.Configuration;
using Praxis.Core.Hosting;
using Praxis.Core.Presentation;
using Praxis.Core.Protocol;
using Praxis.Core.State;
using Praxis.Core.Bluetooth;
using Praxis.Link.Bluetooth;
using Praxis.Core.Security;
using System.Windows.Forms;

using var instanceMutex = new Mutex(
    initiallyOwned: true,
    name: "PraxisBluetoothCore_5DBFC9ED7BAF4C52B5610D3187F7F528",
    createdNew: out var isFirstInstance);
if (!isFirstInstance)
{
    MessageBox.Show(
        "Praxis Bluetooth 已经在运行。请在 Windows 通知区域查找 Praxis 图标。",
        "Praxis Bluetooth",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    return;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var dataDirectory = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    builder.Configuration["Praxis:DataDirectory"] ?? "data"));
Directory.CreateDirectory(dataDirectory);
using var runtimeLog = RuntimeLogCapture.Start(dataDirectory);

var configuredServerName = builder.Configuration["Praxis:ServerName"];
builder.Services.AddSingleton(new PraxisRuntimeOptions
{
    DataDirectory = dataDirectory,
    Port = 0,
    ServerName = string.IsNullOrWhiteSpace(configuredServerName) ? Environment.MachineName : configuredServerName,
    EnableTrayIcon = builder.Configuration.GetValue("Praxis:EnableTrayIcon", true)
});
builder.Services.AddSingleton(new BluetoothCentralOptions
{
    ScanTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Bluetooth:ScanTimeoutSeconds", 15)),
    ReconnectDelay = TimeSpan.FromSeconds(builder.Configuration.GetValue("Bluetooth:ReconnectDelaySeconds", 2)),
    OutgoingQueueCapacity = builder.Configuration.GetValue("Bluetooth:OutgoingQueueCapacity", 128),
    MaximumMessageBytes = builder.Configuration.GetValue(
        "Bluetooth:MaximumMessageBytes",
        BluetoothProtocol.MaximumMessageBytes)
});

builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<ConfigurationStore>();
builder.Services.AddSingleton<CommandDeduplicator>();
builder.Services.AddSingleton<HotkeySender>();
builder.Services.AddSingleton<AppIconProvider>();
builder.Services.AddSingleton<PairingStore>();
builder.Services.AddSingleton<PairingManager>();
builder.Services.AddSingleton<SecureSession>();
builder.Services.AddSingleton<PowerPointRecentFiles>();
builder.Services.AddSingleton<PowerPointAutomation>();
builder.Services.AddSingleton<IPowerPointAutomation>(services => services.GetRequiredService<PowerPointAutomation>());
builder.Services.AddSingleton(PresentationStateCoordinatorOptions.Default);
builder.Services.AddSingleton<PresentationStateCoordinator>();
builder.Services.AddSingleton<IActionHandler, NoopActionHandler>();
builder.Services.AddSingleton<IActionHandler, LaunchActionHandler>();
builder.Services.AddSingleton<IActionHandler, AppToggleActionHandler>();
builder.Services.AddSingleton<IActionHandler, OpenUrlActionHandler>();
builder.Services.AddSingleton<IActionHandler, HotkeyActionHandler>();
builder.Services.AddSingleton<IActionHandler, MediaActionHandler>();
builder.Services.AddSingleton<IActionHandler, ScriptActionHandler>();
builder.Services.AddSingleton<IActionHandler, SystemSummaryActionHandler>();
builder.Services.AddSingleton<IActionHandler, PowerPointActionHandler>();

builder.Services.AddSingleton<BluetoothCentralTransport>();
builder.Services.AddSingleton<BluetoothSessionReadiness>();
builder.Services.AddSingleton<BluetoothPanelMessageSink>();
builder.Services.AddSingleton<IPanelMessageSink>(services => services.GetRequiredService<BluetoothPanelMessageSink>());
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<ActionDispatcher>();
builder.Services.AddSingleton<PanelProtocolRouter>();
builder.Services.AddSingleton<BluetoothPanelSession>();

builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<BluetoothPanelSession>());
builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<BluetoothCentralTransport>());
builder.Services.AddHostedService<BluetoothTrayIconService>();
if (builder.Configuration.GetValue("Praxis:EnableSystemStateMonitor", true))
{
    builder.Services.AddHostedService<SystemStateMonitor>();
}

if (builder.Configuration.GetValue("Praxis:EnablePresentationStateMonitor", true))
{
    builder.Services.AddHostedService<PresentationStateMonitor>();
}

using var host = builder.Build();
await host.Services.GetRequiredService<ConfigurationStore>().InitializeAsync();
await host.Services.GetRequiredService<PairingStore>().InitializeAsync();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Praxis.Core.Bluetooth");
logger.LogInformation(
    "Praxis Bluetooth {Version} starting. No HTTP or WebSocket listener is enabled.",
    BluetoothVersion.Value);

var runSeconds = ReadRunSeconds(args);
if (runSeconds is null)
{
    await host.RunAsync();
}
else
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(runSeconds.Value));
    await host.RunAsync(timeout.Token);
}

static int? ReadRunSeconds(string[] args)
{
    var index = Array.FindIndex(args, value => value.Equals("--run-seconds", StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length &&
           int.TryParse(args[index + 1], out var seconds) && seconds is >= 1 and <= 3600
        ? seconds
        : null;
}
