namespace ExplorerSearch.Core.Models;

public sealed class IndexBuildProgress
{
    public string RootPath { get; init; } = string.Empty;
    public int ProcessedFiles { get; init; }
    public int TotalFiles { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public string? CurrentFileName { get; init; }
}
