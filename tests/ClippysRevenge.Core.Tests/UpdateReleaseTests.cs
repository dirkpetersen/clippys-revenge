using System.Text.Json.Nodes;

namespace ClippysRevenge.Core.Tests;

public class UpdateReleaseTests : IDisposable
{
    private const string ExeHex = "05c97b27c8fc33a1f11c4a658121397d7c309e6d64ccdce3f2bc9ff362b2b2f5";
    private const string InstallHex = "ffc97b27c8fc33a1f11c4a658121397d7c309e6d64ccdce3f2bc9ff362b2b2f5";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-upd-" + Guid.NewGuid().ToString("N"));

    public UpdateReleaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonObject Fixture() => JsonNode.Parse($$"""
        {
          "url": "https://api.github.com/repos/dirkpetersen/clippys-revenge/releases/1",
          "html_url": "https://github.com/dirkpetersen/clippys-revenge/releases/tag/v0.0.4",
          "author": { "login": "someone", "id": 1 },
          "tag_name": "v0.0.4",
          "name": "v0.0.4",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-11-13T10:00:00Z",
          "body": "notes",
          "assets": [
            { "name": "ClippysRevenge.exe", "state": "uploaded", "size": 149542663,
              "digest": "sha256:{{ExeHex}}", "content_type": "application/octet-stream" },
            { "name": "install.ps1", "state": "uploaded", "size": 4000 },
            { "name": "SHA256SUMS.txt", "state": "uploaded", "size": 163 }
          ]
        }
        """)!.AsObject();

    private static ReleaseParseResult Parse(JsonObject o) => ReleaseParser.Parse(o.ToJsonString());

    private static JsonObject Asset(JsonObject root, string name) =>
        root["assets"]!.AsArray().Select(a => a!.AsObject()).First(a => (string?)a["name"] == name);

    [Fact]
    public void AssetUrl_BuildsUrl()
    {
        Assert.Equal(
            "https://github.com/dirkpetersen/clippys-revenge/releases/download/v0.0.6/ClippysRevenge.exe",
            UpdateEndpoints.AssetUrl("v0.0.6", "ClippysRevenge.exe").AbsoluteUri);
        Assert.Equal(
            "https://github.com/dirkpetersen/clippys-revenge/releases/download/v0.0.6/SHA256SUMS.txt",
            UpdateEndpoints.AssetUrl("v0.0.6", "SHA256SUMS.txt").AbsoluteUri);
    }

    [Theory]
    [InlineData("0.0.6", "ClippysRevenge.exe")]
    [InlineData("v0.0.6-beta.1", "ClippysRevenge.exe")]
    [InlineData("v0.0.6/../x", "ClippysRevenge.exe")]
    [InlineData("v0.0.6", "install.ps1")]
    [InlineData("v0.0.6", "clippysrevenge.exe")]
    public void AssetUrl_Rejects(string tag, string asset) =>
        Assert.Throws<ArgumentException>(() => UpdateEndpoints.AssetUrl(tag, asset));

