namespace DatabaseRestQuery.Api.Services;

public interface IDataSourceCircuitBreaker
{
    void ThrowIfOpen(string key);
    void RegisterFailure(string key);
    void RegisterSuccess(string key);
}
