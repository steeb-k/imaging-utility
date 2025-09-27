````markdown
ImagingUtility
===============

Prototype Windows disk/volume imaging utility (C# / .NET 8) with VSS snapshots, compressed chunked format, resume, verification, multi-volume imaging, and a simple GUI.

Key features
- Open, chunked image format (v2) with per-chunk SHA-256, index, and tail.
- Used-blocks-only (NTFS) imaging by default; full-block mode via --all-blocks.
- Compression: zstd (pure C#), default 512 MiB chunks (memory-aware fallback to 64 MiB), configurable via --chunk-size.
- Resume: --resume continues from last valid chunk and rewrites the footer.
- Verify: full verify, or fast sampling-based verify via verify --quick.
- Parallel pipeline with runtime control:
  - --parallel N (default: cores)
  - Live control: --parallel-control-file <path> and/or --parallel-control-pipe <name>
- Smoothing: configurable pipeline window via --pipeline-depth N (default chosen with parallel to target ~4 total workers).
- Write-through is OFF by default (uses OS cache); opt in with --write-through.
- Multi-volume snapshot imaging (one file per volume) and full-disk backup sets.
- GUI (PowerShell/WinForms) to pick a drive, output folder, used-only, VSS, parallelism, pipeline depth, write-through, and live control.

Defaults
- Mode: used-only (NTFS volumes)
- Chunk: 512 MiB preferred, automatic fallback to 64 MiB if memory is constrained
- Parallel: chosen dynamically to target ~4 total workers with pipeline depth
- Pipeline depth: chosen dynamically; bounded capacity = parallel × depth
- Write-through: OFF (use --write-through to enable)

Supported platforms
- Windows x64 and Windows ARM64

Build
```powershell
dotnet restore
dotnet build -c Release
```

Publish single-file binaries (optional)
```powershell
# x64
dotnet publish .\ImagingUtility.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
# ARM64
dotnet publish .\ImagingUtility.csproj -c Release -r win-arm64 -p:PublishSingleFile=true --self-contained true
```

CLI commands
- list – list physical drives
- image – image a device or volumes
- verify – verify chunk integrity (full or quick)
- export-raw / export-vhd – export to raw or fixed VHD
- export-range – read an arbitrary byte range directly from a compressed image
- serve-proxy – serve a read-only block device over TCP or a named pipe for mounting via DevIo/Proxy-compatible clients
- ntfs-extract – extract files/folders from an NTFS partition inside a compressed image (ACLs bypassed)
- ntfs-serve – browse/download NTFS contents over HTTP (ACLs bypassed)
- ntfs-webdav – serve NTFS over WebDAV (read-only); map a drive letter with the Windows WebDAV redirector
- backup-disk – create a full-disk backup set (manifest + per-partition files)
- restore-set / restore-physical – rehydrate a set to raw or to a disk
- dump-pt – dump first N bytes (MBR/GPT region)

Device path guidance
- For raw device access: use \\.-prefixed paths, e.g., \\.\PhysicalDrive1 or \\.\C:
- For a drive letter without VSS, \\.\D: is required. With VSS, you can pass D: (or D:\) and let the tool snapshot and redirect.

Examples
```powershell
# Admin PowerShell recommended

# Image D: with defaults (used-only, write-through OFF, dynamic parallel+pipelining)
.\bin\Release\net8.0\ImagingUtility.exe image --device D: --out 'F:\Backups\D.skzimg'

# Use VSS for a live system volume
.\bin\Release\net8.0\ImagingUtility.exe image --device C: --use-vss --out 'F:\Backups\C.skzimg'

# Increase smoothing window and adjust parallelism
.\bin\Release\net8.0\ImagingUtility.exe image --device D: --out 'F:\Backups\D.skzimg' --parallel 8 --pipeline-depth 3

# Opt in to write-through (reduces OS cache burstiness; may reduce peaks)
.\bin\Release\net8.0\ImagingUtility.exe image --device D: --out 'F:\Backups\D.skzimg' --write-through

# Multi-volume snapshot set
.\bin\Release\net8.0\ImagingUtility.exe image --volumes C:,D: --out-dir 'F:\Backups' --use-vss

# Full-disk backup set (manifest + images + raw dumps)
.\bin\Release\net8.0\ImagingUtility.exe backup-disk --disk 0 --out-dir 'F:\Backups\Disk0-Set'

# Verify (full)
.\bin\Release\net8.0\ImagingUtility.exe verify --in 'F:\Backups\D.skzimg'
# Quick verify (sampling)
.\bin\Release\net8.0\ImagingUtility.exe verify --in 'F:\Backups\D.skzimg' --quick

# Export an arbitrary range without full extraction
.\bin\Release\net8.0\ImagingUtility.exe export-range --in 'F:\Backups\Disk0-D.skzimg' --offset 1048576 --length 4096 --out '.\sector-2048.bin'

# Serve image for mounting via DevIo/Proxy (TCP)
.\bin\Release\net8.0\ImagingUtility.exe serve-proxy --in 'F:\Backups\D.skzimg' --host 127.0.0.1 --port 11459

# Serve a specific partition window (offset/length) and use a named pipe
.\bin\Release\net8.0\ImagingUtility.exe serve-proxy --in 'F:\Backups\Disk0-D.skzimg' --pipe SkzMount --offset 1048576 --length 536870912

# Browse NTFS contents (bypasses ACLs)
.\bin\Release\net8.0\ImagingUtility.exe ntfs-serve --in 'F:\Backups\Disk0-D.skzimg' --offset 1048576 --port 18080
# Then visit http://127.0.0.1:18080/ in a browser

# Extract a specific file or entire directory tree (bypasses ACLs)
.\bin\Release\net8.0\ImagingUtility.exe ntfs-extract --in 'F:\Backups\Disk0-D.skzimg' --offset 1048576 --path 'Users\SomeUser\AppData' --out-dir .\extract
# Or list all files
.\bin\Release\net8.0\ImagingUtility.exe ntfs-extract --in 'F:\Backups\Disk0-D.skzimg' --offset 1048576 --list-only --out-dir NUL

# Map as a drive letter via WebDAV (read-only; bypasses ACLs)
.\bin\Release\net8.0\ImagingUtility.exe ntfs-webdav --in 'F:\Backups\Disk0-D.skzimg' --offset 1048576 --port 18081
# In another admin PowerShell, ensure WebClient is running and map a drive:
Start-Service WebClient
net use Z: http://127.0.0.1:18081/ /persistent:no
```

Live parallelism control
- File: create a text file with an integer; the app polls it periodically
  - --parallel-control-file C:\temp\par.txt
- Named pipe: send a single integer line to the named pipe
  - --parallel-control-pipe MyPipeName

GUI (optional)
- Launch: `powershell -ExecutionPolicy Bypass -File .\tools\ImagingUtility.Gui.ps1`
- Features: select drive, output folder/file, VSS, resume, used-only, write-through (default OFF), parallelism slider, pipeline depth slider, live control (file + pipe), and an option to launch in a separate console for stability.

Notes
- Run as Administrator to access raw devices.
- Resume with used-only currently falls back to a full-range resume for correctness.
- Write-through reduces OS cache burstiness; disabling it can increase peaks but may introduce periodic stalls due to cache flushes.
- When mounting an image via DevIo/Proxy (AIM/ImDisk), Windows enforces NTFS ACLs from the captured volume. To access folders you normally can’t (even as admin), either:
  - Use the userspace tools here (ntfs-extract / ntfs-serve) which bypass ACLs by reading the filesystem structures directly, or
  - Use tools that leverage SeBackupPrivilege (e.g., robocopy /B) on the mounted volume.
- WebDAV mapping (ntfs-webdav) provides an Explorer-friendly, read-only view that bypasses ACLs. Requires the WebClient service. For large bulk copies, direct extraction may be faster.

Future prospects
- Optional: IMDPROXY compatibility for DevIo/Proxy clients if ever needed (de-prioritized since WebDAV meets current needs).
- Self-contained single-exe publish and signed binaries.
System disk imaging (live OS)

More: see docs/mounting.md for a step-by-step mounting and access guide.
