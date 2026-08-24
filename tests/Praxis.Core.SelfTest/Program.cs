using System.Text.Json;
using Praxis.Core.Bluetooth;
using Praxis.Core.Actions;
using Praxis.Core.Configuration;
using Praxis.Core.Hosting;
using Praxis.Core.Protocol;
using Praxis.Core.Presentation;
using Praxis.Core.Security;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Instant controls execute before Bluetooth feedback delivery", ActionDispatchResponsivenessSelfTests.InstantActionStartsBeforeFeedbackDeliveryAsync),
    ("PowerPoint keeps ordered initial feedback", ActionDispatchResponsivenessSelfTests.PowerPointStillWaitsForInitialFeedbackAsync),
    ("Instant controls survive initial feedback failure", ActionDispatchResponsivenessSelfTests.InstantActionSurvivesInitialFeedbackFailureAsync),
    ("默认配置有效", ValidateDefaultConfigurationAsync),
    ("应用图标配置与提取有效", ValidateApplicationIconsAsync),
    ("提取图标可见尺寸正常", ValidateExtractedIconScaleAsync),
    ("应用图标持久缓存可恢复", ValidatePersistentApplicationIconCacheAsync),
    ("系统摘要输出可读取", ValidateSystemSummaryAsync),
    ("重复配置会被拒绝", RejectDuplicateConfigurationAsync),
    ("脚本路径不能越界", ValidateSafePathAsync),
    ("快捷键解析正确", ParseHotkeyAsync),
    ("命令 ID 可以去重", DeduplicateCommandsAsync),
    ("协议消息可序列化", SerializeProtocolAsync),
    ("初始化缓存只请求变更图标", ValidateBootstrapCachePlanAsync),
    ("配对密码学向量一致", SecuritySelfTests.CryptographicVectorsMatchAsync),
    ("安全消息拒绝篡改与重放", SecuritySelfTests.SecureEnvelopeRejectsTamperAndReplayAsync),
    ("首次配对与自动恢复正确", SecuritySelfTests.PairingAndResumeRoundTripAsync),
    ("演示动作配置完整", ValidatePresentationActionsAsync),
    ("演示计时状态正确", ValidatePresentationTimerAsync),
    ("演讲者备注会规范化", NormalizePresentationNotesAsync),
    ("PowerPoint 最近文件列表正确", ValidateRecentPresentationsAsync),
    ("PowerPoint 完成状态会结束放映", PresentationLifecycleSelfTests.SlideShowDoneStateIsNotActiveAsync),
    ("结束状态先于完成消息", PresentationLifecycleSelfTests.EndStatePrecedesCompletionAsync),
    ("开始状态先于完成消息", PresentationLifecycleSelfTests.StartStatePrecedesCompletionAsync),
    ("命令异常可由权威状态恢复", PresentationLifecycleSelfTests.CommandExceptionCanBeRecoveredByConfirmedStateAsync),
    ("结束状态可晚于旧期限出现", PresentationLifecycleSelfTests.EndCanCompleteAfterLegacyTimeoutAsync),
    ("延迟开始状态可正确完成", PresentationLifecycleSelfTests.DelayedStartStateCompletesAsync),
    ("临时读取错误后可恢复", PresentationLifecycleSelfTests.TransientReadErrorsRecoverAsync),
    ("延迟旧快照不会覆盖新状态", PresentationLifecycleSelfTests.DelayedSnapshotCannotOverwriteCommandStateAsync),
    ("结束和重新开始严格串行", PresentationLifecycleSelfTests.EndThenRestartIsStrictlySerializedAsync),
    ("生命周期状态不匹配会失败", PresentationLifecycleSelfTests.LifecycleMismatchReturnsFailureAsync),
    ("翻页不受生命周期串行门影响", PresentationLifecycleSelfTests.NavigationBypassesLifecycleGateAsync)
};

