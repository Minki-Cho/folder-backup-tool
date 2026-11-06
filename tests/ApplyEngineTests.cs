using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using DdalggakBackup;

internal static class ApplyEngineTests
{
    private static string workRoot;
    private static int passed, failed, skipped;
    private static readonly DateTime StartedAt = new DateTime(2026, 11, 6, 12, 34, 56);

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        workRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work", "apply-tests"));
        Directory.CreateDirectory(workRoot);
        Run("backs up the full selected item before replacing files", BackupBeforeOverwrite);
        Run("folder contents merge at item root, retaining subfolders and empty folders", FolderContents);
        Run("custom configured item names select exactly one destination", CustomItem);
        Run("invalid, missing, and multiple target names cannot apply", InvalidTargets);
        Run("missing or empty input lists cannot change the target", MissingInputs);
        Run("empty dropped folders do not perform an application", EmptyInputFolder);
        Run("duplicate relative input names are rejected before backup", DuplicateInputs);
        Run("input file and directory collisions are rejected", InputTypeConflict);
        Run("existing destination file and directory collisions are rejected", DestinationTypeConflict);
        Run("inputs overlapping the selected live item are rejected", InputOverlap);
        Run("input junctions cannot import files from elsewhere", InputJunction);
        Run("excluded source junction prevents any application", TargetJunction);
        Run("unreadable backup source prevents overwrite", FailedBackup);
        Run("unreadable incoming file prevents backup and application", UnreadableInput);
        Run("insufficient staging space rejects a sparse input without copying it", InsufficientStagingSpace);
        Run("pre-cancellation leaves no target changes", PreCancelled);
        Run("cancellation during backup prevents application", CancelDuringBackup);
        Run("staged input stays fixed when original input changes during backup", ImmutableStaging);
        Run("same-size changed target after backup is not overwritten", ChangedAfterBackup);
        Run("new target appearing after backup is not overwritten", CreatedAfterBackup);
        Run("target deleted after backup is not recreated silently", DeletedAfterBackup);
        Run("junction introduced after backup cannot redirect application", JunctionAfterBackup);
        Run("locked destination replacement preserves its original file", LockedAfterBackup);
        Run("cancellation after first applied file preserves backup and remaining originals", CancelAfterFirstFile);
        Run("replacement failure stops remaining files and preserves backups", FailureAfterFirstFile);
        Run("post-success progress failure does not count a successful replacement twice", ProgressFailureAfterSuccess);
        Run("logs inside live item are rejected without target writes", UnsafeLogDirectory);
        Run("overlong destination is rejected before target mutation", LongDestination);
        Console.WriteLine("\nApply: {0} passed; {1} failed; {2} skipped.", passed, failed, skipped);
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (SkipException error) { skipped++; Console.WriteLine("SKIP " + name + ": " + error.Message); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + "\n" + error); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        Assert(EqualityComparer<T>.Default.Equals(expected, actual), message + ": expected " + expected + ", got " + actual);
    }

    private static void Complete(ApplyResult result)
    {
        Equal(BackupState.Complete, result.State, result.Message ?? "application state");
        Assert(result.Backup != null && result.Backup.State == BackupState.Complete, "application completed without a complete backup");
        Equal(1, result.Backup.Items.Count, "backup must contain only the selected item");
        Equal(BackupState.Complete, result.Backup.Items[0].State, "selected backup state");
        Assert(result.Application != null && result.Application.State == BackupState.Complete, "missing completed application result");
        Assert(!String.IsNullOrEmpty(result.LogPath) && File.Exists(result.LogPath), "application log missing");
    }

    private static void NotApplied(ApplyResult result, Fixture fixture, Dictionary<string, string> before)
    {
        Assert(result.State != BackupState.Complete, "invalid operation reported complete");
        if (result.Application != null) Equal(0, result.Application.SucceededFiles, "unexpected applied files");
        EqualSnapshot(before, Snapshot(fixture.Source));
    }

    private static string BackupPath(ApplyResult result, string relative)
    {
        Assert(result.Backup != null && result.Backup.Items.Count == 1, "selected item backup result missing");
        return Path.Combine(result.Backup.Items[0].Destination, relative);
    }

    private static bool ApplyBoundary(ApplyProgress progress)
    {
        return progress != null && progress.Phase == ApplyPhase.Applying && progress.Progress != null && progress.Progress.ProcessedFiles == 0;
    }

    private static void BackupBeforeOverwrite()
    {
        using (Fixture f = new Fixture())
        {
            string old = f.Live("1_LEAD", "data.txt", "old source content");
            f.Live("1_LEAD", "keep.txt", "keep me");
            f.Live("3_BODY", "data.txt", "unselected original");
            Directory.CreateDirectory(Path.Combine(f.Target, "Empty"));
            string input = f.Input("data.txt", "new application content");
            var inputBefore = Snapshot(f.Inbox);
            bool boundarySeen = false, completeCopyExisted = false;
            ApplyResult result = f.Apply(new[] { input }, delegate(ApplyProgress p)
            {
                if (!boundarySeen && ApplyBoundary(p))
                {
                    boundarySeen = true;
                    string parent = Path.Combine(f.Backup, "1_LEAD");
                    string[] dirs = Directory.Exists(parent) ? Directory.GetDirectories(parent) : new string[0];
                    completeCopyExisted = dirs.Length == 1 && !dirs[0].EndsWith(Catalog.IncompleteSuffix, StringComparison.Ordinal)
                        && File.ReadAllText(Path.Combine(dirs[0], "data.txt")) == "old source content"
                        && File.ReadAllText(old) == "old source content";
                }
            });
            Complete(result);
            Assert(boundarySeen && completeCopyExisted, "target mutation began before complete backup was present");
            Equal("new application content", File.ReadAllText(old), "replacement data");
            Equal("old source content", File.ReadAllText(BackupPath(result, "data.txt")), "backup did not preserve original");
            Equal("keep me", File.ReadAllText(Path.Combine(f.Target, "keep.txt")), "unrelated file changed");
            Equal("keep me", File.ReadAllText(BackupPath(result, "keep.txt")), "full item was not backed up");
            Assert(Directory.Exists(BackupPath(result, "Empty")), "empty existing folder was not backed up");
            Equal("unselected original", File.ReadAllText(Path.Combine(f.Source, "3_BODY", "data.txt")), "unselected item changed");
            Assert(!Directory.Exists(Path.Combine(f.Backup, "3_BODY")), "unselected item was backed up");
            EqualSnapshot(inputBefore, Snapshot(f.Inbox));
            Equal(f.Target, result.TargetPath, "reported application target");
        }
    }

    private static void FolderContents()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "root.txt", "old");
            f.Live("1_LEAD", "untouched.txt", "retain");
            string dropped = Path.Combine(f.Inbox, "Config");
            f.Input("Config\\root.txt", "replacement");
            f.Input("Config\\Nested\\camera.ini", "camera=2");
            Directory.CreateDirectory(Path.Combine(dropped, "Empty"));
            string loose = f.Input("loose.txt", "loose");
            ApplyResult result = f.Apply(new[] { dropped, loose }, null);
            Complete(result);
            Equal("replacement", File.ReadAllText(Path.Combine(f.Target, "root.txt")), "dropped root contents");
            Equal("camera=2", File.ReadAllText(Path.Combine(f.Target, "Nested", "camera.ini")), "nested contents");
            Equal("loose", File.ReadAllText(Path.Combine(f.Target, "loose.txt")), "loose input filename");
            Equal("retain", File.ReadAllText(Path.Combine(f.Target, "untouched.txt")), "merge removed existing file");
            Assert(Directory.Exists(Path.Combine(f.Target, "Empty")), "empty input subfolder omitted");
            Assert(!Directory.Exists(Path.Combine(f.Target, "Config")), "top-level dropped folder was incorrectly retained");
        }
    }

    private static void CustomItem()
    {
        using (Fixture f = new Fixture())
        {
            string[] names = { "1_ALPHA", "2_BETA", "3_GAMMA", "4_DELTA", "5_TERRACE", "6_SSIDE" };
            foreach (string item in names) Directory.CreateDirectory(Path.Combine(f.Source, item));
            f.Live("5_TERRACE", "data.txt", "custom old");
            string input = f.Input("data.txt", "custom new");
            ApplyRequest request = f.Request(new[] { input });
            request.Item = "5_TERRACE";
            request.ConfiguredItems = names;
            ApplyResult result = new ApplyEngine().Run(request, null, CancellationToken.None);
            Complete(result);
            Equal("5_TERRACE", result.Application.Item, "custom apply item");
            Equal("5_TERRACE", result.Backup.Items[0].Item, "custom backup item");
            Equal("custom new", File.ReadAllText(Path.Combine(f.Source, "5_TERRACE", "data.txt")), "custom contents");
            Equal("custom old", File.ReadAllText(BackupPath(result, "data.txt")), "custom original backup");
            Assert(Directory.GetFileSystemEntries(f.Target).Length == 0, "default item unexpectedly changed");
        }
    }

    private static void InvalidTargets()
    {
        foreach (string item in new[] { null, "", "../1_LEAD", "1_LEAD,3_BODY", "UNKNOWN" })
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old");
            string input = f.Input("data.txt", "new");
            var before = Snapshot(f.Source);
            ApplyRequest request = f.Request(new[] { input }); request.Item = item;
            NotApplied(new ApplyEngine().Run(request, null, CancellationToken.None), f, before);
            Equal(0, Directory.GetFileSystemEntries(f.Backup).Length, "invalid item created backup");
        }
    }

    private static void MissingInputs()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old");
            var before = Snapshot(f.Source);
            foreach (string[] inputs in new string[][] { null, new string[0], new[] { Path.Combine(f.Inbox, "missing.txt") } })
                NotApplied(f.Apply(inputs, null), f, before);
            Equal(0, Directory.GetFileSystemEntries(f.Backup).Length, "missing inputs created backup");
        }
    }

    private static void EmptyInputFolder()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old");
            var before = Snapshot(f.Source);
            NotApplied(f.Apply(new[] { f.Inbox }, null), f, before);
            Equal(0, Directory.GetFileSystemEntries(f.Backup).Length, "empty input created unnecessary backup");
        }
    }

    private static void DuplicateInputs()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old");
            string a = f.Input("first\\data.txt", "one");
            string b = f.Input("second\\DATA.txt", "two");
            var before = Snapshot(f.Source);
            NotApplied(f.Apply(new[] { a, b }, null), f, before);
            Equal(0, Directory.GetFileSystemEntries(f.Backup).Length, "ambiguous inputs created backup");
        }
    }

    private static void InputTypeConflict()
    {
        using (Fixture f = new Fixture())
        {
            string file = f.Input("file-source\\conflict", "file");
            string folder = Path.Combine(f.Inbox, "folder-source");
            f.Input("folder-source\\conflict\\child.txt", "child");
            var before = Snapshot(f.Source);
            NotApplied(f.Apply(new[] { file, folder }, null), f, before);
        }
    }

    private static void DestinationTypeConflict()
    {
        using (Fixture f = new Fixture())
        {
            Directory.CreateDirectory(Path.Combine(f.Target, "data.txt"));
            string input = f.Input("data.txt", "new");
            var before = Snapshot(f.Source);
            NotApplied(f.Apply(new[] { input }, null), f, before);
        }
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "Nested", "existing file");
            f.Input("payload\\Nested\\child.txt", "new");
            var before = Snapshot(f.Source);
            NotApplied(f.Apply(new[] { Path.Combine(f.Inbox, "payload") }, null), f, before);
        }
    }

    private static void InputOverlap()
    {
        using (Fixture f = new Fixture())
        {
            string sameFile = f.Live("1_LEAD", "data.txt", "old");
            var before = Snapshot(f.Source);
            NotApplied(f.Apply(new[] { sameFile }, null), f, before);
            NotApplied(f.Apply(new[] { f.Target }, null), f, before);
            NotApplied(f.Apply(new[] { f.Source }, null), f, before);
        }
    }

    private static void InputJunction()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old");
            string external = Path.Combine(f.Root, "outside");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "private.txt"), "not an input");
            CreateJunctionOrSkip(Path.Combine(f.Inbox, "linked"), external);
            var before = Snapshot(f.Source);
            NotApplied(f.Apply(new[] { f.Inbox }, null), f, before);
            Equal("not an input", File.ReadAllText(Path.Combine(external, "private.txt")), "junction target changed");
        }
    }

    private static void TargetJunction()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old");
            string external = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "private.txt"), "preserve");
            CreateJunctionOrSkip(Path.Combine(f.Target, "linked"), external);
            string input = f.Input("data.txt", "new");
            var before = Snapshot(f.Source);
            ApplyResult result = f.Apply(new[] { input }, null);
            NotApplied(result, f, before);
            if (result.Backup != null) Assert(result.Backup.State != BackupState.Complete, "backup with excluded junction reported complete");
            Equal("preserve", File.ReadAllText(Path.Combine(external, "private.txt")), "external junction target changed");
        }
    }

    private static void FailedBackup()
    {
        using (Fixture f = new Fixture())
        {
            string target = f.Live("1_LEAD", "data.txt", "old");
            string input = f.Input("data.txt", "new");
            var before = Snapshot(f.Source);
            ApplyResult result;
            using (FileStream locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                result = f.Apply(new[] { input }, null);
            NotApplied(result, f, before);
            if (result.Backup != null) Assert(result.Backup.State != BackupState.Complete, "unreadable source backup completed");
        }
    }

    private static void PreCancelled()
    {
        using (Fixture f = new Fixture())
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            f.Live("1_LEAD", "data.txt", "old"); string input = f.Input("data.txt", "new");
            var before = Snapshot(f.Source); cancellation.Cancel();
            ApplyResult result = new ApplyEngine().Run(f.Request(new[] { input }), null, cancellation.Token);
            Equal(BackupState.Cancelled, result.State, "pre-cancellation state");
            NotApplied(result, f, before);
            Equal(0, Directory.GetFileSystemEntries(f.Backup).Length, "pre-cancelled application created backup");
        }
    }

    private static void UnreadableInput()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old"); string input = f.Input("data.txt", "new");
            var before = Snapshot(f.Source); ApplyResult result;
            using (FileStream locked = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                result = f.Apply(new[] { input }, null);
            NotApplied(result, f, before);
            Assert(result.Backup == null, "backup started before input staging succeeded");
            Equal(0, Directory.GetFileSystemEntries(f.Backup).Length, "unreadable input created a backup");
            Equal("new", File.ReadAllText(input), "unreadable input was changed");
        }
    }

    private static void InsufficientStagingSpace()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old");
            long? available = PathSafety.AvailableBytes(f.Root);
            if (!available.HasValue) throw new SkipException("free space unavailable on fixture filesystem");
            long logicalLength = checked(available.Value + 64L * 1024 * 1024);
            if (logicalLength > 64L * 1024 * 1024 * 1024 * 1024) throw new SkipException("test volume exceeds bounded sparse fixture size");
            string input = Path.Combine(f.Inbox, "oversized-sparse.bin");
            using (FileStream file = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                int returned;
                if (!DeviceIoControl(file.SafeFileHandle, 0x000900C4, null, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw new SkipException("sparse files unsupported: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                Assert((File.GetAttributes(input) & FileAttributes.SparseFile) != 0, "sparse flag missing; refusing logical expansion");
                file.SetLength(logicalLength);
            }
            var before = Snapshot(f.Source);
            ApplyResult result = f.Apply(new[] { input }, null);
            NotApplied(result, f, before);
            Assert(result.Message.IndexOf("공간", StringComparison.Ordinal) >= 0, "space failure explanation absent");
            Assert(result.Backup == null, "backup started despite insufficient staging space");
            Equal(0, Directory.GetFileSystemEntries(f.Backup).Length, "insufficient staging space created backup");
            Equal(logicalLength, new FileInfo(input).Length, "sparse input length altered");
        }
    }

    private static void CancelDuringBackup()
    {
        using (Fixture f = new Fixture())
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            f.Live("1_LEAD", "data.txt", "old"); string input = f.Input("data.txt", "new");
            var before = Snapshot(f.Source); bool sawBackup = false;
            ApplyResult result = new ApplyEngine().Run(f.Request(new[] { input }), delegate(ApplyProgress p)
            {
                if (p.Phase == ApplyPhase.BackingUp) { sawBackup = true; cancellation.Cancel(); }
            }, cancellation.Token);
            Assert(sawBackup, "backup phase callback missing");
            Equal(BackupState.Cancelled, result.State, "backup cancellation state");
            NotApplied(result, f, before);
        }
    }

    private static void ImmutableStaging()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old"); string input = f.Input("data.txt", "first payload");
            bool changed = false;
            ApplyResult result = f.Apply(new[] { input }, delegate(ApplyProgress p)
            {
                if (!changed && p.Phase == ApplyPhase.BackingUp) { changed = true; File.WriteAllText(input, "later payload"); }
            });
            Complete(result); Assert(changed, "input mutation boundary not reached");
            Equal("first payload", File.ReadAllText(Path.Combine(f.Target, "data.txt")), "application reread changing original input");
            Equal("later payload", File.ReadAllText(input), "engine modified dropped input");
        }
    }

    private static void ChangedAfterBackup()
    {
        using (Fixture f = new Fixture())
        {
            string target = f.Live("1_LEAD", "data.txt", "OLD"); string input = f.Input("data.txt", "NEW");
            DateTime timestamp = File.GetLastWriteTimeUtc(target); bool changed = false;
            ApplyResult result = f.Apply(new[] { input }, delegate(ApplyProgress p)
            {
                if (!changed && ApplyBoundary(p)) { changed = true; File.WriteAllText(target, "EVL", new UTF8Encoding(false)); File.SetLastWriteTimeUtc(target, timestamp); }
            });
            Assert(changed, "post-backup boundary missing");
            Assert(result.State != BackupState.Complete, "same-size mutation was overwritten");
            Equal("EVL", File.ReadAllText(target), "new external contents lost");
            Equal("OLD", File.ReadAllText(BackupPath(result, "data.txt")), "original backup changed");
            Equal(0, result.Application.SucceededFiles, "file applied despite changed target");
        }
    }

    private static void CreatedAfterBackup()
    {
        using (Fixture f = new Fixture())
        {
            string input = f.Input("new.txt", "application"); string target = Path.Combine(f.Target, "new.txt"); bool created = false;
            ApplyResult result = f.Apply(new[] { input }, delegate(ApplyProgress p)
            {
                if (!created && ApplyBoundary(p)) { created = true; File.WriteAllText(target, "external new file"); }
            });
            Assert(created && result.State != BackupState.Complete, "unbacked new target was not rejected");
            Equal("external new file", File.ReadAllText(target), "unbacked target overwritten");
            Equal(0, result.Application.SucceededFiles, "unbacked file applied");
        }
    }

    private static void DeletedAfterBackup()
    {
        using (Fixture f = new Fixture())
        {
            string target = f.Live("1_LEAD", "data.txt", "old"); string input = f.Input("data.txt", "new"); bool deleted = false;
            ApplyResult result = f.Apply(new[] { input }, delegate(ApplyProgress p)
            {
                if (!deleted && ApplyBoundary(p)) { deleted = true; File.Delete(target); }
            });
            Assert(deleted && result.State != BackupState.Complete, "deleted backed-up file was recreated");
            Assert(!File.Exists(target), "application restored an externally removed file");
            Equal("old", File.ReadAllText(BackupPath(result, "data.txt")), "backup lost on target deletion");
        }
    }

    private static void JunctionAfterBackup()
    {
        using (Fixture f = new Fixture())
        {
            f.Input("payload\\branch\\new.txt", "application");
            string external = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.txt"), "external original");
            // Verify junction support before the callback so unsupported platforms report a skip.
            string probe = Path.Combine(f.Root, "junction-probe"); CreateJunctionOrSkip(probe, external); Directory.Delete(probe, false);
            bool injected = false;
            ApplyResult result = f.Apply(new[] { Path.Combine(f.Inbox, "payload") }, delegate(ApplyProgress p)
            {
                if (!injected && ApplyBoundary(p)) { injected = true; CreateJunctionOrSkip(Path.Combine(f.Target, "branch"), external); }
            });
            Assert(injected && result.State != BackupState.Complete, "late junction redirect was not blocked");
            Equal(1, Directory.GetFiles(external).Length, "late junction received an applied file");
            Equal("external original", File.ReadAllText(Path.Combine(external, "keep.txt")), "external file changed");
        }
    }

    private static void LockedAfterBackup()
    {
        using (Fixture f = new Fixture())
        {
            string target = f.Live("1_LEAD", "data.txt", "old"); string input = f.Input("data.txt", "new");
            FileStream held = null; ApplyResult result;
            try
            {
                result = f.Apply(new[] { input }, delegate(ApplyProgress p)
                {
                    if (held == null && ApplyBoundary(p)) held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                });
            }
            finally { if (held != null) held.Dispose(); }
            Assert(result.State != BackupState.Complete, "locked replacement unexpectedly completed");
            Equal("old", File.ReadAllText(target), "failed replacement destroyed original");
            Equal("old", File.ReadAllText(BackupPath(result, "data.txt")), "backup missing after failed replacement");
            Equal(0, result.Application.SucceededFiles, "locked file counted as applied");
            Assert(!Directory.GetFiles(f.Target).Any(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)), "temporary target file leaked");
        }
    }

    private static void CancelAfterFirstFile()
    {
        using (Fixture f = new Fixture())
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            string[] inputs = ThreeFiles(f);
            ApplyResult result = new ApplyEngine().Run(f.Request(inputs), delegate(ApplyProgress p)
            {
                if (p.Phase == ApplyPhase.Applying && p.Progress != null && p.Progress.SucceededFiles == 1) cancellation.Cancel();
            }, cancellation.Token);
            Equal(BackupState.Cancelled, result.State, "mid-apply cancellation state");
            Equal(1, result.Application.SucceededFiles, "cancellation did not stop after first application");
            Equal(1, new[] { "a.txt", "b.txt", "c.txt" }.Count(name => File.ReadAllText(Path.Combine(f.Target, name)) == "new " + name), "unexpected applied file count");
            Equal(3, Directory.GetFiles(f.Target).Length, "cancelled application leaked temporary files");
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" }) Equal("old " + name, File.ReadAllText(BackupPath(result, name)), "cancelled run backup");
        }
    }

    private static void FailureAfterFirstFile()
    {
        using (Fixture f = new Fixture())
        {
            string[] inputs = ThreeFiles(f); FileStream held = null; ApplyResult result;
            try
            {
                result = f.Apply(inputs, delegate(ApplyProgress p)
                {
                    if (held == null && p.Phase == ApplyPhase.Applying && p.Progress != null && p.Progress.SucceededFiles == 1)
                        held = new FileStream(Path.Combine(f.Target, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
                });
            }
            finally { if (held != null) held.Dispose(); }
            Assert(result.State != BackupState.Complete, "replacement failure reported complete");
            Equal(2, result.Application.ProcessedFiles, "successful and failed attempts were not both counted");
            Equal(1, result.Application.SucceededFiles, "engine continued applying after first replacement failure");
            Equal(1, result.Application.FailedFiles, "failed replacement attempt count");
            Equal("new a.txt", File.ReadAllText(Path.Combine(f.Target, "a.txt")), "completed first file disappeared");
            Equal("old b.txt", File.ReadAllText(Path.Combine(f.Target, "b.txt")), "failed replacement destroyed second file");
            Equal("old c.txt", File.ReadAllText(Path.Combine(f.Target, "c.txt")), "third file applied after failure");
            Equal(3, Directory.GetFiles(f.Target).Length, "failed application leaked temporary files");
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" }) Equal("old " + name, File.ReadAllText(BackupPath(result, name)), "failed run backup");
        }
    }

    private static void ProgressFailureAfterSuccess()
    {
        using (Fixture f = new Fixture())
        {
            string[] inputs = ThreeFiles(f);
            bool injected = false;
            ApplyResult result = f.Apply(inputs, delegate(ApplyProgress p)
            {
                if (!injected && p.Phase == ApplyPhase.Applying && p.Progress != null && p.Progress.SucceededFiles == 1)
                {
                    injected = true;
                    throw new InvalidOperationException("Injected post-success progress failure.");
                }
            });
            Assert(injected, "post-success progress checkpoint was not reached");
            Equal(BackupState.Partial, result.State, "post-success reporting failure must report partial application");
            Equal(1, result.Application.ProcessedFiles, "successful replacement was processed twice");
            Equal(1, result.Application.SucceededFiles, "successful replacement count lost");
            Equal(0, result.Application.FailedFiles, "successful replacement was also counted as failed");
            Equal("new a.txt", File.ReadAllText(Path.Combine(f.Target, "a.txt")), "completed replacement was not preserved");
            Equal("old b.txt", File.ReadAllText(Path.Combine(f.Target, "b.txt")), "second file applied after progress failure");
            Equal("old c.txt", File.ReadAllText(Path.Combine(f.Target, "c.txt")), "third file applied after progress failure");
            Equal(3, Directory.GetFiles(f.Target).Length, "progress failure leaked a temporary file");
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
                Equal("old " + name, File.ReadAllText(BackupPath(result, name)), "reporting failure changed preserved backup");
        }
    }

    private static string[] ThreeFiles(Fixture f)
    {
        string[] names = { "a.txt", "b.txt", "c.txt" };
        foreach (string name in names) f.Live("1_LEAD", name, "old " + name);
        return names.Select(name => f.Input(name, "new " + name)).ToArray();
    }

    private static void UnsafeLogDirectory()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "data.txt", "old"); string input = f.Input("data.txt", "new"); var before = Snapshot(f.Source);
            ApplyRequest request = f.Request(new[] { input }); request.LogDirectory = Path.Combine(f.Target, "logs");
            NotApplied(new ApplyEngine().Run(request, null, CancellationToken.None), f, before);
        }
    }

    private static void LongDestination()
    {
        using (Fixture f = new Fixture())
        {
            f.Live("1_LEAD", "old.txt", "old");
            int nameLength = 248 - f.Target.Length - 1;
            Assert(nameLength > 10 && nameLength < 255, "fixture path unsuitable for length boundary");
            string input = f.Input(new string('x', nameLength - 4) + ".txt", "too long at destination");
            var before = Snapshot(f.Source);
            NotApplied(f.Apply(new[] { input }, null), f, before);
        }
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SnapshotDirectory(root, root, result);
        return result;
    }

    private static void SnapshotDirectory(string root, string directory, Dictionary<string, string> result)
    {
        foreach (string path in Directory.GetFileSystemEntries(directory))
        {
            string relative = path.Substring(root.Length + 1);
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0) result.Add(relative, "link|" + attrs);
            else if ((attrs & FileAttributes.Directory) != 0)
            {
                result.Add(relative, "directory|" + attrs); SnapshotDirectory(root, path, result);
            }
            else
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                    result.Add(relative, Convert.ToBase64String(sha.ComputeHash(stream)) + "|" + attrs + "|" + File.GetLastWriteTimeUtc(path).Ticks);
            }
        }
    }

    private static void EqualSnapshot(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        Equal(expected.Count, actual.Count, "snapshot entry count");
        foreach (var pair in expected)
        {
            string value; Assert(actual.TryGetValue(pair.Key, out value), "snapshot entry missing: " + pair.Key);
            Equal(pair.Value, value, "snapshot changed: " + pair.Key);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root, Source, Backup, Inbox, Logs, Target;
        public Fixture()
        {
            Root = Path.Combine(workRoot, Guid.NewGuid().ToString("N").Substring(0, 10));
            Source = Path.Combine(Root, "source"); Backup = Path.Combine(Root, "backup");
            Inbox = Path.Combine(Root, "in"); Logs = Path.Combine(Root, "logs"); Target = Path.Combine(Source, "1_LEAD");
            Directory.CreateDirectory(Source); Directory.CreateDirectory(Backup); Directory.CreateDirectory(Inbox);
            foreach (string item in Catalog.Items) Directory.CreateDirectory(Path.Combine(Source, item));
        }
        public string Live(string item, string relative, string content) { return Write(Path.Combine(Source, item, relative), content); }
        public string Input(string relative, string content) { return Write(Path.Combine(Inbox, relative), content); }
        private static string Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, content, new UTF8Encoding(false)); return path;
        }
        public ApplyRequest Request(string[] inputs)
        {
            return new ApplyRequest { SourceRoot = Source, BackupRoot = Backup, Name = "적용전", Item = "1_LEAD",
                ConfiguredItems = (string[])Catalog.Items.Clone(), InputPaths = inputs, StartedAt = StartedAt, LogDirectory = Logs };
        }
        public ApplyResult Apply(string[] inputs, Action<ApplyProgress> progress)
        {
            return new ApplyEngine().Run(Request(inputs), progress, CancellationToken.None);
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(workRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup target");
            if (Directory.Exists(resolved)) RemoveDirectory(resolved);
        }
    }

    private static void RemoveDirectory(string directory)
    {
        FileAttributes attrs = File.GetAttributes(directory);
        if ((attrs & FileAttributes.ReparsePoint) != 0) { Directory.Delete(directory, false); return; }
        foreach (string path in Directory.GetFileSystemEntries(directory))
        {
            FileAttributes child = File.GetAttributes(path);
            if ((child & FileAttributes.Directory) != 0) RemoveDirectory(path);
            else { if ((child & FileAttributes.ReparsePoint) == 0) File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
        File.SetAttributes(directory, FileAttributes.Normal); Directory.Delete(directory, false);
    }

    private static void CreateJunctionOrSkip(string junction, string target)
    {
        Directory.CreateDirectory(junction);
        string substitute = @"\??\" + Path.GetFullPath(target), print = Path.GetFullPath(target);
        byte[] sub = Encoding.Unicode.GetBytes(substitute), display = Encoding.Unicode.GetBytes(print);
        byte[] buffer = new byte[16 + sub.Length + 2 + display.Length + 2];
        using (MemoryStream stream = new MemoryStream(buffer))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(unchecked((uint)0xA0000003)); writer.Write((ushort)(buffer.Length - 8)); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)sub.Length); writer.Write((ushort)(sub.Length + 2)); writer.Write((ushort)display.Length);
            writer.Write(sub); writer.Write((ushort)0); writer.Write(display); writer.Write((ushort)0);
        }
        using (SafeFileHandle handle = CreateFile(junction, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            if (handle.IsInvalid) throw new SkipException("junction handle unavailable: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            int returned;
            if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                throw new SkipException("junction unavailable: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }

    private sealed class SkipException : Exception { public SkipException(string message) : base(message) { } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
