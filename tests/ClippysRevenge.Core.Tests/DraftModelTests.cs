using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

public class DraftModelTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ReplySchema_HasPinnedShape()
    {
        using var d = JsonDocument.Parse(DraftModel.ReplySchema);
        var root = d.RootElement;
        Assert.Equal(["type", "additionalProperties", "required", "properties"], root.EnumerateObject().Select(p => p.Name));
        var props = root.GetProperty("properties");
        Assert.Equal(["needs_reply", "confidence", "reply_all", "body_html", "reason"], props.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, props.GetProperty("confidence").GetProperty("maximum").GetInt32());
        Assert.Equal(200, props.GetProperty("reason").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void StyleSchema_RequiresBodyMarkdown()
    {
        using var d = JsonDocument.Parse(DraftModel.StyleSchema);
        Assert.Equal(["body_markdown"], d.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(600, d.RootElement.GetProperty("properties").GetProperty("meeting_phrasing").GetProperty("maxLength").GetInt32());
        Assert.Equal("""{"type":"object","additionalProperties":false,"required":["body_markdown"],"properties":{"body_markdown":{"type":"string"},"meeting_phrasing":{"type":"string","maxLength":600}}}""", DraftModel.StyleSchema);
        Assert.True(ClaudeCliArgs.TryCompactSchema(DraftModel.StyleSchema, out _));
    }

    [Fact]
    public void Parse_FullReply()
    {
        var r = DraftReply.Parse(J("""{"needs_reply":true,"confidence":0.8,"reply_all":true,"body_html":"<p>Hi</p>","reason":"r"}"""));
        Assert.Equal(new DraftReply(true, 0.8, true, "<p>Hi</p>"), r);
    }

    [Fact]
    public void Parse_NoReply()
    {
        var r = DraftReply.Parse(J("""{"needs_reply":false,"confidence":0.1,"reason":"newsletter"}"""));
        Assert.Equal(new DraftReply(false, 0.1, null, null), r);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void Parse_ConfidenceBounds_Valid(string c) =>
        Assert.NotNull(DraftReply.Parse(J("{\"needs_reply\":false,\"confidence\":" + c + ",\"reason\":\"x\"}")));

    [Theory]
    [InlineData("1.01")]
    [InlineData("-0.01")]
    [InlineData("\"0.5\"")]
    [InlineData("1e400")]
    public void Parse_BadConfidence_Null(string c) =>
        Assert.Null(DraftReply.Parse(J("{\"needs_reply\":false,\"confidence\":" + c + ",\"reason\":\"x\"}")));

    [Fact]
    public void Parse_MissingConfidence_Null() =>
        Assert.Null(DraftReply.Parse(J("""{"needs_reply":false,"reason":"x"}""")));

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void Parse_BadNeedsReply_Null(string v) =>
        Assert.Null(DraftReply.Parse(J("{\"needs_reply\":" + v + ",\"confidence\":0.5}")));

    [Fact]
    public void Parse_MissingNeedsReply_Null() =>
        Assert.Null(DraftReply.Parse(J("""{"confidence":0.5}""")));

    [Theory]
    [InlineData("")]
    [InlineData(",\"body_html\":\"\"")]
    [InlineData(",\"body_html\":\"   \"")]
    public void Parse_NeedsReplyWithoutBody_Null(string body) =>
        Assert.Null(DraftReply.Parse(J("{\"needs_reply\":true,\"confidence\":0.5" + body + "}")));

    [Fact]
    public void Parse_BodyLength()
    {
        static string Doc(int n, bool nr) => "{\"needs_reply\":" + (nr ? "true" : "false") + ",\"confidence\":0.5,\"body_html\":\"" + new string('a', n) + "\"}";
        Assert.NotNull(DraftReply.Parse(J(Doc(50_000, true))));
        Assert.Null(DraftReply.Parse(J(Doc(50_001, true))));
        Assert.Null(DraftReply.Parse(J(Doc(50_001, false))));
    }

    [Fact]
    public void Parse_ReplyAll()
    {
        Assert.Null(DraftReply.Parse(J("""{"needs_reply":false,"confidence":0.5,"reply_all":"yes"}""")));
        var r = DraftReply.Parse(J("""{"needs_reply":false,"confidence":0.5,"reply_all":null}"""));
        Assert.NotNull(r);
        Assert.Null(r!.ReplyAll);
    }

    [Fact]
    public void Parse_WrongTypes_Null()
    {
        Assert.Null(DraftReply.Parse(J("""{"needs_reply":false,"confidence":0.5,"body_html":123}""")));
        Assert.Null(DraftReply.Parse(J("""{"needs_reply":false,"confidence":0.5,"reason":5}""")));
    }

    [Fact]
    public void Parse_Tolerated()
    {
        Assert.NotNull(DraftReply.Parse(J("""{"needs_reply":false,"confidence":0.5}""")));
        Assert.NotNull(DraftReply.Parse(J("{\"needs_reply\":false,\"confidence\":0.5,\"reason\":\"" + new string('r', 500) + "\"}")));
        Assert.NotNull(DraftReply.Parse(J("""{"needs_reply":false,"confidence":0.5,"foo":1}""")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    public void Parse_NonObject_Null(string json) => Assert.Null(DraftReply.Parse(J(json)));

    [Fact]
    public void Parse_DefaultElement_Null() => Assert.Null(DraftReply.Parse(default));
}
