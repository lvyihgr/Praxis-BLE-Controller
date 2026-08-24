using Praxis.Core.Configuration;

namespace Praxis.Core.Actions;

public sealed record ActionExecutionContext(
    string CommandId,
    string? ControlId,
    ActionDefinition Definition,
    string? Source = null,
    string? ItemId = null);

public sealed record ActionExecutionResult(
    string CommandId,
    string ActionId,
    string? ControlId,
    bool Success,
    string Status,
    string? Message,
    string? Output,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public interface IActionHandler
{
    string Type { get; }
    Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken);
}

public sealed record ActionHandlerResult(bool Success, string? Message = null, string? Output = null)
{
    public static ActionHandlerResult Ok(string? message = null, string? output = null) => new(true, message, output);
    public static ActionHandlerResult Fail(string message, string? output = null) => new(false, message, output);
}

public sealed record ActionProgress(int Percent, string Message);
