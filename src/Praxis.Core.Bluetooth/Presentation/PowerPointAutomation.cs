using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Praxis.Core.Presentation;

public sealed record PowerPointSnapshot(
    string Status,
    bool IsInstalled,
    bool IsRunning,
    bool HasPresentation,
    bool IsSlideShow,
    string? PresentationName,
    int CurrentSlide,
    int TotalSlides,
    string Notes,
    string? Error = null,
    IReadOnlyList<RecentPresentation>? RecentPresentations = null)
{
    public static PowerPointSnapshot Closed(
        bool installed,
        bool running = false,
        IReadOnlyList<RecentPresentation>? recentPresentations = null) => new(
        installed ? (running ? "starting" : "closed") : "notInstalled",
        installed,
        running,
        false,
        false,
        null,
        0,
        0,
        string.Empty,
        null,
        recentPresentations);
}

public sealed record PowerPointCommandResult(bool Success, string Message);

public interface IPowerPointAutomation
{
    Task<PowerPointSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    Task<PowerPointCommandResult> ExecuteAsync(
        string command,
        string? itemId,
        CancellationToken cancellationToken = default);
}

public sealed class PowerPointAutomation : IPowerPointAutomation, IDisposable
{
    private readonly BlockingCollection<IWorkItem> _queue = new();
    private readonly Thread _thread;
    private readonly ILogger<PowerPointAutomation> _logger;
    private readonly PowerPointRecentFiles _recentFiles;
    private bool _disposed;

