using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace JunimoServer.Services.Backup
{
    public class CreateBackupRequest
    {
        public BackupDate Date { get; set; }
    }

    public class BackupDate
    {
        public int Day { get; set; }
        public int Season { get; set; }
        public int Year { get; set; }
    }

    public class BackupService
    {
        private readonly HttpClient _httpClient;
        private readonly IMonitor _monitor;

        public BackupService(HttpClient httpClient, IMonitor monitor)
        {
            _httpClient = httpClient;
            _monitor = monitor;
        }

        public bool CreateBackupForCurrentDaySync()
        {
            var dateNow = SDate.Now();
            var request = new CreateBackupRequest
            {
                Date = new BackupDate
                {
                    Day = dateNow.Day,
                    Season = dateNow.SeasonIndex,
                    Year = dateNow.Year
                }
            };


            try
            {
                var response = _httpClient.PostAsJsonAsync("/backup", request);
                response.Wait();
                return response.Result.StatusCode == HttpStatusCode.OK;
            }
            catch (Exception e)
            {
                _monitor.Log(e.ToString(), LogLevel.Error);
                return false;
            }
        }
    }
}