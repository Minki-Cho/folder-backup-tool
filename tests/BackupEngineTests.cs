using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Win32.SafeHandles;
using DdalggakBackup;

internal static class BackupEngineTests
{
    private static string workRoot;
    private static int passed;
    private static int failed;
    private static int skipped;
    private static readonly DateTime Start = new DateTime(2026, 9, 12, 23, 59, 58);
    private const string Label = "설정 변경전";
    private const string BaseName = "260912_설정 변경전";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        workRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work"));
        Directory.CreateDirectory(workRoot);
        Run("names and fixed start date", Names);
        Run("blank names use Korean fallback and share per-item duplicate numbering", DefaultNames);
        Run("selected layout, empty folders, hidden files, immutable source", Layout);
        Run("zero-file item completes", EmptyItem);
        Run("unselected missing items do not block", UnselectedMissing);
        Run("duplicate names and incomplete reservations", Duplicates);
        Run("finalization collision preserves existing backup", FinalizationCollision);
        Run("simultaneous runs reserve different directories", ConcurrentRuns);
        Run("snapshot excludes files added after preparation", SnapshotBoundary);
        Run("source changing during copy retries once successfully", SourceChangesOnce);
        Run("source changing during both attempts remains partial", SourceChangesTwice);
        Run("locked file is partial and remaining files copy", LockedFile);
        Run("unreadable nested directory is reported as partial", UnreadableDirectory);
        Run("destination disappearance aborts remaining items", DisappearingDestination);
        Run("cancellation preserves completed and pending item states", CancelBetweenItems);
        Run("cancellation retains incomplete result", CancelDuringItem);
        Run("pre-cancellation creates no backup", PreCancelled);
        Run("validation rejects invalid requests before backup writes", InvalidRequests);
        Run("overlap with an unselected source item is blocked", SourceOverlap);
        Run("sibling backup branch is allowed", SiblingBranch);
        Run("log creation failure blocks backup writes", LogFailure);
        Run("overlong destination is rejected before copying", LongDestination);
        Run("nested junction is excluded and not followed", NestedJunction);
        Run("selected source junction is blocked", SelectedJunction);
        Run("destination junction into source is blocked", DestinationJunction);
        Run("source with denied write permissions remains readable", DeniedSourceWrites);
        Run("unwritable destination blocks before writes", DeniedDestinationWrites);
        Run("sparse oversized source fails free-space preflight", InsufficientSpace);
        Run("profile settings roundtrip and corrupt configuration", Settings);
        Run("custom item names copy selected folders and number duplicates", CustomItemCopies);
        Run("custom names persist independently per equipment profile", CustomItemSettings);
        Run("legacy settings acquire item defaults without losing saved values", LegacyItemSettings);
        Run("invalid configured item names are rejected before writes", InvalidItemNames);
        Run("unselected custom and legacy source names remain protected", CustomItemOverlap);
        Run("destination junction into an unselected custom item is blocked", CustomItemJunction);
        Run("log location inside an unselected custom item is blocked", CustomItemLogProtection);
        Run("settings saves protect unselected profile sources and junction aliases", SettingsSaveSourceProtection);
        Console.WriteLine("\n{0} passed; {1} failed; {2} skipped.", passed, failed, skipped);
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (SkipException ex) { skipped++; Console.WriteLine("SKIP " + name + ": " + ex.Message); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        Assert(EqualityComparer<T>.Default.Equals(expected, actual), message + ": expected " + expected + ", got " + actual);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (PathTooLongException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Names()
    {
        Equal(BaseName, PathSafety.FolderName("  " + Label + "  ", Start), "trim name and retain clicked date");
        Equal("261231_abc_123", PathSafety.FolderName("abc_123", new DateTime(2026, 12, 31)), "date formatting");
        foreach (string blank in new[] { null, "", "  ", " \t\r\n\u3000 " })
        {
            Equal("과검제어전", PathSafety.ValidateName(blank), "blank suffix uses Korean fallback");
            Equal("260912_과검제어전", PathSafety.FolderName(blank, Start), "blank name retains date prefix");
        }
        Equal("default", PathSafety.ValidateName("default"), "explicit English name is preserved");
        Equal("260912_default", PathSafety.FolderName("default", Start), "explicit English name is not remapped");
        string[] invalid = { ".", "..", "bad/part", "bad\\part", "bad:name", "bad?name", "bad*name", "bad\"name", "bad|name", "bad<name", "bad>name", "bad\nname", "bad." };
        foreach (string value in invalid)
        {
            string candidate = value;
            Throws(delegate { PathSafety.ValidateName(candidate); }, "invalid name accepted: " + candidate);
        }
        Assert(PathSafety.IsWithin(@"C:\fixture\1_LEAD\child", @"c:\fixture\1_lead"), "case insensitive subtree match");
        Assert(!PathSafety.IsWithin(@"C:\fixture\1_LEAD_extra", @"C:\fixture\1_LEAD"), "separator boundary required");
        Throws(delegate { PathSafety.Normalize("relative\\path"); }, "relative path accepted");
        Throws(delegate { PathSafety.Normalize(@"\\?\C:\fixture"); }, "device path accepted");
    }

    private static void DefaultNames()
    {
        using (Fixture f = new Fixture())
        {
            string[] names = { "", null, " \t\r\n\u3000 ", "과검제어전" };
            int bodyBackups = 0;
            for (int run = 0; run < names.Length; run++)
            {
                string payload = "source version " + run;
                f.Write("1_LEAD", "data.txt", payload);
                f.Write("3_BODY", "body.txt", payload);
                Dictionary<string, string> sourceBefore = Snapshot(f.Source);
                Dictionary<string, string> backupBefore = Snapshot(f.BackupRoot);
                // Skip BODY once so collisions must be numbered independently for each item.
                bool selectBody = run != 1;
                BackupRequest request = f.Request(selectBody ? new[] { "1_LEAD", "3_BODY" } : new[] { "1_LEAD" });
                request.Name = names[run];
                BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
                Equal(BackupState.Complete, result.State, "default-name run " + run);
                string leadSuffix = run == 0 ? "" : "_" + run.ToString("D2");
                ItemResult lead = Item(result, "1_LEAD");
                Equal(Path.Combine(f.BackupRoot, "1_LEAD", "260912_과검제어전" + leadSuffix), lead.Destination, "blank and explicit Korean fallback share numbering");
                Equal(payload, File.ReadAllText(Path.Combine(lead.Destination, "data.txt")), "current default backup content");
                if (selectBody)
                {
                    string bodySuffix = bodyBackups == 0 ? "" : "_" + bodyBackups.ToString("D2");
                    ItemResult body = Item(result, "3_BODY");
                    Equal(Path.Combine(f.BackupRoot, "3_BODY", "260912_과검제어전" + bodySuffix), body.Destination, "fallback numbering is per item");
                    Equal(payload, File.ReadAllText(Path.Combine(body.Destination, "body.txt")), "body default backup content");
                    bodyBackups++;
                }
                AssertSnapshotRetained(backupBefore, Snapshot(f.BackupRoot));
                EqualSnapshots(sourceBefore, Snapshot(f.Source));
            }
            Equal(4, Directory.GetDirectories(Path.Combine(f.BackupRoot, "1_LEAD")).Length, "default lead backup count");
            Equal(3, Directory.GetDirectories(Path.Combine(f.BackupRoot, "3_BODY")).Length, "default body backup count");
            Equal("source version 0", File.ReadAllText(Path.Combine(f.BackupRoot, "1_LEAD", "260912_과검제어전", "data.txt")), "first blank-name backup was overwritten");
            Equal("source version 1", File.ReadAllText(Path.Combine(f.BackupRoot, "1_LEAD", "260912_과검제어전_01", "data.txt")), "null-name backup was overwritten");
            Equal("source version 2", File.ReadAllText(Path.Combine(f.BackupRoot, "1_LEAD", "260912_과검제어전_02", "data.txt")), "whitespace-name backup was overwritten");
            Equal("source version 3", File.ReadAllText(Path.Combine(f.BackupRoot, "1_LEAD", "260912_과검제어전_03", "data.txt")), "explicit Korean fallback backup content");
            Dictionary<string, string> priorBackups = Snapshot(f.BackupRoot);
            BackupRequest explicitEnglish = f.Request(new[] { "1_LEAD" });
            explicitEnglish.Name = "default";
            BackupResult englishResult = new BackupEngine().Run(explicitEnglish, null, CancellationToken.None);
            Equal(BackupState.Complete, englishResult.State, "explicit English name backup");
            string englishPath = Path.Combine(f.BackupRoot, "1_LEAD", "260912_default");
            Equal(englishPath, Item(englishResult, "1_LEAD").Destination, "explicit default must remain an independent name");
            Equal("source version 3", File.ReadAllText(Path.Combine(englishPath, "data.txt")), "explicit English name copy content");
            Equal(5, Directory.GetDirectories(Path.Combine(f.BackupRoot, "1_LEAD")).Length, "explicit English name adds an independent backup");
            AssertSnapshotRetained(priorBackups, Snapshot(f.BackupRoot));
        }
    }

    private static void Layout()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "TOP.json", "{\"한글\":true}");
            f.Write("1_LEAD", "Config\\camera.ini", "mode=42");
            f.Write("1_LEAD", "hidden.dat", "hidden-content");
            string hidden = Path.Combine(f.Source, "1_LEAD", "hidden.dat");
            File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.ReadOnly);
            string top = Path.Combine(f.Source, "1_LEAD", "TOP.json");
            DateTime modified = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(top, modified);
            Directory.CreateDirectory(Path.Combine(f.Source, "1_LEAD", "EmptyFolder"));
            f.Write("3_BODY", "piece.txt", "body-data");
            f.Write("2_CORNER", "should-not-copy.txt", "unselected");
            File.WriteAllText(Path.Combine(f.Source, "root-only.txt"), "not an item");
            Dictionary<string, string> before = Snapshot(f.Source);
            List<BackupProgress> progress = new List<BackupProgress>();
            BackupResult result = f.Backup(new[] { "1_LEAD", "3_BODY" }, delegate(BackupProgress p) { progress.Add(p); });
            Equal(BackupState.Complete, result.State, "overall result");
            Equal(2, result.Items.Count, "selected item count");
            ItemResult lead = Item(result, "1_LEAD");
            ItemResult body = Item(result, "3_BODY");
            Equal(Path.Combine(f.BackupRoot, "1_LEAD", BaseName), lead.Destination, "layout");
            Equal(Path.Combine(f.BackupRoot, "3_BODY", BaseName), body.Destination, "same date and name");
            Assert(!Directory.Exists(Path.Combine(lead.Destination, "1_LEAD")), "item folder duplicated");
            Assert(Directory.Exists(Path.Combine(lead.Destination, "EmptyFolder")), "empty folder missing");
            Equal(File.ReadAllText(top), File.ReadAllText(Path.Combine(lead.Destination, "TOP.json")), "content");
            Equal("mode=42", File.ReadAllText(Path.Combine(lead.Destination, "Config", "camera.ini")), "nested file");
            Equal("hidden-content", File.ReadAllText(Path.Combine(lead.Destination, "hidden.dat")), "hidden file");
            Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(lead.Destination, "TOP.json")), "last write time");
            Equal(3, lead.SucceededFiles, "lead count");
            Equal(1, body.SucceededFiles, "body count");
            Equal(0, lead.FailedFiles + body.FailedFiles, "failure count");
            Equal(2, Directory.GetDirectories(f.BackupRoot).Length, "only selected parent folders created");
            Assert(!File.Exists(Path.Combine(lead.Destination, "root-only.txt")), "root file copied");
            EqualSnapshots(before, Snapshot(f.Source));
            CheckLog(result, "success", "TOP.json");
            int previous = 0;
            bool totalSeen = false;
            foreach (BackupProgress p in progress)
            {
                Assert(p.ProcessedFiles >= previous, "global processed count moved backwards");
                Assert(p.ProcessedFiles <= p.TotalFiles, "processed count exceeds total");
                previous = p.ProcessedFiles;
                if (p.TotalFiles == 4) totalSeen = true;
            }
            Assert(totalSeen, "global total never exposed");
        }
    }

    private static void EmptyItem()
    {
        using (Fixture f = new Fixture())
        {
            BackupResult result = f.Backup(new[] { "6_SHSD" });
            Equal(BackupState.Complete, result.State, "empty overall state");
            ItemResult item = Item(result, "6_SHSD");
            Equal(0, item.TotalFiles, "empty count");
            Equal(BackupState.Complete, item.State, "empty item state");
            Assert(Directory.Exists(item.Destination), "empty dated directory missing");
            Equal(0, Directory.GetFileSystemEntries(item.Destination).Length, "empty directory contains artifacts");
        }
    }

    private static void UnselectedMissing()
    {
        using (Fixture f = new Fixture())
        {
            Directory.Delete(Path.Combine(f.Source, "2_CORNER"));
            f.Write("1_LEAD", "keep.txt", "yes");
            Equal(BackupState.Complete, f.Backup(new[] { "1_LEAD" }).State, "unselected missing item should be ignored");
        }
    }

    private static void Duplicates()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "new.txt", "new");
            string parent = Path.Combine(f.BackupRoot, "1_LEAD");
            string previous = Path.Combine(parent, BaseName);
            Directory.CreateDirectory(previous);
            File.WriteAllText(Path.Combine(previous, "sentinel.txt"), "past backup");
            string incomplete = Path.Combine(parent, BaseName + "_01" + Catalog.IncompleteSuffix);
            Directory.CreateDirectory(incomplete);
            File.WriteAllText(Path.Combine(incomplete, "sentinel.txt"), "interrupted backup");
            Dictionary<string, string> before = Snapshot(f.BackupRoot);
            BackupResult result = f.Backup(new[] { "1_LEAD", "3_BODY" });
            Equal(BackupState.Complete, result.State, "duplicate run");
            Equal(Path.Combine(parent, BaseName + "_02"), Item(result, "1_LEAD").Destination, "skip final and incomplete candidates");
            Equal(Path.Combine(f.BackupRoot, "3_BODY", BaseName), Item(result, "3_BODY").Destination, "numbering is per item");
            AssertSnapshotRetained(before, Snapshot(f.BackupRoot));
            BackupResult again = f.Backup(new[] { "1_LEAD" });
            Equal(Path.Combine(parent, BaseName + "_03"), Item(again, "1_LEAD").Destination, "repeat next number");
        }
    }

    private static void FinalizationCollision()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "new.txt", "fresh");
            bool collision = false;
            string occupied = Path.Combine(f.BackupRoot, "1_LEAD", BaseName);
            BackupResult result = f.Backup(new[] { "1_LEAD" }, delegate(BackupProgress p)
            {
                if (collision || p.State != BackupState.Copying) return;
                collision = true;
                Directory.CreateDirectory(occupied);
                File.WriteAllText(Path.Combine(occupied, "sentinel.txt"), "racing writer");
            });
            Assert(collision, "copying callback not observed");
            Equal(BackupState.Complete, result.State, "rename collision should recover");
            Equal(Path.Combine(f.BackupRoot, "1_LEAD", BaseName + "_01"), Item(result, "1_LEAD").Destination, "new candidate at finalization");
            Equal("racing writer", File.ReadAllText(Path.Combine(occupied, "sentinel.txt")), "existing folder overwritten");
            Assert(!File.Exists(Path.Combine(occupied, "new.txt")), "merged into racing directory");
        }
    }

    private static void ConcurrentRuns()
    {
        using (Fixture f = new Fixture())
        using (ManualResetEvent start = new ManualResetEvent(false))
        {
            f.Write("1_LEAD", "file.txt", "same source");
            BackupResult[] results = new BackupResult[2];
            Exception[] errors = new Exception[2];
            Thread[] threads = new Thread[2];
            for (int i = 0; i < threads.Length; i++)
            {
                int index = i;
                threads[i] = new Thread(delegate()
                {
                    try { start.WaitOne(); results[index] = f.Backup(new[] { "1_LEAD" }); }
                    catch (Exception ex) { errors[index] = ex; }
                });
                threads[i].IsBackground = true;
                threads[i].Start();
            }
            start.Set();
            foreach (Thread thread in threads) Assert(thread.Join(30000), "concurrent run timed out");
            for (int i = 0; i < results.Length; i++)
            {
                Assert(errors[i] == null, "concurrent exception: " + errors[i]);
                Equal(BackupState.Complete, results[i].State, "concurrent result " + i + ": " + results[i].Message);
                Equal("same source", File.ReadAllText(Path.Combine(Item(results[i], "1_LEAD").Destination, "file.txt")), "concurrent content");
                CheckLog(results[i], "success", "file.txt");
            }
            Assert(!String.Equals(Item(results[0], "1_LEAD").Destination, Item(results[1], "1_LEAD").Destination, StringComparison.OrdinalIgnoreCase), "concurrent runs reused a destination");
            Assert(!String.Equals(results[0].LogPath, results[1].LogPath, StringComparison.OrdinalIgnoreCase), "concurrent runs reused a log");
        }
    }

    private static void SnapshotBoundary()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "original.txt", "prepared");
            bool added = false;
            BackupResult result = f.Backup(new[] { "1_LEAD" }, delegate(BackupProgress p)
            {
                if (added || p.State != BackupState.Copying) return;
                added = true;
                f.Write("1_LEAD", "late.txt", "next time");
            });
            Equal(BackupState.Complete, result.State, "snapshot result");
            Assert(added, "copying callback not observed");
            Equal(1, Item(result, "1_LEAD").TotalFiles, "late file entered frozen snapshot");
            Assert(!File.Exists(Path.Combine(Item(result, "1_LEAD").Destination, "late.txt")), "late file copied");
        }
    }

    private static void LockedFile()
    {
        using (Fixture f = new Fixture())
        {
            string locked = f.Write("1_LEAD", "a_locked.txt", "locked source");
            f.Write("1_LEAD", "z_good.txt", "copy me");
            Dictionary<string, string> before = Snapshot(f.Source);
            BackupResult result;
            using (FileStream hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                result = f.Backup(new[] { "1_LEAD" });
            Equal(BackupState.Partial, result.State, "locked file overall state");
            ItemResult item = Item(result, "1_LEAD");
            Equal(BackupState.Partial, item.State, "locked item state");
            Equal(1, item.FailedFiles, "locked file failure count");
            Equal(1, item.SucceededFiles, "remaining readable file copied");
            Equal(2, item.ProcessedFiles, "all file attempts counted");
            Assert(item.Destination.EndsWith(Catalog.IncompleteSuffix, StringComparison.Ordinal), "partial folder lacks marker");
            Equal("copy me", File.ReadAllText(Path.Combine(item.Destination, "z_good.txt")), "good file missing");
            Assert(!File.Exists(Path.Combine(item.Destination, "a_locked.txt")), "failed copy was finalized");
            Equal(1, Directory.GetFiles(item.Destination, "*", SearchOption.AllDirectories).Length, "temporary artifact remains after handled error");
            EqualSnapshots(before, Snapshot(f.Source));
            CheckLog(result, "failure", "a_locked.txt");
        }
    }

    private static void SourceChangesOnce() { SourceChanges(1); }
    private static void SourceChangesTwice() { SourceChanges(2); }

    private static void SourceChanges(int mutationLimit)
    {
        using (Fixture f = new Fixture())
        {
            string source = Path.Combine(f.Source, "1_LEAD", "changing.bin");
            const long length = 128L * 1024 * 1024;
            using (FileStream file = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                file.SetLength(length);
                file.Flush(true);
            }
            DateTime originalTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(source, originalTime);
            object gate = new object();
            int mutations = 0;
            Exception mutationError = null;
            bool active = true;
            using (FileSystemWatcher watcher = new FileSystemWatcher(f.BackupRoot, ".ddalggak-*.tmp"))
            {
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName;
                watcher.Created += delegate(object sender, FileSystemEventArgs e)
                {
                    lock (gate)
                    {
                        if (!active || mutations >= mutationLimit) return;
                        try
                        {
                            if (!File.Exists(e.FullPath)) throw new IOException("copy finished before mutation observer ran");
                            // The engine reads source metadata before creating this temporary output file.
                            File.SetLastWriteTimeUtc(source, originalTime.AddHours(mutations + 1));
                            mutations++;
                        }
                        catch (Exception ex) { mutationError = ex; }
                    }
                };
                watcher.EnableRaisingEvents = true;
                BackupResult result = f.Backup(new[] { "1_LEAD" });
                lock (gate) { active = false; }
                watcher.EnableRaisingEvents = false;
                Assert(mutationError == null, "source mutation observer failed: " + mutationError);
                Equal(mutationLimit, mutations, "required source modifications were not observed");
                CheckLog(result, "info", "재시도");
                Equal(length, new FileInfo(source).Length, "source length changed");
                ItemResult item = Item(result, "1_LEAD");
                if (mutationLimit == 1)
                {
                    Equal(BackupState.Complete, result.State, "stable second attempt should complete");
                    Equal(1, item.SucceededFiles, "retry counted as multiple copied files");
                    Equal(1, item.ProcessedFiles, "retry counted as multiple processed files");
                    Equal(length, new FileInfo(Path.Combine(item.Destination, "changing.bin")).Length, "retry output length");
                    Equal(originalTime.AddHours(1), File.GetLastWriteTimeUtc(Path.Combine(item.Destination, "changing.bin")), "retry output timestamp");
                }
                else
                {
                    Equal(BackupState.Partial, result.State, "second source change should fail file");
                    Equal(1, item.FailedFiles, "unstable file failure count");
                    Equal(1, item.ProcessedFiles, "unstable file processed count");
                    Equal(0, item.SucceededFiles, "unstable file marked copied");
                    Assert(item.Destination.EndsWith(Catalog.IncompleteSuffix, StringComparison.Ordinal), "unstable copy lacks marker");
                    Equal(0, Directory.GetFiles(item.Destination).Length, "failed retry left temporary or finalized file");
                    CheckLog(result, "failure", "changing.bin");
                }
            }
        }
    }

    private static void CancelBetweenItems()
    {
        using (Fixture f = new Fixture())
        using (CancellationTokenSource cancel = new CancellationTokenSource())
        {
            f.Write("1_LEAD", "complete.txt", "preserved");
            f.Write("2_CORNER", "later.txt", "pending");
            BackupResult result = new BackupEngine().Run(f.Request(new[] { "1_LEAD", "2_CORNER" }), delegate(BackupProgress p)
            {
                if (p.State == BackupState.Complete && p.Item == "1_LEAD") cancel.Cancel();
            }, cancel.Token);
            Assert(cancel.IsCancellationRequested, "item completion callback missing");
            Equal(BackupState.Cancelled, result.State, "overall cancellation");
            ItemResult first = Item(result, "1_LEAD");
            Equal(BackupState.Complete, first.State, "completed item was downgraded");
            Equal(BackupState.Pending, Item(result, "2_CORNER").State, "unstarted item must stay pending");
            Assert(!first.Destination.EndsWith(Catalog.IncompleteSuffix, StringComparison.Ordinal), "completed item reverted marker");
            Equal("preserved", File.ReadAllText(Path.Combine(first.Destination, "complete.txt")), "completed copy removed");
            Assert(!Directory.Exists(Path.Combine(f.BackupRoot, "2_CORNER")), "unstarted parent created");
        }
    }

    private static void UnreadableDirectory()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "readable.txt", "ordinary");
            f.Write("1_LEAD", "private\\locked.txt", "private source");
            string directory = Path.Combine(f.Source, "1_LEAD", "private");
            Dictionary<string, string> before = Snapshot(f.Source);
            byte[] original = Directory.GetAccessControl(directory).GetSecurityDescriptorBinaryForm();
            DirectorySecurity modified = Directory.GetAccessControl(directory);
            FileSystemAccessRule rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ListDirectory, AccessControlType.Deny);
            bool applied = false;
            try
            {
                try { modified.AddAccessRule(rule); Directory.SetAccessControl(directory, modified); applied = true; }
                catch (UnauthorizedAccessException ex) { throw new SkipException("fixture ACL not supported: " + ex.Message); }
                catch (PlatformNotSupportedException ex) { throw new SkipException("fixture ACL not supported: " + ex.Message); }
                BackupResult result = f.Backup(new[] { "1_LEAD" });
                Equal(BackupState.Partial, result.State, "unreadable directory overall state");
                ItemResult item = Item(result, "1_LEAD");
                Equal(BackupState.Partial, item.State, "unreadable directory item state");
                Assert(item.Destination.EndsWith(Catalog.IncompleteSuffix, StringComparison.Ordinal), "unreadable directory missing incomplete marker");
                Equal("ordinary", File.ReadAllText(Path.Combine(item.Destination, "readable.txt")), "readable sibling not copied");
                Assert(!File.Exists(Path.Combine(item.Destination, "private", "locked.txt")), "unreadable entry copied unexpectedly");
                CheckLog(result, "failure", "private");
            }
            finally { if (applied) RestoreDirectoryAcl(directory, original); }
            EqualSnapshots(before, Snapshot(f.Source));
        }
    }

    private static void DisappearingDestination()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "copy.txt", "first source");
            f.Write("3_BODY", "pending.txt", "later source");
            Dictionary<string, string> before = Snapshot(f.Source);
            string incomplete = Path.Combine(f.BackupRoot, "1_LEAD", BaseName + Catalog.IncompleteSuffix);
            bool removed = false;
            BackupResult result = f.Backup(new[] { "1_LEAD", "3_BODY" }, delegate(BackupProgress p)
            {
                if (removed || p.State != BackupState.Copying || p.Item != "1_LEAD") return;
                // This is the empty, newly allocated test-fixture directory, before file copying starts.
                Directory.Delete(incomplete, false);
                removed = true;
            });
            Assert(removed, "copying boundary not observed");
            Equal(BackupState.Failed, result.State, "lost destination must abort run");
            Equal(BackupState.Failed, Item(result, "1_LEAD").State, "active item fatal state");
            Equal(BackupState.Pending, Item(result, "3_BODY").State, "unstarted item remains pending");
            Assert(!Directory.Exists(Path.Combine(f.BackupRoot, "3_BODY")), "later item started after fatal error");
            EqualSnapshots(before, Snapshot(f.Source));
            CheckLog(result, "failure", "1_LEAD");
        }
    }

    private static void CancelDuringItem()
    {
        using (Fixture f = new Fixture())
        using (CancellationTokenSource cancel = new CancellationTokenSource())
        {
            f.Write("1_LEAD", "first.txt", "first");
            f.Write("1_LEAD", "second.txt", "second");
            bool triggered = false;
            BackupResult result = new BackupEngine().Run(f.Request(new[] { "1_LEAD", "3_BODY" }), delegate(BackupProgress p)
            {
                if (!triggered && p.State == BackupState.Copying)
                {
                    triggered = true;
                    cancel.Cancel();
                }
            }, cancel.Token);
            Assert(triggered, "copy callback missing");
            Equal(BackupState.Cancelled, result.State, "mid-item cancellation");
            ItemResult item = Item(result, "1_LEAD");
            Equal(BackupState.Cancelled, item.State, "active item cancellation");
            Assert(item.Destination.EndsWith(Catalog.IncompleteSuffix, StringComparison.Ordinal), "cancelled item lacks marker");
            Assert(Directory.Exists(item.Destination), "cancelled result directory removed");
            Equal(BackupState.Pending, Item(result, "3_BODY").State, "unstarted item cancellation");
            Equal(0, Directory.GetFiles(item.Destination, "*", SearchOption.AllDirectories).Length, "pre-copy cancellation wrote artifacts");
        }
    }

    private static void PreCancelled()
    {
        using (Fixture f = new Fixture())
        using (CancellationTokenSource cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            BackupResult result = new BackupEngine().Run(f.Request(new[] { "1_LEAD" }), null, cancel.Token);
            Equal(BackupState.Cancelled, result.State, "pre-cancel state");
            Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "pre-cancel backup writes");
            Equal(BackupState.Pending, Item(result, "1_LEAD").State, "pre-cancel item state");
        }
    }

    private static void InvalidRequests()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "preserve.txt", "untouched");
            Dictionary<string, string> before = Snapshot(f.Source);
            List<BackupRequest> requests = new List<BackupRequest>();
            requests.Add(f.Request(new string[0]));
            requests.Add(f.Request(new[] { ".." }));
            BackupRequest badName = f.Request(new[] { "1_LEAD" }); badName.Name = "bad/path"; requests.Add(badName);
            BackupRequest missingSource = f.Request(new[] { "1_LEAD" }); missingSource.SourceRoot = Path.Combine(f.Root, "missing"); requests.Add(missingSource);
            BackupRequest missingDestination = f.Request(new[] { "1_LEAD" }); missingDestination.BackupRoot = Path.Combine(f.Root, "missing-backup"); requests.Add(missingDestination);
            BackupRequest missingItem = f.Request(new[] { "2_CORNER" }); Directory.Delete(Path.Combine(f.Source, "2_CORNER")); requests.Add(missingItem);
            before = Snapshot(f.Source);
            foreach (BackupRequest request in requests)
            {
                BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
                Equal(BackupState.Failed, result.State, "invalid request must return handled failure");
                Assert(!String.IsNullOrEmpty(result.Message), "validation failure lacks explanation");
                Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "invalid request wrote backup files");
            }
            Assert(!Directory.Exists(Path.Combine(f.Root, "missing-backup")), "missing backup root was created");
            EqualSnapshots(before, Snapshot(f.Source));
        }
    }

    private static void SourceOverlap()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "original.txt", "source");
            string inside = Path.Combine(f.Source, "6_SHSD", "backup");
            Directory.CreateDirectory(inside);
            Dictionary<string, string> before = Snapshot(f.Source);
            BackupRequest request = f.Request(new[] { "1_LEAD" }); request.BackupRoot = inside;
            Equal(BackupState.Failed, new BackupEngine().Run(request, null, CancellationToken.None).State, "unselected source overlap must fail");
            EqualSnapshots(before, Snapshot(f.Source));
        }
    }

    private static void SiblingBranch()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "original.txt", "sibling allowed");
            string sibling = Path.Combine(f.Source, "backup");
            Directory.CreateDirectory(sibling);
            BackupRequest request = f.Request(new[] { "1_LEAD" }); request.BackupRoot = sibling;
            BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
            Equal(BackupState.Complete, result.State, "common parent should be allowed");
            Equal("sibling allowed", File.ReadAllText(Path.Combine(Item(result, "1_LEAD").Destination, "original.txt")), "sibling content");
        }
    }

    private static void LogFailure()
    {
        using (Fixture f = new Fixture())
        {
            string logFile = Path.Combine(f.Root, "not-a-directory"); File.WriteAllText(logFile, "preserve");
            BackupRequest request = f.Request(new[] { "1_LEAD" }); request.LogDirectory = logFile;
            BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
            Equal(BackupState.Failed, result.State, "log failure state");
            Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "backup started without log");
            Equal("preserve", File.ReadAllText(logFile), "log-path file altered");
        }
    }

    private static void LongDestination()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "payload.txt", "must not be copied");
            BackupRequest request = f.Request(new[] { "1_LEAD" }); request.Name = new string('x', 240);
            BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
            Equal(BackupState.Failed, result.State, "overlong path state");
            Equal(0, Directory.GetFiles(f.BackupRoot, "*", SearchOption.AllDirectories).Length, "overlong destination copied data");
        }
    }

    private static void NestedJunction()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "ordinary.txt", "included");
            string external = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "secret.txt"), "must not follow");
            string link = Path.Combine(f.Source, "1_LEAD", "linked");
            CreateJunctionOrSkip(link, external);
            BackupResult result = f.Backup(new[] { "1_LEAD" });
            Equal(BackupState.Partial, result.State, "junction exclusion affects overall result");
            ItemResult item = Item(result, "1_LEAD");
            Equal(BackupState.Partial, item.State, "junction item state");
            Assert(item.ExcludedEntries >= 1, "junction exclusion not counted");
            Assert(item.Destination.EndsWith(Catalog.IncompleteSuffix, StringComparison.Ordinal), "junction partial marker missing");
            Assert(!Directory.Exists(Path.Combine(item.Destination, "linked")), "junction directory copied");
            Equal("included", File.ReadAllText(Path.Combine(item.Destination, "ordinary.txt")), "ordinary copy missing");
            Equal("must not follow", File.ReadAllText(Path.Combine(external, "secret.txt")), "junction target altered");
            CheckLog(result, "excluded", "linked");
        }
    }

    private static void SelectedJunction()
    {
        using (Fixture f = new Fixture())
        {
            string item = Path.Combine(f.Source, "1_LEAD"); Directory.Delete(item);
            string external = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.txt"), "unchanged");
            CreateJunctionOrSkip(item, external);
            BackupResult result = f.Backup(new[] { "1_LEAD" });
            Equal(BackupState.Failed, result.State, "selected junction must fail preflight");
            Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "selected junction wrote backup");
            Equal("unchanged", File.ReadAllText(Path.Combine(external, "keep.txt")), "target modified");
        }
    }

    private static void DestinationJunction()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "keep.txt", "unchanged");
            string link = Path.Combine(f.BackupRoot, "1_LEAD");
            CreateJunctionOrSkip(link, Path.Combine(f.Source, "6_SHSD"));
            Dictionary<string, string> before = Snapshot(f.Source);
            BackupResult result = f.Backup(new[] { "1_LEAD" });
            Equal(BackupState.Failed, result.State, "destination junction must fail");
            EqualSnapshots(before, Snapshot(f.Source));
        }
    }

    private static void DeniedSourceWrites()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "protected.txt", "readable");
            string itemPath = Path.Combine(f.Source, "1_LEAD");
            byte[] original = Directory.GetAccessControl(itemPath).GetSecurityDescriptorBinaryForm();
            DirectorySecurity modified = Directory.GetAccessControl(itemPath);
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            FileSystemAccessRule rule = new FileSystemAccessRule(user, FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
            bool applied = false;
            try
            {
                try { modified.AddAccessRule(rule); Directory.SetAccessControl(itemPath, modified); applied = true; }
                catch (UnauthorizedAccessException ex) { throw new SkipException("fixture ACL not supported: " + ex.Message); }
                catch (PlatformNotSupportedException ex) { throw new SkipException("fixture ACL not supported: " + ex.Message); }
                bool denied = false;
                try { File.WriteAllText(Path.Combine(itemPath, "forbidden.tmp"), "probe"); }
                catch (UnauthorizedAccessException) { denied = true; }
                Assert(denied, "fixture did not deny source write");
                Dictionary<string, string> before = Snapshot(f.Source);
                BackupResult result = f.Backup(new[] { "1_LEAD" });
                Equal(BackupState.Complete, result.State, "read-only permission backup failed: " + result.Message);
                Equal("readable", File.ReadAllText(Path.Combine(Item(result, "1_LEAD").Destination, "protected.txt")), "protected file missing");
                EqualSnapshots(before, Snapshot(f.Source));
            }
            finally { if (applied) RestoreDirectoryAcl(itemPath, original); }
        }
    }

    private static void DeniedDestinationWrites()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("1_LEAD", "preserve.txt", "source data");
            byte[] original = Directory.GetAccessControl(f.BackupRoot).GetSecurityDescriptorBinaryForm();
            DirectorySecurity modified = Directory.GetAccessControl(f.BackupRoot);
            FileSystemAccessRule rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.Write, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
            bool applied = false;
            try
            {
                try { modified.AddAccessRule(rule); Directory.SetAccessControl(f.BackupRoot, modified); applied = true; }
                catch (UnauthorizedAccessException ex) { throw new SkipException("fixture ACL not supported: " + ex.Message); }
                catch (PlatformNotSupportedException ex) { throw new SkipException("fixture ACL not supported: " + ex.Message); }
                BackupResult result = f.Backup(new[] { "1_LEAD" });
                Equal(BackupState.Failed, result.State, "unwritable backup root result");
                Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "unwritable root received writes");
                Equal(BackupState.Pending, Item(result, "1_LEAD").State, "unwritable root must fail before item starts");
                Equal("source data", File.ReadAllText(Path.Combine(f.Source, "1_LEAD", "preserve.txt")), "source altered");
            }
            finally { if (applied) RestoreDirectoryAcl(f.BackupRoot, original); }
        }
    }

    private static void InsufficientSpace()
    {
        using (Fixture f = new Fixture())
        {
            long? available = PathSafety.AvailableBytes(f.BackupRoot);
            if (!available.HasValue) throw new SkipException("free space unavailable on fixture filesystem");
            long logicalLength = checked(available.Value + 1024 * 1024);
            if (logicalLength > 64L * 1024 * 1024 * 1024 * 1024) throw new SkipException("test volume exceeds bounded sparse fixture size");
            string path = Path.Combine(f.Source, "1_LEAD", "oversized-sparse.bin");
            using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                int returned;
                if (!DeviceIoControl(file.SafeFileHandle, 0x000900C4, null, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw new SkipException("sparse files unsupported: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                Assert((File.GetAttributes(path) & FileAttributes.SparseFile) != 0, "sparse flag missing; refusing logical expansion");
                file.SetLength(logicalLength);
            }
            BackupResult result = f.Backup(new[] { "1_LEAD" });
            Equal(BackupState.Failed, result.State, "insufficient space state");
            Assert(result.Message.IndexOf("공간", StringComparison.Ordinal) >= 0, "space failure explanation absent");
            Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "space preflight created backups");
            Equal(logicalLength, new FileInfo(path).Length, "sparse source length altered");
            CheckLog(result, "failure", "공간");
        }
    }

    private static void Settings()
    {
        using (Fixture f = new Fixture())
        {
            string directory = Path.Combine(f.Root, "settings");
            string warning;
            AppSettings initial = SettingsStore.LoadFrom(directory, out warning);
            Assert(String.IsNullOrEmpty(warning), "first-run warning");
            Equal(2, initial.Profiles.Count, "profile count");
            Equal(@"", initial.Profiles[0].SourceRoot, "machine 1 source default");
            Equal(@"", initial.Profiles[0].BackupRoot, "machine 1 backup default");
            Equal(@"", initial.Profiles[1].SourceRoot, "machine 2 source default");
            Equal(@"", initial.Profiles[1].BackupRoot, "machine 2 backup default");
            Assert(!Directory.Exists(directory), "loading missing settings created files");
            initial.Profiles[0].SourceRoot = f.Source;
            initial.Profiles[0].BackupRoot = f.BackupRoot;
            initial.Profiles[1].SourceRoot = Path.Combine(f.Root, "unavailable source");
            initial.Profiles[1].BackupRoot = Path.Combine(f.Root, "unavailable backup");
            initial.SelectedProfile = 1;
            initial.LastName = "한글 이름 <&>";
            SettingsStore.SaveTo(initial, directory);
            AppSettings loaded = SettingsStore.LoadFrom(directory, out warning);
            Assert(String.IsNullOrEmpty(warning), "saved settings warning");
            Equal(1, loaded.SelectedProfile, "active profile persistence");
            Equal(initial.LastName, loaded.LastName, "name persistence");
            Equal(initial.Profiles[0].SourceRoot, loaded.Profiles[0].SourceRoot, "source persistence");
            Equal(initial.Profiles[1].SourceRoot, loaded.Profiles[1].SourceRoot, "unavailable source was silently replaced");
            loaded.LastName = "changed";
            SettingsStore.SaveTo(loaded, directory);
            Equal("changed", SettingsStore.LoadFrom(directory, out warning).LastName, "atomic overwrite persistence");
            Equal(1, Directory.GetFiles(directory).Length, "settings temporary artifact leaked");
            string file = Path.Combine(directory, "settings.xml");
            File.WriteAllText(file, "<corrupt", Encoding.UTF8);
            AppSettings corrupt = SettingsStore.LoadFrom(directory, out warning);
            Assert(!String.IsNullOrEmpty(warning), "corrupt settings missing warning");
            foreach (MachineProfile profile in corrupt.Profiles)
            {
                Assert(String.IsNullOrEmpty(profile.SourceRoot), "corrupt config silently substituted source");
                Assert(String.IsNullOrEmpty(profile.BackupRoot), "corrupt config silently substituted backup");
            }
            Equal("<corrupt", File.ReadAllText(file, Encoding.UTF8), "corrupt settings overwritten on read");
        }
    }

    private static void RestoreDirectoryAcl(string path, byte[] securityDescriptor)
    {
        DirectorySecurity restore = new DirectorySecurity();
        restore.SetSecurityDescriptorBinaryForm(securityDescriptor, AccessControlSections.Access);
        Directory.SetAccessControl(path, restore);
    }

    private static string[] CustomNames()
    {
        return new[] { "1_LEAD", "2_CORNER", "3_BODY", "4_SIDE", "5_TERRACE", "6_SSIDE" };
    }

    private static void CustomItemCopies()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("5_TERRACE", "setup.ini", "first terrace");
            f.Write("6_SSIDE", "Sub\\side.txt", "side content");
            Directory.CreateDirectory(Path.Combine(f.Source, "5_TERRACE", "Empty"));
            f.Write("5_TERR", "old-default.txt", "legacy source untouched");
            f.Write("6_SHSD", "old-default.txt", "legacy sixth untouched");
            Dictionary<string, string> sourceBefore = Snapshot(f.Source);
            BackupRequest first = f.Request(new[] { "5_TERRACE" });
            first.ConfiguredItems = CustomNames();
            BackupResult firstResult = new BackupEngine().Run(first, null, CancellationToken.None);
            Equal(BackupState.Complete, firstResult.State, "custom fifth first backup");
            string fifthFirst = Path.Combine(f.BackupRoot, "5_TERRACE", BaseName);
            Equal(fifthFirst, Item(firstResult, "5_TERRACE").Destination, "custom parent name");
            Equal("first terrace", File.ReadAllText(Path.Combine(fifthFirst, "setup.ini")), "custom source content");
            Assert(Directory.Exists(Path.Combine(fifthFirst, "Empty")), "custom empty folder missing");
            Equal(1, Directory.GetDirectories(f.BackupRoot).Length, "only selected custom parent created");
            Assert(!Directory.Exists(Path.Combine(f.BackupRoot, "6_SSIDE")), "unselected custom parent created");
            EqualSnapshots(sourceBefore, Snapshot(f.Source));
            Dictionary<string, string> previousBackup = Snapshot(f.BackupRoot);
            f.Write("5_TERRACE", "setup.ini", "second terrace");
            sourceBefore = Snapshot(f.Source);
            BackupRequest second = f.Request(new[] { "5_TERRACE", "6_SSIDE" });
            second.ConfiguredItems = CustomNames();
            bool mutatedCaller = false;
            BackupResult secondResult = new BackupEngine().Run(second, delegate(BackupProgress p)
            {
                if (mutatedCaller || p.State != BackupState.Copying) return;
                mutatedCaller = true;
                second.ConfiguredItems[5] = "../changed-after-start";
                second.Items[1] = "not-the-selected-item";
            }, CancellationToken.None);
            Assert(mutatedCaller, "configuration snapshot boundary not exercised");
            Equal(BackupState.Complete, secondResult.State, "custom names snapshot and multi-item backup");
            string fifthSecond = Path.Combine(f.BackupRoot, "5_TERRACE", BaseName + "_01");
            string sixthFirst = Path.Combine(f.BackupRoot, "6_SSIDE", BaseName);
            Equal(fifthSecond, Item(secondResult, "5_TERRACE").Destination, "custom fifth duplicate number");
            Equal(sixthFirst, Item(secondResult, "6_SSIDE").Destination, "custom sixth independent numbering");
            Equal("second terrace", File.ReadAllText(Path.Combine(fifthSecond, "setup.ini")), "new custom copy content");
            Equal("side content", File.ReadAllText(Path.Combine(sixthFirst, "Sub", "side.txt")), "custom sixth recursive content");
            AssertSnapshotRetained(previousBackup, Snapshot(f.BackupRoot));
            EqualSnapshots(sourceBefore, Snapshot(f.Source));
            BackupRequest third = f.Request(new[] { "6_SSIDE" }); third.ConfiguredItems = CustomNames();
            BackupResult thirdResult = new BackupEngine().Run(third, null, CancellationToken.None);
            Equal(BackupState.Complete, thirdResult.State, "custom sixth repeat");
            Equal(Path.Combine(f.BackupRoot, "6_SSIDE", BaseName + "_01"), Item(thirdResult, "6_SSIDE").Destination, "custom sixth duplicate name");
            Equal(2, Directory.GetDirectories(f.BackupRoot).Length, "legacy or unselected backup parents were created");
            Assert(!Directory.Exists(Path.Combine(f.BackupRoot, "5_TERR")), "legacy fifth parent was created");
            Assert(!Directory.Exists(Path.Combine(f.BackupRoot, "6_SHSD")), "legacy sixth parent was created");
            Equal("first terrace", File.ReadAllText(Path.Combine(fifthFirst, "setup.ini")), "previous custom backup overwritten");
            Equal("5_TERR", Catalog.Items[4], "global catalog mutated");
            Equal("6_SHSD", Catalog.Items[5], "global catalog mutated");
            CheckLog(secondResult, "success", "6_SSIDE");
        }
    }

    private static void CustomItemSettings()
    {
        using (Fixture f = new Fixture())
        {
            AppSettings defaults = SettingsStore.Defaults();
            Assert(defaults.Profiles[0].ItemNames != null && defaults.Profiles[1].ItemNames != null, "profiles need default item arrays");
            Assert(!Object.ReferenceEquals(defaults.Profiles[0].ItemNames, defaults.Profiles[1].ItemNames), "profiles share mutable item array");
            Assert(!Object.ReferenceEquals(defaults.Profiles[0].ItemNames, Catalog.Items), "profile aliases global catalog");
            defaults.Profiles[0].ItemNames[4] = "5_TERRACE";
            Equal("5_TERR", defaults.Profiles[1].ItemNames[4], "changing first profile altered second profile");
            Equal("5_TERR", Catalog.Items[4], "changing profile altered global catalog");
            string[] profileTwoNames = { "1_FIRST", "2_SECOND", "3_가운데", "4 SIDE", "5_TERR", "6_CUSTOM" };
            defaults.Profiles[0].ItemNames = CustomNames();
            defaults.Profiles[1].ItemNames = (string[])profileTwoNames.Clone();
            defaults.Profiles[0].SourceRoot = f.Source;
            defaults.Profiles[0].BackupRoot = f.BackupRoot;
            defaults.Profiles[1].SourceRoot = Path.Combine(f.Root, "machine2-source");
            defaults.Profiles[1].BackupRoot = Path.Combine(f.Root, "machine2-backup");
            defaults.SelectedProfile = 1;
            defaults.LastName = "항목명 설정 후";
            string directory = Path.Combine(f.Root, "settings");
            SettingsStore.SaveTo(defaults, directory);
            string warning;
            AppSettings loaded = SettingsStore.LoadFrom(directory, out warning);
            Assert(String.IsNullOrEmpty(warning), "valid item settings warning: " + warning);
            EqualNames(CustomNames(), loaded.Profiles[0].ItemNames, "machine one names");
            EqualNames(profileTwoNames, loaded.Profiles[1].ItemNames, "machine two names");
            Equal(defaults.Profiles[0].SourceRoot, loaded.Profiles[0].SourceRoot, "custom names changed source path");
            Equal(defaults.Profiles[1].BackupRoot, loaded.Profiles[1].BackupRoot, "custom names changed second backup path");
            Equal(1, loaded.SelectedProfile, "custom settings active profile");
            Equal(defaults.LastName, loaded.LastName, "custom settings backup name");
            loaded.Profiles[0].ItemNames[5] = "6_EDITED";
            Equal("6_CUSTOM", loaded.Profiles[1].ItemNames[5], "loaded profile arrays are shared");
            Equal("6_SHSD", Catalog.Items[5], "loaded profile changes global catalog");
        }
    }

    private static void LegacyItemSettings()
    {
        using (Fixture f = new Fixture())
        {
            string directory = Path.Combine(f.Root, "legacy-settings");
            AppSettings saved = SettingsStore.Defaults();
            saved.SelectedProfile = 1;
            saved.LastName = "예전 사용자 이름";
            for (int i = 0; i < saved.Profiles.Count; i++)
            {
                saved.Profiles[i].SourceRoot = Path.Combine(f.Root, "legacy-source-" + i);
                saved.Profiles[i].BackupRoot = Path.Combine(f.Root, "legacy-backup-" + i);
                saved.Profiles[i].ItemNames = CustomNames();
            }
            SettingsStore.SaveTo(saved, directory);
            string path = Path.Combine(directory, "settings.xml");
            XmlDocument oldXml = new XmlDocument(); oldXml.Load(path);
            foreach (XmlNode node in oldXml.SelectNodes("/AppSettings/Profiles/MachineProfile/ItemNames")) node.ParentNode.RemoveChild(node);
            oldXml.Save(path);
            string oldContent = File.ReadAllText(path);
            Assert(oldContent.IndexOf("ItemNames", StringComparison.Ordinal) < 0, "fixture still contains new item-name field");
            string warning;
            AppSettings migrated = SettingsStore.LoadFrom(directory, out warning);
            Assert(String.IsNullOrEmpty(warning), "old settings treated as corrupt: " + warning);
            Equal(1, migrated.SelectedProfile, "legacy active profile lost");
            Equal(saved.LastName, migrated.LastName, "legacy backup name lost");
            for (int i = 0; i < saved.Profiles.Count; i++)
            {
                Equal(saved.Profiles[i].SourceRoot, migrated.Profiles[i].SourceRoot, "legacy source path " + i);
                Equal(saved.Profiles[i].BackupRoot, migrated.Profiles[i].BackupRoot, "legacy backup path " + i);
                EqualNames(Catalog.Items, migrated.Profiles[i].ItemNames, "legacy item defaults " + i);
            }
            Assert(!Object.ReferenceEquals(migrated.Profiles[0].ItemNames, migrated.Profiles[1].ItemNames), "legacy profiles share item defaults");
            Equal(oldContent, File.ReadAllText(path), "reading old settings unexpectedly rewrote them");
            SettingsStore.SaveTo(migrated, directory);
            AppSettings reloaded = SettingsStore.LoadFrom(directory, out warning);
            Assert(String.IsNullOrEmpty(warning), "migrated save warning");
            EqualNames(Catalog.Items, reloaded.Profiles[0].ItemNames, "migrated save item defaults");
            Equal(saved.LastName, reloaded.LastName, "migrated save name lost");
        }
    }

    private static void InvalidItemNames()
    {
        using (Fixture f = new Fixture())
        {
            string[] invalidNames = { null, "", " ", ".", "..", "../escape", "bad/name", "bad\\name", @"C:\source", "bad:name", "bad\nname", "bad*name", "bad?name", "bad\"name", "bad|name", "bad<name", "bad>name", "CON", "NUL.txt", "COM1", "trailing.", "trailing ", new string('x', 256) };
            List<string[]> invalidConfigurations = new List<string[]>();
            foreach (string invalid in invalidNames)
            {
                string name = invalid;
                Throws(delegate { PathSafety.ValidateItemName(name); }, "invalid item name accepted: " + name);
                string[] configured = CustomNames(); configured[5] = name; invalidConfigurations.Add(configured);
            }
            invalidConfigurations.Add(new string[0]);
            invalidConfigurations.Add(new[] { "one", "two", "three", "four", "five" });
            invalidConfigurations.Add(new[] { "one", "two", "three", "four", "five", "six", "seven" });
            string[] duplicate = CustomNames(); duplicate[5] = "1_lead"; invalidConfigurations.Add(duplicate);
            string[] exactDuplicate = CustomNames(); exactDuplicate[5] = exactDuplicate[4]; invalidConfigurations.Add(exactDuplicate);
            f.Write("1_LEAD", "keep.txt", "original");
            Dictionary<string, string> before = Snapshot(f.Source);
            foreach (string[] configuration in invalidConfigurations)
            {
                string[] candidate = configuration;
                Throws(delegate { PathSafety.ValidateItemNames(candidate); }, "invalid item array accepted");
                BackupRequest request = f.Request(new[] { "1_LEAD" }); request.ConfiguredItems = configuration;
                BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
                Equal(BackupState.Failed, result.State, "invalid config run must fail");
                Assert(!String.IsNullOrEmpty(result.Message), "invalid config failure explanation missing");
                Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "invalid config wrote backup data");
            }
            BackupRequest notConfigured = f.Request(new[] { "5_TERR" }); notConfigured.ConfiguredItems = CustomNames();
            Equal(BackupState.Failed, new BackupEngine().Run(notConfigured, null, CancellationToken.None).State, "legacy item selected outside configured list");
            Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "unconfigured selection wrote data");
            EqualSnapshots(before, Snapshot(f.Source));
            string[] inputNames = CustomNames();
            string[] validated = PathSafety.ValidateItemNames(inputNames);
            inputNames[0] = "changed";
            Equal("1_LEAD", validated[0], "validated array aliases caller input");
            string[] defaultNames = PathSafety.ValidateItemNames(null);
            EqualNames(Catalog.Items, defaultNames, "null configuration defaults");
            defaultNames[0] = "changed";
            Equal("1_LEAD", Catalog.Items[0], "validated defaults alias global catalog");
            Equal("한글 item", PathSafety.ValidateItemName("한글 item"), "valid exact folder name changed");
            string directory = Path.Combine(f.Root, "settings");
            AppSettings settings = SettingsStore.Defaults();
            SettingsStore.SaveTo(settings, directory);
            Dictionary<string, string> savedBefore = Snapshot(directory);
            settings.Profiles[1].ItemNames = duplicate;
            Throws(delegate { SettingsStore.SaveTo(settings, directory); }, "invalid profile names saved");
            EqualSnapshots(savedBefore, Snapshot(directory));
            string settingsPath = Path.Combine(directory, "settings.xml");
            XmlDocument invalidXml = new XmlDocument(); invalidXml.Load(settingsPath);
            XmlNodeList values = invalidXml.SelectNodes("/AppSettings/Profiles/MachineProfile[1]/ItemNames/string");
            Equal(6, values.Count, "settings serialization item count");
            values[5].InnerText = values[0].InnerText.ToLowerInvariant();
            invalidXml.Save(settingsPath);
            string warning;
            AppSettings blocked = SettingsStore.LoadFrom(directory, out warning);
            Assert(!String.IsNullOrEmpty(warning), "duplicate item config loaded without warning");
            foreach (MachineProfile profile in blocked.Profiles)
            {
                Assert(String.IsNullOrEmpty(profile.SourceRoot), "invalid config silently substitutes a source");
                Assert(String.IsNullOrEmpty(profile.BackupRoot), "invalid config silently substitutes a backup");
            }
        }
    }

    private static void CustomItemOverlap()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("5_TERRACE", "keep.txt", "custom source");
            f.Write("6_SSIDE", "unselected.txt", "unselected custom source");
            string customInside = Path.Combine(f.Source, "6_SSIDE", "backup");
            string legacyInside = Path.Combine(f.Source, "6_SHSD", "backup");
            Directory.CreateDirectory(customInside); Directory.CreateDirectory(legacyInside);
            Dictionary<string, string> before = Snapshot(f.Source);
            foreach (string destination in new[] { customInside, legacyInside })
            {
                BackupRequest request = f.Request(new[] { "5_TERRACE" });
                request.ConfiguredItems = CustomNames(); request.BackupRoot = destination;
                BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
                Equal(BackupState.Failed, result.State, "write into unselected custom or legacy item must fail");
                Equal(0, Directory.GetFileSystemEntries(destination).Length, "source overlap destination was written");
                EqualSnapshots(before, Snapshot(f.Source));
            }
        }
    }

    private static void CustomItemJunction()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("5_TERRACE", "keep.txt", "selected custom source");
            f.Write("6_SSIDE", "keep.txt", "unselected custom source");
            CreateJunctionOrSkip(Path.Combine(f.BackupRoot, "5_TERRACE"), Path.Combine(f.Source, "6_SSIDE"));
            Dictionary<string, string> before = Snapshot(f.Source);
            BackupRequest request = f.Request(new[] { "5_TERRACE" }); request.ConfiguredItems = CustomNames();
            BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
            Equal(BackupState.Failed, result.State, "junction into unselected custom item must fail");
            EqualSnapshots(before, Snapshot(f.Source));
            Equal(1, Directory.GetFileSystemEntries(f.BackupRoot).Length, "junction case wrote extra backups");
        }
    }

    private static void CustomItemLogProtection()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("5_TERRACE", "keep.txt", "selected custom source");
            f.Write("6_SSIDE", "keep.txt", "unselected custom source");
            Dictionary<string, string> before = Snapshot(f.Source);
            BackupRequest request = f.Request(new[] { "5_TERRACE" }); request.ConfiguredItems = CustomNames();
            request.LogDirectory = Path.Combine(f.Source, "6_SSIDE", "logs");
            BackupResult result = new BackupEngine().Run(request, null, CancellationToken.None);
            Equal(BackupState.Failed, result.State, "logs must not write into unselected custom source");
            Equal(0, Directory.GetFileSystemEntries(f.BackupRoot).Length, "backup started with unsafe log path");
            EqualSnapshots(before, Snapshot(f.Source));
        }
    }

    private static void EqualNames(string[] expected, string[] actual, string message)
    {
        Assert(actual != null, message + ": missing names");
        Equal(expected.Length, actual.Length, message + ": name count");
        for (int i = 0; i < expected.Length; i++) Equal(expected[i], actual[i], message + ": slot " + i);
    }

    private static void SettingsSaveSourceProtection()
    {
        using (Fixture f = new Fixture())
        {
            f.Write("6_SSIDE", "keep.txt", "unselected profile source");
            string activeSource = Path.Combine(f.Root, "active-source");
            Directory.CreateDirectory(activeSource);
            AppSettings settings = SettingsStore.Defaults();
            settings.SelectedProfile = 0;
            settings.Profiles[0].SourceRoot = activeSource;
            settings.Profiles[0].BackupRoot = f.BackupRoot;
            settings.Profiles[1].SourceRoot = f.Source;
            settings.Profiles[1].BackupRoot = f.BackupRoot;
            settings.Profiles[1].ItemNames = CustomNames();
            string protectedItem = Path.Combine(f.Source, "6_SSIDE");
            Dictionary<string, string> before = Snapshot(f.Source);
            AssertSettingsSaveBlocked(settings, protectedItem);
            EqualSnapshots(before, Snapshot(f.Source));
            string missingChild = Path.Combine(protectedItem, "settings-not-created");
            AssertSettingsSaveBlocked(settings, missingChild);
            Assert(!Directory.Exists(missingChild), "unsafe settings directory created before source guard");
            EqualSnapshots(before, Snapshot(f.Source));
            string destinationAlias = Path.Combine(f.Root, "settings-alias");
            CreateJunctionOrSkip(destinationAlias, protectedItem);
            AssertSettingsSaveBlocked(settings, destinationAlias);
            EqualSnapshots(before, Snapshot(f.Source));
            string sourceAlias = Path.Combine(f.Root, "source-alias");
            CreateJunctionOrSkip(sourceAlias, f.Source);
            settings.Profiles[1].SourceRoot = sourceAlias;
            AssertSettingsSaveBlocked(settings, protectedItem);
            EqualSnapshots(before, Snapshot(f.Source));
            Assert(!File.Exists(Path.Combine(protectedItem, "settings.xml")), "settings file written inside source");
            Equal(1, Directory.GetFiles(protectedItem).Length, "settings temporary artifact written inside source");
            Equal("unselected profile source", File.ReadAllText(Path.Combine(protectedItem, "keep.txt")), "source content modified by settings save");
        }
    }

    private static void AssertSettingsSaveBlocked(AppSettings settings, string directory)
    {
        bool blocked = false;
        try { SettingsStore.SaveTo(settings, directory); }
        catch (IOException) { blocked = true; }
        catch (ArgumentException) { blocked = true; }
        Assert(blocked, "settings save into source was not rejected: " + directory);
    }

    private static ItemResult Item(BackupResult result, string name)
    {
        foreach (ItemResult item in result.Items) if (item.Item == name) return item;
        throw new InvalidOperationException("Missing result for " + name);
    }

    private static void CheckLog(BackupResult result, string kind, string pathFragment)
    {
        Assert(!String.IsNullOrEmpty(result.LogPath) && File.Exists(result.LogPath), "log missing");
        string full = Path.GetFullPath(result.LogPath);
        Assert(full.StartsWith(workRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "log escaped fixture");
        JavaScriptSerializer json = new JavaScriptSerializer();
        bool found = false;
        int count = 0;
        foreach (string line in File.ReadAllLines(full, Encoding.UTF8))
        {
            if (String.IsNullOrWhiteSpace(line)) continue;
            object parsed = json.DeserializeObject(line);
            Assert(parsed is Dictionary<string, object>, "log is not JSON object per line");
            if (line.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf(pathFragment, StringComparison.OrdinalIgnoreCase) >= 0) found = true;
            count++;
        }
        Assert(count > 0, "empty log");
        Assert(found, "log missing " + kind + " entry for " + pathFragment);
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SnapshotDirectory(root, root, result);
        return result;
    }

    private static void SnapshotDirectory(string root, string directory, Dictionary<string, string> result)
    {
        foreach (string path in Directory.GetFileSystemEntries(directory))
        {
            string relative = path.Substring(root.Length + 1);
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                result[relative] = "link|" + attributes;
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                result[relative] = "directory|" + attributes + "|" + Directory.GetLastWriteTimeUtc(path).Ticks;
                SnapshotDirectory(root, path, result);
            }
            else
            {
                string hash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path)) hash = Convert.ToBase64String(sha.ComputeHash(stream));
                result[relative] = "file|" + attributes + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + hash;
            }
        }
    }

    private static void EqualSnapshots(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        Equal(expected.Count, actual.Count, "snapshot entry count changed");
        foreach (KeyValuePair<string, string> pair in expected)
        {
            string value;
            Assert(actual.TryGetValue(pair.Key, out value), "removed original: " + pair.Key);
            Equal(pair.Value, value, "modified original: " + pair.Key);
        }
    }

    private static void AssertSnapshotRetained(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        foreach (KeyValuePair<string, string> pair in expected)
        {
            string value;
            Assert(actual.TryGetValue(pair.Key, out value), "removed original: " + pair.Key);
            // Parent directory timestamps change legitimately when new sibling backups are added.
            if (pair.Value.StartsWith("directory|", StringComparison.Ordinal)) continue;
            Equal(pair.Value, value, "modified original: " + pair.Key);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root;
        public readonly string Source;
        public readonly string BackupRoot;
        public readonly string Logs;
        public Fixture()
        {
            Root = Path.Combine(workRoot, Guid.NewGuid().ToString("N").Substring(0, 12));
            Source = Path.Combine(Root, "source");
            BackupRoot = Path.Combine(Root, "backup");
            Logs = Path.Combine(Root, "logs");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(BackupRoot);
            foreach (string item in Catalog.Items) Directory.CreateDirectory(Path.Combine(Source, item));
        }
        public string Write(string item, string relative, string text)
        {
            string path = Path.Combine(Source, item, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        public BackupRequest Request(string[] items)
        {
            return new BackupRequest { SourceRoot = Source, BackupRoot = BackupRoot, Name = Label, Items = items, StartedAt = Start, LogDirectory = Logs };
        }
        public BackupResult Backup(string[] items) { return Backup(items, null); }
        public BackupResult Backup(string[] items, Action<BackupProgress> progress)
        {
            return new BackupEngine().Run(Request(items), progress, CancellationToken.None);
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(workRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup target");
            if (Directory.Exists(resolved)) RemoveFixtureDirectory(resolved);
        }
    }

    private static void RemoveFixtureDirectory(string directory)
    {
        FileAttributes attributes = File.GetAttributes(directory);
        if ((attributes & FileAttributes.ReparsePoint) != 0) { Directory.Delete(directory, false); return; }
        foreach (string path in Directory.GetFileSystemEntries(directory))
        {
            FileAttributes childAttributes = File.GetAttributes(path);
            if ((childAttributes & FileAttributes.Directory) != 0) RemoveFixtureDirectory(path);
            else
            {
                if ((childAttributes & FileAttributes.ReparsePoint) == 0) File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        File.SetAttributes(directory, FileAttributes.Normal);
        Directory.Delete(directory, false);
    }

    private static void CreateJunctionOrSkip(string junction, string target)
    {
        Directory.CreateDirectory(junction);
        string substitute = @"\??\" + Path.GetFullPath(target);
        string print = Path.GetFullPath(target);
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(print);
        byte[] buffer = new byte[16 + substituteBytes.Length + 2 + printBytes.Length + 2];
        using (MemoryStream memory = new MemoryStream(buffer))
        using (BinaryWriter writer = new BinaryWriter(memory))
        {
            writer.Write(unchecked((uint)0xA0000003));
            writer.Write((ushort)(buffer.Length - 8)); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write((ushort)substituteBytes.Length);
            writer.Write((ushort)(substituteBytes.Length + 2)); writer.Write((ushort)printBytes.Length);
            writer.Write(substituteBytes); writer.Write((ushort)0);
            writer.Write(printBytes); writer.Write((ushort)0);
        }
        using (SafeFileHandle handle = CreateFile(junction, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            if (handle.IsInvalid) throw new SkipException("junction handle unavailable: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            int returned;
            if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                throw new SkipException("junction unavailable on test filesystem: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }

    private sealed class SkipException : Exception { public SkipException(string message) : base(message) { } }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
