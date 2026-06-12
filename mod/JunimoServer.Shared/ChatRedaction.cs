using System;

namespace JunimoServer.Shared
{
    /// <summary>
    /// In-place masking for values that must not appear verbatim in logs or in the
    /// (publicly published) E2E report. Masks preserve shape so "a value was here"
    /// stays visible: an IP keeps its last octet, a generic value keeps first+last char.
    ///
    /// <para><c>MaskIp</c>/<c>MaskValue</c> are duplicated in the test runner's
    /// <c>ReportRedactor</c> (net10.0, can't reference this net6.0 + game-DLL project).
    /// Keep the two in sync; a future dependency-free shared library would let both consume
    /// one implementation.</para>
    /// </summary>
    public static class ChatRedaction
    {
        /// <summary>
        /// Masks an IP address, keeping the last segment so it reads as an IP:
        /// IPv4 <c>46.38.238.188</c> → <c>***.***.***.188</c>, IPv6 keeps the last hextet.
        /// Loopback / unspecified addresses (<c>127.0.0.1</c>, <c>0.0.0.0</c>, <c>::1</c>,
        /// <c>::</c>) are not sensitive and returned unchanged. Non-IP input is returned
        /// as-is (callers may pass <c>"n/a"</c>).
        /// </summary>
        public static string MaskIp(string ip)
        {
            if (string.IsNullOrEmpty(ip))
            {
                return ip;
            }

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
                // IPv6: keep the last non-empty hextet.
                var parts = ip.Split(':');
                var last = "";
                for (var i = parts.Length - 1; i >= 0; i--)
                {
                    if (parts[i].Length > 0)
                    {
                        last = parts[i];
                        break;
                    }
                }
                return last.Length > 0 ? "***:***:…:" + last : ip;
            }

            var octets = ip.Split('.');
            if (octets.Length == 4)
            {
                return "***.***.***." + octets[3];
            }

            return ip;
        }

        /// <summary>
        /// Masks a value to <c>first***last</c>, hiding the middle while keeping its
        /// presence and rough length-class visible. Values of length ≤2 collapse to
        /// <c>***</c> (too short to reveal a hint). Empty input is returned unchanged.
        /// </summary>
        public static string MaskValue(string value)
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

        /// <summary>
        /// Redacts the password argument in <c>!login &lt;password&gt;</c> so chat text
        /// can be safely written to logs and structured event streams.
        /// </summary>
        public static string MaskSecrets(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message;
            }

            var trimmed = message.TrimStart();
            const string prefix = "!login";
            if (
                trimmed.Length < prefix.Length
                || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length > prefix.Length && !char.IsWhiteSpace(trimmed[prefix.Length]))
            )
            {
                return message;
            }

            var password =
                trimmed.Length > prefix.Length
                    ? trimmed[(prefix.Length + 1)..].Trim()
                    : string.Empty;
            const string stars = "******";
            if (password.Length == 0)
            {
                return "!login";
            }
            if (password.Length == 1)
            {
                return "!login " + stars;
            }
            return $"!login {password[0]}{stars}{password[^1]}";
        }
    }
}
