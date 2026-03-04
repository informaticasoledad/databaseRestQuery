using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using DatabaseRestQuery.Api.Infrastructure;
using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Extensions.Options;
using System.IO.Compression;

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
        var endpointUrl = EnvironmentTemplateResolver.ResolveRequired(_options.EndpointUrl, "S3Export.EndpointUrl");
        var region = EnvironmentTemplateResolver.ResolveRequired(_options.Region, "S3Export.Region");
        var accessKey = EnvironmentTemplateResolver.ResolveRequired(_options.AccessKey, "S3Export.AccessKey");
        var secretKey = EnvironmentTemplateResolver.ResolveRequired(_options.SecretKey, "S3Export.SecretKey");
        var bucket = EnvironmentTemplateResolver.ResolveRequired(_options.Bucket, "S3Export.Bucket");
        var keyPrefixValue = EnvironmentTemplateResolver.ResolveRequired(_options.KeyPrefix, "S3Export.KeyPrefix");

        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Exportacion S3 no habilitada. Configure S3Export.Enabled=true.");
        }

        if (string.IsNullOrWhiteSpace(endpointUrl) ||
            string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey) ||
            string.IsNullOrWhiteSpace(bucket))
        {
            throw new InvalidOperationException("Configuracion S3 incompleta. Revise S3Export en appsettings.");
        }

        var normalizedFormat = ResultPayloadSerializer.NormalizeFormat(format);
        var effectiveCompress = compress && normalizedFormat != "xlsx";
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"database-rest-query-export-{Guid.NewGuid():N}.tmp");
        string contentType;
        string extension;

        try
        {
            await using (var tempWriteStream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                if (effectiveCompress)
                {
                    await using var gzipStream = new GZipStream(tempWriteStream, CompressionLevel.SmallestSize, leaveOpen: true);
                    (contentType, extension) = ResultPayloadSerializer.SerializeToStream(rows, normalizedFormat, gzipStream);
                    await gzipStream.FlushAsync(cancellationToken);
                }
                else
                {
                    (contentType, extension) = ResultPayloadSerializer.SerializeToStream(rows, normalizedFormat, tempWriteStream);
                }

                await tempWriteStream.FlushAsync(cancellationToken);
            }

            var keyPrefix = keyPrefixValue.Trim('/');
            var objectKey = $"{keyPrefix}/{DateTime.UtcNow:yyyy/MM/dd}/{transactionId}_{Guid.NewGuid():N}.{extension}";
            if (effectiveCompress)
            {
                objectKey += ".gz";
            }

            var s3Config = new AmazonS3Config
            {
                ServiceURL = endpointUrl,
                ForcePathStyle = _options.ForcePathStyle,
                AuthenticationRegion = region
            };

            using var client = new AmazonS3Client(accessKey, secretKey, s3Config);
            await using var uploadStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

            var putRequest = new PutObjectRequest
            {
                BucketName = bucket,
                Key = objectKey,
                InputStream = uploadStream,
                AutoCloseStream = false,
                ContentType = contentType
            };

            if (effectiveCompress)
            {
                putRequest.Headers.ContentEncoding = "gzip";
            }

            await client.PutObjectAsync(putRequest, cancellationToken);

            var expiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.PresignedUrlMinutes));
            var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = objectKey,
                Verb = HttpVerb.GET,
                Expires = expiresAt
            });

            return new QueryExportInfo(
                "s3",
                bucket,
                objectKey,
                url,
                expiresAt,
                normalizedFormat,
                effectiveCompress,
                uploadStream.Length
            );
        }
        finally
        {
            try
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
            }
            catch
            {
                // No-op: temp cleanup best effort.
            }
        }
    }
}
