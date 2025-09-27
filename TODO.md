# ImagingUtility — To‑Do List

Last updated: 2025-09-27

Legend: [P0] critical, [P1] important, [P2] nice-to-have

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
- [ ] [P0] End-to-end pipeline audit for async writes
  - Ensure both full-range and used-only paths use async ordered writes consistently; verify no blocking flushes on hot path.
  - Acceptance: Profiling shows steady throughput without periodic stalls across representative disks.
- [ ] [P1] Adaptive concurrency (compressor + pipeline)
  - Optionally auto-tune `--parallel` and `--pipeline-depth` at runtime based on observed I/O and CPU utilization; allow live control to adjust both.
  - Acceptance: With `--auto-tune`, the app converges within ~1–2 minutes to settings within ±10% of the best manual configuration.
- [ ] [P1] Read-ahead and larger aligned reads for raw devices
  - Evaluate overlapped/aligned reads and prefetch windows for better device throughput.
  - Acceptance: +5–15% throughput on HDD/USB targets in benchmarks.
- [ ] [P2] Rate limiting / smoothing mode
  - Optional `--max-throughput` or adaptive backpressure to avoid host saturation.

## UX: CLI, GUI, observability
- [ ] [P0] CLI help and defaults audit
  - Ensure help text and README consistently describe new defaults (512M chunk with fallback, write-through OFF, ~4 workers).
  - Acceptance: `image --help` and README align; tested examples run as described.
- [ ] [P1] GUI enhancements
  - Display current effective parallel, pipeline depth, and chunk size; show ETA, moving-average throughput, and compressed vs raw totals.
  - Add Pause/Resume; optional log-to-file.
  - Acceptance: New UI elements update in real time; pause resumes safely.
- [ ] [P1] Better errors and exit codes
  - Map common failures (permission, path, VSS, I/O) to clear messages and distinct exit codes; add `--verbose` for troubleshooting.

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
- [ ] [P1] Tuning guide
  - Practical guidance for chunk size, parallel, pipeline depth, write-through; example topologies and expected ranges.
- [ ] [P2] Troubleshooting guide
  - Common issues (VSS errors, access denied, path mapping), and remedies.

## Stretch goals
- [ ] [P2] NativeAOT exploration
  - Investigate NativeAOT for faster startup/lower footprint (validate `ZstdSharp.Port` compatibility).

