# Verification System

This document explains the verification mechanisms in ImagingUtility, including both full and quick verification modes, and the persistent acknowledgment system.

## Overview

ImagingUtility provides two verification modes to ensure the integrity of compressed disk images:

- **Full Verification**: Complete integrity check of all data chunks
- **Quick Verification**: Sampling-based verification for faster results

Both modes create persistent acknowledgment files to track verification status.

## Verification Modes

### Full Verification

Full verification performs a complete integrity check of the entire image by:

1. **Reading all chunks** in the image sequentially
2. **Decompressing each chunk** using the zstd algorithm
3. **Computing SHA-256 hash** of the decompressed data
4. **Comparing with stored hash** from the image index
5. **Validating chunk lengths** match expected uncompressed sizes

**Use cases:**
- Critical data verification
- Before restoring important backups
- Forensic analysis
- Compliance requirements

**Performance:** Slower but provides 100% confidence in data integrity.

### Quick Verification

Quick verification uses statistical sampling to verify image integrity:

1. **Structure validation** - Verifies image header and index integrity
2. **Sampling strategy** - Tests first chunk, last chunk, and every Nth chunk
3. **Hash verification** - Validates sampled chunks against stored hashes
4. **Length validation** - Ensures decompressed sizes match expectations

**Use cases:**
- Regular integrity checks
- Quick validation after transfers
- Automated monitoring
- Development testing

**Performance:** Much faster while providing high confidence in data integrity.

## Command Usage

### Individual Image Verification

```bash
# Full verification
ImagingUtility.exe verify --in "backup.skzimg"

# Quick verification  
ImagingUtility.exe verify --in "backup.skzimg" --quick

# Parallel processing
ImagingUtility.exe verify --in "backup.skzimg" --parallel 4
```

### Backup Set Verification

```bash
# Full backup set verification
ImagingUtility.exe verify-set --set-dir "C:\Backups\Disk0-Set"

# Quick backup set verification
ImagingUtility.exe verify-set --set-dir "C:\Backups\Disk0-Set" --quick
```

## Persistent Acknowledgment System

### Overview

ImagingUtility creates persistent acknowledgment files to track verification status. These files provide a simple way to determine if an image has been verified without re-running verification.

### Acknowledgment Files

#### For Individual Images

| Verification Result | Acknowledgment File | Description |
|-------------------|----------------------|-------------|
| **Full Success** | `imageName.VerifiedFull` | Image passed complete verification |
| **Quick Success** | `imageName.VerifiedQuick` | Image passed sampling verification |
| **Failed** | `imageName.BadImage` | Image failed verification (corrupted) |
| **Canceled** | *(no file)* | Verification was interrupted |

#### For Backup Sets

| Verification Result | Acknowledgment File | Description |
|-------------------|----------------------|-------------|
| **Full Success** | `setName.VerifiedFull` | All components passed complete verification |
| **Quick Success** | `setName.VerifiedQuick` | All components passed sampling verification |
| **Failed** | `setName.BadImage` | One or more components failed verification |
| **Canceled** | *(no file)* | Verification was interrupted |

### File Characteristics

- **Empty files**: All acknowledgment files are empty (0 bytes)
- **Simple existence check**: Use file existence to determine verification status
- **Persistent**: Files remain until manually deleted or overwritten
- **Location**: Created in the same directory as the image/set

### Example File Structure

```
F:\Backups\
├── disk0.skzimg
├── disk0.VerifiedFull          # Full verification passed
├── partition1.skzimg
├── partition1.VerifiedQuick   # Quick verification passed
├── partition2.skzimg
└── partition2.BadImage        # Verification failed
```

## Cancellation Handling

### Ctrl+C Behavior

When verification is canceled (Ctrl+C):

1. **Process terminates gracefully** with exit code 130
2. **No acknowledgment files created** - prevents false positives
3. **Progress information displayed** showing elapsed time and average speed
4. **Clean shutdown** of all verification threads

