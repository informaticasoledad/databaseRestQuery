using DatabaseRestQuery.Api.Models;

namespace DatabaseRestQuery.Api.Services;

public interface IQueryRequestValidator
{
    IReadOnlyList<string> Validate(QueryRequest request);
}
