namespace Praxis.Core.Configuration;

public static class ConfigurationTraversal
{
    public static IEnumerable<ControlDefinition> EnumerateControls(this PageDefinition page) =>
        page.Controls.Concat(page.Groups.SelectMany(group => group.Controls));
}
