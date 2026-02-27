using System.Text.Json;

namespace DatabaseRestQuery.Api.Infrastructure;

public static class JsonHelper
{
    public static object? JsonElementToObject(JsonElement? element)
    {
        if (element is null)
        {
            return DBNull.Value;
        }

        return JsonElementToObject(element.Value);
    }

    public static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => DBNull.Value,
            JsonValueKind.Undefined => DBNull.Value,
            _ => element.GetRawText()
        };
    }
}
