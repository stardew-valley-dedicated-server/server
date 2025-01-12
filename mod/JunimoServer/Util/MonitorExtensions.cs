using StardewModdingAPI;

namespace JunimoServer.Util
{
    public static class MonitorExtensions
    {
        public static void LogBanner(this IMonitor monitor, string[] lines, int pad = 4, bool centered = false)
        {
            // Calculate banner width based on longest line
            int longestLine = 0;
            foreach (string line in lines)
            {
                if (line.Length > longestLine)
                {
                    longestLine = line.Length;

                }
            }

            // Ensure longest line to be power of two for correct padding
            if (longestLine % 2 != 0)
            {
                longestLine = longestLine + 1;
            }

            int width = longestLine + pad * 2 + 2;

            // Print full border
            monitor.Log(new string('*', width), LogLevel.Info);

            // Print padding line
            monitor.Log("*" + new string(' ', width - 2) + "*", LogLevel.Info);

            // Print each line, centered within the banner
            foreach (string line in lines)
            {
                if (centered)
                {
                    int totalPadding = width - line.Length - 2;
                    int leftPadding = totalPadding / 2;
                    int rightPadding = totalPadding - leftPadding;

                    monitor.Log("*" + new string(' ', leftPadding) + line + new string(' ', rightPadding) + "*", LogLevel.Info);
                }
                else
                {
                    int totalPadding = width - line.Length - 2;
                    int leftPadding = pad;
                    int rightPadding = totalPadding - leftPadding;

                    monitor.Log("*" + new string(' ', leftPadding) + line + new string(' ', rightPadding) + "*", LogLevel.Info);
                }
            }

            // Print padding line
            monitor.Log("*" + new string(' ', width - 2) + "*", LogLevel.Info);

            // Print full border
            monitor.Log(new string('*', width), LogLevel.Info);
        }
    }
}
