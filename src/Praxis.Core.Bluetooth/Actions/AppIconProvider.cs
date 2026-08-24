using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Praxis.Core.Configuration;
using Praxis.Core.Hosting;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Praxis.Core.Actions;

public sealed class AppIconProvider
{
    private const int IconPixels = 96;
    private const int MaximumIconBytes = 32 * 1024;
    private readonly ConcurrentDictionary<string, MemoryCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _manifestSync = new();
    private readonly string? _cacheDirectory;
    private readonly string? _manifestPath;
    private readonly ILogger<AppIconProvider> _logger;
    private Dictionary<string, PersistentCacheEntry> _manifest = new(StringComparer.OrdinalIgnoreCase);

    public AppIconProvider()
        : this((string?)null, NullLogger<AppIconProvider>.Instance)
    {
    }

    public AppIconProvider(string cacheDirectory)
        : this(cacheDirectory, NullLogger<AppIconProvider>.Instance)
    {
    }

    public AppIconProvider(
        PraxisRuntimeOptions runtime,
        ILogger<AppIconProvider> logger)
        : this(Path.Combine(runtime.DataDirectory, "icon-cache"), logger)
    {
    }

    private AppIconProvider(string? cacheDirectory, ILogger<AppIconProvider> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            return;

        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _manifestPath = Path.Combine(_cacheDirectory, "manifest.json");
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            LoadManifest();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or JsonException)
        {
            _logger.LogDebug(exception, "Praxis application icon cache could not be initialized.");
            _manifest = new Dictionary<string, PersistentCacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public byte[]? GetPng(ActionDefinition action)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        foreach (var path in ResolveExecutablePaths(action))
        {
            try
            {
                var sourceSignature = CreateFileSignature(path);
                if (TryGetCached(action.Id, sourceSignature, out var cached))
                    return cached;

                using var icon = ExtractLargeIcon(path);
                if (icon is null)
                    continue;
                using var bitmap = icon.ToBitmap();
                var png = EncodePng(bitmap);
                if (png is null)
                    continue;
                Store(action.Id, sourceSignature, png);
                return png;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException or ExternalException or Win32Exception)
            {
            }
        }
        return null;
    }

    public async Task<byte[]?> GetPngAsync(
        ActionDefinition action,
        CancellationToken cancellationToken = default)
    {
        var packageFamilyName = action.OptionalParameter("packageFamilyName");
        if (string.IsNullOrWhiteSpace(packageFamilyName) || !OperatingSystem.IsWindows())
            return GetPng(action);

        try
        {
            var package = new PackageManager()
                .FindPackagesForUser(string.Empty)
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Id.FamilyName,
                    packageFamilyName,
                    StringComparison.OrdinalIgnoreCase));
            if (package is null)
                return GetPng(action);

            var packageExecutable = ResolvePackageExecutablePath(package, action);
            if (packageExecutable is not null)
            {
                var executableSignature = CreateFileSignature(packageExecutable);
                if (TryGetCached(action.Id, executableSignature, out var executableCached))
                    return executableCached;

                using var icon = ExtractLargeIcon(packageExecutable);
                if (icon is not null)
                {
                    using var bitmap = icon.ToBitmap();
                    var executablePng = EncodePng(bitmap);
                    if (executablePng is not null)
                    {
                        Store(action.Id, executableSignature, executablePng);
                        return executablePng;
                    }
                }
            }

            var packageVersion = package.Id.Version;
            var sourceSignature = string.Join('|',
                "package",
                package.Id.FamilyName,
                packageVersion.Major,
                packageVersion.Minor,
                packageVersion.Build,
                packageVersion.Revision);
            if (TryGetCached(action.Id, sourceSignature, out var cached))
                return cached;

            var entries = await package.GetAppListEntriesAsync().AsTask(cancellationToken);
            var entry = entries.FirstOrDefault();
            if (entry is null)
                return GetPng(action);

