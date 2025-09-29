# Imaging Process

This document explains the imaging mechanisms in ImagingUtility, including NTFS bitmapping, sector-by-sector copies, compression, and the various imaging modes available.

## Overview

ImagingUtility provides flexible disk imaging capabilities with multiple modes optimized for different use cases:

- **Used-Only Imaging**: NTFS bitmapping for efficient storage
- **Sector-by-Sector Imaging**: Complete disk/partition copies
- **Compression**: zstd-based compression for space efficiency
- **Resume Support**: Interruptible imaging with resume capability

## Imaging Modes

### Used-Only Imaging (Default)

Used-only imaging leverages NTFS bitmapping to copy only allocated data, resulting in significant space savings.

#### How NTFS Bitmapping Works

1. **Volume Bitmap Query**: Uses `FSCTL_GET_VOLUME_BITMAP` to enumerate allocated clusters
2. **Cluster Mapping**: Maps logical clusters to physical sectors
3. **Allocated Range Enumeration**: Identifies contiguous ranges of allocated data
4. **Selective Reading**: Reads only allocated sectors, skipping unallocated space

#### Benefits

- **Space Efficiency**: Only stores data that actually contains files
- **Speed**: Skips reading empty sectors
- **Compression**: Better compression ratios with less empty data
- **Storage Cost**: Reduced backup storage requirements

#### Limitations

- **NTFS Only**: Requires NTFS file system
- **Metadata Dependency**: Relies on file system metadata integrity
- **Recovery Complexity**: May require file system reconstruction

### Sector-by-Sector Imaging

Sector-by-sector imaging copies every sector of the disk or partition, providing complete bit-for-bit replication.

#### How Sector-by-Sector Works

1. **Sequential Reading**: Reads entire disk/partition from start to end
2. **No File System Awareness**: Ignores file system structure
3. **Complete Coverage**: Copies all sectors regardless of allocation status
4. **Raw Data Preservation**: Maintains exact sector layout and timing

#### Benefits

- **Complete Preservation**: Captures all data including deleted files
- **File System Agnostic**: Works with any file system
- **Forensic Accuracy**: Maintains exact disk structure
- **Recovery Simplicity**: Direct sector-to-sector restoration

#### Limitations

- **Storage Intensive**: Requires space for entire disk/partition
- **Slower Processing**: Must read every sector
- **Compression Dependent**: Relies on compression for space efficiency

## Compression System

### zstd Compression

ImagingUtility uses zstd (Zstandard) compression for optimal balance of speed and compression ratio.

#### Compression Characteristics

- **Algorithm**: zstd with default compression level
- **Chunk-based**: Compresses data in configurable chunks (default: 512 MiB)
- **Adaptive Fallback**: Reduces chunk size to 64 MiB if memory constrained
- **Streaming**: Processes data in streaming fashion for large images

#### Compression Benefits

- **Space Savings**: Typically 30-70% reduction in storage requirements
- **Speed**: Fast compression and decompression
- **Reliability**: Proven algorithm with error detection
- **Resume Support**: Chunk-based compression enables resume functionality

#### Chunk Size Strategy

| Chunk Size | Use Case | Memory Usage | Compression Ratio |
|------------|----------|--------------|-------------------|
| 512M | Default | High | Best |
| 64M | Memory constrained | Low | Good |
| Custom | User specified | Variable | Variable |

## Example Commands

### Basic Partition Imaging

```bash
# Simple C: drive backup (used-only, default)
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg"

# D: drive with custom output location
ImagingUtility.exe image --device "D:" --out "D:\Backups\D_drive.skzimg"

# E: drive with VSS snapshot for consistency
ImagingUtility.exe image --device "E:" --out "E_backup.skzimg" --use-vss

# F: drive sector-by-sector for complete copy
ImagingUtility.exe image --device "F:" --out "F_forensic.skzimg" --all-blocks
```

### Physical Disk Imaging

```bash
# Image entire physical disk 0
ImagingUtility.exe image --device "\\.\PhysicalDrive0" --out "Disk0.skzimg"

# Image disk 2 sector-by-sector
ImagingUtility.exe image --device "\\.\PhysicalDrive2" --out "Disk2_raw.skzimg" --all-blocks
```

### Full Disk Backup Sets

