using System;
using System.IO;
using StardewModdingAPI;

namespace JunimoServer.Util
{
    /// <summary>
    /// Manages reading and writing the server invite code to a file
    /// for display in the CLI and other external tools.
    /// </summary>
    public static class InviteCodeFile
    {
        private static readonly string FilePath = "/tmp/invite-code.txt";

        /// <summary>
        /// Checks if the invite code file exists.
        /// </summary>
        public static bool Exists()
        {
            return File.Exists(FilePath);
        }

        /// <summary>
        /// Writes the invite code to the file.
        /// </summary>
        /// <returns>True if write succeeded, false otherwise.</returns>
        public static bool Write(string inviteCode, IMonitor monitor)
        {
            if (string.IsNullOrEmpty(inviteCode))
            {
                monitor.Log($"Failed to write invite code to '{FilePath}': value cannot be null or empty", LogLevel.Error);
                return false;
            }

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                monitor.Log($"Failed to write invite code to '{FilePath}': directory '{directory}' does not exist", LogLevel.Error);
                return false;
            }

            try
            {
                File.WriteAllText(FilePath, inviteCode);
                monitor.Log($"Invite code written to '{FilePath}'", LogLevel.Trace);
                return true;
            }
            catch (Exception ex)
            {
                monitor.Log($"Failed to write invite code to '{FilePath}': {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Reads the invite code from the file.
        /// </summary>
        /// <returns>The invite code, or null if it could not be read.</returns>
        public static string Read(IMonitor monitor)
        {
            if (!File.Exists(FilePath))
            {
                monitor.Log($"Failed to read invite code from '{FilePath}': file does not exist (was Write ever called?)", LogLevel.Warn);
                return null;
            }

            try
            {
                var content = File.ReadAllText(FilePath).Trim();

                if (string.IsNullOrEmpty(content))
                {
                    monitor.Log($"Failed to read invite code from '{FilePath}': file is empty", LogLevel.Error);
                    return null;
                }

                // monitor.Log($"Invite code read from '{FilePath}'", LogLevel.Trace);
                return content;
            }
            catch (Exception ex)
            {
                monitor.Log($"Failed to read invite code from '{FilePath}': {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Clears the invite code file.
        /// </summary>
        /// <returns>True if clear succeeded, false otherwise.</returns>
        public static bool Clear(IMonitor monitor)
        {
            if (!File.Exists(FilePath))
            {
                monitor.Log($"Invite code file '{FilePath}' does not exist, nothing to clear", LogLevel.Trace);
                return true;
            }

            try
            {
                File.Delete(FilePath);
                monitor.Log($"Invite code file '{FilePath}' cleared", LogLevel.Trace);
                return true;
            }
            catch (Exception ex)
            {
                monitor.Log($"Failed to clear invite code file '{FilePath}': {ex.Message}", LogLevel.Error);
                return false;
            }
        }
    }
}
