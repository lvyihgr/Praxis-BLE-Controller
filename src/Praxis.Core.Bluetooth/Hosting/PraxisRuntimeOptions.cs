namespace Praxis.Core.Hosting;

public sealed class PraxisRuntimeOptions
{
    public required string DataDirectory { get; init; }
    public required string ServerName { get; init; }
    public int Port { get; init; }
    public bool EnableTrayIcon { get; init; }
}

public static class AppVersion
{
    public const string Value = "1.2.0";
}
