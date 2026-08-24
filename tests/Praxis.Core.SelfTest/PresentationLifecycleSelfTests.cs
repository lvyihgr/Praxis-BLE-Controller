using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Praxis.Core.Actions;
using Praxis.Core.Configuration;
using Praxis.Core.Hosting;
using Praxis.Core.Presentation;
using Praxis.Core.Protocol;
using Praxis.Core.State;

internal static class PresentationLifecycleSelfTests
{
    public static Task SlideShowDoneStateIsNotActiveAsync()
    {
        Assert(!PowerPointAutomation.IsSlideShowActive(1, 5),
            "ppSlideShowDone 仍被误判为正在放映。");
        Assert(PowerPointAutomation.IsSlideShowActive(1, 3),
            "黑屏状态不应被误判为结束放映。");
        Assert(PowerPointAutomation.IsSlideShowActive(1, 4),
            "白屏状态不应被误判为结束放映。");
        Assert(!PowerPointAutomation.IsSlideShowActive(0, null),
            "没有放映窗口时不应处于放映状态。");
        Assert(PresentationStateCoordinatorOptions.Default.StartLifecycleTimeout == TimeSpan.FromSeconds(3),
            "默认开始放映确认期限不是 3 秒。");
        Assert(PresentationStateCoordinatorOptions.Default.EndLifecycleTimeout == TimeSpan.FromMilliseconds(4500),
            "默认结束放映确认期限不是 4.5 秒。");
        return Task.CompletedTask;
    }

    public static async Task EndStatePrecedesCompletionAsync()
    {
        var current = true;
        var readsAfterCommand = 0;
        await using var fixture = await Fixture.CreateAsync();
        fixture.Automation.ExecuteHandler = (command, _, _) =>
        {
            Assert(command == "end", "结束放映测试收到了错误命令。");
            readsAfterCommand = 0;
            return Task.FromResult(new PowerPointCommandResult(true, "放映已结束"));
        };
        fixture.Automation.ReadHandler = _ =>
        {
            current = Interlocked.Increment(ref readsAfterCommand) < 2;
            return Task.FromResult(Snapshot(current));
        };

        var result = await fixture.ExecuteAsync("end-command", "presentation.end", "presentation-end", confirmed: true);

        Assert(result.Success, result.Message ?? "结束放映失败。");
        AssertStateBeforeTerminal(fixture.Sink.Messages, "end-command", expectedSlideShow: false, "action.completed");
        Assert(!fixture.Sink.Messages.Any(message => message.Type == "action.progress" && message.Id == "end-command"),
            "生命周期结束动作仍发送了会阻塞终态的冗余进度消息。");
    }

    public static async Task StartStatePrecedesCompletionAsync()
    {
        var readsAfterCommand = 0;
        await using var fixture = await Fixture.CreateAsync();
        fixture.Automation.ExecuteHandler = (command, _, _) =>
        {
            Assert(command == "startBeginning", "开始放映测试收到了错误命令。");
            readsAfterCommand = 0;
            return Task.FromResult(new PowerPointCommandResult(true, "已从头开始放映"));
        };
        fixture.Automation.ReadHandler = _ =>
            Task.FromResult(Snapshot(Interlocked.Increment(ref readsAfterCommand) >= 2));

        var result = await fixture.ExecuteAsync(
            "start-command",
            "presentation.startBeginning",
            "presentation-start-beginning");

        Assert(result.Success, result.Message ?? "开始放映失败。");
        AssertStateBeforeTerminal(fixture.Sink.Messages, "start-command", expectedSlideShow: true, "action.completed");
        Assert(!fixture.Sink.Messages.Any(message => message.Type == "action.progress" && message.Id == "start-command"),
            "生命周期开始动作仍发送了会阻塞终态的冗余进度消息。");
    }

