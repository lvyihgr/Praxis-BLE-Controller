using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Praxis.Core.Presentation;

public sealed record RecentPresentation(
    string Id,
    string Name,
    string Folder,
    string Path,
    bool IsAvailable);

public sealed record RecentPresentationFile(
    string Id,
    string Name,
    string Folder,
    string Path,
    bool IsAvailable)
{
    public RecentPresentation ToPanelItem() => new(Id, Name, Folder, Path, IsAvailable);
}

public sealed class PowerPointRecentFiles
{
    public const int MaximumCount = 10;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".ppsm", ".pot", ".potx", ".potm"
    };

    private readonly string _recentDirectory;
    private readonly Func<string, string?> _shortcutResolver;

    public PowerPointRecentFiles()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft",
                "Office",
                "Recent"),
            ResolveShortcut)
    {
    }

    private PowerPointRecentFiles(string recentDirectory, Func<string, string?> shortcutResolver)
    {
        _recentDirectory = recentDirectory;
        _shortcutResolver = shortcutResolver;
    }

    public static PowerPointRecentFiles CreateForTesting(
        string recentDirectory,
        Func<string, string?> shortcutResolver) =>
        new(recentDirectory, shortcutResolver);

    public IReadOnlyList<RecentPresentationFile> Read()
    {
        if (!Directory.Exists(_recentDirectory))
            return Array.Empty<RecentPresentationFile>();

        var result = new List<RecentPresentationFile>(MaximumCount);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileInfo> shortcuts;
        try
        {
            shortcuts = new DirectoryInfo(_recentDirectory)
                .EnumerateFiles("*.lnk", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<RecentPresentationFile>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<RecentPresentationFile>();
        }

        foreach (var shortcut in shortcuts)
        {
            string? target;
            try
            {
                target = _shortcutResolver(shortcut.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(target))
                continue;
            target = Environment.ExpandEnvironmentVariables(target.Trim());
            if (!Path.IsPathFullyQualified(target) || !SupportedExtensions.Contains(Path.GetExtension(target)))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(target);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!seenPaths.Add(fullPath))
                continue;

            result.Add(new RecentPresentationFile(
                CreateId(fullPath),
                Path.GetFileName(fullPath),
                GetFolderName(fullPath),
                fullPath,
                File.Exists(fullPath)));
            if (result.Count == MaximumCount)
                break;
        }

        return result;
    }

    public RecentPresentationFile? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
            return null;
        return Read().FirstOrDefault(file => string.Equals(file.Id, id, StringComparison.Ordinal));
    }

    private static string CreateId(string path)
    {
        var normalized = path.Trim().ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string GetFolderName(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            return "本地文件";
        var name = new DirectoryInfo(directory).Name;
        return string.IsNullOrWhiteSpace(name) ? directory : name;
    }

    private static string? ResolveShortcut(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType is null)
                return null;
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return null;
            shortcut = ((dynamic)shell).CreateShortcut(shortcutPath);
            return Convert.ToString(((dynamic)shortcut).TargetPath);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Release(shortcut);
            Release(shell);
        }
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        try
        {
            Marshal.ReleaseComObject(value);
        }
        catch (InvalidComObjectException)
        {
        }
    }
}
