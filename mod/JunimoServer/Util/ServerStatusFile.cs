using System;
using System.IO;
using System.Text.Json;

namespace JunimoServer.Util
{
    /// <summary>
    /// Manages reading and writing server status to a JSON file
    /// for external tools like Discord bots.
    /// </summary>
    public static class ServerStatusFile
    {
        private static readonly string FilePath = "/tmp/server-status.json";

        public class ServerStatus
        {
            public int PlayerCount { get; set; }
            public int MaxPlayers { get; set; }
            public string InviteCode { get; set; }
            public string ServerVersion { get; set; }
            public bool IsOnline { get; set; }
            public string LastUpdated { get; set; }
        }

        /// <summary>
        /// Writes the server status to the JSON file.
        /// </summary>
        public static void Write(ServerStatus status)
        {
            if (status == null)
            {
                throw new ArgumentNullException(nameof(status));
            }

            status.LastUpdated = DateTime.UtcNow.ToString("o");

            var json = JsonSerializer.Serialize(status, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, json);
        }

        /// <summary>
        /// Reads the server status from the JSON file.
        /// </summary>
        /// <returns>The server status, or null if the file doesn't exist.</returns>
        public static ServerStatus Read()
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<ServerStatus>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        /// <summary>
        /// Clears the server status file.
        /// </summary>
        public static void Clear()
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
    }
}
