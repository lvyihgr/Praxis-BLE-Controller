using Praxis.Core.Configuration;

namespace Praxis.Core.Actions;

public static class ActionParameterExtensions
{
    public static string RequireParameter(this ActionDefinition action, string name)
    {
        if (!action.Parameters.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Action '{action.Id}' 缺少参数 '{name}'。");
        return value;
    }

    public static string? OptionalParameter(this ActionDefinition action, string name) =>
        action.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
