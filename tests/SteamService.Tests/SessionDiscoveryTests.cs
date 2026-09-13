using System.Text.Json;
using Xunit;

namespace SteamService.Tests;

public class FindSessionsTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(
        Path.GetTempPath(),
        "steam-sessions-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
        {
            Directory.Delete(_baseDir, recursive: true);
        }
    }

    [Fact]
    public void MissingDirectoryYieldsEmpty()
    {
        Assert.Empty(SteamAuthService.FindSessions(_baseDir));
    }

    [Fact]
    public void EmptyDirectoryYieldsEmpty()
    {
        Directory.CreateDirectory(_baseDir);

        Assert.Empty(SteamAuthService.FindSessions(_baseDir));
    }

    [Fact]
    public void OrdersNewestFirst()
    {
        var now = DateTime.UtcNow;
        WriteSession("old", now.AddDays(-2));
        WriteSession("newest", now);
        WriteSession("middle", now.AddHours(-1));

        var sessions = SteamAuthService.FindSessions(_baseDir);

        Assert.Equal(["newest", "middle", "old"], sessions.Select(s => s.username));
        Assert.Equal(Path.Combine(_baseDir, "newest", "session.json"), sessions[0].path);
    }

    [Fact]
    public void SkipsMalformedTokenlessAndSessionlessFolders()
    {
        WriteSession("valid", DateTime.UtcNow);
        WriteRaw("malformed", "{ not json");
        WriteRaw("notoken", JsonSerializer.Serialize(new { username = "notoken" }));
        WriteRaw(
            "blanktoken",
            JsonSerializer.Serialize(new { username = "x", refreshToken = " " })
        );
        Directory.CreateDirectory(Path.Combine(_baseDir, "nofile"));

        var sessions = SteamAuthService.FindSessions(_baseDir);

        Assert.Equal(["valid"], sessions.Select(s => s.username));
    }

    [Fact]
    public void UsernameComesFromFileContentNotFolderName()
    {
        WriteRaw(
            "folder",
            JsonSerializer.Serialize(new { username = "inside", refreshToken = "t" })
        );

        Assert.Equal("inside", Assert.Single(SteamAuthService.FindSessions(_baseDir)).username);
    }

    private void WriteSession(string username, DateTime writtenUtc)
    {
        var path = WriteRaw(
            username,
            JsonSerializer.Serialize(new { username, refreshToken = "token-" + username })
        );
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    private string WriteRaw(string folder, string content)
    {
        var dir = Path.Combine(_baseDir, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "session.json");
        File.WriteAllText(path, content);
        return path;
    }
}

public class SessionChoiceTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<(string username, string path, DateTime writtenUtc)> Sessions(
        params string[] usernames
    ) => usernames.Select((u, i) => (u, $"/s/{u}/session.json", Now.AddHours(-i))).ToList();

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("anything")]
    public void NoSessionsAlwaysResolvesToFreshLogin(string input)
    {
        Assert.True(SteamAuthService.TryResolveSessionChoice(Sessions(), input, out var chosen));
        Assert.Null(chosen);
    }

    [Theory]
    [InlineData("", "alice")]
    [InlineData("y", "alice")]
    [InlineData("YES", "alice")]
    [InlineData(null, "alice")]
    [InlineData("n", null)]
    [InlineData(" No ", null)]
    public void SingleSessionIsYesUnlessDeclined(string? input, string? expected)
    {
        Assert.True(
            SteamAuthService.TryResolveSessionChoice(Sessions("alice"), input, out var chosen)
        );
        Assert.Equal(expected, chosen);
    }

    [Theory]
    [InlineData("", "alice")]
    [InlineData(null, "alice")]
    [InlineData("1", "alice")]
    [InlineData("2", "bob")]
    [InlineData(" 3 ", "carol")]
    [InlineData("n", null)]
    [InlineData("no", null)]
    public void MultipleSessionsPickByNumberOrDefaultToNewest(string? input, string? expected)
    {
        Assert.True(
            SteamAuthService.TryResolveSessionChoice(
                Sessions("alice", "bob", "carol"),
                input,
                out var chosen
            )
        );
        Assert.Equal(expected, chosen);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("y")]
    [InlineData("bob")]
    public void MultipleSessionsRejectOutOfRangeOrUnknownInput(string input)
    {
        Assert.False(
            SteamAuthService.TryResolveSessionChoice(
                Sessions("alice", "bob", "carol"),
                input,
                out var chosen
            )
        );
        Assert.Null(chosen);
    }
}
