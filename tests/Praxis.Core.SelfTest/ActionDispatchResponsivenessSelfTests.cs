using Microsoft.Extensions.Logging.Abstractions;
using Praxis.Core.Actions;
using Praxis.Core.Configuration;
using Praxis.Core.Hosting;
using Praxis.Core.Protocol;
using Praxis.Core.State;

internal static class ActionDispatchResponsivenessSelfTests
{
    public static async Task InstantActionStartsBeforeFeedbackDeliveryAsync()
    {
        await using var fixture = await Fixture.CreateAsync("hotkey");
        var execution = fixture.Dispatcher.ExecuteAsync(
            "instant-command",
            new ControlInvokeRequest("shortcut.copy", "copy"),
            "selftest",
            CancellationToken.None);

        await fixture.Sink.AcceptedQueued.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(!execution.IsCompleted, "The dispatch unexpectedly completed while accepted feedback was blocked.");

        fixture.Sink.ReleaseAccepted();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(result.Success, "The instant action did not complete successfully.");
        Assert(fixture.Handler.ExecutionCount == 1, "The instant action executed more than once.");

        var messages = fixture.Sink.Messages;
        var accepted = FindIndex(messages, message => message.Type == "action.accepted");
        var running = FindIndex(messages, message =>
            message.Type == "state.changed" &&
            message.Payload.TryGetProperty("key", out var key) &&
            key.GetString() == "control:copy" &&
            message.Payload.TryGetProperty("status", out var status) &&
            status.GetString() == "running");
        var progress = FindIndex(messages, message => message.Type == "action.progress");
        var completed = FindIndex(messages, message => message.Type == "action.completed");
        Assert(accepted >= 0 && accepted < running && running < progress && progress < completed,
            "Action feedback order changed while removing the execution delay.");
    }

    public static async Task PowerPointStillWaitsForInitialFeedbackAsync()
    {
        await using var fixture = await Fixture.CreateAsync("powerPoint");
        var execution = fixture.Dispatcher.ExecuteAsync(
            "powerpoint-command",
            new ControlInvokeRequest("presentation.open", "presentation-open"),
            "selftest",
            CancellationToken.None);

        await fixture.Sink.AcceptedQueued.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var earlyStart = await Task.WhenAny(
            fixture.Handler.Started.Task,
            Task.Delay(TimeSpan.FromMilliseconds(120)));
        Assert(earlyStart != fixture.Handler.Started.Task,
            "PowerPoint execution overtook its ordered initial feedback.");

        fixture.Sink.ReleaseAccepted();
        await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(result.Success, "The ordered PowerPoint action did not complete successfully.");
    }

    public static async Task InstantActionSurvivesInitialFeedbackFailureAsync()
    {
        await using var fixture = await Fixture.CreateAsync("media", failAccepted: true);
        var result = await fixture.Dispatcher.ExecuteAsync(
            "feedback-failure-command",
            new ControlInvokeRequest("shortcut.playPause", "play"),
            "selftest",
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert(result.Success, "An initial feedback failure incorrectly failed the local action.");
        Assert(fixture.Handler.ExecutionCount == 1,
            "The local action was repeated while recovering from an initial feedback failure.");
        Assert(fixture.Sink.Messages.Count(message => message.Type == "action.completed") == 1,
            "The recovered action did not produce exactly one completion message.");
    }

    private static int FindIndex(
        IReadOnlyList<ProtocolMessage> messages,
        Func<ProtocolMessage, bool> predicate)
    {
        for (var index = 0; index < messages.Count; index += 1)
        {
            if (predicate(messages[index]))
                return index;
        }

        return -1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;

        private Fixture(
            string directory,
            ControlledPanelSink sink,
            RecordingActionHandler handler,
            ActionDispatcher dispatcher)
        {
            _directory = directory;
            Sink = sink;
            Handler = handler;
            Dispatcher = dispatcher;
        }

        public ControlledPanelSink Sink { get; }
        public RecordingActionHandler Handler { get; }
        public ActionDispatcher Dispatcher { get; }

        public static async Task<Fixture> CreateAsync(string handlerType, bool failAccepted = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), "praxis-dispatch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sourceConfiguration = Path.Combine(
                FindRepositoryRoot(),
                "src",
                "Praxis.Core.Bluetooth",
                "data",
                "praxis.json");
            File.Copy(sourceConfiguration, Path.Combine(directory, "praxis.json"));
            var runtime = new PraxisRuntimeOptions
            {
                DataDirectory = directory,
                Port = 0,
                ServerName = "Dispatch responsiveness test",
                EnableTrayIcon = false
            };
            var sink = new ControlledPanelSink(failAccepted);
            var handler = new RecordingActionHandler(handlerType);
            var configuration = new ConfigurationStore(runtime, NullLogger<ConfigurationStore>.Instance);
            await configuration.InitializeAsync();
            var states = new StateStore(sink);
            var dispatcher = new ActionDispatcher(
                configuration,
                [handler],
                new CommandDeduplicator(),
                sink,
                states,
                new AuditLog(runtime, NullLogger<AuditLog>.Instance));
            return new Fixture(directory, sink, handler, dispatcher);
        }

        public ValueTask DisposeAsync()
        {
            Sink.ReleaseAccepted();
            if (Directory.Exists(_directory) &&
                Path.GetFileName(_directory).StartsWith("praxis-dispatch-", StringComparison.Ordinal))
                Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingActionHandler(string type) : IActionHandler
    {
        public string Type { get; } = type;
        public int ExecutionCount { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ActionHandlerResult> ExecuteAsync(
            ActionExecutionContext context,
            IProgress<ActionProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount += 1;
            Started.TrySetResult();
            progress.Report(new ActionProgress(100, "executed"));
            return Task.FromResult(ActionHandlerResult.Ok("executed"));
        }
    }

    private sealed class ControlledPanelSink(bool failAccepted) : IPanelMessageSink
    {
        private readonly object _sync = new();
        private readonly List<ProtocolMessage> _messages = [];
        private readonly TaskCompletionSource _releaseAccepted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acceptedBlocked;

        public int Count => 1;
        public TaskCompletionSource AcceptedQueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ProtocolMessage> Messages
        {
            get
            {
                lock (_sync)
                    return _messages.ToArray();
            }
        }

        public async Task BroadcastAsync(
            ProtocolMessage message,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
                _messages.Add(message);

            if (message.Type == "action.accepted" &&
                Interlocked.CompareExchange(ref _acceptedBlocked, 1, 0) == 0)
            {
                AcceptedQueued.TrySetResult();
                if (failAccepted)
                    throw new IOException("Simulated Bluetooth feedback failure.");
                await _releaseAccepted.Task.WaitAsync(cancellationToken);
            }
        }

        public void ReleaseAccepted() => _releaseAccepted.TrySetResult();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Praxis.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Praxis.sln.");
    }
}