    public static async Task CommandExceptionCanBeRecoveredByConfirmedStateAsync()
    {
        var readCount = 0;
        await using var fixture = await Fixture.CreateAsync();
        fixture.Automation.ExecuteHandler = (_, _, _) =>
            Task.FromResult(new PowerPointCommandResult(
                false,
                "View.Exit 抛出 COM 异常，但 PowerPoint 可能已经执行该操作。"));
        fixture.Automation.ReadHandler = _ =>
            Task.FromResult(Snapshot(Interlocked.Increment(ref readCount) < 2));

        var result = await fixture.ExecuteAsync(
            "recover-end",
            "presentation.end",
            "presentation-end",
            confirmed: true);

        Assert(result.Success, "Exit 抛出 COM 异常后，已确认的非放映状态没有恢复动作结果。");
        AssertStateBeforeTerminal(fixture.Sink.Messages, "recover-end", expectedSlideShow: false, "action.completed");
        Assert(!fixture.Sink.Messages.Any(message => message.Type == "action.failed" && message.Id == "recover-end"),
            "已确认结束放映后仍发送了 action.failed。");
    }

    public static async Task EndCanCompleteAfterLegacyTimeoutAsync()
    {
        var commandStarted = Stopwatch.StartNew();
        await using var fixture = await Fixture.CreateAsync(
            new PresentationStateCoordinatorOptions(
                TimeSpan.FromMilliseconds(300),
                TimeSpan.FromMilliseconds(1800),
                TimeSpan.FromMilliseconds(50)));
        fixture.Automation.ExecuteHandler = (_, _, _) =>
        {
            commandStarted.Restart();
            return Task.FromResult(new PowerPointCommandResult(true, "结束命令已发送"));
        };
        fixture.Automation.ReadHandler = _ =>
            Task.FromResult(Snapshot(commandStarted.Elapsed < TimeSpan.FromMilliseconds(1300)));

        var result = await fixture.ExecuteAsync(
            "slow-end",
            "presentation.end",
            "presentation-end",
            confirmed: true);

        Assert(result.Success, "超过旧 1.2 秒期限后出现的结束状态被错误判为失败。");
        Assert(commandStarted.Elapsed >= TimeSpan.FromMilliseconds(1200),
            "延迟结束测试没有实际越过旧的 1.2 秒期限。");
        AssertStateBeforeTerminal(fixture.Sink.Messages, "slow-end", expectedSlideShow: false, "action.completed");
        Assert(!fixture.Sink.Messages.Any(message => message.Type == "action.failed" && message.Id == "slow-end"),
            "延迟但有效的结束状态产生了 action.failed。");
    }

    public static async Task DelayedStartStateCompletesAsync()
    {
        var readCount = 0;
        await using var fixture = await Fixture.CreateAsync(
            new PresentationStateCoordinatorOptions(
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(20)));
        fixture.Automation.ExecuteHandler = (_, _, _) =>
            Task.FromResult(new PowerPointCommandResult(true, "开始命令已发送"));
        fixture.Automation.ReadHandler = _ =>
            Task.FromResult(Snapshot(Interlocked.Increment(ref readCount) >= 4));

        var result = await fixture.ExecuteAsync(
            "delayed-start",
            "presentation.startCurrent",
            "presentation-start-current");

        Assert(result.Success && readCount >= 4, "延迟出现的开始状态没有被正确确认。");
        AssertStateBeforeTerminal(fixture.Sink.Messages, "delayed-start", expectedSlideShow: true, "action.completed");
    }

    public static async Task TransientReadErrorsRecoverAsync()
    {
        var readCount = 0;
        await using var fixture = await Fixture.CreateAsync(
            new PresentationStateCoordinatorOptions(
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(20)));
        fixture.Automation.ExecuteHandler = (_, _, _) =>
            Task.FromResult(new PowerPointCommandResult(true, "结束命令已发送"));
        fixture.Automation.ReadHandler = _ => Interlocked.Increment(ref readCount) switch
        {
            1 or 2 => Task.FromResult(ErrorSnapshot("PowerPoint 正在切换状态")),
            3 => Task.FromException<PowerPointSnapshot>(new COMException("Temporary read failure.")),
            _ => Task.FromResult(Snapshot(false))
        };

        var result = await fixture.ExecuteAsync(
            "recover-reads",
            "presentation.end",
            "presentation-end",
            confirmed: true);

        Assert(result.Success && readCount >= 4, "临时读取错误后没有恢复到目标状态。");
        AssertStateBeforeTerminal(fixture.Sink.Messages, "recover-reads", expectedSlideShow: false, "action.completed");
        Assert(!PresentationStates(fixture.Sink.Messages).Any(state => state.Status == "error"),
            "生命周期确认期间发布了临时错误快照。");
    }

