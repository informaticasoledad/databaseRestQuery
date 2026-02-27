using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using DatabaseRestQuery.Api.Infrastructure;
using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Extensions.Options;

namespace DatabaseRestQuery.Api.Services;

public sealed class S3ResultExportStorage(IOptions<S3ExportOptions> options) : IResultExportStorage
{
    private readonly S3ExportOptions _options = options.Value;

    public async Task<QueryExportInfo?> ExportAsync(
        string transactionId,
        IReadOnlyList<Dictionary<string, object?>> rows,
        string format,
        bool compress,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Exportacion S3 no habilitada. Configure S3Export.Enabled=true.");
        }

        if (string.IsNullOrWhiteSpace(_options.EndpointUrl) ||
            string.IsNullOrWhiteSpace(_options.AccessKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey) ||
            string.IsNullOrWhiteSpace(_options.Bucket))
        {
            throw new InvalidOperationException("Configuracion S3 incompleta. Revise S3Export en appsettings.");
        }

        var (bytes, contentType, extension) = ResultPayloadSerializer.Serialize(rows, format);
        var payload = compress ? ResultPayloadSerializer.Gzip(bytes) : bytes;
        var normalizedFormat = ResultPayloadSerializer.NormalizeFormat(format);

        var keyPrefix = _options.KeyPrefix.Trim('/');
        var objectKey = $"{keyPrefix}/{DateTime.UtcNow:yyyy/MM/dd}/{transactionId}_{Guid.NewGuid():N}.{extension}";
        if (compress)
        {
            objectKey += ".gz";
        }

        var s3Config = new AmazonS3Config
        {
            ServiceURL = _options.EndpointUrl,
            ForcePathStyle = _options.ForcePathStyle,
            RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Region)
        };

        using var client = new AmazonS3Client(_options.AccessKey, _options.SecretKey, s3Config);
        await using var stream = new MemoryStream(payload);

        var putRequest = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            InputStream = stream,
            AutoCloseStream = false,
            ContentType = contentType
        };

        if (compress)
        {
            putRequest.Headers.ContentEncoding = "gzip";
        }

        await client.PutObjectAsync(putRequest, cancellationToken);

        var expiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.PresignedUrlMinutes));
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = expiresAt
        });

        return new QueryExportInfo(
            "s3",
            _options.Bucket,
            objectKey,
            url,
            expiresAt,
            normalizedFormat,
            compress,
            payload.LongLength
        );
    }
}
