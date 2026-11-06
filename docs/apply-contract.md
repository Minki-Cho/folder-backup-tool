# Backup-before-apply contract (1.2.0)

The user explicitly requested an overwrite workflow in addition to the existing backup workflow. Apply accepts exactly one configured target item. Backup mode retains its existing multi-item behavior.

## Input mapping

- A dropped file maps to `<source-root>/<selected-item>/<file-name>`.
- A dropped folder contributes its contents, without the dropped folder's own name. Its internal directory structure and empty subdirectories are preserved.
- Multiple inputs may be combined only when their relative destinations do not conflict. Reject ambiguous duplicate files and file/directory conflicts before applying anything.
- Files absent from the dropped inputs remain in the selected item. No mirror deletion or automatic backup cleanup is performed.

## Execution boundary

Keep `BackupEngine` and its read-only source contract unchanged. `ApplyEngine` owns the new, explicitly requested writes into the selected item.

1. Snapshot the request and validate the target, inputs, relative mappings, path lengths and reparse/overlap conditions.
2. Prepare an independent staged copy of the input data outside the protected item trees.
3. Back up the entire selected item using `BackupEngine`.
4. Require a completely successful backup before starting target writes. Any backup failure, exclusion, warning, cancellation or log error blocks application.
5. Verify overwrite candidates against their completed backup copies. Reject newly appeared or changed target files that were not safely captured.
6. Copy each staged file into a new temporary file beside its destination, verify it, and finalize it with `File.Replace` for an existing file or `File.Move` for a new file. Never delete an existing file before a replacement attempt.
7. Stop on the first application error or cancellation. Preserve the pre-apply backup and report exactly what was applied. This is not an atomic multi-file transaction and does not automatically roll back completed replacements.

Validate canonical target identity and existing path components before mutations. Links and junctions must not redirect writes outside the selected item. Logs and staged data must not be written into protected source items or modify dropped inputs. File operations must honor cancellation wherever the underlying system call permits it.

## UI and results

- A mode switch distinguishes backup from backup-then-apply.
- In apply mode, checking a target clears the previous target. The apply request has one `Item`, not an array of items.
- Display the folder-contents mapping and selected target before execution.
- Show preparation, pre-apply backup and application as separate phases. Freeze inputs while the operation runs; retain cancellation.
- Display backup location/status separately from application location/status. Backup success alone is never application success.
- Explain that equipment writes should be paused and that partial application is retained after a failure/cancellation.

## Implementation interfaces

`ApplyEngine.Run(ApplyRequest, Action<ApplyProgress>, CancellationToken)` returns `ApplyResult`.

`ApplyRequest`: `SourceRoot`, `BackupRoot`, `Name`, single `Item`, `ConfiguredItems`, `InputPaths`, `StartedAt`, optional fixture-only `LogDirectory`.

`ApplyResult`: overall `State`, pre-apply `Backup`, separate `Application` item result, `TargetPath`, `LogPath`, `Message`, `StartedAt`, `FinishedAt`.

`ApplyProgress`: `Phase` (`Preparing`, `BackingUp`, `Applying`) and a `BackupProgress` payload.

All tests and GUI smoke checks use local `tests/work` fixtures, never deployed equipment shares or production settings.