    public static async Task DelayedSnapshotCannotOverwriteCommandStateAsync()
    {
        var current = false;
        var readCount = 0;
        var staleReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync();
        fixture.Automation.ReadHandler = async cancellationToken =>
        {
            if (Interlocked.Increment(ref readCount) == 1)
            {
                staleReadStarted.TrySetResult();
                await releaseStaleRead.Task.WaitAsync(cancellationToken);
                return Snapshot(false);
            }
            return Snapshot(current);
        };
        fixture.Automation.ExecuteHandler = (command, _, _) =>
        {
            Assert(command == "startBeginning", "延迟快照测试收到了错误命令。");
            current = true;
            return Task.FromResult(new PowerPointCommandResult(true, "已从头开始放映"));
        };

        var staleRefresh = fixture.Coordinator.RefreshAsync();
        await staleReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var action = fixture.ExecuteAsync(
            "ordered-start",
            "presentation.startBeginning",
            "presentation-start-beginning");
        await Task.Delay(30);
        Assert(fixture.Automation.Commands.IsEmpty, "生命周期命令越过了尚未发布的旧读取。" );
        releaseStaleRead.TrySetResult();
        await Task.WhenAll(staleRefresh, action);

        var states = PresentationStates(fixture.Sink.Messages);
        Assert(states.Count >= 2, "没有同时观察到旧状态和命令后的新状态。");
        Assert(states.Zip(states.Skip(1), (left, right) => left.Revision < right.Revision).All(value => value),
            "presentation.state revision 没有严格递增。");
        var commandStateIndex = states.FindIndex(state => state.CommandId == "ordered-start");
        Assert(commandStateIndex >= 0 && states[commandStateIndex].IsSlideShow, "命令状态没有确认放映已开始。");
        Assert(!states.Skip(commandStateIndex + 1).Any(state => !state.IsSlideShow), "旧快照覆盖了命令后的新状态。");
    }

    public static async Task EndThenRestartIsStrictlySerializedAsync()
    {
        var current = true;
        var endStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync();
        fixture.Automation.ExecuteHandler = async (command, _, cancellationToken) =>
        {
            if (command == "end")
            {
                endStarted.TrySetResult();
                await releaseEnd.Task.WaitAsync(cancellationToken);
                current = false;
                return new PowerPointCommandResult(true, "放映已结束");
            }
            Assert(command == "startBeginning", "串行测试收到了意外的生命周期命令。");
            current = true;
            return new PowerPointCommandResult(true, "已从头开始放映");
        };
        fixture.Automation.ReadHandler = _ => Task.FromResult(Snapshot(current));

        var end = fixture.ExecuteAsync("serial-end", "presentation.end", "presentation-end", confirmed: true);
        await endStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var restart = fixture.ExecuteAsync(
            "serial-restart",
            "presentation.startBeginning",
            "presentation-start-beginning");
        await Task.Delay(40);
        Assert(fixture.Automation.Commands.ToArray().SequenceEqual(["end"]), "重新开始命令与结束命令并发执行了。");
        releaseEnd.TrySetResult();
        await Task.WhenAll(end, restart);

        Assert(fixture.Automation.Commands.ToArray().SequenceEqual(["end", "startBeginning"]), "生命周期命令执行顺序错误。");
        var terminalIds = fixture.Sink.Messages
            .Where(message => message.Type == "action.completed")
            .Select(message => message.Id)
            .ToArray();
        Assert(terminalIds.SequenceEqual(["serial-end", "serial-restart"]), "生命周期完成消息顺序错误。");
    }

