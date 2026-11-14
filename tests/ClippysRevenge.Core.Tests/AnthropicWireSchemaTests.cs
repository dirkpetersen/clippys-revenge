using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

public class AnthropicWireSchemaTests
{
    private const string ReducedReply =
        """{"type":"object","additionalProperties":false,"required":["needs_reply","confidence","reason"],"properties":{"needs_reply":{"type":"boolean"},"confidence":{"type":"number"},"reply_all":{"type":"boolean"},"body_html":{"type":"string"},"reason":{"type":"string"}}}""";

    private static JsonElement Reduce(string schema)
    {
        Assert.True(AnthropicWireSchema.TryReduce(schema, out var d));
        return JsonSerializer.SerializeToElement(d);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ReplySchema_ReducesExactly()
    {
        Assert.True(JsonElement.DeepEquals(Parse(ReducedReply), Reduce(DraftModel.ReplySchema)));
    }

    [Fact]
    public void StyleSchema_ReducesExactly()
    {
        Assert.True(JsonElement.DeepEquals(
            Parse("""{"type":"object","additionalProperties":false,"required":["body_markdown"],"properties":{"body_markdown":{"type":"string"},"meeting_phrasing":{"type":"string"}}}"""),
            Reduce(DraftModel.StyleSchema)));
    }

    [Fact]
    public void PropertyNames_AreKept_KeywordsDropped()
    {
        var r = Reduce("""{"type":"object","properties":{"maximum":{"type":"integer","minimum":1},"minLength":{"type":"string","maxLength":3}}}""");
        Assert.True(JsonElement.DeepEquals(
            Parse("""{"type":"object","properties":{"maximum":{"type":"integer"},"minLength":{"type":"string"}}}"""), r));
    }

    [Fact]
    public void Items_MinItems_MaxItems()
    {
        var r = Reduce("""{"type":"array","items":{"type":"string","maxLength":5},"minItems":1,"maxItems":4}""");
        Assert.True(JsonElement.DeepEquals(Parse("""{"type":"array","items":{"type":"string"},"minItems":1}"""), r));
        var r2 = Reduce("""{"type":"array","items":{"type":"string"},"minItems":2}""");
        Assert.True(JsonElement.DeepEquals(Parse("""{"type":"array","items":{"type":"string"}}"""), r2));
    }

    [Fact]
    public void AnyOf_And_Defs_AreReduced()
    {
        var r = Reduce("""{"anyOf":[{"type":"number","exclusiveMaximum":3},{"type":"null"}],"$defs":{"a":{"type":"number","multipleOf":2}}}""");
        Assert.True(JsonElement.DeepEquals(
            Parse("""{"anyOf":[{"type":"number"},{"type":"null"}],"$defs":{"a":{"type":"number"}}}"""), r));
    }

    [Fact]
    public void DefaultAndConst_AreNotRecursed()
    {
        const string s = """{"type":"object","default":{"maximum":1},"const":{"minLength":2}}""";
        Assert.True(JsonElement.DeepEquals(Parse(s), Reduce(s)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{bad")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData(null)]
    public void NotAnObject_ReturnsFalse(string? s)
    {
        Assert.False(AnthropicWireSchema.TryReduce(s, out _));
    }

    [Fact]
    public void MemberOrder_IsDocumentOrder()
    {
        Assert.True(AnthropicWireSchema.TryReduce(DraftModel.ReplySchema, out var d));
        Assert.Equal(["type", "additionalProperties", "required", "properties"], d.Keys.ToArray());
    }
}
