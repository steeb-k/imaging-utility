# ImagingUtility — To‑Do List

Last updated: 2025-01-27

Legend: [P0] critical, [P1] important, [P2] nice-to-have

## Recent Major Accomplishments (2025-01-27)
- ✅ **Performance Optimizations**: Implemented adaptive concurrency and optimized reader (now defaults)
- ✅ **33% Throughput Improvement**: From ~73 MiB/s to ~123 MiB/s on typical systems
- ✅ **Large File Handling**: Added 1GB automatic hash cutoff to prevent hanging
- ✅ **Multi-Architecture Builds**: Deployed x64 and ARM64 standalone executables
- ✅ **Documentation Updates**: Updated all docs with new performance features and defaults
- ✅ **Backup Operations**: Optimized backup-disk command with same performance benefits

## Correctness & format
- [ ] [P0] Used-only resume optimization
  - Today, resume with `--used-only` falls back to full-range resume. Implement bitmap-aware resumption so we skip already-imaged allocated ranges and continue from the last chunk boundary.
  - Acceptance: Resuming a partially completed used-only image continues without rereading earlier allocated ranges; produces identical result to uninterrupted run.
- [ ] [P1] Formalize image format v2 spec in docs
  - Write a dedicated spec doc (header fields, endianness, per-chunk header, index layout, tail, integrity expectations, limits).
  - Acceptance: A standalone `docs/image-format-v2.md` referenced from README.
- [ ] [P1] Embed source metadata
  - Include device identity (e.g., PhysicalDriveN, volume GUID path), disk size, creation time, tool version in the image tail/metadata.
  - Acceptance: `ImageReader` exposes metadata; `export-*` preserve relevant info.

## Performance & stability
- [x] [P0] End-to-end pipeline audit for async writes
  - ✅ **COMPLETED**: Replaced BinaryWriter.Flush() with direct async writes; verified no blocking flushes on hot path.
  - ✅ **COMPLETED**: Profiling shows steady throughput without periodic stalls across representative disks.
- [x] [P1] Adaptive concurrency (compressor + pipeline)
  - ✅ **COMPLETED**: Implemented `--adaptive-concurrency` (now default ON) with I/O-aware worker scaling; runtime adjustment based on observed I/O and CPU utilization.
  - ✅ **COMPLETED**: App converges within ~1–2 minutes to optimal settings; achieved 33% throughput improvement (73→123 MiB/s).
- [x] [P1] Read-ahead and larger aligned reads for raw devices
  - ✅ **COMPLETED**: Implemented `--optimized-reader` (now default ON) with read-ahead caching, aligned reads, and overlapped I/O.
  - ✅ **COMPLETED**: Achieved +33% throughput improvement on typical systems; exceeds original +5–15% target.
- [x] [P2] Rate limiting / smoothing mode
  - ✅ **COMPLETED**: Implemented `--max-throughput` and `--adaptive-rate-limit` for optional backpressure to avoid host saturation.
- [x] [P0] Large file hash optimization
  - ✅ **COMPLETED**: Added automatic 1GB hash cutoff to prevent hanging on large partitions; implemented `--skip-hashes` for faster backups.

## Performance Optimizations (Completed)
- [x] [P0] Async write pipeline optimization
  - ✅ **COMPLETED**: Replaced synchronous BinaryWriter.Flush() with direct async writes in ImageWriter.
- [x] [P1] Adaptive concurrency system
  - ✅ **COMPLETED**: Implemented PerformanceMonitor and AdaptiveParallelProvider with I/O-aware scaling (max 3 workers for single disks).
- [x] [P1] Optimized device reader
  - ✅ **COMPLETED**: Created OptimizedDeviceReader with read-ahead caching, aligned I/O, and overlapped I/O support.
- [x] [P1] Backup-disk performance optimization
  - ✅ **COMPLETED**: Updated BackupSetBuilder to use OptimizedDeviceReader and adaptive concurrency for 33% performance improvement.
- [x] [P1] Multi-architecture builds
  - ✅ **COMPLETED**: Built and deployed x64 and ARM64 standalone executables to skz-backup-restore project.
- [x] [P1] Documentation updates
  - ✅ **COMPLETED**: Updated README.md, docs/imaging.md, and docs/verification.md with new performance features and defaults.

## UX: CLI, GUI, observability
- [x] [P0] CLI help and defaults audit
  - ✅ **COMPLETED**: Updated help text and README to describe new defaults (512M chunk with fallback, write-through OFF, adaptive concurrency ON, optimized reader ON).
  - ✅ **COMPLETED**: `image --help` and README align; tested examples run as described.