            var logo = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(128, 128));
            await using var stream = (await logo.OpenReadAsync().AsTask(cancellationToken)).AsStreamForRead();
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            var png = EncodePng(image);
            if (png is null)
                return GetPng(action);

            Store(action.Id, sourceSignature, png);
            return png;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or COMException or InvalidOperationException or ArgumentException or ExternalException)
        {
            return GetPng(action);
        }
    }

    private bool TryGetCached(string actionId, string sourceSignature, out byte[]? png)
    {
        png = null;
        if (_cache.TryGetValue(actionId, out var memory) &&
            string.Equals(memory.SourceSignature, sourceSignature, StringComparison.Ordinal))
        {
            png = memory.Png;
            return true;
        }

        PersistentCacheEntry? persistent;
        lock (_manifestSync)
        {
            if (!_manifest.TryGetValue(actionId, out persistent) ||
                !string.Equals(persistent.SourceSignature, sourceSignature, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (_cacheDirectory is null || persistent is null)
            return false;

        try
        {
            var path = Path.Combine(_cacheDirectory, GetCacheFileName(actionId));
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > MaximumIconBytes)
                return false;
            var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(contentHash, persistent.ContentHash, StringComparison.OrdinalIgnoreCase))
                return false;

            _cache[actionId] = new MemoryCacheEntry(sourceSignature, bytes);
            png = bytes;
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(exception, "Could not read cached Praxis icon for {ActionId}.", actionId);
            return false;
        }
    }

    private void Store(string actionId, string sourceSignature, byte[] png)
    {
        _cache[actionId] = new MemoryCacheEntry(sourceSignature, png);
        if (_cacheDirectory is null || _manifestPath is null)
            return;

        try
        {
            var fileName = GetCacheFileName(actionId);
            var contentHash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
            var iconPath = Path.Combine(_cacheDirectory, fileName);
            var temporaryIconPath = iconPath + ".tmp";
            lock (_manifestSync)
            {
                File.WriteAllBytes(temporaryIconPath, png);
                File.Move(temporaryIconPath, iconPath, overwrite: true);
                _manifest[actionId] = new PersistentCacheEntry
                {
                    SourceSignature = sourceSignature,
                    ContentHash = contentHash
                };
                PersistManifestLocked();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(exception, "Could not persist Praxis icon cache for {ActionId}.", actionId);
        }
    }

    private void LoadManifest()
    {
        if (_manifestPath is null || !File.Exists(_manifestPath))
            return;
        var json = File.ReadAllText(_manifestPath);
        _manifest = JsonSerializer.Deserialize<Dictionary<string, PersistentCacheEntry>>(json)
                    ?? new Dictionary<string, PersistentCacheEntry>(StringComparer.OrdinalIgnoreCase);
        _manifest = new Dictionary<string, PersistentCacheEntry>(_manifest, StringComparer.OrdinalIgnoreCase);
    }

    private void PersistManifestLocked()
    {
        if (_manifestPath is null)
            return;
        var temporaryPath = _manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_manifest));
        File.Move(temporaryPath, _manifestPath, overwrite: true);
    }

    private static string CreateFileSignature(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return string.Join('|',
            "file",
            file.FullName.ToUpperInvariant(),
            file.Length,
            file.LastWriteTimeUtc.Ticks);
    }

    private static string GetCacheFileName(string actionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actionId)))
            .ToLowerInvariant() + ".png";

    private static byte[]? EncodePng(Image source)
    {
        using var bitmap = new Bitmap(IconPixels, IconPixels, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            var scale = Math.Min((double)IconPixels / source.Width, (double)IconPixels / source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var left = (IconPixels - width) / 2;
            var top = (IconPixels - height) / 2;
            graphics.DrawImage(source, new Rectangle(left, top, width, height));
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.Length is > 0 and <= MaximumIconBytes ? output.ToArray() : null;
    }

    private static IReadOnlyList<string> ResolveExecutablePaths(ActionDefinition action)
    {
        var paths = new List<string>();
        foreach (var parameter in new[] { "iconFile", "file" })
        {
            var configured = action.OptionalParameter(parameter);
            if (string.IsNullOrWhiteSpace(configured))
                continue;
            var expanded = Environment.ExpandEnvironmentVariables(configured);
            if (File.Exists(expanded))
                paths.Add(expanded);
        }

        foreach (var processName in ResolveProcessNames(action))
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var processPath = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
                            paths.Add(processPath);
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                    }
                }
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ResolvePackageExecutablePath(Package package, ActionDefinition action)
    {
        string installedPath;
        try
        {
            installedPath = package.InstalledLocation.Path;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException)
        {
            return null;
        }

        foreach (var processName in ResolveProcessNames(action))
        {
            var executableName = Path.GetFileNameWithoutExtension(processName) + ".exe";
            foreach (var relativeDirectory in new[] { string.Empty, "app" })
            {
                var candidate = Path.Combine(installedPath, relativeDirectory, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }

            try
            {
                var candidate = Directory.EnumerateFiles(
                        installedPath,
                        executableName,
                        SearchOption.AllDirectories)
                    .OrderBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (candidate is not null)
                    return candidate;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveProcessNames(ActionDefinition action)
    {
        var configured = action.OptionalParameter("iconProcess")
                         ?? action.OptionalParameter("processNames");
        return string.IsNullOrWhiteSpace(configured)
            ? []
            : configured.Split(
                [',', ';', '|'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static Icon? ExtractLargeIcon(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
            return new Icon(path, 128, 128);

        var handles = new IntPtr[1];
        var identifiers = new uint[1];
        var extracted = PrivateExtractIcons(path, 0, 128, 128, handles, identifiers, 1, 0);
        if (extracted > 0 && handles[0] != IntPtr.Zero)
        {
            try
            {
                using var borrowed = Icon.FromHandle(handles[0]);
                return (Icon)borrowed.Clone();
            }
            finally
            {
                DestroyIcon(handles[0]);
            }
        }
        return Icon.ExtractAssociatedIcon(path);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string fileName,
        int iconIndex,
        int iconWidth,
        int iconHeight,
        IntPtr[] iconHandles,
        uint[] iconIdentifiers,
        uint iconCount,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private sealed record MemoryCacheEntry(string SourceSignature, byte[] Png);
    private sealed class PersistentCacheEntry
    {
        public string SourceSignature { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
    }
}
