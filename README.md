# Ddalggak Backup

**English** | [한국어](README.ko.md)

A Windows utility for backing up selected model folders from two equipment profiles into ordinary folders named with a date and a label. It implements specification v0.2 and supports six configurable folder names per profile.

The application interface is in Korean. This guide includes the Korean button and status labels so you can find them in the app.

## Getting started

1. Extract the distribution package and run `DdalggakBackup.exe`. In this repository, the executable is in [dist](dist). Keep `DdalggakBackup.exe.config` beside it.
2. Select **1호기 / 2호기** (Equipment 1 / Equipment 2), then open **경로·항목 설정** (Path and item settings) to enter the actual source path, backup path, and item folder names. The public build starts with empty paths and restores your saved settings on subsequent launches.
3. Wait for the access checks, then select the items to back up. All checkboxes start unchecked each time you launch the app.
4. Enter a backup label, or leave it blank, and check the destination preview. A blank label uses `과검제어전`.
5. Click **선택 항목 백업** (Back up selected items). When it finishes, review each item's status, actual destination, and detailed results.

Requires Windows 10/11 and .NET Framework 4.8 or later. No installation or administrator privileges are required. For network paths, the Windows account running the application needs access to the shares. The application does not store account passwords or change source permissions.

## Path settings

The public build does not include real equipment addresses or share paths. Enter your own paths in **경로·항목 설정** on first launch. The following paths are fictional examples, not defaults.

| Profile | Example source | Example backup root |
| --- | --- | --- |
| Equipment 1 | `\\machine-1.example\share\source` | `\\machine-1.example\share\backup` |
| Equipment 2 | `\\machine-2.example\share\source` | `\\machine-2.example\share\backup` |

Each profile has its own paths and six item folder names. The backup root must already exist; the application does not create a missing root or substitute another location. It creates a missing selected item's folder only beneath an existing backup root.

The initial item names are `1_LEAD`, `2_CORNER`, `3_BODY`, `4_SIDE`, `5_TERR`, and `6_SHSD`. Each can be changed to match the corresponding folder on your equipment.

## Customizing item folder names

1. Open **경로·항목 설정** (Path and item settings).
2. Select the equipment profile's tab.
3. For example, change item 5 to `5_TERRACE` and item 6 to `6_SSIDE`, then click **저장** (Save). Enter the exact folder names found under the source root.
4. Check the updated names and access status on the main screen, then select the items and start the backup.

Names are saved independently for each profile and retained across launches. The application reads the configured source folders and backs them up under item folders with the same names. For example: `<backup-root>\5_TERRACE\260915_과검제어전\<source contents>`.

Enter one folder name per item, not a full path. Empty names, path separators, Windows-invalid characters, and duplicate names cannot be saved. Settings from version 1.0.x keep their paths and last backup label while automatically receiving the six default item names.

## Backup layout

Selecting `1_LEAD` and `3_BODY` with the label `과검제어전` on September 12, 2026 produces the following layout:

```text
backup\
  1_LEAD\
    260912_과검제어전\
      TOP.json
      Config\camera.ini
      EmptyFolder\
  3_BODY\
    260912_과검제어전\
      source contents...
```

If a name already exists, the application uses the next available suffix, `_01`, `_02`, and so on, independently for each item. Existing backups are never merged or overwritten. The preview is an estimate: if another run claims the same name, the actual destination suffix may differ.

An empty or whitespace-only label produces `YYMMDD_과검제어전`. Repeated runs on the same day can therefore produce `260912_과검제어전`, `260912_과검제어전_01`, and `260912_과검제어전_02`. Explicitly entering `과검제어전` uses the same naming sequence.

## Status and source protection

| Status shown in the app | Meaning |
| --- | --- |
| 완료 (Complete) | All copies and size checks finished with no failures, exclusions, or modification-time warnings. |
| 일부 실패 (Partial failure) | One or more file errors, skipped links or junctions, or modification-time preservation warnings occurred. |
| 실패 (Failed) | Preflight checks failed, or the operation could not continue because of an error such as insufficient space or a lost connection. |
| 취소 (Cancelled) | Cancellation was requested while some work remained unfinished. |
| 미실행 (Not started) | The item had not started when the operation was cancelled or stopped. |

While copying, the dated folder has the suffix `__미완료` (incomplete). The suffix is removed only when that item succeeds completely. It remains after partial failure, cancellation, or an unexpected application exit. Backups of other items that already completed are preserved.

Sources are accessed read-only. Hidden files and empty folders are included. Links and junctions are not followed and are recorded as exclusions in the detailed results. A backup destination inside any of the six source item folders is blocked, even if that item is not selected. If the actual path cannot be resolved well enough to verify safety, the operation is also blocked.

Each file is copied to a temporary file inside the new backup folder, checked for matching size, then finalized under its original name. If the source file's size or modification time changes during copying, the application retries once. Verification checks file size, not a cryptographic hash. File errors are recorded, and other files continue copying when possible.

Cancellation preserves files already copied and removes only temporary files created by the current copy operation. Temporary files that cannot be removed are listed in the detailed results. The application does not delete or automatically clean up source files or previous backups.

## Settings and logs

Settings are stored in `%LOCALAPPDATA%\DdalggakBackup\settings.xml`. Run logs are stored in the `logs` subfolder of the same application data directory. The application restores the last selected profile, each profile's paths and item folder names, and the last backup label. Dates use the PC's local date and are fixed for the run when the backup button is clicked.

If the settings file is corrupt, the application displays empty paths and prompts you to configure them again. Logs include run times, successful, failed, and excluded paths, error reasons, and actual backup destinations.

## Limitations and operational notes

- Files are read sequentially, so a backup is not a snapshot of every file at exactly the same instant. Pause writes in the source application if you need a consistent point-in-time file set.
- The file list is fixed during preparation. Files added afterward are included in the next run.
- For compatibility, destination paths, including temporary paths, are conservatively limited to 247 characters. If a path is too long, the application asks you to shorten the label or backup path; it does not rename them automatically.
- Cancellation is checked between file-copy blocks and during preparation. Windows network waits may delay cancellation, while the interface remains responsive.
- If free space cannot be determined, the application reports this and handles any write errors encountered during copying.

## Building and validation

The project uses the C# compiler included with Windows. No NuGet packages or .NET SDK download are required.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

Automated tests create their sources, backups, and logs under `tests\work` inside the project. They do not use live equipment shares. See the [validation report (Korean)](docs/검증결과.md) for detailed results. The distribution also includes `검증결과.md`.

Main source files:

- [MainForm.cs](src/MainForm.cs): user interface
- [BackupEngine.cs](src/BackupEngine.cs): backup operations
- [PathSafety.cs](src/PathSafety.cs): path protection
- [SettingsStore.cs](src/SettingsStore.cs): settings persistence
- [BackupEngineTests.cs](tests/BackupEngineTests.cs): regression tests