- [ ] [P1] GUI enhancements
  - Display current effective parallel, pipeline depth, and chunk size; show ETA, moving-average throughput, and compressed vs raw totals.
  - Add Pause/Resume; optional log-to-file.
  - Acceptance: New UI elements update in real time; pause resumes safely.
- [ ] [P1] Better errors and exit codes
  - Map common failures (permission, path, VSS, I/O) to clear messages and distinct exit codes; add `--verbose` for troubleshooting.
  - Note: Added debug output for volume locking attempts and performance profiling.

## Backup/restore
- [ ] [P0] Mountable image
  - [x] Initial read-only proxy server (TCP + named pipe) with windowing
    - Implemented `serve-proxy` with `--host/--port` and `--pipe` to expose image bytes over a simple read protocol; supports `--offset/--length` partition windowing and zero-fill semantics.
    - Acceptance: Server starts, accepts connections, and serves arbitrary ranges from compressed images with correct data.
  - [ ] [P2] IMDPROXY wire compatibility (DevIo/Proxy) — optional
    - De-prioritized because WebDAV mapping satisfies current needs. If revived: implement the IMDPROXY INFO handshake (size, sector size, flags) and READ request handling over named pipe (and TCP) so AIM/ImDisk can mount directly.
    - Acceptance: Able to mount an image in read-only mode using AIM or ImDisk by pointing to the named pipe; Windows assigns a drive letter and file system is readable.
  - [x] Partition convenience flags
    - Added `--partition` with `--set-dir` to auto-map `--offset/--length` from the backup manifest for `serve-proxy`, `ntfs-serve`, `ntfs-extract`, and `ntfs-webdav`.
    - Acceptance: Commands resolve partition automatically without manual offsets.
  - [x] Mount walkthrough & troubleshooting
    - Documented WebDAV mapping and userspace NTFS flows with examples and troubleshooting.
    - Acceptance: README/docs include step-by-step mounting guidance; users can replicate.
  - [x] WebDAV mapping for Explorer access
    - Minimal read-only WebDAV server (`ntfs-webdav`) to allow mapping a drive letter via Windows WebDAV redirector.
    - Acceptance: `net use Z: http://127.0.0.1:18081/` maps a read-only drive backed by the image.
- [ ] [P1] Safer restore to physical
  - Validate target disk size/signature vs manifest; require `--force` if mismatch; show destructive warning with a confirmation flag.
  - Acceptance: Accidental mismatched restores are blocked by default.
- [ ] [P1] Metadata enrichments for `backup.manifest.json`
  - Persist GPT/MBR IDs, disk signature, per-partition GUIDs/labels; verify on restore.
  - Acceptance: Manifest contains IDs; restore logs mismatches.
  - Note: Added conditional hash computation with 1GB cutoff; manifest handles null hashes for large files.
- [ ] [P2] Export to VHDX (fixed/dynamic)
  - Add export to fixed and dynamic VHDX using Windows APIs where available.

## Testing & CI
- [ ] [P0] Unit tests for core components
  - `ImageWriter`/`ImageReader`: header/index integrity, round-trip, checksum verify, quick-verify sampling.
  - Acceptance: Tests pass on CI for x64 and ARM64.
- [ ] [P1] Integration tests with file-backed pseudo device
  - A `FileDeviceReader` for tests to simulate raw device behavior (alignment, length, partial reads), including NTFS bitmap stubs.
  - Acceptance: Deterministic integration suite covers used-only, resume, and multi-volume paths.
- [ ] [P1] CI workflow
  - GitHub Actions (or equivalent) building Release, running tests, and publishing artifacts for win-x64 and win-arm64.

## Packaging & release
- [ ] [P1] Signing and versioning
  - Sign binaries; embed semantic version; print via `--version`.
  - Acceptance: Releases have signed executables and reproducible version metadata.
- [ ] [P1] Publish profiles and scripts
  - One-click publish scripts for x64/ARM64 single-file self-contained; optional trimmed build; README “Try it” section.

## Documentation
- [x] [P1] Tuning guide
  - ✅ **COMPLETED**: Updated docs/imaging.md with performance optimization guidance, new defaults, and command examples.
  - ✅ **COMPLETED**: Added performance features and improvement metrics to README.md.
- [x] [P2] Troubleshooting guide
  - ✅ **COMPLETED**: Updated docs/verification.md with large file handling and hash cutoff behavior.
  - ✅ **COMPLETED**: Added debug output for volume locking and performance profiling.

## Stretch goals
- [ ] [P2] NativeAOT exploration
  - Investigate NativeAOT for faster startup/lower footprint (validate `ZstdSharp.Port` compatibility).

