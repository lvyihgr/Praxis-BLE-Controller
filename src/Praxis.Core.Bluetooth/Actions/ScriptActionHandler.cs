using System.Diagnostics;
using System.Text;
using Praxis.Core.Hosting;
using Praxis.Core.Security;

namespace Praxis.Core.Actions;

public sealed class ScriptActionHandler(PraxisRuntimeOptions runtime) : IActionHandler
{
    public string Type => "script";

    public async Task<ActionHandlerResult> ExecuteAsync(
        ActionExecutionContext context,
        IProgress<ActionProgress> progress,
        CancellationToken cancellationToken)
    {
        var relativePath = context.Definition.RequireParameter("file");
        var scriptsDirectory = Path.Combine(runtime.DataDirectory, "scripts");
        var scriptPath = SafePath.ResolveInside(scriptsDirectory, relativePath);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("找不到已配置的脚本。", scriptPath);

        var startInfo = CreateStartInfo(scriptPath, context.Definition.OptionalParameter("arguments"));
        using var process = new Process { StartInfo = startInfo };
        progress.Report(new ActionProgress(10, "脚本已启动"));
        if (!process.Start())
            return ActionHandlerResult.Fail("无法启动脚本。");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = Limit(await outputTask, 8_000);
        var error = Limit(await errorTask, 4_000);

        if (process.ExitCode != 0)
            return ActionHandlerResult.Fail($"脚本退出码：{process.ExitCode}", error);

        progress.Report(new ActionProgress(100, "脚本执行完成"));
        return ActionHandlerResult.Ok("脚本执行完成", output);
    }

    private static ProcessStartInfo CreateStartInfo(string path, string? arguments)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var startInfo = extension switch
        {
            ".ps1" => new ProcessStartInfo("powershell.exe"),
            ".cmd" or ".bat" => new ProcessStartInfo("cmd.exe"),
            ".exe" => new ProcessStartInfo(path),
            _ => throw new InvalidOperationException("仅允许执行 .ps1、.cmd、.bat 或 .exe 文件。")
        };

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = Path.GetDirectoryName(path)!;

        if (extension == ".ps1")
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(path);
        }
        else if (extension is ".cmd" or ".bat")
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(path);
        }

        if (!string.IsNullOrWhiteSpace(arguments))
            startInfo.ArgumentList.Add(arguments);
        return startInfo;
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
