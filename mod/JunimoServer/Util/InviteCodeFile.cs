using System;
using System.IO;

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
        /// Writes the invite code to the file.
        /// </summary>
        public static void Write(string inviteCode)
        {
            try
            {
                if (string.IsNullOrEmpty(inviteCode))
                {
                    return;
                }

                File.WriteAllText(FilePath, inviteCode);
            }
            catch (Exception)
            {
                // Fail silently - this is a convenience feature
            }
        }

        /// <summary>
        /// Reads the invite code from the file.
        /// Returns null if the file doesn't exist or cannot be read.
        /// </summary>
        public static string Read()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return File.ReadAllText(FilePath).Trim();
                }
            }
            catch (Exception)
            {
                // Fail silently
            }

            return null;
        }

        /// <summary>
        /// Clears the invite code file.
        /// </summary>
        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch (Exception)
            {
                // Fail silently
            }
        }
    }
}
