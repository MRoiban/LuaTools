using System.Text.Json;

namespace CabinetEngine.Compat;

// LuaTools: .NET 8 stand-in for System.Web.Script.Serialization.JavaScriptSerializer, so the
// verbatim Cabinet sources keep their original calls. Mirrors its observable behavior: public
// fields and properties, case-insensitive binding, and DeserializeObject returning
// Dictionary<string, object> / object[] / string / int / long / decimal / bool / null.
sealed class JavaScriptSerializer
{
    public int MaxJsonLength { get; set; } = 2 * 1024 * 1024;
    public int RecursionLimit { get; set; } = 100;

    JsonSerializerOptions Options() => new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = RecursionLimit,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public string Serialize(object? value)
    {
        var json = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options());
        if (json.Length > MaxJsonLength) throw new InvalidOperationException("JSON exceeds MaxJsonLength");
        return json;
    }

    public T Deserialize<T>(string input)
    {
        Check(input);
        return JsonSerializer.Deserialize<T>(input, Options())!;
    }

    public object? DeserializeObject(string input)
    {
        Check(input);
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = RecursionLimit });
        return Convert(document.RootElement);
    }

    void Check(string input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (input.Length > MaxJsonLength) throw new ArgumentException("JSON exceeds MaxJsonLength");
    }

    static object? Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Aggregate(new Dictionary<string, object?>(),
            (map, property) => { map[property.Name] = Convert(property.Value); return map; }),
        JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt32(out var i) ? i
            : element.TryGetInt64(out var l) ? l
            : element.TryGetDecimal(out var d) ? d
            : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
