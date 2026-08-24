using Praxis.Core.Presentation;

namespace Praxis.Core.Actions;

public sealed class PowerPointActionHandler(
    PresentationStateCoordinator coordinator) : IActionHandler
{
    public string Type => "powerPoint";

    public async Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        var command = context.Definition.RequireParameter("command");
        var expectedSlideShow = context.Definition.Id switch
        {
            "presentation.startBeginning" or "presentation.startCurrent" => true,
            "presentation.end" => false,
            _ => (bool?)null
        };
        var result = await coordinator.ExecuteAsync(
            command,
            context.ItemId,
            context.CommandId,
            expectedSlideShow,
            cancellationToken);
        if (!result.Success)
            return ActionHandlerResult.Fail(result.Message);
        if (expectedSlideShow is null)
            progress.Report(new ActionProgress(100, result.Message));
        return ActionHandlerResult.Ok(result.Message);
    }
}
