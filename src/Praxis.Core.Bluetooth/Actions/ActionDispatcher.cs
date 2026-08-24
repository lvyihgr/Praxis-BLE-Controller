using Praxis.Core.Configuration;
using Praxis.Core.Hosting;
using Praxis.Core.Protocol;
using Praxis.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Praxis.Core.Actions;

public sealed class ActionDispatcher
{
    private readonly ConfigurationStore _configuration;
    private readonly IReadOnlyDictionary<string, IActionHandler> _handlers;
    private readonly CommandDeduplicator _deduplicator;
    private readonly IPanelMessageSink _panels;
    private readonly StateStore _states;
    private readonly AuditLog _audit;
    private readonly ILogger<ActionDispatcher> _logger;
    private readonly SemaphoreSlim _concurrency = new(4, 4);
    private readonly SemaphoreSlim _rapidInput = new(1, 1);
    private readonly SemaphoreSlim _presentationLifecycle = new(1, 1);

    public ActionDispatcher(
        ConfigurationStore configuration,
        IEnumerable<IActionHandler> handlers,
        CommandDeduplicator deduplicator,
        IPanelMessageSink panels,
        StateStore states,
        AuditLog audit,
        ILogger<ActionDispatcher>? logger = null)
    {
        _configuration = configuration;
        _handlers = handlers.ToDictionary(x => x.Type, StringComparer.OrdinalIgnoreCase);
        _deduplicator = deduplicator;
        _panels = panels;
        _states = states;
        _audit = audit;
        _logger = logger ?? NullLogger<ActionDispatcher>.Instance;
    }

    public Task<ActionExecutionResult> ExecuteAsync(
        string commandId,
        ControlInvokeRequest request,
        string? source,
        CancellationToken cancellationToken) =>
        _deduplicator.RunOnceAsync(commandId, () => ExecuteCoreAsync(commandId, request, source, cancellationToken));

