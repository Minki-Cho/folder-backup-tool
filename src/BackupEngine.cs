using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DdalggakBackup
{
    /// <summary>Copies a frozen list into newly allocated folders. Never writes to a source path.</summary>
    public sealed class BackupEngine
    {
        public BackupResult Run(BackupRequest request, Action<BackupProgress> progress, CancellationToken cancellation)
        {
            return new Execution(request, progress, cancellation).Run();
        }

        private sealed class FilePlan
        {
            public string Relative;
            public long Length;
            public string PreparationError;
        }

        private sealed class ItemPlan
        {
            public ItemResult Result;
            public string Source;
            public string Parent;
            public string Candidate;
            public string WorkingRoot;
            public readonly List<string> Directories = new List<string>();
            public readonly List<FilePlan> Files = new List<FilePlan>();
            public int PreparationProblems;
        }

        private sealed class SpaceBudget
        {
            public string Probe;
            public long Required;
        }

        private sealed class FatalBackupException : IOException
        {
            public FatalBackupException(string message, Exception inner) : base(message, inner) { }
        }

        private sealed class Execution
        {
            private const string TemporarySample = ".ddalggak-00000000000000000000000000000000.tmp";
            private readonly BackupRequest input;
            private readonly Action<BackupProgress> report;
            private readonly CancellationToken cancellation;
            private readonly BackupResult result = new BackupResult();
            private readonly List<ItemPlan> plans = new List<ItemPlan>();
            private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
            private StreamWriter log;
            private bool logBroken;
            private string sourceRoot;
            private string backupRoot;
            private string folderName;
            private string[] configuredItems;
            private ItemPlan current;
            private int total;
            private int processed;
            private int succeeded;
            private int failed;

            public Execution(BackupRequest request, Action<BackupProgress> progress, CancellationToken token)
            {
                // Snapshot mutable caller inputs before doing any IO.
                if (request != null)
                    input = new BackupRequest {
                        SourceRoot = request.SourceRoot, BackupRoot = request.BackupRoot,
                        Name = request.Name, Items = request.Items == null ? null : (string[])request.Items.Clone(),
                        ConfiguredItems = request.ConfiguredItems == null ? null : (string[])request.ConfiguredItems.Clone(),
                        StartedAt = request.StartedAt, LogDirectory = request.LogDirectory
                    };
                report = progress;
                cancellation = token;
            }

            public BackupResult Run()
            {
                result.StartedAt = input == null || input.StartedAt == default(DateTime) ? DateTime.Now : input.StartedAt;
                result.State = BackupState.Preparing;
                try
                {
                    PrepareRequest();
                    cancellation.ThrowIfCancellationRequested();
                    OpenLog();
                    Record(null, "info", backupRoot, "백업 준비 시작. 원본: " + sourceRoot + ", 폴더명: " + folderName);
                    Notify(BackupState.Preparing, null, "", "복사할 파일 목록을 확인하고 있습니다.");
                    foreach (ItemPlan plan in plans)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        current = plan;
                        Scan(plan);
                        plan.Result.TotalFiles = plan.Files.Count;
                        total += plan.Files.Count;
                        plan.Candidate = PathSafety.PreviewPath(backupRoot, plan.Result.Item, folderName);
                        ValidatePlanPaths(plan, plan.Candidate);
                    }
                    current = null;
                    CheckSpace();
                    Record(null, "info", backupRoot, "대상 목록 확정: 파일 " + total.ToString(CultureInfo.InvariantCulture) + "개. 이후 추가된 파일은 다음 백업 대상입니다.");
                    foreach (ItemPlan plan in plans)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        current = plan;
                        CopyItem(plan);
                        current = null;
                    }
                    bool partial = false;
                    foreach (ItemPlan plan in plans) if (plan.Result.State != BackupState.Complete) partial = true;
                    result.State = partial ? BackupState.Partial : BackupState.Complete;
                    result.Message = partial ? "백업을 마쳤으나 실패·제외 또는 경고가 있습니다. 상세 결과를 확인해 주세요." : "선택한 항목의 백업이 완료되었습니다.";
                    Record(null, "info", backupRoot, result.Message + " 성공 " + succeeded + "개, 실패 " + failed + "개.");
                }
                catch (OperationCanceledException)
                {
                    if (current != null && current.Result.State != BackupState.Complete)
                        current.Result.State = BackupState.Cancelled;
                    result.State = BackupState.Cancelled;
                    result.Message = "백업이 취소되었습니다. 완료한 복사본은 보존했으며, 미완료 폴더는 표식을 유지합니다.";
                    RecordBestEffort(current, "info", CurrentPath(), result.Message);
                }
                catch (Exception error)
                {
                    if (current != null && current.Result.State != BackupState.Complete)
                        current.Result.State = BackupState.Failed;
                    result.State = BackupState.Failed;
                    result.Message = "백업을 진행할 수 없습니다: " + error.Message;
                    RecordBestEffort(current, "failure", CurrentPath(), result.Message);
                }
                finally
                {
                    if (log != null)
                    {
                        try { log.Dispose(); }
                        catch (Exception error)
                        {
                            result.State = BackupState.Failed;
                            result.Message = "실행 로그를 안전하게 닫지 못했습니다: " + error.Message;
                            logBroken = true;
                            RecordBestEffort(null, "failure", result.LogPath, result.Message);
                        }
                    }
                    result.FinishedAt = DateTime.Now;
                    Notify(result.State, current, "", result.Message);
                }
                return result;
            }

            private void PrepareRequest()
            {
                if (input == null) throw new ArgumentException("백업 설정이 없습니다.");
                if (input.Items == null || input.Items.Length == 0) throw new ArgumentException("백업할 항목을 하나 이상 선택해 주세요.");
                configuredItems = PathSafety.ValidateItemNames(input.ConfiguredItems);
                HashSet<string> configured = new HashSet<string>(configuredItems, StringComparer.OrdinalIgnoreCase);
                HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string item in input.Items)
                {
                    PathSafety.ValidateItemName(item);
                    if (!configured.Contains(item)) throw new ArgumentException("현재 기기에 설정되지 않은 백업 항목입니다: " + item);
                    if (!selected.Add(item)) throw new ArgumentException("같은 항목이 중복 선택되었습니다: " + item);
                    ItemResult itemResult = new ItemResult { Item = item };
                    result.Items.Add(itemResult);
                }
                sourceRoot = PathSafety.Normalize(input.SourceRoot);
                backupRoot = PathSafety.Normalize(input.BackupRoot);
                folderName = PathSafety.FolderName(input.Name, result.StartedAt);
                PathSafety.ValidateRoots(sourceRoot, backupRoot, input.Items, configuredItems);
                foreach (ItemResult itemResult in result.Items)
                    plans.Add(new ItemPlan { Result = itemResult, Source = Path.Combine(sourceRoot, itemResult.Item), Parent = Path.Combine(backupRoot, itemResult.Item) });
            }

            private void OpenLog()
            {
                string directory = String.IsNullOrEmpty(input.LogDirectory) ? Path.Combine(SettingsStore.DataDirectory, "logs") : PathSafety.Normalize(input.LogDirectory);
                PathSafety.EnsureSafeWrite(directory, sourceRoot, configuredItems);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, result.StartedAt.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".jsonl");
                PathSafety.EnsureSafeWrite(path, sourceRoot, configuredItems);
                FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                try { log = new StreamWriter(stream, new UTF8Encoding(false)); log.AutoFlush = true; }
                catch { stream.Dispose(); throw; }
                result.LogPath = path;
            }

            private void Scan(ItemPlan plan)
            {
                Stack<string> pending = new Stack<string>();
                pending.Push(plan.Source);
                while (pending.Count > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    string[] entries;
                    try
                    {
                        PathSafety.EnsureOrdinarySource(directory, plan.Source);
                        entries = Directory.GetFileSystemEntries(directory);
                        Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Exception error)
                    {
                        if (directory == plan.Source || IsFatal(error)) throw;
                        plan.PreparationProblems++;
                        Record(plan, "failure", directory, "하위 폴더 목록을 읽지 못했습니다: " + error.Message);
                        continue;
                    }
                    Notify(BackupState.Preparing, plan, directory, "대상 목록 확인 중");
                    foreach (string entry in entries)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        FileAttributes attributes;
                        try { attributes = File.GetAttributes(entry); }
                        catch (Exception error)
                        {
                            if (IsFatal(error)) throw;
                            plan.PreparationProblems++;
                            Record(plan, "failure", entry, "항목 속성을 확인하지 못했습니다: " + error.Message);
                            continue;
                        }
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            plan.Result.ExcludedEntries++;
                            Record(plan, "excluded", entry, "링크·정션은 따라가지 않고 제외했습니다.");
                            continue;
                        }
                        string relative = entry.Substring(plan.Source.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            plan.Directories.Add(relative);
                            pending.Push(entry);
                        }
                        else
                        {
                            FilePlan file = new FilePlan { Relative = relative };
                            try { PathSafety.EnsureOrdinarySource(entry, plan.Source); file.Length = new FileInfo(entry).Length; }
                            catch (Exception error)
                            {
                                if (IsFatal(error)) throw;
                                file.PreparationError = "준비 중 파일 정보를 읽지 못했습니다: " + error.Message;
                            }
                            plan.Files.Add(file);
                        }
                    }
                }
                plan.Directories.Sort(delegate(string a, string b) { int length = a.Length.CompareTo(b.Length); return length == 0 ? StringComparer.OrdinalIgnoreCase.Compare(a, b) : length; });
                plan.Files.Sort(delegate(FilePlan a, FilePlan b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Relative, b.Relative); });
            }

            private void ValidatePlanPaths(ItemPlan plan, string finalRoot)
            {
                // A long temporary path must be rejected before any source file is copied.
                string incomplete = finalRoot + Catalog.IncompleteSuffix;
                PathSafety.ValidateDestinationLength(finalRoot);
                PathSafety.ValidateDestinationLength(incomplete);
                PathSafety.EnsureSafeWrite(incomplete, sourceRoot, configuredItems);
                foreach (string relative in plan.Directories)
                {
                    PathSafety.ValidateDestinationLength(Path.Combine(finalRoot, relative));
                    PathSafety.ValidateDestinationLength(Path.Combine(incomplete, relative));
                }
                foreach (FilePlan file in plan.Files)
                {
                    string path = Path.Combine(incomplete, file.Relative);
                    PathSafety.ValidateDestinationLength(Path.Combine(finalRoot, file.Relative));
                    PathSafety.ValidateDestinationLength(path);
                    PathSafety.ValidateDestinationLength(Path.Combine(Path.GetDirectoryName(path), TemporarySample));
                }
            }

            private void CheckSpace()
            {
                Dictionary<string, SpaceBudget> budgets = new Dictionary<string, SpaceBudget>(StringComparer.OrdinalIgnoreCase);
                foreach (ItemPlan plan in plans)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string canonical = PathSafety.Canonicalize(plan.Parent);
                    string volume = Path.GetPathRoot(canonical);
                    SpaceBudget budget;
                    if (!budgets.TryGetValue(volume, out budget))
                    {
                        string probe = canonical;
                        while (!Directory.Exists(probe))
                        {
                            string parent = Path.GetDirectoryName(probe);
                            if (String.IsNullOrEmpty(parent) || parent == probe) break;
                            probe = parent;
                        }
                        budget = new SpaceBudget { Probe = probe };
                        budgets.Add(volume, budget);
                    }
                    foreach (FilePlan file in plan.Files) budget.Required = checked(budget.Required + file.Length);
                }
                foreach (SpaceBudget budget in budgets.Values)
                {
                    long? available = PathSafety.AvailableBytes(budget.Probe);
                    if (!available.HasValue)
                    {
                        string unknown = "가용 공간을 확인할 수 없습니다. 복사 중 쓰기 오류를 확인합니다. 필요한 용량: " + budget.Required + "바이트.";
                        Record(null, "info", budget.Probe, unknown);
                        Notify(BackupState.Preparing, null, budget.Probe, unknown);
                        continue;
                    }
                    string message = "필요한 용량 " + budget.Required + "바이트, 가용 용량 " + available.Value + "바이트.";
                    Record(null, "info", budget.Probe, message);
                    Notify(BackupState.Preparing, null, budget.Probe, message);
                    if (available.Value < budget.Required) throw new IOException("저장 공간이 부족합니다. " + message);
                }
            }

            private void CopyItem(ItemPlan plan)
            {
                plan.Result.State = BackupState.Preparing;
                PathSafety.EnsureSafeWrite(plan.Parent, sourceRoot, configuredItems);
                if (!Directory.Exists(plan.Parent)) Directory.CreateDirectory(plan.Parent);
                Allocate(plan);
                // Recheck lengths if another process consumed the preview number.
                ValidatePlanPaths(plan, plan.Candidate);
                plan.Result.State = BackupState.Copying;
                Record(plan, "info", plan.WorkingRoot, "항목 백업 시작. 파일 " + plan.Files.Count + "개.");
                Notify(BackupState.Copying, plan, "", "파일 복사 중");
                foreach (string relative in plan.Directories)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string target = Path.Combine(plan.WorkingRoot, relative);
                    try
                    {
                        GuardWrite(plan, target);
                        if (!PathSafety.TryCreateDirectoryExclusive(target)) throw new IOException("새 백업 폴더 안에 같은 이름이 이미 생겼습니다.");
                    }
                    catch (Exception error)
                    {
                        if (IsFatal(error)) throw;
                        plan.PreparationProblems++;
                        Record(plan, "failure", target, "하위 폴더를 만들지 못했습니다: " + error.Message);
                    }
                }
                foreach (FilePlan file in plan.Files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string source = Path.Combine(plan.Source, file.Relative);
                    string target = Path.Combine(plan.WorkingRoot, file.Relative);
                    Notify(BackupState.Copying, plan, source, "파일 복사 중");
                    bool counted = false;
                    try
                    {
                        if (file.PreparationError != null) throw new IOException(file.PreparationError);
                        string warning = CopyFile(plan, source, target);
                        plan.Result.SucceededFiles++;
                        succeeded++;
                        plan.Result.ProcessedFiles++;
                        processed++;
                        counted = true;
                        Record(plan, "success", target, "복사 및 파일 크기 확인 완료. 원본: " + source);
                        if (warning != null)
                        {
                            plan.Result.Warnings++;
                            Record(plan, "warning", target, warning);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        if (!counted)
                        {
                            plan.Result.ProcessedFiles++;
                            processed++;
                            plan.Result.FailedFiles++;
                            failed++;
                        }
                        if (error is FatalBackupException)
                        {
                            RecordBestEffort(plan, "failure", source, counted ? "파일 복사는 완료했으나 작업을 계속할 수 없습니다: " + error.Message : "파일 복사를 중단했습니다: " + error.Message);
                            throw;
                        }
                        Record(plan, "failure", source, "파일 복사 실패: " + error.Message);
                        if (IsFatal(error) || !Directory.Exists(plan.Source) || !Directory.Exists(plan.WorkingRoot))
                            throw new FatalBackupException("원본 또는 백업 위치에 계속 접근할 수 없어 작업을 중단합니다.", error);
                    }
                    Notify(BackupState.Copying, plan, source, "파일 처리 완료");
                }
                cancellation.ThrowIfCancellationRequested();
                if (plan.Result.FailedFiles > 0 || plan.PreparationProblems > 0 || plan.Result.ExcludedEntries > 0 || plan.Result.Warnings > 0)
                {
                    plan.Result.State = BackupState.Partial;
                    Record(plan, "info", plan.WorkingRoot, "일부 실패·제외 또는 경고가 있어 미완료 표식을 유지합니다.");
                }
                else
                {
                    string previous = plan.WorkingRoot;
                    FinalizeItem(plan);
                    plan.Result.State = BackupState.Complete;
                    Record(plan, "info", plan.Result.Destination, "항목 백업 완료. 폴더 이름 확정: " + previous + " → " + plan.Result.Destination + ". 이전 복사 기록의 경로는 이 실제 저장 위치 아래로 이동했습니다.");
                }
                Notify(plan.Result.State, plan, "", plan.Result.State == BackupState.Complete ? "항목 백업 완료" : "항목 일부 실패");
            }

            private void Allocate(ItemPlan plan)
            {
                for (int number = 0; number < Int32.MaxValue; number++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string candidate = Candidate(plan, number);
                    string incomplete = candidate + Catalog.IncompleteSuffix;
                    PathSafety.ValidateDestinationLength(incomplete);
                    if (Exists(candidate) || Exists(incomplete)) continue;
                    PathSafety.EnsureSafeWrite(incomplete, sourceRoot, configuredItems);
                    if (!PathSafety.TryCreateDirectoryExclusive(incomplete)) continue;
                    plan.Candidate = candidate;
                    plan.WorkingRoot = incomplete;
                    plan.Result.Destination = incomplete;
                    return;
                }
                throw new IOException("사용 가능한 백업 번호를 찾을 수 없습니다.");
            }

            private void FinalizeItem(ItemPlan plan)
            {
                string candidate = plan.Candidate;
                int number = 0;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (Exists(candidate) || (candidate != plan.Candidate && Exists(candidate + Catalog.IncompleteSuffix)))
                    {
                        candidate = Candidate(plan, ++number);
                        continue;
                    }
                    ValidatePlanPaths(plan, candidate);
                    GuardWrite(plan, plan.WorkingRoot);
                    PathSafety.EnsureSafeWrite(candidate, sourceRoot, configuredItems);
                    try { Directory.Move(plan.WorkingRoot, candidate); }
                    catch (IOException)
                    {
                        // Directory.Move never overwrites; a competing creator can only force a new number.
                        if (Exists(candidate)) { candidate = Candidate(plan, ++number); continue; }
                        throw;
                    }
                    string previous = plan.WorkingRoot;
                    plan.WorkingRoot = candidate;
                    plan.Result.Destination = candidate;
                    // Keep in-memory detail paths usable after the containing folder has been renamed.
                    foreach (DetailEntry detail in plan.Result.Details)
                        if (!String.IsNullOrEmpty(detail.Path) && PathSafety.IsWithin(detail.Path, previous))
                            detail.Path = candidate + detail.Path.Substring(previous.Length);
                    return;
                }
            }

            private string CopyFile(ItemPlan plan, string source, string destination)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    PathSafety.EnsureOrdinarySource(source, plan.Source);
                    FileInfo before = new FileInfo(source);
                    long lengthBefore = before.Length;
                    DateTime modifiedBefore = before.LastWriteTimeUtc;
                    string temporary = Path.Combine(Path.GetDirectoryName(destination), ".ddalggak-" + Guid.NewGuid().ToString("N") + ".tmp");
                    bool temporaryOwned = false;
                    try
                    {
                        GuardWrite(plan, temporary);
                        long written = 0;
                        using (FileStream inputStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 128, FileOptions.SequentialScan))
                        using (FileStream outputStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, FileOptions.SequentialScan))
                        {
                            temporaryOwned = true;
                            byte[] buffer = new byte[1024 * 128];
                            while (true)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                int count = inputStream.Read(buffer, 0, buffer.Length);
                                if (count == 0) break;
                                outputStream.Write(buffer, 0, count);
                                written = checked(written + count);
                            }
                            outputStream.Flush(true);
                        }
                        cancellation.ThrowIfCancellationRequested();
                        PathSafety.EnsureOrdinarySource(source, plan.Source);
                        FileInfo after = new FileInfo(source);
                        bool changed = after.Length != lengthBefore || after.LastWriteTimeUtc != modifiedBefore;
                        if (changed)
                        {
                            RemoveTemporary(plan, temporary);
                            temporaryOwned = false;
                            if (attempt == 0)
                            {
                                Record(plan, "info", source, "복사 중 원본 크기 또는 수정 시각 변경을 감지하여 한 번 재시도합니다.");
                                continue;
                            }
                            throw new IOException("한 번 재시도한 뒤에도 원본 크기 또는 수정 시각이 변경되었습니다.");
                        }
                        if (written != lengthBefore || new FileInfo(temporary).Length != lengthBefore)
                            throw new IOException("복사한 파일의 크기가 원본 크기와 일치하지 않습니다.");
                        string warning = null;
                        try
                        {
                            GuardWrite(plan, temporary);
                            File.SetLastWriteTimeUtc(temporary, modifiedBefore);
                            if (File.GetLastWriteTimeUtc(temporary) != modifiedBefore)
                                warning = "파일 내용은 복사했지만 저장 장치에서 원본의 최종 수정 시각을 정확히 유지하지 못했습니다.";
                        }
                        catch (Exception error)
                        {
                            if (IsFatal(error)) throw;
                            warning = "파일 내용은 복사했지만 최종 수정 시각을 유지하지 못했습니다: " + error.Message;
                        }
                        cancellation.ThrowIfCancellationRequested();
                        GuardWrite(plan, destination);
                        GuardWrite(plan, temporary);
                        File.Move(temporary, destination);
                        temporaryOwned = false;
                        return warning;
                    }
                    finally
                    {
                        if (temporaryOwned)
                        {
                            try { RemoveTemporary(plan, temporary); }
                            catch (Exception error)
                            {
                                plan.Result.Warnings++;
                                RecordBestEffort(plan, "warning", temporary, "이번 작업의 복사 중 임시 파일을 제거하지 못했습니다: " + error.Message);
                            }
                        }
                    }
                }
                throw new IOException("파일 재시도를 완료하지 못했습니다.");
            }

            private void RemoveTemporary(ItemPlan plan, string path)
            {
                GuardWrite(plan, path);
                File.Delete(path);
            }

            private void GuardWrite(ItemPlan plan, string path)
            {
                if (!PathSafety.IsWithin(PathSafety.Normalize(path), PathSafety.Normalize(plan.WorkingRoot)))
                    throw new FatalBackupException("이번 백업 폴더 밖에 쓰려는 작업을 차단했습니다: " + path, null);
                try { PathSafety.EnsureSafeWrite(path, sourceRoot, configuredItems); }
                catch (Exception error) { throw new FatalBackupException("백업 쓰기 경로의 안전성을 확인하지 못했습니다: " + path, error); }
                // Never follow a link inserted into a destination after initial root validation.
                string existing = path;
                while (!Exists(existing) && !String.Equals(existing, plan.WorkingRoot, StringComparison.OrdinalIgnoreCase))
                    existing = Path.GetDirectoryName(existing);
                try { PathSafety.EnsureOrdinarySource(existing, plan.WorkingRoot); }
                catch (Exception error) { throw new FatalBackupException("백업 폴더의 안전성을 확인하지 못했습니다: " + path, error); }
            }

            private string Candidate(ItemPlan plan, int number)
            {
                return Path.Combine(plan.Parent, folderName + (number == 0 ? "" : "_" + number.ToString("D2", CultureInfo.InvariantCulture)));
            }

            private static bool Exists(string path)
            {
                // File.Exists hides access failures and can miss dangling links. An existing
                // directory entry must reserve its name even when its target cannot be opened.
                try { File.GetAttributes(path); return true; }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            }

            private static bool IsFatal(Exception error)
            {
                if (error is FatalBackupException) return true;
                IOException io = error as IOException;
                if (io == null) return false;
                int code = io.HResult & 0xFFFF;
                return code == 21 || code == 39 || code == 53 || code == 55 || code == 64 || code == 67 ||
                    code == 112 || code == 121 || code == 1117 || code == 1231 || code == 1232;
            }

            private string CurrentPath()
            {
                if (current != null) return current.Result.Destination ?? current.Source;
                return backupRoot ?? (input == null ? "" : input.BackupRoot);
            }

            private void Record(ItemPlan plan, string kind, string path, string message)
            {
                DetailEntry entry = new DetailEntry { Time = DateTime.Now, Item = plan == null ? "" : plan.Result.Item, Kind = kind, Path = path, Message = message };
                result.Details.Add(entry);
                if (plan != null) plan.Result.Details.Add(entry);
                if (logBroken) throw new FatalBackupException("실행 로그에 더 이상 기록할 수 없습니다.", null);
                if (log == null) return;
                try
                {
                    log.WriteLine(serializer.Serialize(new {
                        time = entry.Time.ToString("o", CultureInfo.InvariantCulture), item = entry.Item,
                        kind = entry.Kind, path = entry.Path, message = entry.Message
                    }));
                    log.Flush();
                }
                catch (Exception error)
                {
                    logBroken = true;
                    throw new FatalBackupException("실행 로그에 기록할 수 없어 백업을 중단합니다: " + error.Message, error);
                }
            }

            private void RecordBestEffort(ItemPlan plan, string kind, string path, string message)
            {
                try { Record(plan, kind, path, message); }
                catch (Exception error)
                {
                    result.Message = (result.Message ?? "") + " 로그 기록 실패: " + error.Message;
                    if (result.State != BackupState.Cancelled) result.State = BackupState.Failed;
                }
            }

            private void Notify(BackupState state, ItemPlan plan, string path, string message)
            {
                if (report == null) return;
                // UI disposal or a progress observer must not crash a file operation.
                try { report(new BackupProgress { State = state, Item = plan == null ? "" : plan.Result.Item, CurrentFile = path, Message = message, TotalFiles = total, ProcessedFiles = processed, SucceededFiles = succeeded, FailedFiles = failed }); }
                catch { }
            }
        }
    }
}
