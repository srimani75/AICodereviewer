using System.Text.Json.Nodes;
using GeminiAgenticCodeReview;
using Xunit;

namespace GeminiAgenticCodeReview.Tests;

public class PromptParsingTests
{
    [Fact]
    public void NumberLines_prefixes_line_numbers()
    {
        var input = "a\nb";
        var result = PromptParsing.NumberLines(input);
        Assert.Contains("1:", result);
        Assert.Contains("2:", result);
        Assert.Contains("a", result);
        Assert.Contains("b", result);
    }

    [Fact]
    public void ExtractJsonObject_parses_plain_json()
    {
        var raw = """{"x":1}""";
        var obj = PromptParsing.ExtractJsonObject(raw);
        Assert.NotNull(obj);
        Assert.Equal(1, obj!["x"]?.GetValue<int>());
    }

    [Fact]
    public void ExtractJsonObject_strips_json_fenced_block()
    {
        var raw = """
            ```json
            {"selected_files":["a.cs"]}
            ```
            """;
        var obj = PromptParsing.ExtractJsonObject(raw);
        Assert.NotNull(obj);
        var arr = obj!["selected_files"]?.AsArray();
        Assert.NotNull(arr);
        Assert.Single(arr!);
        Assert.Equal("a.cs", arr[0]!.GetValue<string>());
    }

    [Fact]
    public void ExtractJsonObject_extracts_first_object_from_noise()
    {
        var raw = """
            Here you go:
            {"file":"Program.cs","findings":[]}
            trailing
            """;
        var obj = PromptParsing.ExtractJsonObject(raw);
        Assert.NotNull(obj);
        Assert.Equal("Program.cs", obj!["file"]?.GetValue<string>());
    }
}
