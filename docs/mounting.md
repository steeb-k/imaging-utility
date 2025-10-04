# Mounting and Accessing Images

This guide summarizes ways to access data inside images created by ImagingUtility, including direct mounts, userspace browsing (bypassing ACLs), and conversion to mountable formats.

Status: experimental. We are actively adding DevIo/Proxy (IMDPROXY) compatibility for seamless mounting with clients like Arsenal Image Mounter (AIM) and ImDisk.

## 1) Direct mount via read-only proxy (experimental)

Launch a proxy that exposes a read-only block device backed by a compressed image. You can use a TCP port or a named pipe.

Examples:

```powershell
# TCP
ImagingUtility.exe serve-proxy --in 'F:\Backups\D.skzimg' --host 127.0.0.1 --port 11459

# Named pipe
ImagingUtility.exe serve-proxy --in 'F:\Backups\D.skzimg' --pipe SkzMount

# Serve a specific partition window
ImagingUtility.exe serve-proxy --in 'F:\Backups\Disk0.skzimg' --pipe SkzMount --offset 1048576 --length 536870912
```

Notes:
- Current wire protocol is minimal and read-only. IMDPROXY compatibility is in progress. Once available, you can attach the named pipe in AIM/ImDisk using their proxy mount UI.
- For backup sets, use the new `--partition N` flag to auto-select a partition (see below) instead of manual offsets.

## 2) Userspace NTFS browse/extract (bypasses ACLs)

If you need to access folders that deny access when mounted (due to NTFS ACLs and inherited permissions), use the userspace tools. They do not enforce NTFS ACLs and are read-only.

```powershell
# HTTP browser
ImagingUtility.exe ntfs-serve --in 'F:\Backups\Disk0-Partition3.skzimg' --offset 0 --port 18080
# Open http://127.0.0.1:18080/

# Extract a folder
ImagingUtility.exe ntfs-extract --in 'F:\Backups\Disk0-Partition3.skzimg' --offset 0 --path 'Users\SomeUser\AppData' --out-dir .\extract
```

Tip: With backup sets, you can use `--set-dir` and `--partition` to locate the right partition image automatically.

### Map to a drive letter without AIM (WebDAV)

You can map filesystem views to drive letters using the built-in Windows WebDAV Redirector. Support is available for NTFS, FAT32, and exFAT filesystems.

#### One-Command Solution (Recommended)

The `mount-webdav` command handles everything automatically - starting the WebDAV server and mapping the drive letter:

```powershell
# Automatic filesystem detection (recommended for v3 images)
ImagingUtility.exe mount-webdav --in 'F:\Backups\Partition3.skzimg' --offset 0 --drive Z
ImagingUtility.exe mount-webdav --in 'F:\Backups\FAT32-Partition.skzimg' --offset 0 --drive Y
ImagingUtility.exe mount-webdav --in 'F:\Backups\exFAT-Partition.skzimg' --offset 0 --drive X

# Using backup sets (auto-resolves partition and filesystem)
ImagingUtility.exe mount-webdav --set-dir 'F:\Backups\Disk0-Set' --partition 3 --drive Z

# Manual filesystem specification (still supported)
ImagingUtility.exe mount-webdav --in 'F:\Backups\Partition3.skzimg' --offset 0 --filesystem ntfs --drive Z
ImagingUtility.exe mount-webdav --in 'F:\Backups\FAT32-Partition.skzimg' --offset 0 --filesystem fat --drive Y
ImagingUtility.exe mount-webdav --in 'F:\Backups\exFAT-Partition.skzimg' --offset 0 --filesystem exfat --drive X
```

#### Manual Two-Step Process

If you prefer manual control, you can start the WebDAV server and map the drive separately:

1) Start the appropriate WebDAV server:

```powershell
# NTFS
ImagingUtility.exe ntfs-webdav --set-dir 'F:\Backups\Disk0-Set' --partition 3 --port 18081
# or, without a set:
ImagingUtility.exe ntfs-webdav --in 'F:\Backups\Partition3.skzimg' --offset 0 --port 18081

# FAT32
ImagingUtility.exe fat-webdav --in 'F:\Backups\FAT32-Partition.skzimg' --offset 0 --port 18082

# exFAT
ImagingUtility.exe exfat-webdav --in 'F:\Backups\exFAT-Partition.skzimg' --offset 0 --port 18083
```

2) Ensure the WebClient service is running (required by the redirector):

```powershell
Start-Service WebClient
```

3) Map a drive letter to the WebDAV share:

```powershell
net use Z: http://127.0.0.1:18081/ /persistent:no  # NTFS
net use Y: http://127.0.0.1:18082/ /persistent:no  # FAT32
net use X: http://127.0.0.1:18083/ /persistent:no  # exFAT
```

