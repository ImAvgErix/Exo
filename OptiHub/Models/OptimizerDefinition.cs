namespace OptiHub.Models;

public enum OptimizerStatus
{
    Available,
    Applied,
    ComingSoon
}

public sealed class OptimizerDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Description { get; init; }
    public required string AccentGlyph { get; init; }
    public string? LogoPath { get; init; }
    public OptimizerStatus Status { get; set; } = OptimizerStatus.ComingSoon;
    public string? Teaser { get; init; }
}