static Task ValidateBootstrapCachePlanAsync()
{
    var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["chatgpt"] = "hash-a",
        ["vscode"] = "hash-b",
        ["presentation.open"] = "hash-c"
    };
    var cached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["chatgpt"] = "HASH-A",
        ["vscode"] = "stale"
    };
    var missing = BluetoothBootstrapCachePlanner.FindMissingIcons(current, cached);
    Assert(missing.Count == 2 && missing.Contains("vscode") && missing.Contains("presentation.open"),
        "初始化缓存没有准确选择缺失或变更的图标。");
    Assert(BluetoothBootstrapCachePlanner.FindMissingIcons(current, current).Count == 0,
        "完全命中的图标缓存仍触发了传输。");
    Assert(BluetoothBootstrapCachePlanner.ShouldUseCachedFastPath(true, 0),
        "完整客户端缓存没有启用快速初始化路径。");
    Assert(!BluetoothBootstrapCachePlanner.ShouldUseCachedFastPath(true, 1) &&
           !BluetoothBootstrapCachePlanner.ShouldUseCachedFastPath(false, 0),
        "缺失图标或旧客户端错误启用了快速初始化路径。");
    return Task.CompletedTask;
}

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Count - failures.Count}/{tests.Count} checks passed.");
if (failures.Count > 0)
{
    Environment.ExitCode = 1;
    Console.WriteLine(string.Join(Environment.NewLine, failures));
}

static async Task ValidateDefaultConfigurationAsync()
{
    var root = FindRepositoryRoot();
    var path = Path.Combine(root, "src", "Praxis.Core.Bluetooth", "data", "praxis.json");
    await using var stream = File.OpenRead(path);
    var configuration = await JsonSerializer.DeserializeAsync<PraxisConfiguration>(stream, JsonDefaults.Options)
        ?? throw new Exception("配置为空。");
    Assert(ConfigurationValidator.Validate(configuration).Count == 0, "默认配置未通过校验。");
    var overview = configuration.Profiles[0].Pages[0];
    var demo = configuration.Profiles[0].Pages[1];
    Assert(configuration.Profiles[0].Pages.Count == 2, "默认面板应包含控制和演示两页。");
    Assert(overview.Name == "控制", "第一页名称应为控制。");
    Assert(overview.Groups.Count == 3, "控制页应包含应用、系统和快捷指令三个分区。");
    Assert(overview.Groups[0].Controls.Count == 7, "应用分区应包含 7 个应用按钮。");
    Assert(overview.Groups[1].Controls.Count == 7, "系统分区应包含 7 个系统按钮。");
    Assert(overview.Groups[2].Controls.Count == 7, "快捷指令分区应包含 7 个紧凑按钮。");
    Assert(overview.EnumerateControls().Count() == 21, "控制页应包含 21 个控件。");
    Assert(demo.Name == "演示", "第二页名称应为演示。");
    Assert(demo.Controls.Count == 0, "演示页应由专用界面呈现，而不是普通控制按钮。");
    Assert(configuration.Actions.Any(action => action.Id == "core.ping"), "顶部连接测试按钮所需动作缺失。");
    Assert(configuration.Actions.Any(action => action.Id == "system.summary"), "顶部状态按钮所需的系统摘要动作缺失。");
    Assert(configuration.Actions.Any(action => action.Id == "shortcut.voiceInput" && action.Parameters.GetValueOrDefault("keys") == "RightAlt+Space"), "语音输入快捷键配置错误。");
    Assert(configuration.Actions.Count(action => action.Type == "appToggle") == 7, "7 个应用应使用任务栏式切换动作。");
    Assert(configuration.Actions.All(action => action.Id != "web.chatgpt"), "旧的 ChatGPT 网页动作应被移除。");
}