    public PowerPointAutomation(
        ILogger<PowerPointAutomation> logger,
        PowerPointRecentFiles recentFiles)
    {
        _logger = logger;
        _recentFiles = recentFiles;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Praxis PowerPoint automation"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<PowerPointSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
        Enqueue(ReadSnapshotCore, cancellationToken);

    public Task<PowerPointCommandResult> ExecuteAsync(
        string command,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, null, cancellationToken);

    public Task<PowerPointCommandResult> ExecuteAsync(
        string command,
        string? itemId,
        CancellationToken cancellationToken = default) =>
        Enqueue(() => ExecuteCore(command, itemId), cancellationToken);

    private Task<T> Enqueue<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = new WorkItem<T>(operation, cancellationToken);
        try
        {
            _queue.Add(item, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(PowerPointAutomation));
        }
        return item.Task;
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                item.Execute();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "PowerPoint automation work item failed.");
                item.Fail(exception);
            }
        }
    }

    private PowerPointSnapshot ReadSnapshotCore()
    {
        var recentPresentations = _recentFiles.Read()
            .Select(file => file.ToPanelItem())
            .ToArray();
        var processRunning = IsProcessRunning();
        if (!TryGetApplication(out var application, out var installed))
            return PowerPointSnapshot.Closed(installed, processRunning, recentPresentations);

        object? presentations = null;
        object? presentation = null;
        object? slides = null;
        object? slideShowWindows = null;
        object? slideShowWindow = null;
        object? slideShowView = null;
        object? activeWindow = null;
        object? editView = null;
        object? slide = null;
        try
        {
            dynamic app = application!;
            presentations = app.Presentations;
            if (Convert.ToInt32(((dynamic)presentations).Count) < 1)
            {
                return new PowerPointSnapshot(
                    "noPresentation", true, true, false, false, null, 0, 0, string.Empty,
                    null, recentPresentations);
            }

            presentation = app.ActivePresentation;
            if (presentation is null)
            {
                return new PowerPointSnapshot(
                    "noPresentation", true, true, false, false, null, 0, 0, string.Empty,
                    null, recentPresentations);
            }

            dynamic deck = presentation;
            var name = Convert.ToString(deck.Name);
            slides = deck.Slides;
            var totalSlides = Convert.ToInt32(((dynamic)slides).Count);

            slideShowWindows = app.SlideShowWindows;
            var slideShowWindowCount = Convert.ToInt32(((dynamic)slideShowWindows).Count);
            int? slideShowState = null;
            if (slideShowWindowCount > 0)
            {
                slideShowWindow = ((dynamic)slideShowWindows).Item(1);
                slideShowView = ((dynamic)slideShowWindow).View;
                try
                {
                    slideShowState = Convert.ToInt32(((dynamic)slideShowView).State);
                }
                catch (COMException)
                {
                }
            }

            var isSlideShow = IsSlideShowActive(slideShowWindowCount, slideShowState);
            var currentSlide = 0;
            if (isSlideShow)
            {
                var isBlank = slideShowState is 3 or 4;
                if (!isBlank)
                {
                    try
                    {
                        slide = ((dynamic)slideShowView!).Slide;
                    }
                    catch (COMException)
                    {
                        slide = null;
                    }
                }

                if (slide is null)
                {
                    try
                    {
                        currentSlide = Convert.ToInt32(((dynamic)slideShowView!).CurrentShowPosition);
                    }
                    catch (COMException)
                    {
                        currentSlide = totalSlides;
                    }
                    currentSlide = Math.Clamp(currentSlide, 1, Math.Max(1, totalSlides));
                    try
                    {
                        slide = ((dynamic)slides).Item(currentSlide);
                    }
                    catch (COMException)
                    {
                        slide = null;
                    }
                }
            }
            else
            {
                try
                {
                    activeWindow = app.ActiveWindow;
                    if (activeWindow is not null)
                    {
                        editView = ((dynamic)activeWindow).View;
                        slide = ((dynamic)editView).Slide;
                    }
                }
                catch (COMException)
                {
                    slide = null;
                }
            }

            if (currentSlide == 0 && slide is not null)
                currentSlide = Convert.ToInt32(((dynamic)slide).SlideIndex);
            var notes = slide is null ? string.Empty : ReadNotes(slide);
            return new PowerPointSnapshot(
                isSlideShow ? "presenting" : "ready",
                true,
                true,
                true,
                isSlideShow,
                name,
                currentSlide,
                totalSlides,
                notes,
                null,
                recentPresentations);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            return new PowerPointSnapshot(
                "error", true, processRunning, false, false, null, 0, 0, string.Empty,
                "暂时无法读取 PowerPoint 状态", recentPresentations);
        }
        finally
        {
            Release(slide);
            Release(editView);
            Release(activeWindow);
            Release(slideShowView);
            Release(slideShowWindow);
            Release(slideShowWindows);
            Release(slides);
            Release(presentation);
            Release(presentations);
            Release(application);
        }
    }

    public static bool IsSlideShowActive(int slideShowWindowCount, int? slideShowViewState) =>
        slideShowWindowCount > 0 && slideShowViewState is not 5;

    private PowerPointCommandResult ExecuteCore(string command, string? itemId)
    {
        if (string.Equals(command, "open", StringComparison.OrdinalIgnoreCase))
            return OpenOrShow();

        if (!TryGetApplication(out var application, out var installed))
            return new PowerPointCommandResult(false, installed ? "PowerPoint 尚未打开" : "这台电脑未安装 PowerPoint");

        object? presentation = null;
        object? slideShowWindows = null;
        object? slideShowWindow = null;
        object? view = null;
        try
        {
            dynamic app = application!;
            if (string.Equals(command, "openRecent", StringComparison.OrdinalIgnoreCase))
                return OpenRecent(app, itemId);

            presentation = app.ActivePresentation;
            if (presentation is null)
                return new PowerPointCommandResult(false, "请先在 PowerPoint 中打开演示文稿");

            if (string.Equals(command, "startBeginning", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "startCurrent", StringComparison.OrdinalIgnoreCase))
                return StartSlideShow(app, presentation, command);

            slideShowWindows = app.SlideShowWindows;
            if (Convert.ToInt32(((dynamic)slideShowWindows).Count) < 1)
                return new PowerPointCommandResult(false, "PowerPoint 当前没有正在进行的放映");

            slideShowWindow = ((dynamic)slideShowWindows).Item(1);
            view = ((dynamic)slideShowWindow).View;
            dynamic showView = view;
            switch (command)
            {
                case "previous":
                    showView.Previous();
                    return new PowerPointCommandResult(true, "已切换到上一页");
                case "next":
                    showView.Next();
                    return new PowerPointCommandResult(true, "已切换到下一页");
                case "end":
                    showView.Exit();
                    return new PowerPointCommandResult(true, "放映已结束");
                default:
                    return new PowerPointCommandResult(false, "不支持的 PowerPoint 操作");
            }
        }
        catch (COMException)
        {
            return new PowerPointCommandResult(false, "PowerPoint 暂时无法执行该操作");
        }
        finally
        {
            Release(view);
            Release(slideShowWindow);
            Release(slideShowWindows);
            Release(presentation);
            Release(application);
        }
    }

    private PowerPointCommandResult OpenRecent(dynamic application, string? itemId)
    {
        var recent = _recentFiles.FindById(itemId);
        if (recent is null)
            return new PowerPointCommandResult(false, "最近文件列表已更新，请重新选择");
        if (!recent.IsAvailable || !File.Exists(recent.Path))
            return new PowerPointCommandResult(false, "文件已移动、删除或当前不可用");

        object? presentations = null;
        object? candidate = null;
        object? candidateWindows = null;
        object? candidateWindow = null;
        object? openedPresentation = null;
        object? openedWindows = null;
        object? openedWindow = null;
        try
        {
            presentations = application.Presentations;
            var count = Convert.ToInt32(((dynamic)presentations).Count);
            for (var index = 1; index <= count; index += 1)
            {
                try
                {
                    candidate = ((dynamic)presentations).Item(index);
                    var fullName = Convert.ToString(((dynamic)candidate).FullName);
                    if (!string.Equals(fullName, recent.Path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    candidateWindows = ((dynamic)candidate).Windows;
                    if (Convert.ToInt32(((dynamic)candidateWindows).Count) > 0)
                    {
                        candidateWindow = ((dynamic)candidateWindows).Item(1);
                        ((dynamic)candidateWindow).Activate();
                    }
                    ActivatePowerPointWindow();
                    return new PowerPointCommandResult(true, $"已显示 {recent.Name}");
                }
                finally
                {
                    Release(candidateWindow);
                    candidateWindow = null;
                    Release(candidateWindows);
                    candidateWindows = null;
                    Release(candidate);
                    candidate = null;
                }
            }

            openedPresentation = ((dynamic)presentations).Open(recent.Path, 0, 0, -1);
            openedWindows = ((dynamic)openedPresentation).Windows;
            if (Convert.ToInt32(((dynamic)openedWindows).Count) > 0)
            {
                openedWindow = ((dynamic)openedWindows).Item(1);
                ((dynamic)openedWindow).Activate();
            }
            ActivatePowerPointWindow();
            return new PowerPointCommandResult(true, $"已打开 {recent.Name}");
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            return new PowerPointCommandResult(false, "PowerPoint 无法打开这个文件，请在电脑上检查文件状态");
        }
        finally
        {
            Release(openedWindow);
            Release(openedWindows);
            Release(openedPresentation);
            Release(candidateWindow);
            Release(candidateWindows);
            Release(candidate);
            Release(presentations);
        }
    }

    private static void ActivatePowerPointWindow()
    {
        string[] processNames = ["POWERPNT"];
        var foreground = Actions.AppWindowController.ForegroundWindow;
        if (Actions.AppWindowController.IsFromProcess(foreground, processNames))
        {
            Actions.AppWindowController.ActivateMaximized(foreground);
            return;
        }
        var windows = Actions.AppWindowController.Find(processNames, requireWindowTitle: false);
        if (windows.Count > 0)
            Actions.AppWindowController.ActivateMaximized(windows[0]);
    }

    private static PowerPointCommandResult OpenOrShow()
    {
        string[] processNames = ["POWERPNT"];
        var foreground = Actions.AppWindowController.ForegroundWindow;
        if (Actions.AppWindowController.IsFromProcess(foreground, processNames) &&
            !Actions.AppWindowController.IsMinimized(foreground))
        {
            Actions.AppWindowController.Minimize(foreground);
            return new PowerPointCommandResult(true, "PowerPoint 已最小化");
        }

        var windows = Actions.AppWindowController.Find(processNames, requireWindowTitle: false);
        if (windows.Count > 0)
        {
            var target = windows.FirstOrDefault(Actions.AppWindowController.IsMinimized);
            if (target == IntPtr.Zero)
                target = windows[0];
            Actions.AppWindowController.ActivateMaximized(target);
            return new PowerPointCommandResult(true, "PowerPoint 已置前并最大化");
        }

        if (Type.GetTypeFromProgID("PowerPoint.Application", throwOnError: false) is null)
            return new PowerPointCommandResult(false, "这台电脑未安装 PowerPoint");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powerpnt.exe",
                UseShellExecute = true
            });
            for (var attempt = 0; attempt < 50; attempt += 1)
            {
                Thread.Sleep(100);
                windows = Actions.AppWindowController.Find(processNames, requireWindowTitle: false);
                if (windows.Count < 1)
                    continue;
                Actions.AppWindowController.ActivateMaximized(windows[0]);
                return new PowerPointCommandResult(true, "PowerPoint 已打开、置前并最大化");
            }
            return new PowerPointCommandResult(true, "PowerPoint 已打开");
        }
        catch (Win32Exception)
        {
            return new PowerPointCommandResult(false, "无法打开 PowerPoint");
        }
    }

    private static PowerPointCommandResult StartSlideShow(dynamic application, object presentation, string command)
    {
        object? settings = null;
        object? activeWindow = null;
        object? editView = null;
        object? activeSlide = null;
        object? showWindow = null;
        object? showView = null;
        try
        {
            dynamic deck = presentation;
            settings = deck.SlideShowSettings;
            dynamic showSettings = settings;
            var startFromCurrent = string.Equals(command, "startCurrent", StringComparison.OrdinalIgnoreCase);
            var currentSlide = 1;
            if (startFromCurrent)
            {
                try
                {
                    activeWindow = application.ActiveWindow;
                    if (activeWindow is not null)
                    {
                        editView = ((dynamic)activeWindow).View;
                        activeSlide = ((dynamic)editView).Slide;
                        currentSlide = Convert.ToInt32(((dynamic)activeSlide).SlideIndex);
                    }
                }
                catch (COMException)
                {
                    currentSlide = 1;
                }
            }

            showSettings.RangeType = 1;
            showWindow = showSettings.Run();
            if (startFromCurrent && currentSlide > 1)
            {
                showView = ((dynamic)showWindow).View;
                ((dynamic)showView).GotoSlide(currentSlide, -1);
            }
            return new PowerPointCommandResult(
                true,
                string.Equals(command, "startBeginning", StringComparison.OrdinalIgnoreCase)
                    ? "已从头开始放映"
                    : "已从当前页开始放映");
        }
        finally
        {
            Release(showView);
            Release(showWindow);
            Release(activeSlide);
            Release(editView);
            Release(activeWindow);
            Release(settings);
        }
    }

    public static string NormalizeNotes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return string.Join("\n", value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse()
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse())
            .Trim();
    }

    private static string ReadNotes(object slide)
    {
        object? notesPage = null;
        object? shapes = null;
        var bodyNotes = new List<string>();
        var fallbackNotes = new List<string>();
        try
        {
            notesPage = ((dynamic)slide).NotesPage;
            shapes = ((dynamic)notesPage).Shapes;
            var count = Convert.ToInt32(((dynamic)shapes).Count);
            for (var index = 1; index <= count; index += 1)
            {
                object? shape = null;
                object? textFrame = null;
                object? textRange = null;
                object? placeholder = null;
                try
                {
                    shape = ((dynamic)shapes).Item(index);
                    if (Convert.ToInt32(((dynamic)shape).HasTextFrame) == 0)
                        continue;
                    textFrame = ((dynamic)shape).TextFrame;
                    if (Convert.ToInt32(((dynamic)textFrame).HasText) == 0)
                        continue;
                    textRange = ((dynamic)textFrame).TextRange;
                    var text = ReadTextRange(textRange);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var placeholderType = 0;
                    try
                    {
                        placeholder = ((dynamic)shape).PlaceholderFormat;
                        placeholderType = Convert.ToInt32(((dynamic)placeholder).Type);
                    }
                    catch (COMException)
                    {
                    }

                    if (placeholderType == 2)
                        bodyNotes.Add(text);
                    else if (placeholderType is not (13 or 14 or 15 or 16))
                        fallbackNotes.Add(text);
                }
                catch (COMException)
                {
                }
                finally
                {
                    Release(placeholder);
                    Release(textRange);
                    Release(textFrame);
                    Release(shape);
                }
            }
        }
        finally
        {
            Release(shapes);
            Release(notesPage);
        }

        return string.Join("\n\n", bodyNotes.Count > 0 ? bodyNotes : fallbackNotes);
    }

    private static string ReadTextRange(object textRange)
    {
        var raw = Convert.ToString(((dynamic)textRange).Text) ?? string.Empty;
        var sourceParagraphs = raw
            .Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', '\r')
            .Split('\r');
        var rendered = new List<string>(sourceParagraphs.Length);
        for (var index = 0; index < sourceParagraphs.Length; index += 1)
        {
            var paragraphText = sourceParagraphs[index].TrimEnd();
            object? paragraph = null;
            object? paragraphFormat = null;
            object? bullet = null;
            try
            {
                paragraph = ((dynamic)textRange).Paragraphs(index + 1, 1);
                paragraphFormat = ((dynamic)paragraph).ParagraphFormat;
                bullet = ((dynamic)paragraphFormat).Bullet;
                var bulletVisible = Convert.ToInt32(((dynamic)bullet).Visible) != 0;
                if (bulletVisible && !string.IsNullOrWhiteSpace(paragraphText) &&
                    !paragraphText.TrimStart().StartsWith('•'))
                    paragraphText = "• " + paragraphText.TrimStart();
            }
            catch (COMException)
            {
            }
            finally
            {
                Release(bullet);
                Release(paragraphFormat);
                Release(paragraph);
            }
            rendered.Add(paragraphText);
        }
        return NormalizeNotes(string.Join("\n", rendered));
    }

    private static bool TryGetApplication(out object? application, out bool installed)
    {
        application = null;
        Type? type;
        try
        {
            type = Type.GetTypeFromProgID("PowerPoint.Application", throwOnError: false);
        }
        catch (COMException)
        {
            type = null;
        }
        installed = type is not null;
        if (type is null)
            return false;

        try
        {
            var classId = type.GUID;
            GetActiveObject(ref classId, IntPtr.Zero, out application);
            return application is not null;
        }
        catch (COMException)
        {
            application = null;
            return false;
        }
    }

    private static bool IsProcessRunning()
    {
        var processes = Process.GetProcessesByName("POWERPNT");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.ReleaseComObject(value);
            }
            catch (InvalidComObjectException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private interface IWorkItem
    {
        void Execute();
        void Fail(Exception exception);
    }

    private sealed class WorkItem<T>(Func<T> operation, CancellationToken cancellationToken) : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }
            _completion.TrySetResult(operation());
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? application);
}
