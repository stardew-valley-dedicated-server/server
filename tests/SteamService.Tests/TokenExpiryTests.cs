using System.Text;
using System.Text.Json;
using Xunit;

namespace SteamService.Tests;

public class TokenExpiryTests
{
    [Fact]
    public void ReadsExpClaimFromBase64UrlPayload()
    {
        var exp = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);

        var parsed = SteamAuthService.GetTokenExpiry(Jwt(new { exp = exp.ToUnixTimeSeconds() }));

        Assert.Equal(exp, parsed);
    }

    [Fact]
    public void PayloadNeedingPaddingStillParses()
    {
        // Shorter and longer payloads land on every base64 remainder class.
        foreach (var filler in new[] { "", "a", "ab", "abc", "abcd" })
        {
            var exp = 1_800_000_000L;
            var parsed = SteamAuthService.GetTokenExpiry(Jwt(new { exp, sub = filler }));

            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp), parsed);
        }
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    [InlineData("h.!!notbase64!!.s")]
    public void NonJwtInputReturnsNull(string token)
    {
        Assert.Null(SteamAuthService.GetTokenExpiry(token));
    }

    [Fact]
    public void MissingOrNonNumericExpReturnsNull()
    {
        Assert.Null(SteamAuthService.GetTokenExpiry(Jwt(new { sub = "1" })));
        Assert.Null(SteamAuthService.GetTokenExpiry(Jwt(new { exp = "soon" })));
    }

    [Fact]
    public void OutOfRangeExpReturnsNull()
    {
        // Fits Int64 but is far outside DateTimeOffset's range; must not throw.
        Assert.Null(SteamAuthService.GetTokenExpiry(Jwt(new { exp = 99_999_999_999_999L })));
        Assert.Null(SteamAuthService.GetTokenExpiry(Jwt(new { exp = long.MinValue })));
    }

    private static string Jwt(object payload)
    {
        static string B64Url(string s) =>
            Convert
                .ToBase64String(Encoding.UTF8.GetBytes(s))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{B64Url("{\"alg\":\"none\"}")}.{B64Url(JsonSerializer.Serialize(payload))}.sig";
    }
}
