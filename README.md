````markdown
ImagingUtility
===============

Prototype Windows disk/volume imaging utility (C#/.NET 8).

Goals
- Create sector-aware, chunked, zstd-compressed images of physical disks and volumes.
- Support VSS snapshots for consistent live-volume imaging (now implemented).
- Support raw sector-by-sector imaging for devices that cannot be snapshotted.
- Produce a documented open image format that supports resume and verification.

Current state
- CLI with `list`, `image`, `verify`, `export-raw`, `export-vhd`, `backup-disk`, `restore-set`, `restore-physical`, and `dump-pt` commands.
- Physical drive enumeration and a RawDeviceReader that opens `\\.\\PhysicalDriveN` and reads sector-aware data.
- Chunked Zstd writer with header and per-chunk metadata (index, offset, sizes, SHA256) and compressed payload.
- Footer index for resume/verify. `--resume` can append to an existing image and continue from the last chunk.
- VSS support via WMI: uses `Win32_ShadowCopy` to create and delete snapshots for live-volume imaging.
- Multi-volume snapshot support: `image --volumes C:,D: --out-dir <dir> --use-vss` creates per-volume snapshots via a snapshot set and writes one image per volume.
- Full-disk backup sets: `backup-disk --disk N --out-dir <dir> --use-vss` creates a container directory with a JSON manifest, a small partition-table dump, compressed per-volume images for VSS-capable partitions, and raw dumps for non-VSS ones (EFI/MSR/Recovery). `restore-set` can reconstruct a single raw disk file from the set, or `restore-physical` writes back to a disk.
- Used-blocks-only imaging for NTFS volumes is the default (queries FSCTL_GET_VOLUME_BITMAP on VSS snapshots) to skip free clusters. Opt out with `--all-blocks`.
- Configurable chunk size via `--chunk-size` (supports suffixes K/M/G). Default chunk size is 64 MiB.
- Parallel pipelines for imaging and verification. Control worker count with `--parallel N` (default: max cores). Higher values increase CPU and memory usage.
- Fast verification mode: `verify --quick` performs a sampling-based integrity check (first, last, and periodic chunks) for much faster verification with reduced coverage.

Supported platforms
- Windows x64 and Windows ARM64 are supported. Build or publish with the appropriate RID.

Publish single-file binaries
```powershell
# x64
dotnet publish .\ImagingUtility.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
# ARM64
dotnet publish .\ImagingUtility.csproj -c Release -r win-arm64 -p:PublishSingleFile=true --self-contained true
```

Usage examples
- List drives:
  dotnet run -- list
- Image a physical drive (raw):
  dotnet run -- image --device \\.
PhysicalDrive1 --out C:\tmp\drive1.skzimg
- Image a live volume with VSS (WMI):
  dotnet run -- image --device C: --use-vss --out C:\tmp\C-drive.skzimg --parallel 6
- Image a live NTFS volume with used-blocks-only and larger chunks:
  dotnet run -- image --device C: --use-vss --used-only --chunk-size 16M --out C:\tmp\C-usedonly.skzimg --parallel 6
- Resume an interrupted image (appends more chunks and rewrites footer index):
  dotnet run -- image --device \\.
PhysicalDrive1 --out C:\tmp\drive1.skzimg --resume
- Multi-volume snapshots (writes one image per volume in the directory):
  dotnet run -- image --volumes C:,D: --out-dir C:\images --use-vss --used-only --parallel 6
- Full-disk backup set (container + manifest + per-partition files):
  dotnet run -- backup-disk --disk 0 --out-dir C:\Backups\Disk0-Set --use-vss --parallel 6
- Verify an image (full):
  dotnet run -- verify --in C:\tmp\drive1.skzimg --parallel 6
- Quick verify (sampling):
  dotnet run -- verify --in C:\tmp\drive1.skzimg --quick --parallel 6
- Export an image to raw:
  dotnet run -- export-raw --in C:\tmp\drive1.skzimg --out C:\tmp\drive1.raw
- Restore from a backup set to a raw disk image:
  dotnet run -- restore-set --set-dir C:\Backups\Disk0-Set --out-raw C:\Backups\Disk0-restored.raw
- Restore a backup set directly to a physical disk (DESTRUCTIVE):
  dotnet run -- restore-physical --set-dir C:\Backups\Disk0-Set --disk 1

```powershell
cd C:\Users\steeb\imaging-utility
dotnet restore
dotnet build -c Release
```

Usage examples
- List drives:
  dotnet run -- list
  dotnet run -- image --device \\.\\PhysicalDrive1 --out C:\tmp\drive1.skzimg
 Image a physical drive (raw):
  dotnet run -- image --device \\.\\PhysicalDrive1 --out C:\tmp\drive1.skzimg
  dotnet run -- image --device C: --use-vss --out C:\tmp\C-drive.skzimg --parallel 6
 Image a live volume with VSS (WMI):
  dotnet run -- image --device C: --use-vss --out C:\tmp\C-drive.skzimg --parallel 6
  dotnet run -- image --device C: --use-vss --used-only --chunk-size 16M --out C:\tmp\C-usedonly.skzimg --parallel 6
 Image a live NTFS volume with used-blocks-only and larger chunks:
  dotnet run -- image --device C: --use-vss --used-only --chunk-size 16M --out C:\tmp\C-usedonly.skzimg --parallel 6
  dotnet run -- image --device \\.\\PhysicalDrive1 --out C:\tmp\drive1.skzimg --resume
 Resume an interrupted image (appends more chunks and rewrites footer index):
  dotnet run -- image --device \\.\\PhysicalDrive1 --out C:\tmp\drive1.skzimg --resume
  dotnet run -- image --volumes C:,D: --out-dir C:\images --use-vss --used-only --parallel 6
 Verify an image (full):
  dotnet run -- verify --in C:\tmp\drive1.skzimg --parallel 6
  dotnet run -- backup-disk --disk 0 --out-dir C:\Backups\Disk0-Set --use-vss
 Quick verify (sampling):
  dotnet run -- verify --in C:\tmp\drive1.skzimg --quick --parallel 6
  dotnet run -- restore-set --set-dir C:\Backups\Disk0-Set --out-raw C:\Backups\Disk0-restored.raw
 To capture a consistent image of your current C: volume using VSS:
  dotnet run -- image --device C: --use-vss --used-only --chunk-size 16M --parallel 6 --out C:\tmp\system-C.skzimg
  dotnet run -- restore-physical --set-dir C:\Backups\Disk0-Set --disk 1
 Verify
  dotnet run -- verify --in C:\tmp\system-C.skzimg --parallel 6
  dotnet run -- verify --in C:\tmp\drive1.skzimg --parallel 6
 or quick verification (sampling)
  dotnet run -- verify --in C:\tmp\system-C.skzimg --quick --parallel 6
- Quick verify (sampling):
  dotnet run -- verify --in C:\tmp\drive1.skzimg --quick --parallel 6

System disk imaging (live OS)
- To capture a consistent image of your current C: volume using VSS:

```powershell
# Admin PowerShell
C:\Users\steeb\imaging-utility\bin\Release\net8.0\ImagingUtility.exe `
  image --device C: `
  --use-vss `
  --used-only `
  --chunk-size 16M `
  --parallel 6 `
  --out C:\tmp\system-C.skzimg

# Verify
C:\Users\steeb\imaging-utility\bin\Release\net8.0\ImagingUtility.exe `
  verify --in C:\tmp\system-C.skzimg --parallel 6
# or quick verification (sampling)
C:\Users\steeb\imaging-utility\bin\Release\net8.0\ImagingUtility.exe `
  verify --in C:\tmp\system-C.skzimg --quick --parallel 6
```

Future prospects
- Direct mount of compressed images via a proxy target compatible with Arsenal Image Mounter (DevIo/Proxy).
- Self-contained single-exe publish and signed binaries.
````
