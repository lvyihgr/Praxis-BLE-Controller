using System.Diagnostics;
using System.Text.Json;
using Praxis.Core.Protocol;
using Praxis.Core.State;

namespace Praxis.Core.Presentation;

public sealed record PresentationStateCoordinatorOptions(
    TimeSpan StartLifecycleTimeout,
    TimeSpan EndLifecycleTimeout,
    TimeSpan RetryInterval)
{
    public static PresentationStateCoordinatorOptions Default { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMilliseconds(4500),
        TimeSpan.FromMilliseconds(75));
}

public sealed record PresentationRefreshResult(
    PowerPointSnapshot Snapshot,
    PresentationPanelState? PublishedState);

public sealed class PresentationStateCoordinator
{
    private readonly IPowerPointAutomation _powerPoint;
    private readonly StateStore _states;
    private readonly PresentationStateCoordinatorOptions _options;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly object _refreshSync = new();
    private readonly PresentationSessionTracker _session = new();
    private long _revision;
    private int _consecutiveReadErrors;
    private string? _lastSemanticState;

    public PresentationStateCoordinator(
        IPowerPointAutomation powerPoint,
        StateStore states,
        PresentationStateCoordinatorOptions options)
    {
        _powerPoint = powerPoint;
        _states = states;
        _options = options;
    }

    public async Task<PresentationRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _powerPoint.ReadAsync(cancellationToken);
            if (string.Equals(snapshot.Status, "error", StringComparison.OrdinalIgnoreCase))
            {
                _consecutiveReadErrors += 1;
                if (_consecutiveReadErrors < 3)
                    return new PresentationRefreshResult(snapshot, null);
            }
            else
            {
                _consecutiveReadErrors = 0;
            }

            var published = await PublishLockedAsync(snapshot, commandId: null, forcePublish: false, cancellationToken);
            return new PresentationRefreshResult(snapshot, published);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<PowerPointCommandResult> ExecuteAsync(
        string command,
        string? itemId,
        string commandId,
        bool? expectedSlideShow,
        CancellationToken cancellationToken = default)
    {
        if (expectedSlideShow is not null)
            await _lifecycleGate.WaitAsync(cancellationToken);

        try
        {
            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                PowerPointCommandResult commandResult;
                try
                {
                    commandResult = await _powerPoint.ExecuteAsync(command, itemId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    commandResult = new PowerPointCommandResult(false, exception.Message);
                }

                if (expectedSlideShow is null)
                {
                    if (!commandResult.Success)
                        return commandResult;

                    var snapshot = await _powerPoint.ReadAsync(cancellationToken);
                    await PublishLockedAsync(snapshot, commandId, forcePublish: true, cancellationToken);
                    return commandResult;
                }

                return await ConfirmLifecycleStateLockedAsync(
                    commandResult,
                    expectedSlideShow.Value,
                    commandId,
                    expectedSlideShow.Value
                        ? _options.StartLifecycleTimeout
                        : _options.EndLifecycleTimeout,
                    cancellationToken);
            }
            finally
            {
                _stateGate.Release();
                RequestRefresh();
            }
        }
        finally
        {
            if (expectedSlideShow is not null)
                _lifecycleGate.Release();
        }
    }

    public void RequestRefresh()
    {
        lock (_refreshSync)
        {
            if (_refreshSignal.CurrentCount == 0)
                _refreshSignal.Release();
        }
    }

    public Task<bool> WaitForRefreshAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _refreshSignal.WaitAsync(timeout, cancellationToken);

    private async Task<PowerPointCommandResult> ConfirmLifecycleStateLockedAsync(
        PowerPointCommandResult commandResult,
        bool expectedSlideShow,
        string commandId,
        TimeSpan confirmationTimeout,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        PowerPointSnapshot? lastUsableSnapshot = null;
        string? lastReadError = null;
        while (true)
        {
            PowerPointSnapshot? snapshot = null;
            try
            {
                snapshot = await _powerPoint.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastReadError = exception.Message;
            }

            if (snapshot is not null)
            {
                if (string.Equals(snapshot.Status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    lastReadError = snapshot.Error ?? snapshot.Status;
                }
                else
                {
                    lastUsableSnapshot = snapshot;
                    if (snapshot.IsSlideShow == expectedSlideShow)
                    {
                        await PublishLockedAsync(snapshot, commandId, forcePublish: true, cancellationToken);
                        return commandResult.Success
                            ? commandResult
                            : new PowerPointCommandResult(
                                true,
                                expectedSlideShow ? "已确认 PowerPoint 开始放映" : "已确认 PowerPoint 结束放映");
                    }
                }
            }

            var remaining = confirmationTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(
                remaining < _options.RetryInterval ? remaining : _options.RetryInterval,
                cancellationToken);
        }

        if (lastUsableSnapshot is not null)
            await PublishLockedAsync(lastUsableSnapshot, commandId, forcePublish: true, cancellationToken);

        var expectedText = expectedSlideShow ? "开始" : "结束";
        var diagnostics = new List<string>();
        if (!commandResult.Success && !string.IsNullOrWhiteSpace(commandResult.Message))
            diagnostics.Add($"命令调用：{commandResult.Message}");
        if (!string.IsNullOrWhiteSpace(lastReadError))
            diagnostics.Add($"最后一次读取错误：{lastReadError}");
        if (lastUsableSnapshot is not null)
            diagnostics.Add($"最后可用状态：{lastUsableSnapshot.Status}，isSlideShow={lastUsableSnapshot.IsSlideShow}");
        var diagnosticText = diagnostics.Count == 0 ? string.Empty : $"（{string.Join("；", diagnostics)}）";
        return new PowerPointCommandResult(
            false,
            $"PowerPoint 在 {confirmationTimeout.TotalSeconds:0.0} 秒内未确认放映已{expectedText}{diagnosticText}。");
    }

    private async Task<PresentationPanelState?> PublishLockedAsync(
        PowerPointSnapshot snapshot,
        string? commandId,
        bool forcePublish,
        CancellationToken cancellationToken)
    {
        var value = new PresentationPanelState(
            snapshot.Status,
            snapshot.IsInstalled,
            snapshot.IsRunning,
            snapshot.HasPresentation,
            snapshot.IsSlideShow,
            snapshot.PresentationName,
            snapshot.CurrentSlide,
            snapshot.TotalSlides,
            snapshot.Notes,
            _session.Update(snapshot.IsSlideShow, DateTimeOffset.UtcNow),
            snapshot.Error,
            snapshot.RecentPresentations ?? Array.Empty<RecentPresentation>(),
            Revision: 0,
            CommandId: null);
        var semanticState = JsonSerializer.Serialize(value, JsonDefaults.CompactOptions);
        if (!forcePublish && string.Equals(semanticState, _lastSemanticState, StringComparison.Ordinal))
            return null;

        var published = value with
        {
            Revision = checked(++_revision),
            CommandId = commandId
        };
        _lastSemanticState = semanticState;
        await _states.SetAsync(
            "presentation.state",
            published,
            snapshot.Status,
            "PowerPoint 演示",
            cancellationToken,
            forcePublish: forcePublish);
        return published;
    }
}