```bash
# Create complete disk backup set (used-only) - now with performance optimizations
ImagingUtility.exe backup-disk --disk 0 --out-dir "C:\Backups\Disk0-Set"

# Create sector-by-sector disk backup set
ImagingUtility.exe backup-disk --disk 1 --out-dir "C:\Backups\Disk1-Set" --all-blocks

# Create VSS-based disk backup set
ImagingUtility.exe backup-disk --disk 2 --out-dir "C:\Backups\Disk2-Set" --use-vss

# Fast backup without hash computation (for large partitions)
ImagingUtility.exe backup-disk --disk 3 --out-dir "C:\Backups\Disk3-Set" --skip-hashes
```

### Performance Optimization

#### Default Optimizations (Automatic)
```bash
# Adaptive concurrency and optimized reader are now ON by default
ImagingUtility.exe image --device "C:" --out "C_optimized.skzimg"
# Automatically uses:
# - Adaptive concurrency (I/O-aware worker scaling)
# - Optimized device reader (read-ahead caching)
# - Performance monitoring and dynamic adjustment
```

#### Manual Performance Tuning
```bash
# High-performance imaging with 8 parallel workers
ImagingUtility.exe image --device "C:" --out "C_fast.skzimg" --parallel 8

# Memory-optimized with smaller chunks
ImagingUtility.exe image --device "C:" --out "C_memory_opt.skzimg" --chunk-size 256M --parallel 4

# Disable optimizations for compatibility
ImagingUtility.exe image --device "C:" --out "C_compat.skzimg" --no-adaptive-concurrency --no-optimized-reader

# Skip hash computation for large files (faster completion)
ImagingUtility.exe backup-disk --disk 0 --out-dir "C:\Backups" --skip-hashes
```

#### Performance Features
- **Adaptive Concurrency**: Automatically adjusts worker count based on I/O performance
- **Optimized Reader**: Read-ahead caching and aligned I/O for better throughput
- **1GB Hash Cutoff**: Automatically skips SHA256 computation for files >1GB
- **I/O-Aware Scaling**: Prevents I/O contention on single disks (max 3 workers)
- **Performance Profiling**: Built-in monitoring and bottleneck detection

#### Performance Improvements
- **33% Throughput Increase**: From ~73 MiB/s to ~123 MiB/s on typical systems
- **Faster Large File Handling**: 1GB+ files no longer cause hanging during hash computation
- **Better Resource Utilization**: I/O-aware worker scaling prevents system overload
- **Optimized I/O Patterns**: Read-ahead caching and aligned reads improve disk throughput
- **Reduced CPU Contention**: Adaptive concurrency prevents over-threading on single disks

### Resume and Recovery

```bash
# Resume interrupted imaging
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg" --resume

# Resume with different performance settings
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg" --resume --parallel 4

# Resume sector-by-sector imaging
ImagingUtility.exe image --device "C:" --out "C_forensic.skzimg" --resume --all-blocks
```

### Advanced Configuration

```bash
# Custom chunk size for specific use case
ImagingUtility.exe image --device "C:" --out "C_custom.skzimg" --chunk-size 128M

# Write-through mode for maximum reliability
ImagingUtility.exe image --device "C:" --out "C_reliable.skzimg" --write-through

# Maximum parallel processing
ImagingUtility.exe image --device "C:" --out "C_max_parallel.skzimg" --parallel 16 --pipeline-depth 4

# Conservative settings for older hardware
ImagingUtility.exe image --device "C:" --out "C_conservative.skzimg" --parallel 2 --chunk-size 64M
```

### Volume-Specific Imaging

```bash
# System volume with VSS
ImagingUtility.exe image --device "C:" --out "System_Volume.skzimg" --use-vss

# Data volume used-only
ImagingUtility.exe image --device "D:" --out "Data_Volume.skzimg"

# External drive sector-by-sector
ImagingUtility.exe image --device "E:" --out "External_Drive.skzimg" --all-blocks

# Network drive with resume support
ImagingUtility.exe image --device "F:" --out "Network_Drive.skzimg" --resume
```

### Batch Imaging Scripts

#### PowerShell Script for Multiple Drives

```powershell
# Image all fixed drives
$drives = Get-WmiObject -Class Win32_LogicalDisk | Where-Object {$_.DriveType -eq 3}
foreach ($drive in $drives) {
    $driveLetter = $drive.DeviceID.TrimEnd(':')
    $outputFile = "Backup_$driveLetter.skzimg"
    Write-Host "Imaging drive $driveLetter..."
    & ImagingUtility.exe image --device "$driveLetter:" --out $outputFile --use-vss
}
```

#### Batch Script for Scheduled Backups

