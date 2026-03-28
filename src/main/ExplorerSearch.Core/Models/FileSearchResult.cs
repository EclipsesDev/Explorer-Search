namespace ExplorerSearch.Core.Models;

public sealed class FileSearchResult
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
}
