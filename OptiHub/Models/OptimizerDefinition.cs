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
    public required string LogoPath { get; init; }
    public OptimizerStatus Status { get; set; } = OptimizerStatus.ComingSoon;
}
