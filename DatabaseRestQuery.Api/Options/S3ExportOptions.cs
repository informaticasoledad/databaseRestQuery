namespace DatabaseRestQuery.Api.Options;

public sealed class S3ExportOptions
{
    public const string SectionName = "S3Export";

    public bool Enabled { get; set; } = false;
    public string EndpointUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = "database-rest-query/exports";
    public bool ForcePathStyle { get; set; } = true;
    public int PresignedUrlMinutes { get; set; } = 60;
}
