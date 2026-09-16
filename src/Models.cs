using System;
using System.Collections.Generic;
using System.Threading;

namespace DdalggakBackup
{
    public static class Catalog
    {
        public static readonly string[] Items = { "1_LEAD", "2_CORNER", "3_BODY", "4_SIDE", "5_TERR", "6_SHSD" };
        public const string IncompleteSuffix = "__미완료";
    }
    public sealed class MachineProfile
    {
        public string Name { get; set; }
        public string SourceRoot { get; set; }
        public string BackupRoot { get; set; }
        public string[] ItemNames { get; set; }
        public override string ToString() { return Name; }
    }
    public sealed class AppSettings
    {
        public int SelectedProfile { get; set; }
        public string LastName { get; set; }
        public List<MachineProfile> Profiles { get; set; }
    }
    public sealed class BackupRequest
    {
        public string SourceRoot { get; set; }
        public string BackupRoot { get; set; }
        public string Name { get; set; }
        public string[] Items { get; set; }
        // All six configured source folder names, including unselected items.
        // Null retains compatibility with requests from earlier versions.
        public string[] ConfiguredItems { get; set; }
        public DateTime StartedAt { get; set; }
        // Empty in production; tests can keep all data inside their sandbox.
        public string LogDirectory { get; set; }
    }
    public enum BackupState { Pending, Preparing, Copying, Complete, Partial, Failed, Cancelled }
    public sealed class DetailEntry
    {
        public DateTime Time { get; set; }
        public string Item { get; set; }
        public string Kind { get; set; }
        public string Path { get; set; }
        public string Message { get; set; }
    }
    public sealed class ItemResult
    {
        public string Item { get; set; }
        public BackupState State { get; set; }
        public string Destination { get; set; }
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int SucceededFiles { get; set; }
        public int FailedFiles { get; set; }
        public int ExcludedEntries { get; set; }
        public int Warnings { get; set; }
        public List<DetailEntry> Details { get; set; }
        public ItemResult() { Details = new List<DetailEntry>(); State = BackupState.Pending; }
    }
    public sealed class BackupResult
    {
        public BackupState State { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime FinishedAt { get; set; }
        public string LogPath { get; set; }
        public string Message { get; set; }
        public List<ItemResult> Items { get; set; }
        public List<DetailEntry> Details { get; set; }
        public BackupResult() { Items = new List<ItemResult>(); Details = new List<DetailEntry>(); }
    }
    public sealed class BackupProgress
    {
        public BackupState State { get; set; }
        public string Item { get; set; }
        public string CurrentFile { get; set; }
        public string Message { get; set; }
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int SucceededFiles { get; set; }
        public int FailedFiles { get; set; }
    }
    public sealed class SourceStatus
    {
        public bool Available { get; set; }
        public string Message { get; set; }
    }
}