### Why No Files for Canceled Verifications?

Canceled verifications don't create acknowledgment files because:

- **Incomplete verification** - Can't determine if image is good or bad
- **False positives** - Would incorrectly mark incomplete verifications as "verified"
- **Data integrity** - Only completed verifications provide reliable status

## Implementation Details

### Verification Process

1. **Image Reader Initialization**
   - Validates image header and index
   - Loads chunk metadata into memory
   - Prepares decompression pipeline

2. **Parallel Processing**
   - Multiple worker threads for decompression
   - Configurable parallelism (default: CPU cores)
   - Ordered processing to maintain chunk sequence

3. **Hash Validation**
   - SHA-256 computation of decompressed data
   - Comparison with stored chunk hashes
   - Length validation for each chunk

4. **Progress Reporting**
   - Real-time progress updates
   - Speed and ETA calculations
   - Completion statistics

### Error Handling

- **Chunk corruption**: Individual chunk failures reported
- **Hash mismatches**: Detailed error messages with chunk numbers
- **Decompression errors**: Exception handling for malformed data
- **File I/O errors**: Graceful handling of disk issues

## Best Practices

### When to Use Full Verification

- **Critical data** - Important backups before restoration
- **Long-term storage** - Periodic integrity checks
- **Forensic analysis** - Legal or compliance requirements
- **After transfers** - When data has been moved between systems

### When to Use Quick Verification

- **Regular monitoring** - Automated integrity checks
- **Development testing** - Quick validation during development
- **Large images** - Faster verification of large backups
- **Routine maintenance** - Regular health checks

### Acknowledgment File Management

- **Check before restore** - Verify acknowledgment files exist
- **Clean up old files** - Remove outdated acknowledgments
- **Monitor for .BadImage files** - Investigate failed verifications
- **Backup acknowledgments** - Include in backup strategies

## Troubleshooting

### Common Issues

**No acknowledgment file created:**
- Verification was canceled (Ctrl+C)
- File system permissions issue
- Disk space insufficient

**Unexpected .BadImage file:**
- Image corruption detected
- Hash mismatch in chunks
- Decompression failure

**Verification hangs:**
- Large image processing
- Slow storage device
- Insufficient system resources

### Performance Optimization

- **Use --parallel N** for multi-core systems
- **Quick verification** for regular checks
- **SSD storage** for faster I/O
- **Sufficient RAM** for decompression buffers

## Exit Codes

| Code | Meaning | Acknowledgment File |
|------|---------|-------------------|
| 0 | Verification successful | `.VerifiedFull` or `.VerifiedQuick` |
| 4 | Verification failed | `.BadImage` |
| 130 | Verification canceled | *(none)* |

## Integration Examples

### Batch Verification Script

```batch
@echo off
for %%f in (*.skzimg) do (
    ImagingUtility.exe verify --in "%%f" --quick
    if exist "%%~nf.VerifiedQuick" (
        echo %%f: VERIFIED
    ) else if exist "%%~nf.BadImage" (
        echo %%f: FAILED
    ) else (
        echo %%f: NOT VERIFIED
    )
)
```

### PowerShell Integration

```powershell
$images = Get-ChildItem "*.skzimg"
foreach ($image in $images) {
    & ImagingUtility.exe verify --in $image.FullName --quick
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($image.Name)
    
    if (Test-Path "$baseName.VerifiedQuick") {
        Write-Host "$($image.Name): VERIFIED" -ForegroundColor Green
    } elseif (Test-Path "$baseName.BadImage") {
        Write-Host "$($image.Name): FAILED" -ForegroundColor Red
    } else {
        Write-Host "$($image.Name): NOT VERIFIED" -ForegroundColor Yellow
    }
}
```

This verification system provides robust integrity checking with persistent status tracking, making it easy to manage and monitor the health of your disk images.