    private async Task<ActionExecutionResult> ExecuteCoreAsync(
        string commandId,
        ControlInvokeRequest request,
        string? source,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var definition = await _configuration.FindActionAsync(request.ActionId, cancellationToken);
        if (definition is null)
            return await RejectAsync(commandId, request, source, "找不到对应的动作。", startedAt, cancellationToken);
        if (definition.RequiresConfirmation && !request.Confirmed)
            return await RejectAsync(commandId, request, source, "该动作需要确认。", startedAt, cancellationToken);
        if (!_handlers.TryGetValue(definition.Type, out var handler))
            return await RejectAsync(commandId, request, source, "当前未安装对应的动作处理器。", startedAt, cancellationToken);

        var executionGate = IsPresentationLifecycle(definition.Id)
            ? _presentationLifecycle
            : request.SuppressFeedback ? _rapidInput : _concurrency;
        await executionGate.WaitAsync(cancellationToken);
        var initialFeedback = Task.CompletedTask;
        try
        {
            if (!request.SuppressFeedback)
            {
                if (CanExecuteDuringInitialFeedback(definition))
                {
                    initialFeedback = PublishInitialFeedbackSafelyAsync(
                        commandId,
                        request.ControlId,
                        definition,
                        cancellationToken);
                }
                else
                {
                    await PublishInitialFeedbackAsync(
                        commandId,
                        request.ControlId,
                        definition,
                        cancellationToken);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
            var progress = request.SuppressFeedback
                ? new OrderedActionProgress(_ => Task.CompletedTask)
                : new OrderedActionProgress(async update =>
                {
                    await initialFeedback.ConfigureAwait(false);
                    await _panels.BroadcastAsync(ProtocolMessage.Create("action.progress", new
                    {
                        commandId,
                        actionId = definition.Id,
                        controlId = request.ControlId,
                        percent = Math.Clamp(update.Percent, 0, 100),
                        message = update.Message
                    }, commandId), CancellationToken.None).ConfigureAwait(false);
                });

            var handlerResult = await handler.ExecuteAsync(
                new ActionExecutionContext(commandId, request.ControlId, definition, source, request.ItemId),
                progress,
                timeout.Token);
            await initialFeedback;
            await progress.DrainAsync();
            var result = new ActionExecutionResult(
                commandId,
                definition.Id,
                request.ControlId,
                handlerResult.Success,
                handlerResult.Success ? "completed" : "failed",
                handlerResult.Message,
                handlerResult.Output,
                startedAt,
                DateTimeOffset.UtcNow);
            await CompleteAsync(result, source, request.SuppressFeedback, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await initialFeedback;
            var result = new ActionExecutionResult(
                commandId, definition.Id, request.ControlId, false, "failed",
                $"动作在 {definition.TimeoutSeconds} 秒后超时。", null, startedAt, DateTimeOffset.UtcNow);
            await CompleteAsync(result, source, request.SuppressFeedback, CancellationToken.None);
            return result;
        }
        catch (Exception exception)
        {
            await initialFeedback;
            var result = new ActionExecutionResult(
                commandId, definition.Id, request.ControlId, false, "failed",
                exception.Message, null, startedAt, DateTimeOffset.UtcNow);
            await CompleteAsync(result, source, request.SuppressFeedback, CancellationToken.None);
            return result;
        }
        finally
        {
            executionGate.Release();
        }
    }

    private async Task PublishInitialFeedbackAsync(
        string commandId,
        string? controlId,
        ActionDefinition definition,
        CancellationToken cancellationToken)
    {
        await _panels.BroadcastAsync(ProtocolMessage.Create("action.accepted", new
        {
            commandId,
            actionId = definition.Id,
            controlId,
            name = definition.Name
        }, commandId), cancellationToken);
        await SetActionStateAsync(controlId, "running", definition.Name, cancellationToken);
    }

    private async Task PublishInitialFeedbackSafelyAsync(
        string commandId,
        string? controlId,
        ActionDefinition definition,
        CancellationToken cancellationToken)
    {
        try
        {
            await PublishInitialFeedbackAsync(commandId, controlId, definition, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not deliver the initial feedback for action {ActionId}; local execution was not delayed.",
                definition.Id);
        }
    }

    private sealed class OrderedActionProgress : IProgress<ActionProgress>
    {
        private readonly object _sync = new();
        private readonly Func<ActionProgress, Task> _send;
        private Task _tail = Task.CompletedTask;

        public OrderedActionProgress(Func<ActionProgress, Task> send)
        {
            _send = send;
        }

        public void Report(ActionProgress update)
        {
            lock (_sync)
                _tail = SendAfterAsync(_tail, update);
        }

        public Task DrainAsync()
        {
            lock (_sync)
                return _tail;
        }

        private async Task SendAfterAsync(Task previous, ActionProgress update)
        {
            await previous.ConfigureAwait(false);
            await _send(update).ConfigureAwait(false);
        }
    }

    private async Task<ActionExecutionResult> RejectAsync(
        string commandId,
        ControlInvokeRequest request,
        string? source,
        string message,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var result = new ActionExecutionResult(
            commandId, request.ActionId, request.ControlId, false, "rejected", message, null, startedAt, DateTimeOffset.UtcNow);
        await CompleteAsync(result, source, request.SuppressFeedback, cancellationToken);
        return result;
    }

    private async Task CompleteAsync(
        ActionExecutionResult result,
        string? source,
        bool suppressFeedback,
        CancellationToken cancellationToken)
    {
        if (suppressFeedback)
        {
            if (!result.Success)
                await _audit.WriteAsync("action." + result.Status, source, false, $"{result.ActionId}: {result.Message}", cancellationToken);
            return;
        }
        await _panels.BroadcastAsync(ProtocolMessage.Create(
            result.Success ? "action.completed" : "action.failed", result, result.CommandId), cancellationToken);
        try
        {
            await SetActionStateAsync(
                result.ControlId,
                result.Success ? "success" : "error",
                result.Message,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not publish the post-action control state after the terminal response.");
        }
        try
        {
            await _audit.WriteAsync(
                "action." + result.Status,
                source,
                result.Success,
                $"{result.ActionId}: {result.Message}",
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not write the audit entry after the terminal action response.");
        }
    }

    private Task SetActionStateAsync(string? controlId, string status, string? label, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(controlId)
            ? Task.CompletedTask
            : _states.SetAsync($"control:{controlId}", status, status, label, cancellationToken);

    private static bool IsPresentationLifecycle(string actionId) => actionId is
        "presentation.startBeginning" or "presentation.startCurrent" or "presentation.end";

    private static bool CanExecuteDuringInitialFeedback(ActionDefinition definition) =>
        definition.Type.Equals("appToggle", StringComparison.OrdinalIgnoreCase) ||
        definition.Type.Equals("launch", StringComparison.OrdinalIgnoreCase) ||
        definition.Type.Equals("hotkey", StringComparison.OrdinalIgnoreCase) ||
        definition.Type.Equals("media", StringComparison.OrdinalIgnoreCase);
}
