namespace Praxis.Core.Presentation;

public sealed record PresentationPanelState(
    string Status,
    bool IsInstalled,
    bool IsRunning,
    bool HasPresentation,
    bool IsSlideShow,
    string? PresentationName,
    int CurrentSlide,
    int TotalSlides,
    string Notes,
    int ElapsedSeconds,
    string? Error,
    IReadOnlyList<RecentPresentation> RecentPresentations,
    long Revision = 0,
    string? CommandId = null);

public sealed class PresentationSessionTracker
{
    private bool _wasPresenting;
    private DateTimeOffset? _startedAt;
    private int _elapsedSeconds;

    public int Update(bool isPresenting, DateTimeOffset now)
    {
        if (isPresenting)
        {
            if (!_wasPresenting || _startedAt is null)
            {
                _startedAt = now;
                _elapsedSeconds = 0;
            }
            else
            {
                _elapsedSeconds = Math.Max(0, (int)(now - _startedAt.Value).TotalSeconds);
            }
        }
        else if (_wasPresenting && _startedAt is not null)
        {
            _elapsedSeconds = Math.Max(0, (int)(now - _startedAt.Value).TotalSeconds);
            _startedAt = null;
        }

        _wasPresenting = isPresenting;
        return _elapsedSeconds;
    }
}

public sealed class PresentationStateMonitor(
    PresentationStateCoordinator coordinator,
    ILogger<PresentationStateMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var refresh = await coordinator.RefreshAsync(stoppingToken);
                var delay = refresh.PublishedState is null &&
                    string.Equals(refresh.Snapshot.Status, "error", StringComparison.OrdinalIgnoreCase)
                    ? 150
                    : refresh.Snapshot.IsSlideShow ? 350 : 900;
                await coordinator.WaitForRefreshAsync(
                    TimeSpan.FromMilliseconds(delay),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not refresh PowerPoint presentation state.");
                await Task.Delay(1200, stoppingToken);
            }
        }
    }
}