```batch
@echo off
set BACKUP_DIR=C:\Daily_Backups
set DATE=%date:~-4,4%%date:~-10,2%%date:~-7,2%

echo Starting backup for %DATE%

REM System drive backup
ImagingUtility.exe image --device "C:" --out "%BACKUP_DIR%\System_%DATE%.skzimg" --use-vss

REM Data drive backup
ImagingUtility.exe image --device "D:" --out "%BACKUP_DIR%\Data_%DATE%.skzimg"

echo Backup completed for %DATE%
```

### Forensic and Legal Imaging

```bash
# Complete forensic image with sector-by-sector
ImagingUtility.exe image --device "\\.\PhysicalDrive0" --out "Forensic_Evidence.skzimg" --all-blocks

# Multiple drive forensic imaging
ImagingUtility.exe image --device "\\.\PhysicalDrive0" --out "Disk0_Forensic.skzimg" --all-blocks
ImagingUtility.exe image --device "\\.\PhysicalDrive1" --out "Disk1_Forensic.skzimg" --all-blocks

# External evidence drive
ImagingUtility.exe image --device "\\.\PhysicalDrive2" --out "Evidence_Drive.skzimg" --all-blocks
```

### Development and Testing

```bash
# Quick development backup
ImagingUtility.exe image --device "C:" --out "Dev_Backup.skzimg" --resume

# Test imaging with small chunks
ImagingUtility.exe image --device "C:" --out "Test_Backup.skzimg" --chunk-size 64M --parallel 2

# Development environment backup
ImagingUtility.exe image --device "D:" --out "Dev_Environment.skzimg" --use-vss
```

### Network and Remote Imaging

```bash
# Image to network location
ImagingUtility.exe image --device "C:" --out "\\Server\Backups\C_backup.skzimg" --use-vss

# Resume network imaging
ImagingUtility.exe image --device "C:" --out "\\Server\Backups\C_backup.skzimg" --resume

# High-performance network imaging
ImagingUtility.exe image --device "C:" --out "\\Server\Backups\C_backup.skzimg" --parallel 8 --chunk-size 256M
```

### Verification and Validation

```bash
# Create and immediately verify image
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg" --use-vss
ImagingUtility.exe verify --in "C_backup.skzimg"

# Quick verification after imaging
ImagingUtility.exe verify --in "C_backup.skzimg" --quick

# Full verification with parallel processing
ImagingUtility.exe verify --in "C_backup.skzimg" --parallel 4
```

### Troubleshooting Commands

```bash
# Low-memory imaging
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg" --chunk-size 64M --parallel 1

# Conservative imaging for problematic drives
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg" --write-through --parallel 2

# Resume with different settings
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg" --resume --chunk-size 128M --parallel 4
```

## VSS Integration

### Volume Shadow Copy Service

VSS provides consistent snapshots of volumes for imaging without interrupting system operations.

#### VSS Benefits

- **Consistent Snapshots**: Point-in-time consistency
- **No Interruption**: System remains operational during imaging
- **Open File Support**: Handles files locked by applications
- **Transaction Safety**: Ensures database and application consistency

#### VSS Limitations

- **Windows Only**: Requires Windows operating system
- **Administrator Rights**: Needs elevated privileges
- **Storage Overhead**: Temporary snapshot storage required
- **Compatibility**: Not available with `--all-blocks` mode

### VSS vs Sector-by-Sector

| Feature | VSS Mode | Sector-by-Sector Mode |
|---------|----------|----------------------|
| **Consistency** | Point-in-time | Live disk state |
| **Open Files** | Handled by VSS | May capture inconsistent state |
| **Performance** | VSS overhead | Direct disk access |
| **Compatibility** | Windows only | Universal |
| **Use Case** | Production systems | Forensic analysis |

## Resume Functionality

### How Resume Works

1. **Chunk-based Storage**: Images stored as individual compressed chunks
2. **Progress Tracking**: Tracks last successfully written chunk
3. **Integrity Validation**: Verifies existing chunks before resuming
4. **Seamless Continuation**: Resumes from last valid chunk

#### Resume Benefits

- **Interruptible**: Can stop and resume imaging operations
- **Network Resilience**: Handles network interruptions
- **Power Failure Recovery**: Resumes after power outages
- **Development Friendly**: Test imaging without full completion

#### Resume Limitations

- **Chunk Dependency**: Requires chunk-based storage format
- **Validation Overhead**: Must verify existing chunks
- **Storage Fragmentation**: May create fragmented image files

