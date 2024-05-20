using JunimoServer.Util;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace JunimoServer.Services.Backup
{
    public class BackupScheduler
    {
        private readonly BackupService _backupService;
        private readonly IModHelper _modHelper;
        private readonly IMonitor _monitor;

        private const string BackupCreating = "Creating Backup...";
        private const string BackupSuccess = "Backup Successful";
        private const string BackupFail = "Backup Failed";


        public BackupScheduler(IModHelper modHelper, BackupService backupService, IMonitor monitor)
        {
            _backupService = backupService;
            _monitor = monitor;
            _modHelper = modHelper;

            modHelper.Events.GameLoop.SaveCreated += OnSaveCreated; // day 0
            modHelper.Events.GameLoop.Saved += OnSaveCreated; // every other day
        }

        private void OnSaveCreated(object sender, SavedEventArgs e)
        {
            CreateBackup();
        }

        private void OnSaveCreated(object sender, SaveCreatedEventArgs e)
        {
            CreateBackup();
        }

        private void CreateBackup()
        {
            _monitor.Log(BackupCreating, LogLevel.Info);
            _modHelper.SendPublicMessage(BackupCreating);
            var success = _backupService.CreateBackupForCurrentDaySync();

            var backupStatus = success ? BackupSuccess : BackupFail;
            _modHelper.SendPublicMessage(backupStatus);
            _monitor.Log(backupStatus, LogLevel.Info);
        }
    }
}