    public static async Task LifecycleMismatchReturnsFailureAsync()
    {
        await using var fixture = await Fixture.CreateAsync(
            new PresentationStateCoordinatorOptions(
                TimeSpan.FromMilliseconds(80),
                TimeSpan.FromMilliseconds(80),
                TimeSpan.FromMilliseconds(10)));
        fixture.Automation.ExecuteHandler = (_, _, _) =>
            Task.FromResult(new PowerPointCommandResult(true, "已请求开始放映"));
        fixture.Automation.ReadHandler = _ => Task.FromResult(Snapshot(false));

        var result = await fixture.ExecuteAsync(
            "timeout-start",
            "presentation.startBeginning",
            "presentation-start-beginning");

        Assert(!result.Success && result.Message?.Contains("未确认放映已开始", StringComparison.Ordinal) == true,
            "状态不匹配时仍返回了成功，或错误信息不明确。");
        AssertStateBeforeTerminal(fixture.Sink.Messages, "timeout-start", expectedSlideShow: false, "action.failed");
        Assert(!fixture.Sink.Messages.Any(message => message.Type == "action.completed" && message.Id == "timeout-start"),
            "状态不匹配时错误发送了 action.completed。");
        Assert(fixture.Sink.Messages.Count(message => message.Type == "action.failed" && message.Id == "timeout-start") == 1,
            "真正超时时没有且仅发送一次 action.failed。");
    }

    public static async Task NavigationBypassesLifecycleGateAsync()
    {
        var current = true;
        var endStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync();
        fixture.Automation.ExecuteHandler = async (command, _, cancellationToken) =>
        {
            Assert(command == "end", "低延迟翻页测试收到了错误的 PowerPoint 命令。");
            endStarted.TrySetResult();
            await releaseEnd.Task.WaitAsync(cancellationToken);
            current = false;
            return new PowerPointCommandResult(true, "放映已结束");
        };
        fixture.Automation.ReadHandler = _ => Task.FromResult(Snapshot(current));

        var end = fixture.ExecuteAsync("blocked-end", "presentation.end", "presentation-end", confirmed: true);
        await endStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();
        var navigation = fixture.ExecuteAsync(
            "fast-next",
            "presentation.next",
            "presentation-next",
            suppressFeedback: true);
        await navigation.WaitAsync(TimeSpan.FromMilliseconds(250));
        stopwatch.Stop();

        Assert(stopwatch.Elapsed < TimeSpan.FromMilliseconds(200), "翻页动作被生命周期串行门阻塞。");
        Assert(fixture.Hotkey.ExecutionCount == 1, "翻页热键没有立即执行。");
        Assert(!fixture.Sink.Messages.Any(message => message.Id == "fast-next"),
            "suppressFeedback 翻页动作产生了 accepted、progress 或完成回执。");
        releaseEnd.TrySetResult();
        await end;
    }

    private static void AssertStateBeforeTerminal(
        IReadOnlyList<ProtocolMessage> messages,
        string commandId,
        bool expectedSlideShow,
        string terminalType)
    {
        var stateIndex = -1;
        var terminalIndex = -1;
        for (var index = 0; index < messages.Count; index += 1)
        {
            var state = PresentationState(messages[index]);
            if (state?.CommandId == commandId && state.IsSlideShow == expectedSlideShow)
                stateIndex = index;
            if (messages[index].Type == terminalType && messages[index].Id == commandId)
                terminalIndex = index;
        }
        Assert(stateIndex >= 0, $"{commandId} 缺少匹配的 presentation.state。");
        Assert(terminalIndex >= 0, $"{commandId} 缺少 {terminalType}。");
        Assert(stateIndex < terminalIndex, $"{commandId} 的 {terminalType} 早于权威 presentation.state。");
    }

    private static List<PresentationPanelState> PresentationStates(IReadOnlyList<ProtocolMessage> messages) =>
        messages.Select(PresentationState).Where(state => state is not null).Cast<PresentationPanelState>().ToList();

    private static PresentationPanelState? PresentationState(ProtocolMessage message)
    {
        if (message.Type != "state.changed")
            return null;
        var entry = message.Payload.Deserialize<StateEntry>(JsonDefaults.Options);
        return entry is not null && entry.Key == "presentation.state"
            ? entry.Value.Deserialize<PresentationPanelState>(JsonDefaults.Options)
            : null;
    }

    private static PowerPointSnapshot Snapshot(bool isSlideShow) => new(
        isSlideShow ? "presenting" : "ready",
        true,
        true,
        true,
        isSlideShow,
        "Lifecycle test.pptx",
        1,
        3,
        "",
        null,
        []);

