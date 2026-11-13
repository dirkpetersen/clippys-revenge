using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace ClippysRevenge.Core;

/// <summary>Holds the expected bearer token. Thread-safe.</summary>
public sealed class TokenHolder
{
    private sealed record Snapshot(string Token, byte[] Hash);

    private Snapshot _snapshot;

    public TokenHolder(string initialToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialToken);
        _snapshot = Make(initialToken);
    }

    public string Current => Volatile.Read(ref _snapshot).Token;

    public void Set(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Volatile.Write(ref _snapshot, Make(token));
    }

    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }
        var snap = Volatile.Read(ref _snapshot);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(hash, snap.Hash);
    }

    public override string ToString() => "TokenHolder";

    private static Snapshot Make(string token) =>
        new(token, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>Middleware guarding the MCP endpoint. Put it before MapMcp.</summary>
public sealed class BearerAuth
{
    private const string Prefix = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly TokenHolder _tokens;

    public BearerAuth(RequestDelegate next, TokenHolder tokens)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(tokens);
        _next = next;
        _tokens = tokens;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Headers.ContainsKey(HeaderNames.Origin))
        {
            return Reject(context);
        }

        var auth = request.Headers.Authorization;
        if (auth.Count != 1)
        {
            return Reject(context);
        }

        var value = auth[0];
        if (value is null || !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(context);
        }

        if (!_tokens.Matches(value.Substring(Prefix.Length)))
        {
            return Reject(context);
        }

        return _next(context);
    }

    private static Task Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }
}

public static class BearerAuthExtensions
{
    public static IApplicationBuilder UseBearerAuth(this IApplicationBuilder app) =>
        app.UseMiddleware<BearerAuth>();
}
