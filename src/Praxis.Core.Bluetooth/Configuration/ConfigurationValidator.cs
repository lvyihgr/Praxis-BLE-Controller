namespace Praxis.Core.Configuration;

public static class ConfigurationValidator
{
    public static List<string> Validate(PraxisConfiguration configuration)
    {
        var errors = new List<string>();
        if (configuration.SchemaVersion != 1)
            errors.Add("当前仅支持 schemaVersion 1。");
        if (configuration.Profiles.Count == 0)
            errors.Add("至少需要一个 Profile。");

        AddDuplicateErrors(configuration.Profiles.Select(x => x.Id), "Profile", errors);
        AddDuplicateErrors(configuration.Actions.Select(x => x.Id), "Action", errors);

        var actionIds = configuration.Actions.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supportedTypes = new HashSet<string>(
            ["noop", "launch", "appToggle", "openUrl", "hotkey", "media", "script", "systemSummary", "powerPoint"],
            StringComparer.OrdinalIgnoreCase);

        foreach (var action in configuration.Actions)
        {
            if (string.IsNullOrWhiteSpace(action.Id) || string.IsNullOrWhiteSpace(action.Name))
                errors.Add("Action 的 id 和 name 不能为空。");
            if (!supportedTypes.Contains(action.Type))
                errors.Add($"Action '{action.Id}' 使用了不支持的类型 '{action.Type}'。");
            if (action.TimeoutSeconds is < 1 or > 600)
                errors.Add($"Action '{action.Id}' 的 timeoutSeconds 必须在 1 到 600 之间。");
        }

        foreach (var profile in configuration.Profiles)
        {
            AddDuplicateErrors(profile.Pages.Select(x => x.Id), $"Profile '{profile.Id}' 中的 Page", errors);
            foreach (var page in profile.Pages)
            {
                if (page.Columns is < 2 or > 8)
                    errors.Add($"Page '{page.Id}' 的 columns 必须在 2 到 8 之间。");
                AddDuplicateErrors(page.Groups.Select(x => x.Id), $"Page '{page.Id}' 中的 Group", errors);
                foreach (var group in page.Groups)
                {
                    if (group.Columns is < 2 or > 8)
                        errors.Add($"Group '{group.Id}' 的 columns 必须在 2 到 8 之间。");
                    if (group.Size is not ("large" or "medium" or "compact"))
                        errors.Add($"Group '{group.Id}' 使用了不支持的尺寸 '{group.Size}'。");
                }

                var controls = page.EnumerateControls().ToArray();
                AddDuplicateErrors(controls.Select(x => x.Id), $"Page '{page.Id}' 中的 Control", errors);
                foreach (var control in controls)
                {
                    if (!actionIds.Contains(control.ActionId))
                        errors.Add($"Control '{control.Id}' 引用了不存在的 Action '{control.ActionId}'。");
                }
            }
        }

        if (!configuration.Profiles.Any(x => string.Equals(x.Id, configuration.ActiveProfileId, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"activeProfileId '{configuration.ActiveProfileId}' 不存在。");

        return errors;
    }

    private static void AddDuplicateErrors(IEnumerable<string> ids, string kind, List<string> errors)
    {
        foreach (var duplicate in ids.Where(x => !string.IsNullOrWhiteSpace(x))
                     .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add($"{kind} id '{duplicate.Key}' 重复。");
    }
}
