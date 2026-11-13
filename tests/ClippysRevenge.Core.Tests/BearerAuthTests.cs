using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace ClippysRevenge.Core.Tests;

public class BearerAuthTests
{
    private const string Good = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string New = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private sealed record Result(int Status, bool Called, long BodyLength, long? ContentLength, IHeaderDictionary Headers);

    private static async Task<Result> Run(
        Action<IHeaderDictionary> headers, TokenHolder? holder = null, string method = "POST", BearerAuth? mw = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = "/mcp";
        ctx.Response.Body = new MemoryStream();
        headers(ctx.Request.Headers);
        var called = false;
        RequestDelegate next = c => { called = true; c.Response.StatusCode = 200; return Task.CompletedTask; };
        mw ??= new BearerAuth(next, holder ?? new TokenHolder(Good));
        await mw.InvokeAsync(ctx);
        return new Result(ctx.Response.StatusCode, called, ctx.Response.Body.Length, ctx.Response.ContentLength, ctx.Response.Headers);
    }

    private static Task<Result> Auth(string value) => Run(h => h.Authorization = value);

    private static void AssertRejected(Result r)
    {
        Assert.Equal(401, r.Status);
        Assert.False(r.Called);
        Assert.Equal(0, r.BodyLength);
        Assert.Equal(0, r.ContentLength);
        Assert.False(r.Headers.ContainsKey("WWW-Authenticate"));
    }

    private static void AssertPassed(Result r)
    {
        Assert.True(r.Called);
        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task MissingAuthorization_Returns401() => AssertRejected(await Run(_ => { }));

    [Fact]
    public async Task CorrectToken_CallsNext() => AssertPassed(await Auth($"Bearer {Good}"));

    [Fact]
    public async Task WrongToken_Returns401() => AssertRejected(await Auth($"Bearer {Good[..^1]}1"));

    [Fact]
    public async Task WrongLengthToken_Returns401()
    {
        AssertRejected(await Auth("Bearer abc"));
        AssertRejected(await Auth($"Bearer {Good}x"));
    }

    [Fact]
    public async Task EmptyBearerToken_Returns401()
    {
        AssertRejected(await Auth("Bearer "));
        AssertRejected(await Auth("Bearer"));
    }

    [Fact]
    public async Task OtherScheme_Returns401()
    {
        AssertRejected(await Auth($"Basic {Good}"));
        AssertRejected(await Auth($"Token {Good}"));
        AssertRejected(await Auth(Good));
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("BEARER")]
    [InlineData("BeArEr")]
    public async Task SchemeIsCaseInsensitive(string scheme) => AssertPassed(await Auth($"{scheme} {Good}"));

    [Fact]
    public async Task TokenIsCaseSensitive() => AssertRejected(await Auth($"Bearer {Good.ToUpperInvariant()}"));

    [Fact]
    public async Task ExtraWhitespaceInToken_Returns401()
    {
        AssertRejected(await Auth($"Bearer  {Good}"));
        AssertRejected(await Auth($"Bearer {Good} "));
    }

    [Fact]
    public async Task MultipleAuthorizationValues_Returns401() =>
        AssertRejected(await Run(h => h.Authorization = new StringValues(new[] { $"Bearer {Good}", $"Bearer {Good}" })));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://127.0.0.1:8765")]
    [InlineData("null")]
    [InlineData("")]
    public async Task OriginHeader_WithCorrectToken_Returns401(string origin) =>
        AssertRejected(await Run(h =>
        {
            h.Authorization = $"Bearer {Good}";
            h.Origin = origin;
        }));

    [Fact]
    public async Task OriginHeader_WithoutToken_Returns401() =>
        AssertRejected(await Run(h => h.Origin = "https://evil.example", method: "OPTIONS"));

    [Fact]
    public async Task LiveTokenSwap_OldRejectedNewAccepted()
    {
        var holder = new TokenHolder(Good);
        var mw = new BearerAuth(c => { c.Response.StatusCode = 200; return Task.CompletedTask; }, holder);
        Assert.Equal(200, (await Run(h => h.Authorization = $"Bearer {Good}", mw: mw)).Status);
        holder.Set(New);
        AssertRejected(await Run(h => h.Authorization = $"Bearer {Good}", mw: mw));
        Assert.Equal(200, (await Run(h => h.Authorization = $"Bearer {New}", mw: mw)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TokenHolder_RejectsNullEmptyWhitespace(string? x)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TokenHolder(x!));
        var holder = new TokenHolder(Good);
        Assert.ThrowsAny<ArgumentException>(() => holder.Set(x!));
        Assert.Equal(Good, holder.Current);
    }

    [Fact]
    public void TokenHolder_Matches()
    {
        var holder = new TokenHolder(Good);
        Assert.True(holder.Matches(Good));
        Assert.False(holder.Matches(null));
        Assert.False(holder.Matches(""));
        Assert.False(holder.Matches(Good + "x"));
        Assert.False(holder.Matches(Good[..^1]));
    }

    [Fact]
    public void TokenHolder_ToString_DoesNotRevealToken()
    {
        var s = new TokenHolder(Good).ToString();
        Assert.DoesNotContain(Good, s);
        Assert.DoesNotContain(Good[..8], s);
    }

    [Fact]
    public async Task UseBearerAuth_ResolvesHolderFromDi()
    {
        var sp = new ServiceCollection().AddSingleton(new TokenHolder(Good)).BuildServiceProvider();
        var app = new ApplicationBuilder(sp);
        app.UseBearerAuth();
        app.Run(c => { c.Response.StatusCode = 200; return Task.CompletedTask; });
        var pipeline = app.Build();

        async Task<int> Go(string? auth)
        {
            var ctx = new DefaultHttpContext { RequestServices = sp };
            ctx.Response.Body = new MemoryStream();
            if (auth is not null)
            {
                ctx.Request.Headers.Authorization = auth;
            }
            await pipeline(ctx);
            return ctx.Response.StatusCode;
        }

        Assert.Equal(200, await Go($"Bearer {Good}"));
        Assert.Equal(401, await Go(null));
        sp.GetRequiredService<TokenHolder>().Set(New);
        Assert.Equal(401, await Go($"Bearer {Good}"));
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var holder = new TokenHolder(Good);
        RequestDelegate next = _ => Task.CompletedTask;
        Assert.Throws<ArgumentNullException>(() => new BearerAuth(null!, holder));
        Assert.Throws<ArgumentNullException>(() => new BearerAuth(next, null!));
    }
}
