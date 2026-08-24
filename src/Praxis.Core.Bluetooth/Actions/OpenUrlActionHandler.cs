using System.Diagnostics;

namespace Praxis.Core.Actions;

public sealed class OpenUrlActionHandler : IActionHandler
{
    public string Type => "openUrl";

    public Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawUrl = context.Definition.RequireParameter("url");
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("只允许打开 HTTP 或 HTTPS 地址。");

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        progress.Report(new ActionProgress(100, "已在默认浏览器中打开"));
        return Task.FromResult(ActionHandlerResult.Ok("已打开网页"));
    }
}
