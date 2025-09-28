## Quick orientation for AI coding agents

This repository contains a Windows-focused CLI (ImagingUtility) for block/volume/disk imaging and a separate WPF UI (in the sibling `skz-backup-restore` repo) that drives it. The goal of these notes is to capture the set of project-specific conventions and important files so an AI can make correct, low-risk changes quickly.

High level
- ImagingUtility (C#, .NET 8) is the core CLI that performs imaging, compression, hashing and verification. See `Program.cs` for the CLI command dispatch and supported verbs (image, verify, export-raw, export-vhd, export-range, ntfs-extract, ntfs-serve, backup-disk, restore-set, etc.).
- The WPF front-end (separate repo `skz-backup-restore`) invokes the CLI and parses its "plain" stdout lines for live progress. Do not change output formats without updating the UI parser in `SkzBackupRestore.Wpf`.

Key files to inspect before editing behavior
- `Program.cs` — CLI entrypoint, argument parsing, VSS handling, resume logic and high-level orchestration.
- `ImageWriter.cs` (ChunkedZstdWriter) — chunking, compression pipeline, async ordered writer, footer/index format.
- `ImageReader.cs` — image header/index parsing, VerifyAll implementation (parallel verify), ComputeResumePoint.
- `RawImageBlockDevice.cs` / `RandomAccessImage.cs` — random access reads into compressed images; used by export-range and ntfs tools.
- `Progress.cs` (ConsoleProgressScope) — controls "plain" vs interactive console lines. WPF uses plain output; `--plain` or environment variable `IMAGINGUTILITY_PLAIN=1` forces it.
- `VssUtils.cs`, `VssSetUtils.*` (used from `BackupSetBuilder.cs`) — snapshot creation and cleanup. VSS calls are Windows-only (WMI/Management APIs).
- `BackupSetBuilder.cs` — shows how the tool builds a multi-file backup set, how it decides which partitions to image vs raw-dump, and how it cleans up snapshots.

Important project-specific patterns (do not break these)
- Image format: header "IMG1", index "IDX1", and tail "TAIL" (see `ImageFormat` in `ImageWriter.cs`). Version 2 adds a DeviceLength field. Keep these constants if editing IO.
- Resume model: existing image footer/index is read (ImageReader.ComputeResumePoint), the writer truncates the footer/index and appends new chunks. Avoid changing footer layout without preserving resume semantics.
- Used-only imaging: default behavior is used-blocks-only for NTFS via `WriteAllocatedOnly(...)` (faster & smaller). CLI flags: default used-only; `--all-blocks` or `--no-used-only` opt out.
- Chunking and memory heuristics: preferred chunk size is 512 MiB with fallback to 64 MiB depending on available memory (see `ResolveDefaultChunkSizeSafe`). Changing default sizes affects memory/perf tradeoffs across the codebase.
- Parallel pipeline: read -> compress -> ordered write. Compression workers are adjustable at runtime (`AdjustableCompressorPool`); parallelism can be controlled via `--parallel`, or live with `--parallel-control-file` or `--parallel-control-pipe` (implementations poll/control the worker pool).
- Ordered write: although compression is parallel, chunks are written in ascending chunk index to preserve an index that maps device offsets to file payloads. Tests/consumers assume stable ordering.

Build / run / developer workflows
- Build CLI (from `imaging-utility` folder):
  - dotnet restore
  - dotnet build -c Release
- Publish single-file exes (used by the WPF app):
  - dotnet publish .\ImagingUtility.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
  - dotnet publish .\ImagingUtility.csproj -c Release -r win-arm64 -p:PublishSingleFile=true --self-contained true
- The WPF project expects published executables under `SkzBackupRestore.Wpf/ThirdParty/ImagingUtility/<rid>/`. Use the top-level script from the WPF repo to build+copy both RIDs: `skz-backup-restore/build-imaging-utility.ps1` (it discovers a sibling `imaging-utility` folder by default; pass `-ImagingUtilityPath` to override).

Runtime gotchas and conventions
- Always run as Administrator for raw device access and VSS snapshot creation. `Program.Main` checks elevation and exits if not elevated.
- Device path forms:
  - Raw physical: `\\.\PhysicalDriveN`
  - Drive-letter (non-VSS/raw): `\\.\D:`
  - Volume GUID: `\\?\Volume{...}` (use trailing `\` when requesting VSS)
  - For VSS, pass a volume path with trailing backslash and use `--use-vss` so the CLI snapshots and uses the shadow device.
- GUI integration: the WPF UI shells the CLI and parses plain-mode progress. If you change progress output layout or tokens, update `SkzBackupRestore.Wpf`'s parser.

Testing notes
- There are no unit tests in the repo. Safe edits should preserve on-disk binary formats and CLI output used by the UI. Manual smoke tests:
  - Build CLI and run `ImagingUtility.exe list` to confirm drive enumeration works.
  - Run non-destructive commands like `export-range` on a small test image.

When editing code, prefer small, focused changes:
- Preserve public image format constants in `ImageFormat` and index/footer layout unless intentionally bumping version and adding compatibility code.
- If changing CLI output consumed by the WPF app, update the UI's parser in `skz-backup-restore/SkzBackupRestore.Wpf`.
- Adjust chunking/parallel defaults only after profiling; these affect memory/perf tradeoffs across code paths (imaging, backup-set building, and restore).

Examples (powershell)
- Image a live system volume using VSS:
  .\bin\Release\net8.0\ImagingUtility.exe image --device C: --use-vss --out 'F:\Backups\C.skzimg'
- Quick verify:
  .\bin\Release\net8.0\ImagingUtility.exe verify --in 'F:\Backups\D.skzimg' --quick
- Live parallel control via file:
  .\bin\Release\net8.0\ImagingUtility.exe image --device D: --out 'D.skzimg' --parallel-control-file C:\temp\par.txt

If anything in this summary is unclear or you'd like more detail for a specific area (index/footer layout, resume behavior, or the WPF stdout parser), tell me which area to expand and I will iterate.
