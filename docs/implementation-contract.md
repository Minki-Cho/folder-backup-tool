# Implementation contract

For the separately authorized backup-before-apply workflow in 1.2.0, see [apply-contract.md](apply-contract.md). The read-only source rules below continue to describe `BackupEngine`; `ApplyEngine` performs the new, explicitly selected target writes after successful backup.

For editable per-equipment folder names introduced in 1.1.0, see [item-names-contract.md](item-names-contract.md). Its ConfiguredItems overloads and dynamic GUI names extend the initial contract below.

Windows Forms / C# 5 / .NET Framework 4.8. No NuGet or SDK needed; build with Windows Framework64 v4.0.30319 csc.exe. Namespace DdalggakBackup. Shared DTOs: src/Models.cs. Source files UTF-8.

Root owns Models.cs, PathSafety.cs, SettingsStore.cs, build/test scripts, documentation, integration.

Engine agent owns BackupEngine.cs. Public synchronous entry point:
`BackupResult Run(BackupRequest request, Action<BackupProgress> progress, CancellationToken cancellation)`.
Runs on GUI worker. Never call GUI API. Return results for handled failures, including preparation failures; items not started stay Pending. Progress is global. LogDirectory may override the default for local tests. Defaults `SettingsStore.DataDirectory/logs`. UTF-8 JSON-lines log via System.Web.Script.Serialization.JavaScriptSerializer, append + flush. Detail kind values: success, failure, excluded, warning, info. Log creation failure blocks backup. File operations remain exclusively under newly-created incomplete backup folders (except missing selected parent folders). Atomic exclusive directory creation helper and safe rename needed to avoid races.

Root-provided PathSafety API:
- `string ValidateName(string text)` => trimmed valid suffix; empty/null/whitespace returns `과검제어전` (user correction in 1.0.3), other invalid input throws ArgumentException with Korean explanation.
- `string FolderName(string text, DateTime start)` => YYMMDD_ + validated suffix.
- `string Normalize(string path)` => absolute normal path, requires absolute drive/UNC, reject invalid/device paths.
- `string Canonicalize(string path)` => normalized actual path resolving existing directories/links using Win32 handle, missing leaf resolved from nearest existing ancestor. Throws on inaccessible paths.
- `bool IsWithin(string candidate, string directory)` => case insensitive separator-boundary comparison, includes equality.
- `void ValidateRoots(string sourceRoot, string backupRoot, IEnumerable<string> selected)` => configured/existing/readable roots, valid selected item dirs, root/item parent destinations outside ALL SIX source item trees. Does not write. Selected item roots reparse points fail validation; nested reparse entries are engine exclusions.
- `void EnsureSafeWrite(string path, string sourceRoot)` => canonical destination must be outside all six canonical source items. Recheck before mutations.
- `void EnsureOrdinarySource(string path, string itemRoot)` => ensure path and each descendant ancestor within item root has no ReparsePoint, reject on reparse or escape. Read only.
- `void ValidateDestinationLength(string path)` => conservative Windows .NET Framework path bounds (directories <=247 chars, component <=255); reject before copy with reason. Engine must validate all planned directory and file target paths (including temp name margin); do not silently shorten.
- `SourceStatus CheckSource(string root, string item)` => Available + Korean status, no writes.
- `string PreviewPath(string backupRoot, string item, string folderName)` => first candidate with neither final nor incomplete path existing (UI should call off main thread); advisory only.
- `bool TryCreateDirectoryExclusive(string path)` => Win32 CreateDirectoryW: true created, false already exists, otherwise throws. Engine calls safety before helper.
- `long? AvailableBytes(string path)` => GetDiskFreeSpaceEx, null on inability.

Root-provided SettingsStore API:
- `static string DataDirectory { get; }` LocalAppData/DdalggakBackup
- `static AppSettings Defaults()` returns two specified machine profiles.
- `static AppSettings Load(out string warning)` missing => defaults, corrupt => warning + defaults (no silently replacing invalid configured paths).
- `static void Save(AppSettings settings)` atomic XML settings in user data.

GUI agent owns MainForm.cs + Program.cs. MainForm public parameterless constructor. Both machine profiles editable in path settings, last active restored, checks always false. Async source status and async duplicate preview to avoid frozen UNC checks. Freeze profile, paths, name, checks while running; cancellation remains enabled. Capture DateTime.Now in click handler. Details must expose failures/exclusions/warnings and actual result folders. Close while busy requests cancel and waits asynchronously (no abrupt success assumption). Do not auto-run backup. Optional `--smoke-test` only if it uses fixture settings and does not contact actual network.

Test agent owns tests/BackupEngineTests.cs console executable. Use temp directory under `tests/work` derived from exe/project path, never real profile network or real user appdata. No external packages. Main runs cases, prints PASS/FAIL and returns nonzero on failure. Compile scripts provided by root once all files exist. Tests should include readonly snapshots, layout, duplicates, empty folders, invalid inputs, overlap guard vs sibling branch, cancellation preserving completed items, reparse exclusion, locked file, low space/failure if injectable. Can add tests once engine implementation known; do not modify shared contracts without coordinating.
