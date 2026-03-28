namespace ExplorerSearch.Core.Models;

public sealed class SearchOptions
{
    public IReadOnlyList<string> RootDirectories { get; init; } = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
    public bool IncludeSubdirectories { get; init; } = true;
    public int MaxResults { get; init; } = 500;
    public bool MatchCase { get; init; } = false;
}