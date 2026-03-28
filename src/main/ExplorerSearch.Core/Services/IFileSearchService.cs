using ExplorerSearch.Core.Models;

namespace ExplorerSearch.Core.Services;

public interface IFileSearchService
{
    Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default);
}