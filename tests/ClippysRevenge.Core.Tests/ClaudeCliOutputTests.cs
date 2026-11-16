using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

public class ClaudeCliOutputTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Local = TimeZoneInfo.CreateCustomTimeZone("T+1", TimeSpan.FromHours(1), "T+1", "T+1");
    private static readonly TimeSpan D = TimeSpan.FromMilliseconds(1234);

    private const string Good =
        """{"type":"result","subtype":"success","is_error":false,"result":"","usage":{"input_tokens":120,"output_tokens":45},"structured_output":{"needs_reply":true,"confidence":0.8,"reason":"x","body_html":"<p>Hi</p>"}}""";

    private static ModelResult P(int exit, string stdout, string stderr = "") =>
        ClaudeCliOutput.Parse(exit, stdout, stderr, D, Now, Local);

    private static string Err(string result, string extra = "") =>
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"result\":" + JsonSerializer.Serialize(result) + extra + "}";

    private static DateTimeOffset Utc(string s) => DateTimeOffset.Parse(s);

    [Fact]
    public void Ok_StructuredOutput_Cloned()
    {
        var r = Assert.IsType<ModelResult.Ok>(P(0, Good));
        Assert.Equal(0.8, r.Output.GetProperty("confidence").GetDouble());
        Assert.Equal(D, r.Duration);
        Assert.Equal(120, r.InputTokens);
        Assert.Equal(45, r.OutputTokens);
        GC.Collect();
        Assert.Equal("<p>Hi</p>", r.Output.GetProperty("body_html").GetString());
    }

    private static string WithUsage(string usageJson) =>
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"usage\":" + usageJson + ",\"structured_output\":{\"a\":1}}";

    [Fact]
    public void Usage_AllFourFields()
    {
        var r = Assert.IsType<ModelResult.Ok>(P(0, WithUsage(
            """{"input_tokens":120,"output_tokens":45,"cache_read_input_tokens":3000,"cache_creation_input_tokens":200}""")));
        Assert.Equal(new TokenUsage(120, 45, 3000, 200), r.Usage);
        Assert.Equal(120, r.InputTokens);
    }

    [Fact]
    public void Usage_InputOutputOnly()
    {
        var r = Assert.IsType<ModelResult.Ok>(P(0, Good));
        Assert.Equal(new TokenUsage(120, 45, null, null), r.Usage);
    }

    [Fact]
    public void Usage_NoUsageObject_IsNull()
    {
        var r = Assert.IsType<ModelResult.Ok>(P(0, """{"type":"result","subtype":"success","structured_output":{"a":1}}"""));
        Assert.Null(r.Usage);
    }

    [Fact]
    public void Usage_InvalidFields_AreNull()
    {
        var r = Assert.IsType<ModelResult.Ok>(P(0, WithUsage(
            """{"input_tokens":-1,"output_tokens":"45","cache_read_input_tokens":1.5,"cache_creation_input_tokens":null}""")));
        Assert.Equal(new TokenUsage(null, null, null, null), r.Usage);
        Assert.False(r.Usage!.Known);
    }

    [Fact]
    public void Usage_HugeInput_KeepsLongButNotInt()
    {
        var r = Assert.IsType<ModelResult.Ok>(P(0, WithUsage("""{"input_tokens":3000000000,"output_tokens":5}""")));
        Assert.Equal(3_000_000_000L, r.Usage!.Input);
        Assert.Null(r.InputTokens);
    }

    [Fact]
    public void Usage_OnErrorEnvelope_StructuredRetries()
    {
        var r = Assert.IsType<ModelResult.Failed>(P(1,
            """{"type":"result","subtype":"error_max_structured_output_retries","is_error":true,"usage":{"input_tokens":7,"output_tokens":8}}"""));
        Assert.Equal(ModelFailure.NoStructuredOutput, r.Kind);
        Assert.Equal(new TokenUsage(7, 8, null, null), r.Usage);
    }

    [Fact]
    public void Usage_OnProcessExit()
    {
        var r = Assert.IsType<ModelResult.Failed>(P(1, Err("boom", ",\"usage\":{\"input_tokens\":7,\"output_tokens\":8}")));
        Assert.Equal(ModelFailure.ProcessExit, r.Kind);
        Assert.Equal(1, r.Code);
        Assert.Equal(new TokenUsage(7, 8, null, null), r.Usage);
    }

    [Fact]
    public void Usage_NotJson_IsNull()
    {
        var r = Assert.IsType<ModelResult.Failed>(P(0, "plain text"));
        Assert.Equal(ModelFailure.BadOutput, r.Kind);
        Assert.Null(r.Usage);
    }

    [Fact]
    public void Ok_NoUsage_NullTokens()
    {
        var r = Assert.IsType<ModelResult.Ok>(P(0, """{"type":"result","subtype":"success","structured_output":{"a":1}}"""));
        Assert.Null(r.InputTokens);
        Assert.Null(r.OutputTokens);
        var r2 = Assert.IsType<ModelResult.Ok>(P(0, """{"type":"result","subtype":"success","usage":{"input_tokens":"12"},"structured_output":{"a":1}}"""));
        Assert.Null(r2.InputTokens);
    }

    [Fact]
    public void Ok_ResultFallback()
    {
        var r = Assert.IsType<ModelResult.Ok>(P(0, """{"type":"result","subtype":"success","result":"{\"ok\":true}"}"""));
        Assert.True(r.Output.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Ok_ResultFenceStripped()
    {
        var env = "{\"type\":\"result\",\"subtype\":\"success\",\"result\":" + JsonSerializer.Serialize("```json\n{\"ok\":true}\n```") + "}";
        Assert.IsType<ModelResult.Ok>(P(0, env));
    }

    [Fact]
    public void NoStructuredOutput_Cases()
    {
        Assert.Equal(new ModelResult.Failed(ModelFailure.NoStructuredOutput, null),
            P(0, """{"type":"result","subtype":"success","result":"I can't help with that."}"""));
        Assert.Equal(new ModelResult.Failed(ModelFailure.NoStructuredOutput, null),
            P(0, """{"type":"result","subtype":"success","structured_output":[1],"result":"no"}"""));
    }

    [Fact]
    public void Envelope_FoundInLinesArrayAndWithJunk()
    {
        Assert.IsType<ModelResult.Ok>(P(0, """{"type":"system","subtype":"init"}""" + "\n" + Good));
        Assert.IsType<ModelResult.Ok>(P(0, """[{"type":"system"},""" + Good + "]"));
        Assert.IsType<ModelResult.Ok>(P(0, Good + "\nWarning: x"));
    }

    [Fact]
    public void RateLimited_UnixReset()
    {
        Assert.Equal(new ModelResult.RateLimited(Utc("2026-11-13T15:00:00Z")),
            P(1, Err("Claude AI usage limit reached|1794582000")));
        Assert.Equal(new ModelResult.RateLimited(null), P(1, Err("Claude AI usage limit reached|1796083200")));
    }

    [Fact]
    public void RateLimited_ResetsClockTimes()
    {
        Assert.Equal(new ModelResult.RateLimited(Utc("2026-11-13T23:00:00Z")),
            P(1, Err("You've hit your limit · resets 3pm (America/Los_Angeles)")));
        Assert.Equal(new ModelResult.RateLimited(Utc("2026-11-14T08:30:00Z")),
            P(1, Err("You've hit your limit · resets 9:30am")));
        Assert.Equal(new ModelResult.RateLimited(Utc("2026-11-13T23:00:00Z")),
            P(1, Err("You've hit your limit · resets 12am (Mars/Base)")));
        Assert.Equal(new ModelResult.RateLimited(Utc("2026-11-14T11:00:00Z")),
            P(1, Err("You've hit your limit · resets 12pm")));
        Assert.Equal(new ModelResult.RateLimited(null),
            P(1, Err("You've hit your weekly limit · resets Nov 15 at 9am")));
    }

    [Fact]
    public void RateLimited_OtherSources()
    {
        Assert.Equal(new ModelResult.RateLimited(null), P(1, Err("API Error: 529 Overloaded")));
        Assert.Equal(new ModelResult.RateLimited(null),
            P(1, """{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["rate_limit_error"]}"""));
        Assert.Equal(new ModelResult.RateLimited(null), P(1, Err("x", ",\"api_error_status\":429")));
        Assert.Equal(new ModelResult.RateLimited(null), P(1, "", "Error: Rate limit exceeded"));
    }

    [Fact]
    public void NotSignedIn_Cases()
    {
        Assert.IsType<ModelResult.NotSignedIn>(P(1, Err("Invalid API key · Please run /login")));
        Assert.IsType<ModelResult.NotSignedIn>(P(1, Err("Not logged in · Please run /login")));
        Assert.IsType<ModelResult.NotSignedIn>(P(1, Err("OAuth token has expired")));
        Assert.IsType<ModelResult.NotSignedIn>(P(1, Err("x", ",\"api_error_status\":401")));
        Assert.IsType<ModelResult.NotSignedIn>(P(1, Err("x", ",\"api_error_status\":403")));
    }

    [Fact]
    public void Failures()
    {
        Assert.Equal(new ModelResult.Failed(ModelFailure.NoStructuredOutput, null),
            P(1, """{"type":"result","subtype":"error_max_structured_output_retries","is_error":true,"errors":["x"]}"""));
        Assert.Equal(new ModelResult.Failed(ModelFailure.ProcessExit, 2), P(2, "Error: something broke"));
        Assert.Equal(new ModelResult.Failed(ModelFailure.ProcessExit, 1), P(1, "", "error: unknown option '--safe-mode'"));
        Assert.Equal(new ModelResult.Failed(ModelFailure.BadOutput, null), P(0, "hello"));
        Assert.Equal(new ModelResult.Failed(ModelFailure.ProcessExit, 0), P(0, Err("boom")));
        Assert.Equal(new ModelResult.Failed(ModelFailure.ProcessExit, 3, new TokenUsage(120, 45, null, null)), P(3, Good));
        Assert.Equal(new ModelResult.Failed(ModelFailure.ProcessExit, 1), P(1, "", "took 4290ms"));
    }

    [Fact]
    public void DeepNesting_DoesNotThrow() =>
        Assert.Equal(new ModelResult.Failed(ModelFailure.BadOutput, null), P(0, new string('[', 100)));
}
