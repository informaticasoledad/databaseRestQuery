namespace DatabaseRestQuery.Api.Models;

public sealed record QueryExportInfo(
    string Provider,
    string Bucket,
    string ObjectKey,
    string? Url,
    DateTime? UrlExpiresAtUtc,
    string Format,
    bool Compressed,
    long? SizeBytes
);
