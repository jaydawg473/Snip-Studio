#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace SnipStudio;

// A deliberately small adapter for the existing framed local-worker protocol.
// No arbitrary CLR types, reflection binding or JavaScript evaluation is used.
internal sealed class BoundedJsonSerializer
{
    public int MaxJsonLength { get; init; } = 1024 * 1024;
    public int RecursionLimit { get; init; } = 12;

    internal string Serialize(object value)
    {
        string result = JsonSerializer.Serialize(value, Options());
        ValidateLength(result);
        return result;
    }

    internal object? DeserializeObject(string value)
    {
        ValidateLength(value);
        using JsonDocument document = JsonDocument.Parse(value, new JsonDocumentOptions
        {
            MaxDepth = RecursionLimit,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        return ConvertValue(document.RootElement);
    }

    private JsonSerializerOptions Options() => new() { MaxDepth = RecursionLimit };

    private void ValidateLength(string value)
    {
        if (value is null || value.Length > MaxJsonLength || Encoding.UTF8.GetByteCount(value) > MaxJsonLength)
            throw new ArgumentException("The local translation frame exceeds its permitted size.");
    }

    private static object? ConvertValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (JsonProperty property in value.EnumerateObject())
                    if (!result.TryAdd(property.Name, ConvertValue(property.Value)))
                        throw new JsonException("Duplicate keys are not valid in a local translation frame.");
                return result;
            case JsonValueKind.Array:
                var items = new List<object?>();
                foreach (JsonElement item in value.EnumerateArray()) items.Add(ConvertValue(item));
                return items.ToArray();
            case JsonValueKind.String: return value.GetString();
            case JsonValueKind.Number:
                if (value.TryGetInt32(out int integer)) return integer;
                if (value.TryGetInt64(out long longer)) return longer;
                return value.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            default: throw new JsonException("The local translation frame contains an unsupported value.");
        }
    }
}
