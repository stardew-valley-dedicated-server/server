using System;

namespace JunimoServer.Shared
{
    public static class ChatRedaction
    {
        /// <summary>
        /// Redacts the password argument in <c>!login &lt;password&gt;</c> so chat text
        /// can be safely written to logs and structured event streams.
        /// </summary>
        public static string MaskSecrets(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            var trimmed = message.TrimStart();
            const string prefix = "!login";
            if (trimmed.Length < prefix.Length
                || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length > prefix.Length && !char.IsWhiteSpace(trimmed[prefix.Length])))
            {
                return message;
            }

            var password = trimmed.Length > prefix.Length
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
