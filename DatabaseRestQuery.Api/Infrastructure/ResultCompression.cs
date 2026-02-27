using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DatabaseRestQuery.Api.Infrastructure;

public static class ResultCompression
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToZipBase64(IReadOnlyList<Dictionary<string, object?>> result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("result.json", CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            entryStream.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }
}
