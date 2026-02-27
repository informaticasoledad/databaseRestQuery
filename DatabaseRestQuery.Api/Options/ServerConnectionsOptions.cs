namespace DatabaseRestQuery.Api.Options;

public sealed class ServerConnectionItem
{
    public string ConnectionName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Connstr { get; set; } = string.Empty;
}