static async Task ValidatePresentationActionsAsync()
{
    var root = FindRepositoryRoot();
    var path = Path.Combine(root, "src", "Praxis.Core.Bluetooth", "data", "praxis.json");
    await using var stream = File.OpenRead(path);
    var configuration = await JsonSerializer.DeserializeAsync<PraxisConfiguration>(stream, JsonDefaults.Options)
        ?? throw new Exception("配置为空。");
    var presentationActions = configuration.Actions.Where(action => action.Id.StartsWith("presentation.", StringComparison.Ordinal)).ToArray();
    Assert(presentationActions.Length == 7, "演示页应包含 7 个 PowerPoint 动作。");
    var automationActions = presentationActions.Where(action => action.Type == "powerPoint").ToArray();
    var commands = automationActions.Select(action => action.Parameters.GetValueOrDefault("command")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(commands.SetEquals(["open", "openRecent", "startBeginning", "startCurrent", "end"]), "PowerPoint 自动化动作集合不完整。");
    var previous = presentationActions.Single(action => action.Id == "presentation.previous");
    var next = presentationActions.Single(action => action.Id == "presentation.next");
    Assert(previous.Type == "hotkey" && previous.Parameters.GetValueOrDefault("keys") == "Up", "上一页没有模拟键盘上键。");
    Assert(next.Type == "hotkey" && next.Parameters.GetValueOrDefault("keys") == "Down", "下一页没有模拟键盘下键。");
    var open = presentationActions.Single(action => action.Id == "presentation.open");
    Assert(open.Parameters.ContainsKey("processNames") ||
           open.Parameters.ContainsKey("iconProcess") ||
           open.Parameters.ContainsKey("iconFile"),
        "PowerPoint 动作缺少运行时图标来源配置。");
}

static Task ValidatePresentationTimerAsync()
{
    var tracker = new PresentationSessionTracker();
    var start = DateTimeOffset.Parse("2026-08-05T10:00:00Z");
    Assert(tracker.Update(false, start) == 0, "未放映时计时器不应启动。");
    Assert(tracker.Update(true, start.AddSeconds(2)) == 0, "检测到新放映时应从零开始计时。");
    Assert(tracker.Update(true, start.AddSeconds(14)) == 12, "放映计时没有持续累计。");
    Assert(tracker.Update(false, start.AddSeconds(17)) == 15, "结束放映时没有保留已用时间。");
    Assert(tracker.Update(false, start.AddSeconds(30)) == 15, "放映结束后计时不应继续增加。");
    Assert(tracker.Update(true, start.AddSeconds(40)) == 0, "新的放映没有重置计时。");
    return Task.CompletedTask;
}

static Task NormalizePresentationNotesAsync()
{
    var normalized = PowerPointAutomation.NormalizeNotes("\r\n  第一条  \r第二条\r\n\r\n");
    Assert(normalized == "第一条\n第二条", "备注段落在规范化时丢失。");
    Assert(PowerPointAutomation.NormalizeNotes("  \r\n") == string.Empty, "空备注没有被正确识别。");
    return Task.CompletedTask;
}

static Task ValidateRecentPresentationsAsync()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "praxis-recent-" + Guid.NewGuid().ToString("N"));
    var recentDirectory = Path.Combine(tempRoot, "Recent");
    var targetDirectory = Path.Combine(tempRoot, "Presentations");
    Directory.CreateDirectory(recentDirectory);
    Directory.CreateDirectory(targetDirectory);
    try
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        for (var index = 0; index < 12; index += 1)
        {
            var presentation = Path.Combine(targetDirectory, $"演示文稿 {index + 1}.pptx");
            File.WriteAllText(presentation, string.Empty);
            var shortcut = Path.Combine(recentDirectory, $"最近文件 {index + 1}.lnk");
            File.WriteAllText(shortcut, string.Empty);
            File.SetLastWriteTimeUtc(shortcut, now.AddMinutes(-index));
            targets[shortcut] = presentation;
        }

        var provider = PowerPointRecentFiles.CreateForTesting(
            recentDirectory,
            shortcut => targets.GetValueOrDefault(shortcut));
        var recent = provider.Read();
        Assert(recent.Count == PowerPointRecentFiles.MaximumCount, "最近文件没有限制为最多 10 项。");
        Assert(recent[0].Name == "演示文稿 1.pptx" && recent[^1].Name == "演示文稿 10.pptx", "最近文件排序错误。");
        Assert(recent.All(file => file.IsAvailable), "存在的最近文件被错误标记为不可用。");
        Assert(recent[0].ToPanelItem().Path == recent[0].Path, "最近文件面板数据没有包含文件路径。");
        Assert(provider.FindById(recent[0].Id)?.Path == recent[0].Path, "最近文件标识无法安全解析。");
        Assert(provider.FindById("not-a-real-id") is null, "未知最近文件标识不应被接受。");

        foreach (var shortcut in Directory.EnumerateFiles(recentDirectory, "*.lnk"))
            File.Delete(shortcut);
        targets.Clear();
        for (var index = 0; index < 2; index += 1)
        {
            var shortcut = Path.Combine(recentDirectory, $"少量文件 {index + 1}.lnk");
            File.WriteAllText(shortcut, string.Empty);
            targets[shortcut] = Path.Combine(targetDirectory, $"演示文稿 {index + 1}.pptx");
        }
        Assert(provider.Read().Count == 2, "不足 10 个最近文件时不应补充空项目。");
    }
    finally
    {
        var normalizedRoot = Path.GetFullPath(tempRoot);
        var normalizedTemp = Path.GetFullPath(Path.GetTempPath());
        if (normalizedRoot.StartsWith(normalizedTemp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(normalizedRoot))
            Directory.Delete(normalizedRoot, recursive: true);
    }
    return Task.CompletedTask;
}

