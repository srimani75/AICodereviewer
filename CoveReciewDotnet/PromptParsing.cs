using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeminiAgenticCodeReview;
///testing...
public static class PromptParsing
{
    public static string NumberLines(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            builder.Append($"{i + 1,4}: ");
            builder.Append(lines[i]);
            if (i < lines.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    public static JsonObject? ExtractJsonObject(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Trim('`');
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                text = text[4..].Trim();
            }
        }

        if (TryParseObject(text, out var parsed))
        {
            return parsed;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var segment = text[start..(end + 1)];
            if (TryParseObject(segment, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryParseObject(string text, out JsonObject? obj)
    {
        obj = null;
        try
        {
            var node = JsonNode.Parse(text);
            obj = node as JsonObject;
            return obj is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