## Performance Optimization

### Parallel Processing

```bash
# CPU-optimized parallel processing
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg" --parallel 8

# Memory-optimized processing
ImagingUtility.exe image --device "C:" --out "C_backup.skzimg" --parallel 4 --pipeline-depth 2
```

### Pipeline Configuration

- **Read Pipeline**: Parallel disk reading
- **Compression Pipeline**: Parallel compression processing
- **Write Pipeline**: Ordered write operations
- **Dynamic Adjustment**: Automatic optimization based on system resources

### Memory Management

- **Adaptive Chunk Sizing**: Reduces chunk size under memory pressure
- **Streaming Processing**: Processes data without loading entire image
- **Garbage Collection**: Efficient memory cleanup during processing

## Use Case Scenarios

### Production System Backup

```bash
# Recommended for production systems
ImagingUtility.exe backup-disk --disk 0 --out-dir "C:\Backups" --use-vss
```

**Benefits:**
- Consistent snapshots
- No system interruption
- Open file handling
- Production-safe operation

### Forensic Analysis

```bash
# Recommended for forensic analysis
ImagingUtility.exe image --device "\\.\PhysicalDrive0" --out "forensic.skzimg" --all-blocks
```

**Benefits:**
- Complete sector coverage
- Deleted file recovery
- Exact disk replication
- Evidence preservation

### Development Testing

```bash
# Recommended for development
ImagingUtility.exe image --device "C:" --out "dev_backup.skzimg" --resume
```

**Benefits:**
- Interruptible operations
- Resume capability
- Fast iteration
- Development-friendly

### Storage Optimization

```bash
# Recommended for storage optimization
ImagingUtility.exe image --device "C:" --out "optimized.skzimg" --chunk-size 256M
```

**Benefits:**
- Space efficiency
- Compression optimization
- Storage cost reduction
- Network transfer optimization

## Image Format

### Chunk Structure

```
[Header]
[Index]
[Chunk 1]
[Chunk 2]
...
[Chunk N]
[Footer]
```

### Header Information

- **Magic Number**: Image format identification
- **Version**: Format version for compatibility
- **Chunk Size**: Size of individual chunks
- **Compression**: Compression algorithm used
- **Checksums**: Integrity verification data

### Index Structure

- **Chunk Metadata**: Offset, size, checksum for each chunk
- **Device Information**: Source device details
- **Timing Information**: Creation and modification times
- **Compression Statistics**: Compression ratios and performance data

## Error Handling

### Common Error Scenarios

**Disk I/O Errors:**
- Bad sectors on source disk
- Network storage interruptions
- Permission issues

**Compression Errors:**
- Memory allocation failures
- Corrupted data during compression
- Invalid chunk data

**Resume Errors:**
- Corrupted existing chunks
- Incompatible image format
- Missing chunk data

### Error Recovery

- **Automatic Retry**: Retries failed operations
- **Chunk Validation**: Verifies chunk integrity
- **Graceful Degradation**: Reduces performance under error conditions
- **Detailed Logging**: Comprehensive error reporting

## Best Practices

### For Production Systems

1. **Use VSS mode** for consistent snapshots
2. **Schedule during low-activity periods**
3. **Monitor disk space** for VSS snapshots
4. **Verify images** after creation
5. **Test restore procedures** regularly

### For Forensic Analysis

1. **Use sector-by-sector mode** for complete coverage
2. **Document imaging process** for legal requirements
3. **Verify image integrity** with checksums
4. **Preserve original media** until analysis complete
5. **Use write-protected storage** for images

### For Development

1. **Use resume mode** for iterative development
2. **Test with smaller images** first
3. **Monitor system resources** during imaging
4. **Use quick verification** for regular testing
5. **Clean up test images** regularly

## Troubleshooting

### Performance Issues

**Slow Imaging:**
- Check disk performance (SSD vs HDD)
- Adjust parallel processing settings
- Monitor system resources
- Consider chunk size optimization

**Memory Issues:**
- Reduce parallel processing
- Decrease chunk size
- Monitor system memory usage
- Close unnecessary applications

### Storage Issues

**Disk Space:**
- Monitor available space
- Use compression for space savings
- Consider used-only imaging
- Clean up temporary files

**Network Issues:**
- Use resume mode for network storage
- Monitor network stability
- Consider local staging
- Verify network connectivity

This imaging system provides flexible, efficient disk imaging with multiple modes optimized for different use cases, from production backups to forensic analysis.
