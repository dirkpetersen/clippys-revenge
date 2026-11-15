using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

public class DraftRevisitTests
{
    private static RevisitReply? P(string json)
    {
        using var d = JsonDocument.Parse(json);
        return DraftRevisit.Parse(d.RootElement.Clone());
    }

    [Fact]
    public void Schema_shape_and_meeting_equals_reply_schema()
    {
        using var s = JsonDocument.Parse(DraftModel.RevisitSchema);
        using var r = JsonDocument.Parse(DraftModel.ReplySchema);
        var root = s.RootElement;
        Assert.Equal(["action", "confidence", "reason"], root.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["keep", "replace", "remove"],
            root.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.True(JsonElement.DeepEquals(root.GetProperty("properties").GetProperty("meeting"),
            r.RootElement.GetProperty("properties").GetProperty("meeting")));
    }

    [Fact]
    public void Wire_schema_reduces_as_expected()
    {
        Assert.True(AnthropicWireSchema.TryReduce(DraftModel.RevisitSchema, out var d));
        using var expected = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["action","confidence","reason"],"properties":{"action":{"type":"string","enum":["keep","replace","remove"]},"confidence":{"type":"number"},"reply_all":{"type":"boolean"},"body_html":{"type":"string"},"reason":{"type":"string"},"meeting":{"type":"object","additionalProperties":false,"required":["requested"],"properties":{"requested":{"type":"boolean"},"duration_minutes":{"type":"integer"},"earliest_date":{"type":"string"},"latest_date":{"type":"string"},"outside_hours_heading":{"type":"string"}}}}}""");
        Assert.Equal(expected.RootElement.EnumerateObject().Select(p => p.Name), d.Keys);
        foreach (var p in expected.RootElement.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(p.Value, d[p.Name]), p.Name);
    }

    [Fact]
    public void Keep_parses()
    {
        Assert.Equal(new RevisitReply(RevisitAction.Keep, 0.9, null, null, null),
            P("""{"action":"keep","confidence":0.9,"reason":"fine"}"""));
    }

    [Fact]
    public void Remove_drops_extras()
    {
        Assert.Equal(new RevisitReply(RevisitAction.Remove, 0.8, null, null, null),
            P("""{"action":"remove","confidence":0.8,"reason":"x","reply_all":true,"body_html":"<p>a</p>","meeting":{"requested":true}}"""));
    }

    [Fact]
    public void Replace_parses_with_meeting()
    {
        var r = P("""{"action":"replace","confidence":0.7,"reply_all":true,"body_html":"<p>New</p>","reason":"r","meeting":{"requested":true,"duration_minutes":30}}""");
        Assert.NotNull(r);
        Assert.Equal(RevisitAction.Replace, r.Action);
        Assert.Equal(0.7, r.Confidence);
        Assert.True(r.ReplyAll);
        Assert.Equal("<p>New</p>", r.BodyHtml);
        Assert.Equal(30, r.Meeting!.DurationMinutes);
    }

    [Fact]
    public void Replace_optional_parts()
    {
        Assert.Null(P("""{"action":"replace","confidence":0.7,"body_html":"x","reason":"r"}""")!.ReplyAll);
        Assert.Null(P("""{"action":"replace","confidence":0.7,"body_html":"x","reason":"r","meeting":{"requested":false}}""")!.Meeting);
        Assert.Null(P("""{"action":"replace","confidence":0.7,"body_html":"x","reason":"r","meeting":"x"}""")!.Meeting);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"action":"Keep","confidence":0.5}""")]
    [InlineData("""{"action":"delete","confidence":0.5}""")]
    [InlineData("""{"action":" keep","confidence":0.5}""")]
    [InlineData("""{"action":"","confidence":0.5}""")]
    [InlineData("""{"action":1,"confidence":0.5}""")]
    [InlineData("""{"action":null,"confidence":0.5}""")]
    [InlineData("""{"confidence":0.5}""")]
    [InlineData("""{"action":"keep","confidence":-0.01}""")]
    [InlineData("""{"action":"keep","confidence":1.01}""")]
    [InlineData("""{"action":"keep","confidence":"0.5"}""")]
    [InlineData("""{"action":"keep"}""")]
    [InlineData("""{"action":"keep","confidence":0.5,"reply_all":"yes"}""")]
    [InlineData("""{"action":"keep","confidence":0.5,"body_html":5}""")]
    [InlineData("""{"action":"keep","confidence":0.5,"reason":5}""")]
    [InlineData("""{"action":"replace","confidence":0.5}""")]
    [InlineData("""{"action":"replace","confidence":0.5,"body_html":null}""")]
    [InlineData("""{"action":"replace","confidence":0.5,"body_html":"  "}""")]
    public void Invalid_gives_null(string json) => Assert.Null(P(json));

    [Theory]
    [InlineData("keep")]
    [InlineData("replace")]
    [InlineData("remove")]
    public void Oversized_body_is_invalid_for_every_action(string action) =>
        Assert.Null(P("{\"action\":\"" + action + "\",\"confidence\":0.5,\"body_html\":\"" + new string('a', 50_001) + "\"}"));

    [Fact]
    public void Valid_edges()
    {
        Assert.Equal(0, P("""{"action":"keep","confidence":0}""")!.Confidence);
        Assert.Equal(1, P("""{"action":"keep","confidence":1}""")!.Confidence);
        Assert.NotNull(P("{\"action\":\"replace\",\"confidence\":0.5,\"body_html\":\"" + new string('a', 50_000) + "\"}"));
        Assert.NotNull(P("{\"action\":\"keep\",\"confidence\":0.5,\"reason\":\"" + new string('r', 500) + "\"}"));
        Assert.Null(P("""{"action":"replace","confidence":0.5,"body_html":"x","reply_all":null}""")!.ReplyAll);
        Assert.NotNull(P("""{"action":"keep","confidence":0.5,"x":1}"""));
    }

    [Fact]
    public void Constants()
    {
        Assert.Equal(14, DraftRevisit.MaxDraftAgeDays);
        Assert.Equal(10, DraftRevisit.MaxRevisitsPerMailbox);
        Assert.Equal(3, DraftRevisit.MaxAttempts);
    }
}
