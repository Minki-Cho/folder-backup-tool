using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DdalggakBackup
{
    /// <summary>Stages incoming files, completes one full backup, then replaces only planned files.</summary>
    public sealed class ApplyEngine
    {
        public ApplyResult Run(ApplyRequest request, Action<ApplyProgress> progress, CancellationToken cancellation)
        {
            return new Execution(request, progress, cancellation).Run();
        }

        private sealed class InputRoot
        {
            public string Path;
            public string Canonical;
            public bool Directory;
        }

        private sealed class IncomingFile
        {
            public InputRoot Root;
            public string Source;
            public string Relative;
            public long Length;
            public DateTime Modified;
            public string Staged;
            public bool OwnsStaged;
            public byte[] Hash;
            public bool ReplacesExisting;
            public string OriginalBackup;
        }

        private sealed class Execution
        {
            private const string TemporaryName = ".ddalggak-apply-00000000000000000000000000000000.tmp";
            private readonly ApplyRequest input;
            private readonly Action<ApplyProgress> report;
            private readonly CancellationToken cancellation;
            private readonly ApplyResult result = new ApplyResult();
            private readonly List<InputRoot> roots = new List<InputRoot>();
            private readonly List<IncomingFile> files = new List<IncomingFile>();
            private readonly List<string> directories = new List<string>();
            private readonly Dictionary<string, bool> destinations = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
            private string[] configured;
            private string sourceRoot;
            private string backupRoot;
            private string target;
            private string canonicalTarget;
            private string logDirectory;
            private string canonicalLogDirectory;
            private string stageRoot;
            private string canonicalStage;
            private string completedBackup;
            private string canonicalBackup;
            private StreamWriter log;
            private ApplyPhase phase = ApplyPhase.Preparing;
            private string currentPath;
            private bool changedTarget;
            private bool applying;

            public Execution(ApplyRequest request, Action<ApplyProgress> progress, CancellationToken token)
            {
                if (request != null)
                    input = new ApplyRequest {
                        SourceRoot = request.SourceRoot, BackupRoot = request.BackupRoot, Name = request.Name,
                        Item = request.Item, StartedAt = request.StartedAt, LogDirectory = request.LogDirectory,
                        ConfiguredItems = request.ConfiguredItems == null ? null : (string[])request.ConfiguredItems.Clone(),
                        InputPaths = request.InputPaths == null ? null : (string[])request.InputPaths.Clone()
                    };
                report = progress;
                cancellation = token;
            }

            public ApplyResult Run()
            {
                result.StartedAt = input == null || input.StartedAt == default(DateTime) ? DateTime.Now : input.StartedAt;
                result.State = BackupState.Preparing;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    Prepare();
                    Notify(BackupState.Preparing, target, "적용할 파일과 경로를 확인했습니다.");
                    cancellation.ThrowIfCancellationRequested();
                    OpenLog();
                    StageInputs();
                    cancellation.ThrowIfCancellationRequested();

                    phase = ApplyPhase.BackingUp;
                    Notify(BackupState.Preparing, target, "선택한 항목 전체를 먼저 백업합니다. 백업이 완전히 성공해야 적용합니다.");
                    cancellation.ThrowIfCancellationRequested();
                    result.Backup = new BackupEngine().Run(new BackupRequest {
                        SourceRoot = sourceRoot, BackupRoot = backupRoot, Name = input.Name,
                        Items = new[] { input.Item }, ConfiguredItems = configured,
                        StartedAt = result.StartedAt, LogDirectory = logDirectory
                    }, delegate(BackupProgress value) {
                        if (report != null) report(new ApplyProgress { Phase = ApplyPhase.BackingUp, Progress = value });
                    }, cancellation);
                    if (result.Backup.State != BackupState.Complete || result.Backup.Items.Count != 1 || result.Backup.Items[0].State != BackupState.Complete)
                    {
                        result.State = result.Backup.State == BackupState.Cancelled ? BackupState.Cancelled : BackupState.Failed;
                        result.Application.State = BackupState.Pending;
                        result.Message = "사전 백업이 완전히 완료되지 않아 적용하지 않았습니다. " + result.Backup.Message;
                        Record("blocked", target, result.Message);
                        return result;
                    }
                    completedBackup = result.Backup.Items[0].Destination;
                    canonicalBackup = PathSafety.Canonicalize(completedBackup);
                    Record("info", completedBackup, "사전 백업 완료. 적용 전 백업 경로입니다.");
                    cancellation.ThrowIfCancellationRequested();

                    phase = ApplyPhase.Applying;
                    // This checkpoint precedes every target mutation, including creating directories or temporary files.
                    Notify(BackupState.Preparing, target, "사전 백업 완료. 적용 전 대상 변경 여부를 확인합니다.");
                    cancellation.ThrowIfCancellationRequested();
                    ValidateAfterBackup();
                    applying = true;
                    result.Application.State = BackupState.Copying;
                    ApplyFiles();
                    result.Application.State = BackupState.Complete;
                    result.State = BackupState.Complete;
                    result.Message = "선택한 항목을 먼저 백업한 후 적용을 완료했습니다. 기존의 다른 파일은 유지했습니다.";
                    Record("info", target, result.Message);
                }
                catch (OperationCanceledException)
                {
                    result.State = BackupState.Cancelled;
                    result.Application.State = applying ? BackupState.Cancelled : BackupState.Pending;
                    result.Message = changedTarget
                        ? "적용을 중단했습니다. 이미 적용한 내용은 유지되며 자동 복원하지 않습니다. 사전 백업 경로를 확인하세요."
                        : "파일 내용 적용 전에 작업을 취소했습니다. 기존 대상 파일의 내용은 교체하지 않았습니다.";
                    RecordBestEffort("cancelled", currentPath ?? target, result.Message);
                }
                catch (Exception error)
                {
                    result.State = changedTarget ? BackupState.Partial : BackupState.Failed;
                    result.Application.State = applying ? result.State : BackupState.Pending;
                    if (applying && !String.IsNullOrEmpty(currentPath))
                    {
                        result.Application.ProcessedFiles++;
                        result.Application.FailedFiles++;
                    }
                    result.Message = changedTarget
                        ? "적용 중 오류가 발생하여 중단했습니다. 이미 적용한 내용은 유지되며 자동 복원하지 않습니다: " + error.Message
                        : "기존 대상 파일의 내용은 교체하지 않았습니다: " + error.Message;
                    RecordBestEffort("failure", currentPath ?? target, result.Message);
                }
                finally
                {
                    CleanupStage();
                    if (result.Application.Warnings > 0)
                    {
                        result.Message += " 임시 파일 정리 경고가 있습니다. 남아 있는 파일·폴더의 경로는 상세 결과를 확인해 주세요.";
                        RecordBestEffort("warning", target, result.Message);
                    }
                    if (log != null)
                    {
                        try { log.Dispose(); }
                        catch (Exception error)
                        {
                            result.State = changedTarget ? BackupState.Partial : BackupState.Failed;
                            if (applying) result.Application.State = result.State;
                            result.Message = "적용 로그를 안전하게 닫지 못했습니다. 실제 적용 결과와 사전 백업을 확인하세요: " + error.Message;
                            RecordBestEffort("failure", result.LogPath, result.Message);
                        }
                    }
                    result.FinishedAt = DateTime.Now;
                    Notify(result.State, target, result.Message);
                }
                return result;
            }

            private void Prepare()
            {
                if (input == null) throw new ArgumentException("적용 설정이 없습니다.");
                configured = PathSafety.ValidateItemNames(input.ConfiguredItems);
                PathSafety.ValidateItemName(input.Item);
                bool found = false;
                foreach (string item in configured) if (String.Equals(item, input.Item, StringComparison.OrdinalIgnoreCase)) found = true;
                if (!found) throw new ArgumentException("현재 기기에 설정되지 않은 적용 항목입니다.");
                if (input.InputPaths == null || input.InputPaths.Length == 0) throw new ArgumentException("적용할 파일 또는 폴더를 추가해 주세요.");
                sourceRoot = PathSafety.Normalize(input.SourceRoot);
                backupRoot = PathSafety.Normalize(input.BackupRoot);
                PathSafety.FolderName(input.Name, result.StartedAt);
                PathSafety.ValidateRoots(sourceRoot, backupRoot, new[] { input.Item }, configured);
                target = Path.Combine(sourceRoot, input.Item);
                canonicalTarget = PathSafety.Canonicalize(target);
                result.TargetPath = target;
                result.Application.Item = input.Item;
                result.Application.Destination = target;
                logDirectory = String.IsNullOrEmpty(input.LogDirectory)
                    ? Path.Combine(SettingsStore.DataDirectory, "logs") : PathSafety.Normalize(input.LogDirectory);
                PathSafety.EnsureSafeWrite(logDirectory, sourceRoot, configured);
                canonicalLogDirectory = PathSafety.Canonicalize(logDirectory);
                string backupParent = PathSafety.Canonicalize(Path.Combine(backupRoot, input.Item));
                foreach (string value in input.InputPaths)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string path = PathSafety.Normalize(value);
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("적용 입력의 링크·정션은 허용하지 않습니다: " + path);
                    var root = new InputRoot { Path = path, Canonical = PathSafety.Canonicalize(path), Directory = (attributes & FileAttributes.Directory) != 0 };
                    if (PathSafety.IsWithin(root.Canonical, canonicalTarget) || PathSafety.IsWithin(canonicalTarget, root.Canonical))
                        throw new IOException("적용할 입력과 적용 대상이 겹칩니다: " + path);
                    if (PathSafety.IsWithin(canonicalLogDirectory, root.Canonical) || PathSafety.IsWithin(backupParent, root.Canonical))
                        throw new IOException("입력 안에 로그 또는 사전 백업을 만들 수 없습니다: " + path);
                    foreach (InputRoot prior in roots)
                        if (PathSafety.IsWithin(root.Canonical, prior.Canonical) || PathSafety.IsWithin(prior.Canonical, root.Canonical))
                            throw new ArgumentException("서로 겹치거나 중복된 입력을 함께 적용할 수 없습니다: " + path);
                    roots.Add(root);
                }
                foreach (InputRoot root in roots)
                {
                    if (root.Directory) ScanDirectory(root, root.Path, "");
                    else AddFile(root, root.Path, Path.GetFileName(root.Path));
                }
                if (files.Count == 0 && directories.Count == 0) throw new ArgumentException("드롭한 폴더 안에 적용할 파일이나 하위 폴더가 없습니다.");
                directories.Sort(delegate(string a, string b) {
                    int depth = a.Split('\\').Length.CompareTo(b.Split('\\').Length);
                    return depth != 0 ? depth : StringComparer.OrdinalIgnoreCase.Compare(a, b);
                });
                files.Sort(delegate(IncomingFile a, IncomingFile b) { return StringComparer.OrdinalIgnoreCase.Compare(a.Relative, b.Relative); });
                result.Application.TotalFiles = files.Count;
                foreach (string relative in directories) ValidatePlannedTarget(relative, true);
                foreach (IncomingFile file in files) ValidatePlannedTarget(file.Relative, false);
            }

            private void ScanDirectory(InputRoot root, string directory, string relative)
            {
                cancellation.ThrowIfCancellationRequested();
                GuardInput(root, directory);
                string[] children = Directory.GetFileSystemEntries(directory);
                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                foreach (string child in children)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(child);
                    PathSafety.ValidateItemName(name);
                    string childRelative = String.IsNullOrEmpty(relative) ? name : Path.Combine(relative, name);
                    FileAttributes attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("적용 입력의 링크·정션은 허용하지 않습니다: " + child);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        RegisterDestination(childRelative, true);
                        directories.Add(childRelative);
                        ScanDirectory(root, child, childRelative);
                    }
                    else AddFile(root, child, childRelative);
                }
            }

            private void AddFile(InputRoot root, string path, string relative)
            {
                GuardInput(root, path);
                RegisterDestination(relative, false);
                FileInfo info = new FileInfo(path);
                files.Add(new IncomingFile { Root = root, Source = path, Relative = relative, Length = info.Length, Modified = info.LastWriteTimeUtc });
            }

            private void RegisterDestination(string relative, bool directory)
            {
                foreach (string part in relative.Split('\\')) PathSafety.ValidateItemName(part);
                if (destinations.ContainsKey(relative)) throw new ArgumentException("같은 적용 위치가 중복되거나 파일·폴더가 충돌합니다: " + relative);
                string parent = Path.GetDirectoryName(relative);
                while (!String.IsNullOrEmpty(parent))
                {
                    bool parentDirectory;
                    if (destinations.TryGetValue(parent, out parentDirectory) && !parentDirectory) throw new ArgumentException("적용 파일과 폴더 경로가 충돌합니다: " + relative);
                    parent = Path.GetDirectoryName(parent);
                }
                if (!directory)
                    foreach (string existing in destinations.Keys)
                        if (existing.StartsWith(relative + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("적용 파일과 폴더 경로가 충돌합니다: " + relative);
                destinations.Add(relative, directory);
            }

            private void ValidatePlannedTarget(string relative, bool directory)
            {
                string destination = Path.Combine(target, relative);
                GuardTarget(destination);
                PathSafety.ValidateDestinationLength(destination);
                if (!directory) PathSafety.ValidateDestinationLength(Path.Combine(Path.GetDirectoryName(destination), TemporaryName));
                FileAttributes attributes;
                if (TryAttributes(destination, out attributes) && (((attributes & FileAttributes.Directory) != 0) != directory))
                    throw new IOException("적용 위치의 기존 파일·폴더 종류가 다릅니다: " + destination);
            }

            private void OpenLog()
            {
                GuardLog(logDirectory);
                Directory.CreateDirectory(logDirectory);
                string path = Path.Combine(logDirectory, "apply_" + result.StartedAt.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N") + ".jsonl");
                PathSafety.ValidateDestinationLength(path);
                GuardLog(path);
                FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                try { log = new StreamWriter(stream, new UTF8Encoding(false)); log.AutoFlush = true; }
                catch { stream.Dispose(); throw; }
                result.LogPath = path;
                Record("info", target, "적용 준비 시작. 드롭한 폴더는 최상위 폴더 이름을 제외한 내용물을 적용합니다.");
            }

            private void StageInputs()
            {
                stageRoot = Path.Combine(logDirectory, ".apply-stage-" + Guid.NewGuid().ToString("N"));
                PathSafety.ValidateDestinationLength(stageRoot);
                GuardLog(stageRoot);
                if (!PathSafety.TryCreateDirectoryExclusive(stageRoot)) throw new IOException("적용 준비 폴더를 확보하지 못했습니다.");
                canonicalStage = PathSafety.Canonicalize(stageRoot);
                long required = 0;
                foreach (IncomingFile file in files) required = checked(required + file.Length);
                long? available = PathSafety.AvailableBytes(stageRoot);
                if (available.HasValue && available.Value < required) throw new IOException("적용 파일을 준비할 저장 공간이 부족합니다.");
                for (int i = 0; i < files.Count; i++)
                {
                    IncomingFile file = files[i];
                    cancellation.ThrowIfCancellationRequested();
                    currentPath = file.Source;
                    Notify(BackupState.Preparing, file.Source, "드롭한 파일의 적용용 사본을 준비합니다.");
                    cancellation.ThrowIfCancellationRequested();
                    GuardInput(file.Root, file.Source);
                    file.Staged = Path.Combine(stageRoot, i.ToString("D6", CultureInfo.InvariantCulture) + ".bin");
                    PathSafety.ValidateDestinationLength(file.Staged);
                    GuardStage(file.Staged);
                    using (FileStream source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (source.Length != file.Length || File.GetLastWriteTimeUtc(file.Source) != file.Modified)
                            throw new IOException("준비 중 입력 파일이 변경되었습니다. 다시 추가한 뒤 실행해 주세요: " + file.Source);
                        using (FileStream destination = new FileStream(file.Staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            file.OwnsStaged = true;
                            file.Hash = CopyAndHash(source, destination, file.Length);
                        }
                        if (source.Length != file.Length || File.GetLastWriteTimeUtc(file.Source) != file.Modified)
                            throw new IOException("복사 중 입력 파일이 변경되었습니다: " + file.Source);
                    }
                }
                currentPath = null;
                Record("info", stageRoot, "적용 파일 준비 완료. 파일 " + files.Count + "개.");
            }

            private void ValidateAfterBackup()
            {
                GuardTarget(target);
                GuardBackup(completedBackup);
                foreach (string relative in directories)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string destination = Path.Combine(target, relative);
                    string saved = Path.Combine(completedBackup, relative);
                    GuardTarget(destination);
                    GuardBackup(saved);
                    FileAttributes liveAttributes, savedAttributes;
                    bool liveExists = TryAttributes(destination, out liveAttributes), savedExists = TryAttributes(saved, out savedAttributes);
                    if (liveExists != savedExists || (liveExists && (((liveAttributes & FileAttributes.Directory) == 0) || ((savedAttributes & FileAttributes.Directory) == 0))))
                        throw new IOException("사전 백업 이후 대상 폴더가 변경되었습니다: " + destination);
                }
                foreach (IncomingFile file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    file.OriginalBackup = Path.Combine(completedBackup, file.Relative);
                    GuardBackup(file.OriginalBackup);
                    FileAttributes attributes;
                    file.ReplacesExisting = TryAttributes(file.OriginalBackup, out attributes);
                    if (file.ReplacesExisting && (attributes & FileAttributes.Directory) != 0) throw new IOException("사전 백업 위치가 파일이 아닙니다: " + file.OriginalBackup);
                    VerifyUnchanged(file);
                }
            }

            private void VerifyUnchanged(IncomingFile file)
            {
                string destination = Path.Combine(target, file.Relative);
                GuardTarget(destination);
                FileAttributes attributes;
                bool exists = TryAttributes(destination, out attributes);
                if (exists != file.ReplacesExisting || (exists && (attributes & FileAttributes.Directory) != 0))
                    throw new IOException("사전 백업 이후 대상 파일이 생성·삭제되거나 종류가 변경되었습니다: " + destination);
                if (exists)
                {
                    GuardBackup(file.OriginalBackup);
                    if (!EqualHash(HashFile(destination), HashFile(file.OriginalBackup)))
                        throw new IOException("사전 백업 이후 대상 파일 내용이 변경되었습니다: " + destination);
                }
            }

            private void ApplyFiles()
            {
                foreach (string relative in directories)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string destination = Path.Combine(target, relative);
                    GuardTarget(destination);
                    FileAttributes attributes;
                    if (TryAttributes(destination, out attributes))
                    {
                        if ((attributes & FileAttributes.Directory) == 0) throw new IOException("적용할 폴더 위치에 파일이 있습니다: " + destination);
                    }
                    else
                    {
                        Record("intent", destination, "적용할 하위 폴더 생성");
                        GuardTarget(destination);
                        if (!PathSafety.TryCreateDirectoryExclusive(destination)) throw new IOException("적용 중 같은 폴더 이름이 새로 생겼습니다: " + destination);
                        changedTarget = true;
                    }
                }
                foreach (IncomingFile file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string destination = Path.Combine(target, file.Relative);
                    currentPath = destination;
                    Notify(BackupState.Copying, destination, "사전 백업된 대상에 파일을 적용합니다.");
                    cancellation.ThrowIfCancellationRequested();
                    VerifyUnchanged(file);
                    ApplyFile(file, destination);
                    changedTarget = true;
                    result.Application.ProcessedFiles++;
                    result.Application.SucceededFiles++;
                    // Replacement already committed. A later log/progress error is an operation
                    // failure, not a second (failed) outcome for this successfully applied file.
                    currentPath = null;
                    Record("success", destination, file.ReplacesExisting ? "사전 백업 후 기존 파일 교체 완료" : "사전 백업 후 새 파일 추가 완료");
                    Notify(BackupState.Copying, destination, "파일 적용 완료");
                    cancellation.ThrowIfCancellationRequested();
                }
            }

            private void ApplyFile(IncomingFile file, string destination)
            {
                string temporary = Path.Combine(Path.GetDirectoryName(destination), ".ddalggak-apply-" + Guid.NewGuid().ToString("N") + ".tmp");
                bool ownsTemporary = false;
                try
                {
                    GuardStage(file.Staged);
                    GuardTarget(temporary);
                    PathSafety.ValidateDestinationLength(temporary);
                    using (FileStream source = new FileStream(file.Staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        ownsTemporary = true;
                        byte[] hash = CopyAndHash(source, output, file.Length);
                        if (!EqualHash(hash, file.Hash)) throw new IOException("준비한 적용 파일의 내용이 변경되었습니다.");
                    }
                    GuardTarget(temporary);
                    File.SetLastWriteTimeUtc(temporary, file.Modified);
                    cancellation.ThrowIfCancellationRequested();
                    VerifyUnchanged(file);
                    Record("intent", destination, file.ReplacesExisting ? "기존 파일을 검증된 임시 사본으로 교체합니다." : "검증된 임시 사본을 새 파일로 확정합니다.");
                    GuardTarget(destination);
                    GuardTarget(temporary);
                    cancellation.ThrowIfCancellationRequested();
                    if (file.ReplacesExisting) File.Replace(temporary, destination, null);
                    else File.Move(temporary, destination);
                    ownsTemporary = false;
                }
                finally
                {
                    if (ownsTemporary)
                    {
                        try { GuardTarget(temporary); File.Delete(temporary); }
                        catch (Exception error)
                        {
                            result.Application.Warnings++;
                            RecordBestEffort("warning", temporary, "이번 적용 작업의 임시 파일을 제거하지 못했습니다: " + error.Message);
                        }
                    }
                }
            }

            private byte[] CopyAndHash(Stream source, FileStream destination, long expectedLength)
            {
                long written = 0;
                byte[] buffer = new byte[128 * 1024];
                using (SHA256 hash = SHA256.Create())
                {
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int count = source.Read(buffer, 0, buffer.Length);
                        if (count == 0) break;
                        destination.Write(buffer, 0, count);
                        hash.TransformBlock(buffer, 0, count, buffer, 0);
                        written = checked(written + count);
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    destination.Flush(true);
                    if (written != expectedLength || destination.Length != expectedLength) throw new IOException("적용 사본의 파일 크기가 일치하지 않습니다.");
                    return hash.Hash;
                }
            }

            private byte[] HashFile(string path)
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (SHA256 hash = SHA256.Create())
                {
                    byte[] buffer = new byte[128 * 1024];
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int count = stream.Read(buffer, 0, buffer.Length);
                        if (count == 0) break;
                        hash.TransformBlock(buffer, 0, count, buffer, 0);
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    return hash.Hash;
                }
            }

            private static bool EqualHash(byte[] a, byte[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int index = 0; index < a.Length; index++) if (a[index] != b[index]) return false;
                return true;
            }

            private void GuardInput(InputRoot root, string path)
            {
                if (!String.Equals(PathSafety.Canonicalize(root.Path), root.Canonical, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("적용 입력의 실제 위치가 변경되었습니다: " + root.Path);
                PathSafety.EnsureOrdinarySource(path, root.Path);
                if (!PathSafety.IsWithin(PathSafety.Canonicalize(path), root.Canonical)) throw new IOException("적용 입력 밖의 경로입니다: " + path);
            }

            private void GuardTarget(string path)
            {
                if (!PathSafety.IsWithin(path, target)) throw new IOException("선택한 적용 항목 밖에 쓰려는 작업을 차단했습니다: " + path);
                if (!String.Equals(PathSafety.Canonicalize(target), canonicalTarget, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("적용 대상의 실제 위치가 변경되었습니다: " + target);
                string actual = PathSafety.Canonicalize(path);
                if (!PathSafety.IsWithin(actual, canonicalTarget)) throw new IOException("실제 적용 위치가 선택 항목 밖입니다: " + path);
                GuardOrdinaryExisting(path, target);
            }

            private void GuardBackup(string path)
            {
                if (!String.Equals(PathSafety.Canonicalize(completedBackup), canonicalBackup, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("사전 백업의 실제 위치가 변경되었습니다.");
                if (!PathSafety.IsWithin(path, completedBackup) || !PathSafety.IsWithin(PathSafety.Canonicalize(path), canonicalBackup))
                    throw new IOException("사전 백업 밖의 경로입니다: " + path);
                GuardOrdinaryExisting(path, completedBackup);
            }

            private void GuardLog(string path)
            {
                PathSafety.EnsureSafeWrite(path, sourceRoot, configured);
                if (!PathSafety.IsWithin(PathSafety.Canonicalize(path), canonicalLogDirectory)) throw new IOException("로그 위치의 실제 경로가 변경되었습니다.");
                foreach (InputRoot root in roots)
                    if (PathSafety.IsWithin(PathSafety.Canonicalize(path), root.Canonical)) throw new IOException("적용 입력 안에 로그를 만들 수 없습니다.");
                GuardOrdinaryExisting(path, logDirectory);
            }

            private void GuardStage(string path)
            {
                GuardLog(path);
                if (!PathSafety.IsWithin(path, stageRoot) || !PathSafety.IsWithin(PathSafety.Canonicalize(path), canonicalStage))
                    throw new IOException("이번 적용 준비 폴더 밖의 경로입니다.");
                GuardOrdinaryExisting(path, stageRoot);
            }

            private static void GuardOrdinaryExisting(string path, string root)
            {
                string existing = path;
                FileAttributes attributes;
                while (!TryAttributes(existing, out attributes))
                {
                    if (String.Equals(existing, root, StringComparison.OrdinalIgnoreCase)) return;
                    string parent = Path.GetDirectoryName(existing);
                    if (String.IsNullOrEmpty(parent) || !PathSafety.IsWithin(parent, root)) return;
                    existing = parent;
                }
                PathSafety.EnsureOrdinarySource(existing, root);
            }

            private static bool TryAttributes(string path, out FileAttributes attributes)
            {
                try { attributes = File.GetAttributes(path); return true; }
                catch (FileNotFoundException) { attributes = 0; return false; }
                catch (DirectoryNotFoundException) { attributes = 0; return false; }
            }

            private void CleanupStage()
            {
                if (String.IsNullOrEmpty(stageRoot) || String.IsNullOrEmpty(canonicalStage)) return;
                try
                {
                    GuardStage(stageRoot);
                    foreach (IncomingFile file in files)
                    {
                        if (!file.OwnsStaged) continue;
                        GuardStage(file.Staged);
                        FileAttributes attributes;
                        if (!TryAttributes(file.Staged, out attributes)) { file.OwnsStaged = false; continue; }
                        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                            throw new IOException("적용 준비 폴더 안에 예상하지 않은 폴더나 링크가 있습니다.");
                        File.Delete(file.Staged);
                        file.OwnsStaged = false;
                    }
                    GuardStage(stageRoot);
                    Directory.Delete(stageRoot, false);
                }
                catch (Exception error)
                {
                    result.Application.Warnings++;
                    if (result.State == BackupState.Complete)
                    {
                        result.State = BackupState.Partial;
                        result.Application.State = BackupState.Partial;
                        result.Message = "파일 적용은 완료했지만 적용 준비 파일을 정리하지 못했습니다. 로그를 확인해 주세요.";
                    }
                    RecordBestEffort("warning", stageRoot, "적용 준비 파일을 정리하지 못했습니다: " + error.Message);
                }
            }

            private void Record(string kind, string path, string message)
            {
                DetailEntry entry = new DetailEntry { Time = DateTime.Now, Item = input == null ? null : input.Item, Kind = kind, Path = path, Message = message };
                result.Application.Details.Add(entry);
                if (log != null) log.WriteLine(serializer.Serialize(entry));
            }

            private void RecordBestEffort(string kind, string path, string message)
            {
                try { Record(kind, path, message); }
                catch { }
            }

            private void Notify(BackupState state, string path, string message)
            {
                if (report == null) return;
                report(new ApplyProgress {
                    Phase = phase,
                    Progress = new BackupProgress {
                        State = state, Item = input == null ? null : input.Item, CurrentFile = path, Message = message,
                        TotalFiles = result.Application.TotalFiles, ProcessedFiles = result.Application.ProcessedFiles,
                        SucceededFiles = result.Application.SucceededFiles, FailedFiles = result.Application.FailedFiles
                    }
                });
            }
        }
    }
}
