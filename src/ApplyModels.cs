using System;

namespace DdalggakBackup
{
    public sealed class ApplyRequest
    {
        public string SourceRoot { get; set; }
        public string BackupRoot { get; set; }
        public string Name { get; set; }
        public string Item { get; set; }
        public string[] ConfiguredItems { get; set; }
        public string[] InputPaths { get; set; }
        public DateTime StartedAt { get; set; }
        public string LogDirectory { get; set; }
    }

    public enum ApplyPhase { Preparing, BackingUp, Applying }

    public sealed class ApplyProgress
    {
        public ApplyPhase Phase { get; set; }
        public BackupProgress Progress { get; set; }
    }

    public sealed class ApplyResult
    {
        public BackupState State { get; set; }
        public BackupResult Backup { get; set; }
        public ItemResult Application { get; set; }
        public string TargetPath { get; set; }
        public string LogPath { get; set; }
        public string Message { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }

        public ApplyResult()
        {
            State = BackupState.Pending;
            Application = new ItemResult();
        }
    }
}
