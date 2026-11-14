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
        Assert.Equal(["needs_reply", "confidence", "reply_all", "body_html", "reason", "meeting"], props.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["needs_reply", "confidence", "reason"], root.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, props.GetProperty("confidence").GetProperty("maximum").GetInt32());
        Assert.Equal(200, props.GetProperty("reason").GetProperty("maxLength").GetInt32());
        var m = props.GetProperty("meeting");
        Assert.Equal(["requested"], m.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.False, m.GetProperty("additionalProperties").ValueKind);
        var mp = m.GetProperty("properties");
        Assert.Equal(["requested", "duration_minutes", "earliest_date", "latest_date", "outside_hours_heading"],
            mp.EnumerateObject().Select(p => p.Name));
        var dur = mp.GetProperty("duration_minutes");
        Assert.Equal("integer", dur.GetProperty("type").GetString());
        Assert.Equal(15, dur.GetProperty("minimum").GetInt32());
        Assert.Equal(480, dur.GetProperty("maximum").GetInt32());
        Assert.Equal(10, mp.GetProperty("earliest_date").GetProperty("maxLength").GetInt32());
        Assert.Equal(10, mp.GetProperty("latest_date").GetProperty("maxLength").GetInt32());
        Assert.Equal(80, mp.GetProperty("outside_hours_heading").GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void ReplySchema_IsCompact() =>
        Assert.Equal(DraftModel.ReplySchema, JsonSerializer.Serialize(JsonDocument.Parse(DraftModel.ReplySchema).RootElement));

    private static DraftReply? Reply(string? meetingJsonOrNull)
    {
        var meeting = meetingJsonOrNull is null ? "" : ",\"meeting\":" + meetingJsonOrNull;
        return DraftReply.Parse(J("{\"needs_reply\":true,\"confidence\":0.8,\"body_html\":\"<p>Hi</p>\",\"reason\":\"r\"" + meeting + "}"));
    }

    private static MeetingAsk? Ask(string meeting)
    {
        var r = Reply(meeting);
        Assert.NotNull(r);
        return r.Meeting;
    }

    [Fact]
    public void Meeting_Absent()
    {
        var r = Reply(null);
        Assert.NotNull(r);
        Assert.Null(r.Meeting);
    }

    [Fact]
    public void Meeting_Full() =>
        Assert.Equal(new MeetingAsk(30, new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 27), "Außerhalb der Bürozeiten in Europa:"),
            Ask("""{"requested":true,"duration_minutes":30,"earliest_date":"2026-11-23","latest_date":"2026-11-27","outside_hours_heading":"Außerhalb der Bürozeiten in Europa:"}"""));

    [Fact]
    public void Meeting_RequestedOnly() =>
        Assert.Equal(new MeetingAsk(null, null, null, null), Ask("""{"requested":true}"""));

    [Fact]
    public void Meeting_UnknownPropertiesIgnored() =>
        Assert.Equal(new MeetingAsk(null, null, null, null), Ask("""{"requested":true,"foo":1}"""));

    [Theory]
    [InlineData("""{"requested":false,"duration_minutes":30}""")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("\"yes\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"requested":"true"}""")]
    [InlineData("""{"requested":1}""")]
    [InlineData("""{"requested":null}""")]
    public void Meeting_NotRequested(string meeting) => Assert.Null(Ask(meeting));

    [Theory]
    [InlineData("14")] [InlineData("481")] [InlineData("30.5")] [InlineData("30.0")] [InlineData("1e2")]
    [InlineData("\"30\"")] [InlineData("-60")] [InlineData("null")] [InlineData("99999999999")]
    public void Meeting_BadDuration(string d) =>
        Assert.Null(Ask("{\"requested\":true,\"duration_minutes\":" + d + "}")!.DurationMinutes);

    [Theory]
    [InlineData(15)] [InlineData(480)]
    public void Meeting_DurationBoundaries(int d) =>
        Assert.Equal(d, Ask("{\"requested\":true,\"duration_minutes\":" + d + "}")!.DurationMinutes);

    [Theory]
    [InlineData("\"2026-2-03\"")] [InlineData("\"2026-02-30\"")] [InlineData("\" 2026-11-20\"")]
    [InlineData("\"2026-11-20 \"")] [InlineData("\"2026-11-20T00:00\"")] [InlineData("\"20.11.2026\"")]
    [InlineData("20261120")] [InlineData("null")] [InlineData("\"٢٠٢٦-١١-٢٠\"")]
    public void Meeting_BadDate(string bad)
    {
        var good = new DateOnly(2026, 11, 20);
        var a = Ask("{\"requested\":true,\"earliest_date\":" + bad + ",\"latest_date\":\"2026-11-20\"}")!;
        Assert.Null(a.EarliestDate);
        Assert.Equal(good, a.LatestDate);
        var b = Ask("{\"requested\":true,\"earliest_date\":\"2026-11-20\",\"latest_date\":" + bad + "}")!;
        Assert.Equal(good, b.EarliestDate);
        Assert.Null(b.LatestDate);
    }

    [Fact]
    public void Meeting_LatestBeforeEarliestKept()
    {
        var a = Ask("""{"requested":true,"earliest_date":"2026-11-27","latest_date":"2026-11-23"}""")!;
        Assert.Equal(new DateOnly(2026, 11, 27), a.EarliestDate);
        Assert.Equal(new DateOnly(2026, 11, 23), a.LatestDate);
    }

    [Theory]
    [InlineData("")] [InlineData("   <b>x</b>   ")]
    public void Meeting_HeadingVerbatim(string h) =>
        Assert.Equal(h, Ask("{\"requested\":true,\"outside_hours_heading\":\"" + h + "\"}")!.OutsideHoursHeading);

    [Fact]
    public void Meeting_LongHeadingVerbatim()
    {
        var h = new string('x', 500);
        Assert.Equal(h, Ask("{\"requested\":true,\"outside_hours_heading\":\"" + h + "\"}")!.OutsideHoursHeading);
    }

    [Theory]
    [InlineData("5")] [InlineData("null")]
    public void Meeting_HeadingNotString(string h) =>
        Assert.Null(Ask("{\"requested\":true,\"outside_hours_heading\":" + h + "}")!.OutsideHoursHeading);

    [Fact]
    public void Meeting_DoesNotRescueInvalidReply() =>
        Assert.Null(DraftReply.Parse(J("""{"needs_reply":true,"confidence":2,"body_html":"<p>Hi</p>","reason":"r","meeting":{"requested":true}}""")));

    [Fact]
    public void Meeting_ParsedWithoutReply()
    {
        var r = DraftReply.Parse(J("""{"needs_reply":false,"confidence":0.5,"reason":"r","meeting":{"requested":true}}"""));
        Assert.NotNull(r);
        Assert.Equal(new MeetingAsk(null, null, null, null), r.Meeting);
    }

    [Fact]
    public void MeetingAsk_ParseDefaultAndConstants()
    {
        Assert.Null(MeetingAsk.Parse(default));
        Assert.Equal(60, MeetingAsk.DefaultDurationMinutes);
        Assert.Equal(15, MeetingAsk.MinDurationMinutes);
        Assert.Equal(480, MeetingAsk.MaxDurationMinutes);
    }

    [Fact]
    public void StyleSchema_RequiresBodyMarkdown()
    {
        using var d = JsonDocument.Parse(DraftModel.StyleSchema);
        Assert.Equal(["body_markdown"], d.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
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