    [Fact]
    public void ReleaseNotesUrl()
    {
        Assert.Equal("https://github.com/dirkpetersen/clippys-revenge/releases/tag/v0.0.6",
            UpdateEndpoints.ReleaseNotesUrl("v0.0.6"));
        Assert.Throws<ArgumentException>(() => UpdateEndpoints.ReleaseNotesUrl("0.0.6"));
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sp=r", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://GitHub.com/x", true)]
    [InlineData("https://github.com:443/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    [InlineData("https://evil.com/x", false)]
    [InlineData("https://github.com.evil.com/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    [InlineData("https://user@github.com/x", false)]
    [InlineData("https://api.github.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    public void IsAllowedRedirect(string url, bool expected) =>
        Assert.Equal(expected, UpdateEndpoints.IsAllowedRedirect(new Uri(url)));

    [Fact]
    public void IsAllowedRedirect_RelativeAndNull()
    {
        Assert.False(UpdateEndpoints.IsAllowedRedirect(new Uri("/x", UriKind.Relative)));
        Assert.False(UpdateEndpoints.IsAllowedRedirect(null));
        Assert.Equal(["github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"],
            UpdateEndpoints.AllowedDownloadHosts);
    }

    [Fact]
    public void UserAgent() =>
        Assert.Equal("ClippysRevenge/0.0.6", UpdateEndpoints.UserAgent(SemVersion.Parse("0.0.6")));

    [Fact]
    public void Parse_RealisticRelease()
    {
        var r = Parse(Fixture());
        Assert.Null(r.Error);
        var rel = r.Release!;
        Assert.Equal("v0.0.4", rel.Tag);
        Assert.Equal("0.0.4", rel.Version.ToString());
        Assert.Equal(149542663L, rel.ExeSize);
        Assert.Equal(163L, rel.SumsSize);
        Assert.Equal(ExeHex, rel.ExeDigest);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero), rel.PublishedUtc);
        Assert.Equal("https://github.com/dirkpetersen/clippys-revenge/releases/tag/v0.0.4", rel.NotesUrl);
    }

    private static void Expect(Action<JsonObject> mutate, string error)
    {
        var o = Fixture();
        mutate(o);
        var r = Parse(o);
        Assert.Null(r.Release);
        Assert.Equal(error, r.Error);
    }

    [Fact]
    public void Parse_Variations()
    {
        Expect(o => o["draft"] = true, UpdateTexts.ReasonNotFullRelease);
        Expect(o => o["prerelease"] = true, UpdateTexts.ReasonNotFullRelease);
        Expect(o => o["prerelease"] = "no", UpdateTexts.ReasonBadJson);
        Expect(o => o["tag_name"] = "v0.0.5-beta.1", UpdateTexts.ReasonNotFullRelease);
        Expect(o => o["tag_name"] = "0.0.5", UpdateTexts.ReasonNotFullRelease);
        Expect(o => o.Remove("tag_name"), UpdateTexts.ReasonNotFullRelease);
        Expect(o => o.Remove("assets"), UpdateTexts.ReasonNoAssets);
        Expect(o => o["assets"] = new JsonObject(), UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "ClippysRevenge.exe")["name"] = "other.exe", UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "SHA256SUMS.txt")["name"] = "other.txt", UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "ClippysRevenge.exe")["name"] = "clippysrevenge.exe", UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "ClippysRevenge.exe")["state"] = "starter", UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "ClippysRevenge.exe")["size"] = 1000, UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "ClippysRevenge.exe")["size"] = 500L * 1024 * 1024, UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "ClippysRevenge.exe")["size"] = "149542663", UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "SHA256SUMS.txt")["size"] = 0, UpdateTexts.ReasonNoAssets);
        Expect(o => Asset(o, "SHA256SUMS.txt")["size"] = 70000, UpdateTexts.ReasonNoAssets);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("")]
    public void Parse_BadJson(string json)
    {
        var r = ReleaseParser.Parse(json);
        Assert.Null(r.Release);
        Assert.Equal(UpdateTexts.ReasonBadJson, r.Error);
    }

    [Fact]
    public void Parse_NullAndTooLongAndTooDeep()
    {
        Assert.Equal(UpdateTexts.ReasonBadJson, ReleaseParser.Parse(null).Error);
        Assert.Equal(UpdateTexts.ReasonBadJson,
            ReleaseParser.Parse(new string(' ', ReleaseParser.MaxJsonBytes + 1)).Error);
        var deep = Fixture().ToJsonString();
        deep = deep.TrimEnd('}') + ",\"x\":" + new string('[', 40) + new string(']', 40) + "}";
        Assert.Equal(UpdateTexts.ReasonBadJson, ReleaseParser.Parse(deep).Error);
    }

    [Fact]
    public void Parse_OptionalFields()
    {
        var o = Fixture();
        o["published_at"] = "yesterday";
        var r = Parse(o);
        Assert.NotNull(r.Release);
        Assert.Null(r.Release.PublishedUtc);

        o = Fixture();
        Asset(o, "ClippysRevenge.exe").Remove("digest");
        Assert.Null(Parse(o).Release!.ExeDigest);

        o = Fixture();
        Asset(o, "ClippysRevenge.exe")["digest"] = "md5:abc";
        Assert.Null(Parse(o).Release!.ExeDigest);

        o = Fixture();
        Asset(o, "ClippysRevenge.exe")["digest"] = "sha256:" + ExeHex.ToUpperInvariant();
        Assert.Equal(ExeHex, Parse(o).Release!.ExeDigest);
    }

    [Fact]
    public void Parse_FirstDuplicateWins()
    {
        var o = Fixture();
        var extra = new JsonObject { ["name"] = "ClippysRevenge.exe", ["state"] = "uploaded", ["size"] = 2_000_000 };
        o["assets"]!.AsArray().Add(extra);
        Assert.Equal(149542663L, Parse(o).Release!.ExeSize);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Sums_Find_Basic(string eol)
    {
        var text = $"{ExeHex} *ClippysRevenge.exe{eol}{InstallHex} *install.ps1{eol}";
        Assert.Equal(ExeHex, Sha256Sums.Find(text, "ClippysRevenge.exe"));
        Assert.Equal(InstallHex, Sha256Sums.Find(text, "install.ps1"));
    }

    [Fact]
    public void Sums_Find_Variants()
    {
        Assert.Equal(ExeHex, Sha256Sums.Find("﻿" + ExeHex + " *ClippysRevenge.exe\n", "ClippysRevenge.exe"));
        Assert.Equal(ExeHex, Sha256Sums.Find(ExeHex + "  ClippysRevenge.exe\n", "ClippysRevenge.exe"));
        Assert.Equal(ExeHex, Sha256Sums.Find(ExeHex.ToUpperInvariant() + " *ClippysRevenge.exe", "ClippysRevenge.exe"));
        Assert.Null(Sha256Sums.Find(InstallHex + " *install.ps1\n", "ClippysRevenge.exe"));
        Assert.Null(Sha256Sums.Find(ExeHex + " *ClippysRevenge.exe.old\n", "ClippysRevenge.exe"));
        Assert.Null(Sha256Sums.Find(ExeHex + " *./ClippysRevenge.exe\n", "ClippysRevenge.exe"));
        Assert.Equal(ExeHex, Sha256Sums.Find(
            ExeHex + " *ClippysRevenge.exe\n" + ExeHex + " *ClippysRevenge.exe\n", "ClippysRevenge.exe"));
        Assert.Null(Sha256Sums.Find(
            ExeHex + " *ClippysRevenge.exe\n" + InstallHex + " *ClippysRevenge.exe\n", "ClippysRevenge.exe"));
        Assert.Null(Sha256Sums.Find(ExeHex[1..] + " *ClippysRevenge.exe\n", "ClippysRevenge.exe"));
        Assert.Null(Sha256Sums.Find(null, "ClippysRevenge.exe"));
    }

    [Fact]
    public async Task Hash_OfFile()
    {
        var path = Path.Combine(_dir, "abc.txt");
        await File.WriteAllBytesAsync(path, "abc"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            await UpdateHash.OfFileAsync(path, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            UpdateHash.OfFileAsync(Path.Combine(_dir, "missing"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Hash_Helpers()
    {
        Assert.True(UpdateHash.IsSha256Hex(ExeHex));
        Assert.True(UpdateHash.IsSha256Hex(ExeHex.ToUpperInvariant()));
        Assert.False(UpdateHash.IsSha256Hex(ExeHex[1..]));
        Assert.False(UpdateHash.IsSha256Hex("g" + ExeHex[1..]));
        Assert.False(UpdateHash.IsSha256Hex(null));
        Assert.True(UpdateHash.Same(ExeHex, ExeHex.ToUpperInvariant()));
        Assert.False(UpdateHash.Same(ExeHex, null));
        Assert.False(UpdateHash.Same(null, null));
        Assert.Equal("00ff10", UpdateHash.ToHex([0x00, 0xFF, 0x10]));
    }
}
