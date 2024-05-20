using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using JunimoServer.Services.GameCreator;
using StardewModdingAPI;

namespace JunimoServer.Services.Daemon
{
    /// <summary>
    /// StatusUpdateBody
    /// 0 is unchanged
    /// 1 is false
    /// 2 is true
    /// </summary>
    public class StatusUpdateBody
    {
        public int BackupRestoreSuccessful { get; set; }
        public int ServerConnectable { get; set; }
    }

    public class DaemonService
    {
        private readonly HttpClient _httpClient;
        private readonly IMonitor _monitor;


        public async Task<NewGameConfig> GetConfig()
        {
            _monitor.Log("Fetching Config from Daemon", LogLevel.Info);

            var response = await _httpClient.GetAsync("/config");
            var config = await response.Content.ReadFromJsonAsync<NewGameConfig>();

            _monitor.Log($"Received config: {config}", LogLevel.Info);
            return config;
        }

        private async Task UpdateStatus(StatusUpdateBody update)
        {
            await _httpClient.PostAsJsonAsync("/status", update);
        }

        public async Task UpdateConnectableStatus()
        {
            await UpdateStatus(new StatusUpdateBody
            {
                BackupRestoreSuccessful = 0,
                ServerConnectable = 2
            });
        }

        public async Task UpdateNotConnectableStatus()
        {
            await UpdateStatus(new StatusUpdateBody
            {
                BackupRestoreSuccessful = 0,
                ServerConnectable = 1
            });
        }


        public DaemonService(HttpClient httpClient, IMonitor monitor)
        {
            _monitor = monitor;
            _httpClient = httpClient;
        }
    }
}