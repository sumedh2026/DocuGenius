using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocuGenious.Core.Models;

namespace DocuGenious.Integration.Gemini;

/// <summary>
/// Tolerant JSON converters for <see cref="AnalysisResult"/> collection fields.
/// LLMs occasionally return these fields in unexpected shapes:
///   • Array of strings instead of array of objects
///   • Single object instead of array
///   • Object property names that differ from the schema (e.g. "url" vs "path")
///   • null instead of empty array
/// These converters accept all of the above so that one malformed field
/// never causes the entire response to fail deserialisation.
/// </summary>

// ─────────────────────────────────────────────────────────────────────────────
// Shared helper
// ─────────────────────────────────────────────────────────────────────────────

internal static class ConverterHelpers
{
    /// <summary>
    /// Returns the string value of a scalar JSON token, or skips a complex
    /// token (object / array) and returns an empty string.
    /// </summary>
    internal static string ReadScalarOrSkip(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:       return reader.GetString() ?? string.Empty;
            case JsonTokenType.True:         return "true";
            case JsonTokenType.False:        return "false";
            case JsonTokenType.Number:       return reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString();
            case JsonTokenType.Null:         return string.Empty;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:   reader.Skip(); return string.Empty;
            default:                         return string.Empty;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// List<ApiEndpoint>
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class FlexibleApiEndpointListConverter : JsonConverter<List<ApiEndpoint>>
{
    public override List<ApiEndpoint> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<ApiEndpoint>();

        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return result;

            case JsonTokenType.StartArray:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var ep = ReadOneEndpoint(ref reader);
                    if (ep != null) result.Add(ep);
                }
                break;

            case JsonTokenType.StartObject:
                var single = ReadEndpointObject(ref reader);
                if (single != null) result.Add(single);
                break;

            case JsonTokenType.String:
                result.Add(ParseEndpointFromString(reader.GetString() ?? string.Empty));
                break;

            default:
                reader.Skip();
                break;
        }

        return result;
    }

    private static ApiEndpoint? ReadOneEndpoint(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject: return ReadEndpointObject(ref reader);
            case JsonTokenType.String:      return ParseEndpointFromString(reader.GetString() ?? string.Empty);
            case JsonTokenType.Null:        return null;
            default:                        reader.Skip(); return null;
        }
    }

    private static ApiEndpoint? ReadEndpointObject(ref Utf8JsonReader reader)
    {
        var ep = new ApiEndpoint();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var propName = reader.GetString()?.ToLowerInvariant() ?? string.Empty;
            if (!reader.Read()) break;

            var value = ConverterHelpers.ReadScalarOrSkip(ref reader);

            switch (propName)
            {
                case "method":
                    ep.Method = value; break;

                case "path" or "url" or "endpoint" or "route" or "uri":
                    ep.Path = value; break;

                case "description" or "summary" or "desc" or "detail":
                    ep.Description = value; break;

                case "requestbody" or "request" or "request_body" or "requestpayload"
                     or "body" or "payload" or "input":
                    ep.RequestBody = value; break;

                case "responsebody" or "response" or "response_body" or "responsepayload"
                     or "returns" or "output" or "result":
                    ep.ResponseBody = value; break;
            }
        }

        return string.IsNullOrWhiteSpace(ep.Method) &&
               string.IsNullOrWhiteSpace(ep.Path)   &&
               string.IsNullOrWhiteSpace(ep.Description)
            ? null
            : ep;
    }

    /// <summary>Parses "GET /api/users – Returns all users."</summary>
    private static ApiEndpoint ParseEndpointFromString(string str)
    {
        var ep = new ApiEndpoint { Description = str };
        var m  = Regex.Match(str,
            @"^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+(/\S*)\s*[-–—:]\s*(.+)$",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            ep.Method      = m.Groups[1].Value.ToUpperInvariant();
            ep.Path        = m.Groups[2].Value;
            ep.Description = m.Groups[3].Value.Trim();
        }
        return ep;
    }

    // Write: serialize without this converter to avoid recursion
    private static readonly JsonSerializerOptions _writeOpts =
        new(JsonSerializerDefaults.Web);

    public override void Write(
        Utf8JsonWriter writer, List<ApiEndpoint> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, _writeOpts);
}

// ─────────────────────────────────────────────────────────────────────────────
// List<Feature>
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class FlexibleFeatureListConverter : JsonConverter<List<Feature>>
{
    public override List<Feature> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<Feature>();

        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return result;

            case JsonTokenType.StartArray:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var f = ReadOneFeature(ref reader);
                    if (f != null) result.Add(f);
                }
                break;

            case JsonTokenType.StartObject:
                var single = ReadFeatureObject(ref reader);
                if (single != null) result.Add(single);
                break;

            case JsonTokenType.String:
                result.Add(new Feature { Name = reader.GetString() ?? string.Empty });
                break;

            default:
                reader.Skip();
                break;
        }

        return result;
    }

    private static Feature? ReadOneFeature(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject: return ReadFeatureObject(ref reader);
            case JsonTokenType.String:      return new Feature { Name = reader.GetString() ?? string.Empty };
            case JsonTokenType.Null:        return null;
            default:                        reader.Skip(); return null;
        }
    }

    private static Feature? ReadFeatureObject(ref Utf8JsonReader reader)
    {
        var f = new Feature();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var propName = reader.GetString()?.ToLowerInvariant() ?? string.Empty;
            if (!reader.Read()) break;

            var value = ConverterHelpers.ReadScalarOrSkip(ref reader);

            switch (propName)
            {
                case "name" or "title" or "feature":
                    f.Name = value; break;

                case "description" or "desc" or "summary" or "detail" or "details":
                    f.Description = value; break;

                case "usageexample" or "usage" or "example" or "usage_example"
                     or "howto" or "how_to":
                    f.UsageExample = value; break;
            }
        }

        return string.IsNullOrWhiteSpace(f.Name) && string.IsNullOrWhiteSpace(f.Description)
            ? null
            : f;
    }

    private static readonly JsonSerializerOptions _writeOpts =
        new(JsonSerializerDefaults.Web);

    public override void Write(
        Utf8JsonWriter writer, List<Feature> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, _writeOpts);
}

// ─────────────────────────────────────────────────────────────────────────────
// List<string>  (Dependencies / Recommendations / KnownIssues)
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class FlexibleStringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<string>();

        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return result;

            case JsonTokenType.StartArray:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var s = ReadOneString(ref reader);
                    if (!string.IsNullOrWhiteSpace(s))
                        result.Add(s);
                }
                break;

            case JsonTokenType.String:
                // Single string — split on newlines into individual items
                var raw = reader.GetString() ?? string.Empty;
                result.AddRange(
                    raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.TrimStart('-', '*', '•', ' ').Trim())
                       .Where(l => !string.IsNullOrWhiteSpace(l)));
                break;

            default:
                reader.Skip();
                break;
        }

        return result;
    }

    private static string ReadOneString(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString() ?? string.Empty;

            case JsonTokenType.StartObject:
                // Object item in a string list — flatten to "key: value" pairs
                var sb = new System.Text.StringBuilder();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var key = reader.GetString();
                    if (!reader.Read()) break;
                    var val = ConverterHelpers.ReadScalarOrSkip(ref reader);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        if (sb.Length > 0) sb.Append(" — ");
                        sb.Append(string.IsNullOrWhiteSpace(key) ? val : $"{key}: {val}");
                    }
                }
                return sb.ToString();

            case JsonTokenType.Null:
                return string.Empty;

            default:
                reader.Skip();
                return string.Empty;
        }
    }

    public override void Write(
        Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (IEnumerable<string>)value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