static async Task ValidateApplicationIconsAsync()
{
    var root = FindRepositoryRoot();
    var path = Path.Combine(root, "src", "Praxis.Core.Bluetooth", "data", "praxis.json");
    await using var stream = File.OpenRead(path);
    var configuration = await JsonSerializer.DeserializeAsync<PraxisConfiguration>(stream, JsonDefaults.Options)
        ?? throw new Exception("配置为空。");
    foreach (var action in configuration.Actions.Where(action => action.Type == "appToggle"))
    {
        Assert(action.Parameters.ContainsKey("processNames") ||
               action.Parameters.ContainsKey("iconProcess") ||
               action.Parameters.ContainsKey("iconFile") ||
               action.Parameters.ContainsKey("file") ||
               action.Parameters.ContainsKey("packageFamilyName"),
            $"{action.Name} 缺少运行时图标来源配置。");
    }

    var portableAction = CreatePortableIconAction(root);
    var png = await new AppIconProvider().GetPngAsync(portableAction);
    Assert(png is { Length: > 100 and <= 32768 }, "无法从 Praxis 自身可执行文件提取安全尺寸的应用图标。");
}

static async Task ValidatePersistentApplicationIconCacheAsync()
{
    var cacheRoot = Path.Combine(
        Path.GetTempPath(),
        "praxis-icon-cache-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var action = CreatePortableIconAction(FindRepositoryRoot());
        var first = new AppIconProvider(cacheRoot).GetPng(action);
        Assert(first is { Length: > 100 }, "首次图标提取失败。");
        Assert(File.Exists(Path.Combine(cacheRoot, "manifest.json")), "图标缓存清单没有写入磁盘。");

        var restored = new AppIconProvider(cacheRoot).GetPng(action);
        Assert(restored is not null && restored.SequenceEqual(first),
            "新 AppIconProvider 实例没有恢复持久图标缓存。");
    }
    finally
    {
        var normalizedRoot = Path.GetFullPath(cacheRoot);
        var normalizedTemp = Path.GetFullPath(Path.GetTempPath());
        if (normalizedRoot.StartsWith(normalizedTemp, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(normalizedRoot))
        {
            Directory.Delete(normalizedRoot, recursive: true);
        }
    }
}

static Task ValidateExtractedIconScaleAsync()
{
    var root = FindRepositoryRoot();
    var action = CreatePortableIconAction(root);
    var png = new AppIconProvider().GetPng(action)
              ?? throw new Exception("无法读取 Praxis 应用图标。");
    Assert(png.Length > 100, "Praxis 应用图标内容无效。");

    using var stream = new MemoryStream(png);
    using var image = System.Drawing.Image.FromStream(stream);
    using var bitmap = new System.Drawing.Bitmap(image);
    var minimumX = bitmap.Width;
    var minimumY = bitmap.Height;
    var maximumX = -1;
    var maximumY = -1;
    for (var y = 0; y < bitmap.Height; y += 1)
    {
        for (var x = 0; x < bitmap.Width; x += 1)
        {
            if (bitmap.GetPixel(x, y).A <= 8)
                continue;
            minimumX = Math.Min(minimumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumX = Math.Max(maximumX, x);
            maximumY = Math.Max(maximumY, y);
        }
    }

    var visibleWidth = maximumX >= minimumX ? maximumX - minimumX + 1 : 0;
    var visibleHeight = maximumY >= minimumY ? maximumY - minimumY + 1 : 0;
    Assert(visibleWidth >= 64 && visibleHeight >= 64,
        $"提取图标透明边距过大：可见区域仅 {visibleWidth}×{visibleHeight}。");
    return Task.CompletedTask;
}

static ActionDefinition CreatePortableIconAction(string repositoryRoot)
{
    var executable = Path.Combine(
        repositoryRoot,
        "src",
        "Praxis.Core.Bluetooth",
        "bin",
        "Debug",
        "net10.0-windows10.0.22621.0",
        "Praxis.Bluetooth.exe");
    Assert(File.Exists(executable), "未找到用于图标自测的 Praxis 可执行文件。");
    return new ActionDefinition
    {
        Id = "test.icon.portable",
        Name = "图标自测",
        Type = "appToggle",
        Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["file"] = executable
        }
    };
}

static async Task ValidateSystemSummaryAsync()
{
    var definition = new ActionDefinition
    {
        Id = "system.summary",
        Name = "系统摘要",
        Type = "systemSummary"
    };
    var handler = new SystemSummaryActionHandler();
    var result = await handler.ExecuteAsync(
        new ActionExecutionContext("summary-test", "system-summary", definition),
        new Progress<ActionProgress>(),
        CancellationToken.None);
    Assert(result.Success && !string.IsNullOrWhiteSpace(result.Output), "系统摘要没有返回内容。");
    using var summary = JsonDocument.Parse(result.Output!);
    Assert(summary.RootElement.TryGetProperty("Computer", out var computer) && !string.IsNullOrWhiteSpace(computer.GetString()), "系统摘要 JSON 无效。");
    Assert(summary.RootElement.TryGetProperty("CpuUsagePercent", out var cpuUsage) && cpuUsage.ValueKind == JsonValueKind.Number, "系统摘要缺少 CPU 使用率。");
    Assert(cpuUsage.GetDouble() is >= 0 and <= 100, "CPU 使用率超出有效范围。");
    Assert(summary.RootElement.TryGetProperty("GpuUsagePercent", out var gpuUsage) && gpuUsage.ValueKind == JsonValueKind.Number, "系统摘要缺少 GPU 使用率。");
    Assert(gpuUsage.GetDouble() is >= 0 and <= 100, "GPU 使用率超出有效范围。");
    Assert(summary.RootElement.TryGetProperty("GpuDedicatedUsedGB", out var gpuMemory) && gpuMemory.ValueKind == JsonValueKind.Number, "系统摘要缺少显存占用。");
}

static Task RejectDuplicateConfigurationAsync()
{
    var configuration = new PraxisConfiguration
    {
        ActiveProfileId = "same",
        Profiles =
        [
            new ProfileDefinition { Id = "same", Name = "A" },
            new ProfileDefinition { Id = "same", Name = "B" }
        ]
    };
    Assert(ConfigurationValidator.Validate(configuration).Any(error => error.Contains("重复")), "重复 Profile 未被拒绝。");
    return Task.CompletedTask;
}

static Task ValidateSafePathAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "praxis-safe-root");
    var valid = SafePath.ResolveInside(root, "hello.ps1");
    Assert(valid.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "有效路径解析错误。");
    var rejected = false;
    try { _ = SafePath.ResolveInside(root, "..\\outside.ps1"); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "目录穿越路径未被拒绝。");
    return Task.CompletedTask;
}

