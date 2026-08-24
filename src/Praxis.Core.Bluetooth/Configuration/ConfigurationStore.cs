using System.Text.Json;
using Praxis.Core.Hosting;
using Praxis.Core.Protocol;

namespace Praxis.Core.Configuration;

public sealed class ConfigurationStore(PraxisRuntimeOptions runtime, ILogger<ConfigurationStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(runtime.DataDirectory, "praxis.json");
    private PraxisConfiguration? _current;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
                throw new FileNotFoundException("找不到 Praxis 配置文件。", _path);
            _current = await ReadAndValidateAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PraxisConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _current ?? throw new InvalidOperationException("配置尚未初始化。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(PraxisConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path))
            {
                var backupDirectory = Path.Combine(Path.GetDirectoryName(_path)!, "backups");
                Directory.CreateDirectory(backupDirectory);
                var backupPath = Path.Combine(backupDirectory, $"praxis-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");
                File.Copy(_path, backupPath, overwrite: false);
            }

            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(configuration, JsonDefaults.Options),
                cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
            _current = configuration;
            logger.LogInformation("Praxis configuration updated.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActionDefinition?> FindActionAsync(string id, CancellationToken cancellationToken = default)
    {
        var configuration = await GetAsync(cancellationToken);
        return configuration.Actions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<PraxisConfiguration> ReadAndValidateAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_path);
        var configuration = await JsonSerializer.DeserializeAsync<PraxisConfiguration>(stream, JsonDefaults.Options, cancellationToken)
            ?? throw new InvalidOperationException("Praxis 配置文件为空。");
        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
            throw new InvalidOperationException("Praxis 配置无效：" + string.Join("；", errors));
        return configuration;
    }
}
