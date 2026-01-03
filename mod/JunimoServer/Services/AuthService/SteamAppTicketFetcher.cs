using System;
using System.Diagnostics;
using System.Text.Json;

namespace JunimoServer.Services.Auth
{
    public class SteamEncryptedAppTicket
    {
        public string Ticket { get; set; }
        public string SteamId { get; set; }
        public long Created { get; set; }
    }

    public class SteamAppTicketFetcher
    {
        private readonly string _nodeScriptPath = "/data/Tools/steam-appticket-generator/index.js";
        private readonly string _username;
        private readonly string _password;
        private readonly int _timeoutMs;

        public SteamAppTicketFetcher(string username, string password, int timeoutMs = 30000)
        {
            _username = username;
            _password = password;
            _timeoutMs = timeoutMs;
        }

        public SteamEncryptedAppTicket? GetTicket()
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{_nodeScriptPath}\" \"{_username}\" \"{_password}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();

            if (!process.WaitForExit(_timeoutMs))
            {
                process.Kill();
                throw new Exception("Node.js process timed out");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Node.js process exited with code {process.ExitCode}.\nStderr: {stderr}");
            }

            var jsonLine = ExtractJson(stdout);
            if (jsonLine == null)
            {
                throw new Exception($"No JSON output found. Stdout:\n{stdout}\nStderr:\n{stderr}");
            }

            return JsonSerializer.Deserialize<SteamEncryptedAppTicket>(jsonLine);
        }

        private string? ExtractJson(string stdout)
        {
            int start = stdout.IndexOf('{');
            int end = stdout.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                return stdout[start..(end + 1)];
            }
            return null;
        }
    }
}
