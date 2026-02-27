using DatabaseRestQuery.Api.Models;

namespace DatabaseRestQuery.Api.Services;

public interface IResultExportStorage
{
    Task<QueryExportInfo?> ExportAsync(
        string transactionId,
        IReadOnlyList<Dictionary<string, object?>> rows,
        string format,
        bool compress,
        CancellationToken cancellationToken);
}
