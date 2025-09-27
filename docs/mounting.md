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

You can map the userspace NTFS view to a drive letter using the built-in Windows WebDAV Redirector and the `ntfs-webdav` command.

1) Start the WebDAV server against your image/partition:

```powershell
ImagingUtility.exe ntfs-webdav --set-dir 'F:\Backups\Disk0-Set' --partition 3 --port 18081
# or, without a set:
ImagingUtility.exe ntfs-webdav --in 'F:\Backups\Partition3.skzimg' --offset 0 --port 18081
```

2) Ensure the WebClient service is running (required by the redirector):

```powershell
Start-Service WebClient
```

3) Map a drive letter to the WebDAV share:

```powershell
net use Z: http://127.0.0.1:18081/ /persistent:no
```

You should now see drive Z: in Explorer. This is read-only and bypasses NTFS ACLs (permissions are not enforced server-side).

Troubleshooting:
- “System error 67”: the WebClient service is not running.
- Slow transfers: the WebDAV redirector is chatty and may be slower than local mounts; for bulk extraction, use `ntfs-extract` or `export-vhd`.
- Authentication prompts: the server is anonymous; usually you can press Enter. If your environment enforces auth, configure WebDAV client policies as needed.

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