You should now see the mapped drives in Explorer. These are read-only and bypass filesystem ACLs (permissions are not enforced server-side).

#### Automatic Cleanup

The `mount-webdav` command includes automatic cleanup features:

- **Process monitoring**: Automatically unmounts the drive when the parent process terminates
- **GUI-friendly**: Works seamlessly with GUI applications - drive letters are automatically cleaned up when the application closes
- **Manual cleanup**: Press Ctrl+C to unmount and stop the WebDAV server
- **Multi-level support**: Handles complex process hierarchies (GUI → ImagingUtility → WebDAV server)
- **Automatic port detection**: If the default port is in use, automatically finds the next available port
- **Elevation separation**: WebDAV server runs as Administrator, drive mapping runs as current user for proper Windows Explorer visibility

#### Filesystem Support

| Filesystem | Command | Default Port | Features |
|------------|---------|--------------|----------|
| NTFS | `ntfs-webdav` / `mount-webdav --filesystem ntfs` | 18081 | Full ACL bypass, file permissions ignored |
| FAT32 | `fat-webdav` / `mount-webdav --filesystem fat` | 18082 | No permissions, all files accessible |
| exFAT | `exfat-webdav` / `mount-webdav --filesystem exfat` | 18083 | No permissions, large file support |

#### Automatic Filesystem Detection

The `mount-webdav` command can automatically detect the filesystem type using multiple methods:

1. **Backup Set Manifest**: When using `--set-dir` and `--partition`, reads filesystem from `backup.manifest.json`
2. **Image Metadata**: For v3 images, reads filesystem information stored in the image header
3. **Manual Override**: Still accepts `--filesystem` parameter for explicit specification

**Detection Priority:**
- Backup set manifest (highest priority)
- Image metadata (v3 images only)
- Manual `--filesystem` parameter
- Error if none available

**Example Output:**
```
Using filesystem from manifest: NTFS
Using filesystem from image metadata: FAT32
```

Troubleshooting:
- "System error 67": the WebClient service is not running.
- Slow transfers: the WebDAV redirector is chatty and may be slower than local mounts; for bulk extraction, use `ntfs-extract` or `export-vhd`.
- Authentication prompts: the server is anonymous; usually you can press Enter. If your environment enforces auth, configure WebDAV client policies as needed.
- "This tool must be run as Administrator": WebDAV servers require administrator privileges; use `mount-webdav` for automatic elevation handling.

## 3) Convert to VHD and mount with Windows (needs space)

For compatibility with built-in Windows mounting:

```powershell
ImagingUtility.exe export-vhd --in 'F:\Backups\D.skzimg' --out 'F:\Backups\D.vhd'
# Then right-click VHD and choose Mount (or use Disk Management)
```

## Partition selection made easy

When you created a backup set with `backup-disk`, a `backup.manifest.json` was saved with partition entries. You can now specify a partition by index or drive letter and we’ll resolve the correct image automatically.

Examples:

```powershell
# Serve the 3rd partition from a backup set (auto-selects the right .skzimg)
ImagingUtility.exe serve-proxy --set-dir 'F:\Backups\Disk0-Set' --partition 3 --pipe SkzMount

# Browse NTFS contents of the EFI partition by letter (if present in manifest)
ImagingUtility.exe ntfs-serve --set-dir 'F:\Backups\Disk0-Set' --partition E --port 18080

# Extract a protected folder from partition 2 without ACLs
ImagingUtility.exe ntfs-extract --set-dir 'F:\Backups\Disk0-Set' --partition 2 --path 'System Volume Information' --out-dir .\extract
```

Resolution rules:
- Provide `--set-dir` pointing to the backup set folder containing `backup.manifest.json`.
- `--partition` accepts an index (1-based) or a drive letter like `C` or `C:`.
- If `--set-dir` is omitted, the tool attempts to locate a manifest in the same directory as `--in`.
- We select the partition’s compressed image (`ImageFile` in the manifest). If it’s missing, the command will error and explain.

## Permissions: why ACLs block access and how to proceed

Mounting a volume through DevIo/Proxy makes Windows enforce the NTFS ACLs stored on the volume. That means some folders (e.g., other users’ profiles, System Volume Information) may be inaccessible by default.

Options:
- Use userspace tools (`ntfs-serve` or `ntfs-extract`) that bypass ACLs by reading filesystem structures directly. These are read-only and safe.
- If you prefer working on a mounted volume, use utilities that leverage backup privileges, for example:
  - `robocopy /B` to copy with Backup semantics
  - A process started with SeBackupPrivilege enabled

## Roadmap for direct mounting

- IMDPROXY (DevIo/Proxy) wire compatibility so you can attach our named pipe directly in AIM/ImDisk.
- Partition selection baked into `serve-proxy` for one-command mounts of any partition from a backup set.
- Optional metrics and logs when serving mounted volumes.