static Task ParseHotkeyAsync()
{
    var sender = new HotkeySender();
    var keys = sender.Parse("Ctrl+Shift+B");
    Assert(keys.Count == 3, "快捷键数量错误。");
    Assert(keys[0] == 0x11 && keys[1] == 0x10 && keys[2] == 'B', "快捷键代码错误。");
    var voiceKeys = sender.Parse("RightAlt+Space");
    Assert(voiceKeys.Count == 2 && voiceKeys[0] == 0xA5 && voiceKeys[1] == 0x20, "右 Alt + Space 快捷键代码错误。");
    Assert(sender.NativeInputSize == (IntPtr.Size == 8 ? 40 : 28), "Windows INPUT 结构尺寸错误。");
    return Task.CompletedTask;
}

static async Task DeduplicateCommandsAsync()
{
    var deduplicator = new CommandDeduplicator();
    var executions = 0;
    Task<ActionExecutionResult> Factory()
    {
        executions += 1;
        return Task.FromResult(new ActionExecutionResult(
            "same-command", "test", "button", true, "completed", null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    var first = deduplicator.RunOnceAsync("same-command", Factory);
    var second = deduplicator.RunOnceAsync("same-command", Factory);
    await Task.WhenAll(first, second);
    Assert(executions == 1, "相同命令执行了多次。");
}

static Task SerializeProtocolAsync()
{
    var message = ProtocolMessage.Create("test", new { value = 42 }, "fixed-id");
    var json = JsonSerializer.Serialize(message, JsonDefaults.Options);
    var restored = JsonSerializer.Deserialize<ProtocolMessage>(json, JsonDefaults.Options)
        ?? throw new Exception("协议消息反序列化为空。");
    Assert(restored.Id == "fixed-id" && restored.Type == "test", "协议消息往返失败。");
    Assert(restored.Payload.GetProperty("value").GetInt32() == 42, "协议 Payload 丢失。");
    var requestJson = JsonSerializer.Serialize(
        new ControlInvokeRequest("presentation.next", "presentation-next", SuppressFeedback: true),
        JsonDefaults.Options);
    var request = JsonSerializer.Deserialize<ControlInvokeRequest>(requestJson, JsonDefaults.Options)
        ?? throw new Exception("动作请求反序列化为空。");
    Assert(request.SuppressFeedback, "动作请求的低延迟反馈选项丢失。");
    return Task.CompletedTask;
}

static string FindRepositoryRoot()
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

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
