using System.Text.RegularExpressions;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Redaction boundary for the published E2E report. The report bundle inlines the
/// full run snapshot (captured container logs, error text, diagnostics) and is served
/// from a <b>public</b> URL, so any secret or infrastructure detail in that text must be
/// masked before publishing.
///
/// <para>Strategy is layered by confidence to avoid false positives (a bare-IPv4 regex
/// would corrupt benign version strings like <c>Unix 6.1.0.17</c>):</para>
/// <list type="number">
///   <item>Known-value pass — literal replacement of values the runner actually knows
///   (VPS host/IP, Steam credentials), longest-first. Zero ambiguity.</item>
///   <item>High-confidence regex — shapes that don't collide with benign text: private-key
///   blocks, <c>/run/secrets</c> snippets, <c>key=value</c> secret assignments, SSH
///   <c>user@host</c> destinations.</item>
/// </list>
/// <para>No broad bare-IP regex here: IPs are masked at the mod source
/// (<c>ChatRedaction.MaskIp</c>); a real infra IP that still reaches the report is caught
/// by the known-value pass.</para>
///
/// <para>The <c>MaskIp</c>/<c>MaskValue</c> helpers duplicate
/// <c>mod/JunimoServer.Shared/ChatRedaction.cs</c> (the mod is net6.0 + references the game
/// DLL, so the net10.0 runner can't reference it). Keep the two in sync; a future
/// dependency-free shared library would let both consume one implementation.</para>
/// </summary>
public static class ReportRedactor
{
    // Private-key PEM blocks (any "BEGIN ... PRIVATE KEY" ... "END ... PRIVATE KEY").
    private static readonly Regex PrivateKeyBlock = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled
    );

    // `password=value`, `pass=...`, `token=...`, `secret=...`, `key=...` (value up to a
    // quote, whitespace, or shell separator). Keeps the key, masks the value.
    private static readonly Regex SecretAssignment = new(
        @"(?<key>\b(?:password|passwd|pass|token|secret|api[_-]?key|access[_-]?key)\b\s*[=:]\s*)(?<val>[^\s""'`&|;]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // `/run/secrets/<name>` paths and the `$(cat /run/secrets/<name>)` snippet.
    private static readonly Regex RunSecrets = new(
        @"/run/secrets/[A-Za-z0-9_.-]+",
        RegexOptions.Compiled
    );

    // SSH-style user@host destinations (host is a hostname or IPv4).
    private static readonly Regex SshDestination = new(
        @"\b[A-Za-z0-9._-]+@(?:\d{1,3}(?:\.\d{1,3}){3}|[A-Za-z0-9.-]+\.[A-Za-z]{2,})\b",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Returns <paramref name="snapshotJson"/> with sensitive values masked in place.
    /// Masked output stays within <c>[A-Za-z0-9*.:@_-]</c>, so it never introduces a quote
    /// or backslash and the JSON (incl. the rewritten <c>artifacts/&lt;hash&gt;</c> media
    /// paths) remains valid. Must run <b>after</b> the media-path rewrite.
    /// </summary>
    public static string Scrub(string snapshotJson, IReadOnlyCollection<string> knownSecrets)
    {
        if (string.IsNullOrEmpty(snapshotJson))
        {
            return snapshotJson;
        }

        var result = snapshotJson;

        // 1. Known values — longest-first so e.g. "user@host" is masked before the bare
        //    "host" substring it contains.
        foreach (
            var secret in knownSecrets.Where(s => s.Length >= 3).OrderByDescending(s => s.Length)
        )
        {
            var masked = LooksLikeIp(secret) ? MaskIp(secret) : MaskValue(secret);
            result = result.Replace(secret, masked);
        }

        // 2. High-confidence regex.
        result = PrivateKeyBlock.Replace(
            result,
            "-----BEGIN PRIVATE KEY----- [redacted] -----END PRIVATE KEY-----"
        );
        result = RunSecrets.Replace(result, "/run/secrets/***");
        result = SshDestination.Replace(result, m => MaskValue(m.Value));
        result = SecretAssignment.Replace(
            result,
            m => m.Groups["key"].Value + MaskValue(m.Groups["val"].Value)
        );

        return result;
    }

    private static bool LooksLikeIp(string value)
    {
        if (value.IndexOf(':') >= 0)
        {
            return true; // candidate IPv6
        }

        var octets = value.Split('.');
        return octets.Length == 4
            && octets.All(o => int.TryParse(o, out var n) && n is >= 0 and <= 255);
    }

    // --- Masking helpers (mirror ChatRedaction; see class remarks) ---

    /// <summary>IPv4 → <c>***.***.***.last</c>, IPv6 → <c>***:***:…:lastHextet</c>; loopback kept.</summary>
    private static string MaskIp(string ip)
    {
        switch (ip)
        {
            case "127.0.0.1":
            case "0.0.0.0":
            case "::1":
            case "::":
                return ip;
        }

        if (ip.IndexOf(':') >= 0)
        {
            var parts = ip.Split(':');
            for (var i = parts.Length - 1; i >= 0; i--)
            {
                if (parts[i].Length > 0)
                {
                    return "***:***:…:" + parts[i];
                }
            }

            return ip;
        }

        var octets = ip.Split('.');
        return octets.Length == 4 ? "***.***.***." + octets[3] : ip;
    }

    /// <summary>Generic value → <c>first***last</c>; length ≤2 → <c>***</c>.</summary>
    private static string MaskValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length <= 2)
        {
            return "***";
        }

        return $"{value[0]}***{value[value.Length - 1]}";
    }
}