    private static PowerPointSnapshot ErrorSnapshot(string error) => new(
        "error",
        true,
        true,
        false,
        false,
        null,
        0,
        0,
        string.Empty,
        error,
        []);

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
            RecordingPanelSink sink,
            ScriptedPowerPointAutomation automation,
            PresentationStateCoordinator coordinator,
            RecordingHotkeyHandler hotkey,
            ActionDispatcher dispatcher)
        {
            _directory = directory;
            Sink = sink;
            Automation = automation;
            Coordinator = coordinator;
            Hotkey = hotkey;
            Dispatcher = dispatcher;
        }

        public RecordingPanelSink Sink { get; }
        public ScriptedPowerPointAutomation Automation { get; }
        public PresentationStateCoordinator Coordinator { get; }
        public RecordingHotkeyHandler Hotkey { get; }
        public ActionDispatcher Dispatcher { get; }

        public static async Task<Fixture> CreateAsync(PresentationStateCoordinatorOptions? options = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "praxis-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sourceConfiguration = Path.Combine(FindRepositoryRoot(), "src", "Praxis.Core.Bluetooth", "data", "praxis.json");
            File.Copy(sourceConfiguration, Path.Combine(directory, "praxis.json"));
            var runtime = new PraxisRuntimeOptions
            {
                DataDirectory = directory,
                Port = 0,
                ServerName = "Lifecycle test",
                EnableTrayIcon = false
            };
            var sink = new RecordingPanelSink();
            var states = new StateStore(sink);
            var automation = new ScriptedPowerPointAutomation();
            var coordinator = new PresentationStateCoordinator(
                automation,
                states,
                options ?? new PresentationStateCoordinatorOptions(
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(250),
                    TimeSpan.FromMilliseconds(10)));
            var configuration = new ConfigurationStore(runtime, NullLogger<ConfigurationStore>.Instance);
            await configuration.InitializeAsync();
            var hotkey = new RecordingHotkeyHandler();
            var dispatcher = new ActionDispatcher(
                configuration,
                [new PowerPointActionHandler(coordinator), hotkey],
                new CommandDeduplicator(),
                sink,
                states,
                new AuditLog(runtime, NullLogger<AuditLog>.Instance));
            return new Fixture(directory, sink, automation, coordinator, hotkey, dispatcher);
        }

        public Task<ActionExecutionResult> ExecuteAsync(
            string commandId,
            string actionId,
            string controlId,
            bool confirmed = false,
            bool suppressFeedback = false) =>
            Dispatcher.ExecuteAsync(
                commandId,
                new ControlInvokeRequest(actionId, controlId, confirmed, SuppressFeedback: suppressFeedback),
                "selftest",
                CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_directory) &&
                Path.GetFileName(_directory).StartsWith("praxis-lifecycle-", StringComparison.Ordinal))
                Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedPowerPointAutomation : IPowerPointAutomation
    {
        public Func<CancellationToken, Task<PowerPointSnapshot>> ReadHandler { get; set; } =
            _ => Task.FromResult(Snapshot(false));
        public Func<string, string?, CancellationToken, Task<PowerPointCommandResult>> ExecuteHandler { get; set; } =
            (command, _, _) => Task.FromResult(new PowerPointCommandResult(true, command));
        public ConcurrentQueue<string> Commands { get; } = new();

        public Task<PowerPointSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            ReadHandler(cancellationToken);

        public Task<PowerPointCommandResult> ExecuteAsync(
            string command,
            string? itemId,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command);
            return ExecuteHandler(command, itemId, cancellationToken);
        }
    }

    private sealed class RecordingHotkeyHandler : IActionHandler
    {
        public string Type => "hotkey";
        public int ExecutionCount { get; private set; }

        public Task<ActionHandlerResult> ExecuteAsync(
            ActionExecutionContext context,
            IProgress<ActionProgress> progress,
            CancellationToken cancellationToken)
        {
            ExecutionCount += 1;
            return Task.FromResult(ActionHandlerResult.Ok("hotkey sent"));
        }
    }

    private sealed class RecordingPanelSink : IPanelMessageSink
    {
        private readonly object _sync = new();
        private readonly List<ProtocolMessage> _messages = [];

        public int Count => 1;
        public IReadOnlyList<ProtocolMessage> Messages
        {
            get
            {
                lock (_sync)
                    return _messages.ToArray();
            }
        }

        public Task BroadcastAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
        {
            lock (_sync)
                _messages.Add(message);
            return Task.CompletedTask;
        }
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
        throw new DirectoryNotFoundException("找不到 Praxis.sln。");
    }
}
