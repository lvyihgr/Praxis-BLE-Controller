namespace Praxis.Core.Configuration;

public sealed record PraxisConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public string ActiveProfileId { get; init; } = "default";
    public List<ProfileDefinition> Profiles { get; init; } = [];
    public List<ActionDefinition> Actions { get; init; } = [];
}

public sealed record ProfileDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<PageDefinition> Pages { get; init; } = [];
}

public sealed record PageDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Columns { get; init; } = 4;
    public List<ControlDefinition> Controls { get; init; } = [];
    public List<ControlGroupDefinition> Groups { get; init; } = [];
}

public sealed record ControlGroupDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Columns { get; init; } = 4;
    public string Size { get; init; } = "medium";
    public List<ControlDefinition> Controls { get; init; } = [];
}

public sealed record ControlDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Subtitle { get; init; }
    public string? Icon { get; init; }
    public string Color { get; init; } = "blue";
    public required string ActionId { get; init; }
    public string? StateBinding { get; init; }
}

public sealed record ActionDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int TimeoutSeconds { get; init; } = 30;
    public bool RequiresConfirmation { get; init; }
}
