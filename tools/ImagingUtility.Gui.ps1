# Requires: Windows PowerShell 5.1+, Windows Forms, run as Administrator for ImagingUtility
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Test-IsAdmin {
  try {
    $wi = [Security.Principal.WindowsIdentity]::GetCurrent()
    $wp = [Security.Principal.WindowsPrincipal]::new($wi)
    return $wp.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  } catch { return $false }
}

function Get-ImagingUtilityPath {
  $root = Split-Path -Parent $PSCommandPath
  $repo = Split-Path -Parent $root
  $candidates = @(
    (Join-Path $repo 'ImagingUtility.exe'),
    (Join-Path $repo 'bin/Release/net8.0/win-x64/ImagingUtility.exe'),
    (Join-Path $repo 'bin/Release/net8.0/win-arm64/ImagingUtility.exe'),
    (Join-Path $repo 'bin/Release/net8.0/ImagingUtility.exe')
  )
  foreach ($c in $candidates) { if (Test-Path $c) { return (Resolve-Path $c).Path } }
  $dll = Join-Path $repo 'bin/Release/net8.0/ImagingUtility.dll'
  if (Test-Path $dll) { return (Resolve-Path $dll).Path }
  return $null
}

function New-Label($text, $x, $y) {
  $lbl = New-Object System.Windows.Forms.Label
  $lbl.Text = $text
  $lbl.Location = New-Object System.Drawing.Point($x, $y)
  $lbl.AutoSize = $true
  return $lbl
}

function New-Button($text, $x, $y, $w=100, $h=28) {
  $btn = New-Object System.Windows.Forms.Button
  $btn.Text = $text
  $btn.Location = New-Object System.Drawing.Point($x, $y)
  $btn.Size = New-Object System.Drawing.Size($w, $h)
  return $btn
}

function New-TextBox($x, $y, $w=360) {
  $tb = New-Object System.Windows.Forms.TextBox
  $tb.Location = New-Object System.Drawing.Point($x, $y)
  $tb.Size = New-Object System.Drawing.Size($w, 23)
  return $tb
}

function New-Checkbox($text, $x, $y, $checked=$false) {
  $cb = New-Object System.Windows.Forms.CheckBox
  $cb.Text = $text
  $cb.Location = New-Object System.Drawing.Point($x, $y)
  $cb.AutoSize = $true
  $cb.Checked = [bool]$checked
  return $cb
}

function New-TrackBar($x, $y, $min, $max, $val) {
  $tb = New-Object System.Windows.Forms.TrackBar
  $tb.Location = New-Object System.Drawing.Point($x, $y)
  $tb.Minimum = $min
  $tb.Maximum = $max
  $tb.Value = [Math]::Min([Math]::Max($val, $min), $max)
  $tb.TickStyle = [System.Windows.Forms.TickStyle]::BottomRight
  $tb.LargeChange = 1
  $tb.SmallChange = 1
  $tb.Width = 360
  return $tb
}

function Get-FixedDrives {
  [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } | ForEach-Object { $_.Name.TrimEnd('\') }
}

function New-PipeSend([string]$PipeName, [int]$Value) {
  try {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::Out)
    $client.Connect(100)
    $sw = New-Object System.IO.StreamWriter($client, [System.Text.Encoding]::UTF8, 256, $false)
    $sw.WriteLine([string]$Value)
    $sw.Flush(); $sw.Dispose(); $client.Dispose()
  } catch {}
}

function Start-Imaging([string]$ExeOrDll, [string]$Device, [string]$OutPath, [hashtable]$Opts, [System.Windows.Forms.TextBox]$LogBox, [bool]$Separate=$false, [bool]$Elevate=$false) {
  $cliList = @('image', '--device', $Device, '--out', '"{0}"' -f $OutPath)
  if ($Opts.useVss) { $cliList += '--use-vss' }
  if ($Opts.resume) { $cliList += '--resume' }
  if ($Opts.allBlocks) { $cliList += '--all-blocks' }
  if ($Opts.parallel -gt 0) { $cliList += @('--parallel', [string]$Opts.parallel) }
  if ($Opts.parFile) { $cliList += @('--parallel-control-file', $Opts.parFile) }
  if ($Opts.parPipe) { $cliList += @('--parallel-control-pipe', $Opts.parPipe) }

  if ($Separate) {
    # Launch in a separate console for stability; optionally elevate.
    $file = $ExeOrDll
    $startArgs = ''
    if ($ExeOrDll -like '*.dll') {
      $file = (Get-Command dotnet).Source
      $startArgs = '"{0}" {1}' -f $ExeOrDll, ($cliList -join ' ')
    } else {
      $startArgs = ($cliList -join ' ')
    }
    $spParams = @{ FilePath = $file; ArgumentList = $startArgs; PassThru = $true; WindowStyle = 'Normal' }
    if ($Elevate) { $spParams['Verb'] = 'RunAs' }
    $p = Start-Process @spParams
    $LogBox.Invoke([System.Action]{ $LogBox.AppendText("Launched PID $($p.Id) in separate console.`r`n") }) | Out-Null
    return $p
  } else {
    # Attached with redirected output
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if ($ExeOrDll -like '*.dll') {
      $psi.FileName = (Get-Command dotnet).Source
      $psi.Arguments = ('"{0}" {1}' -f $ExeOrDll, ($cliList -join ' '))
    } else {
      $psi.FileName = $ExeOrDll
      $psi.Arguments = ($cliList -join ' ')
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $null = $proc.Start()
    $append = {
      param($line)
      if ($null -ne $line) {
        $LogBox.Invoke([System.Action]{ $LogBox.AppendText($line + [Environment]::NewLine) }) | Out-Null
      }
    }
    $proc.add_OutputDataReceived({ param($s,$e) $append.Invoke($e.Data) })
    $proc.add_ErrorDataReceived({ param($s,$e) $append.Invoke($e.Data) })
    $null = $proc.BeginOutputReadLine()
    $null = $proc.BeginErrorReadLine()
    return $proc
  }
}

[System.Windows.Forms.Application]::EnableVisualStyles()

$form = New-Object System.Windows.Forms.Form
$form.Text = 'ImagingUtility GUI (basic)'
$form.Size = New-Object System.Drawing.Size(640, 520)
$form.StartPosition = 'CenterScreen'

$isAdmin = Test-IsAdmin
$adminText = if ($isAdmin) { 'Running as Administrator' } else { 'Not elevated: run PowerShell as Administrator' }
$lblAdmin = New-Label $adminText 12 8
if ($isAdmin) { $lblAdmin.ForeColor = [System.Drawing.Color]::DarkGreen } else { $lblAdmin.ForeColor = [System.Drawing.Color]::Red }
$form.Controls.Add($lblAdmin)

$lblDrive = New-Label 'Drive:' 12 40
$cmbDrive = New-Object System.Windows.Forms.ComboBox
$cmbDrive.Location = New-Object System.Drawing.Point(80, 36)
$cmbDrive.Width = 120
$cmbDrive.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$drives = Get-FixedDrives
if ($drives -is [System.Array]) { $cmbDrive.Items.AddRange([object[]]$drives) } else { if ($drives) { $cmbDrive.Items.Add($drives) } }
if ($cmbDrive.Items.Count -gt 0) { $cmbDrive.SelectedIndex = 0 }
$form.Controls.AddRange(@($lblDrive, $cmbDrive))

$lblOut = New-Label 'Output folder:' 220 40
$tbOut = New-TextBox 320 36 220
$btnBrowse = New-Button 'Browse...' 548 35 70 24
$btnBrowse.Add_Click({
  $fbd = New-Object System.Windows.Forms.FolderBrowserDialog
  if ($fbd.ShowDialog() -eq 'OK') { $tbOut.Text = $fbd.SelectedPath }
})
$form.Controls.AddRange(@($lblOut, $tbOut, $btnBrowse))

$lblFile = New-Label 'Output file:' 12 72
$tbFile = New-TextBox 100 68 300
$form.Controls.AddRange(@($lblFile, $tbFile))

$cbVss = New-Checkbox 'Use VSS snapshot' 12 100 $true
$cbResume = New-Checkbox 'Resume if image exists' 180 100 $false
$cbUsedOnly = New-Checkbox 'Used-only (NTFS)' 380 100 $true
$form.Controls.AddRange(@($cbVss, $cbResume, $cbUsedOnly))

$lblPar = New-Label 'Parallelism:' 12 134
$cores = [Environment]::ProcessorCount
$trkPar = New-TrackBar 100 128 1 ($cores*2) $cores
$lblParVal = New-Label $cores 470 134
$form.Controls.AddRange(@($lblPar, $trkPar, $lblParVal))
$trkPar.Add_ValueChanged({ $lblParVal.Text = $trkPar.Value })

$cbLive = New-Checkbox 'Enable live control (file+pipe)' 12 176 $true
$form.Controls.Add($cbLive)

$cbSeparate = New-Checkbox 'Launch in separate console (stable)' 300 176 $true
$form.Controls.Add($cbSeparate)

$tbLog = New-Object System.Windows.Forms.TextBox
$tbLog.Location = New-Object System.Drawing.Point(12, 240)
$tbLog.Size = New-Object System.Drawing.Size(606, 240)
$tbLog.Multiline = $true
$tbLog.ScrollBars = 'Vertical'
$tbLog.ReadOnly = $true
$form.Controls.Add($tbLog)

$btnStart = New-Button 'Start' 12 188 90 28
$btnStop  = New-Button 'Stop'  110 188 90 28
$btnStop.Enabled = $false
$form.Controls.AddRange(@($btnStart, $btnStop))

# Defaults for output dir and file based on selection
function Update-Defaults() {
  $drv = $cmbDrive.SelectedItem
  if ([string]::IsNullOrWhiteSpace($tbOut.Text)) {
    if (Test-Path 'F:\\Backups') { $tbOut.Text = 'F:\\Backups' }
    else { $tbOut.Text = [Environment]::GetFolderPath('MyDocuments') }
  }
  if ($drv) {
    $letter = ($drv.Substring(0,1))
    $tbFile.Text = "$letter.skzimg"
  }
}
$cmbDrive.Add_SelectedIndexChanged({ Update-Defaults })
Update-Defaults

$procRef = $null
$parFile = $null
$parPipe = $null

# Single ValueChanged handler using current control file/pipe
$trkPar.Add_ValueChanged({
  if ($cbLive.Checked) {
    try { if ($parFile) { Set-Content -Path $parFile -Value $trkPar.Value -Encoding ASCII } } catch {}
    try { if ($parPipe) { New-PipeSend -PipeName $parPipe -Value $trkPar.Value } } catch {}
  }
})

$btnStart.Add_Click({
  try {
    $device = $cmbDrive.SelectedItem
    if (-not $device) { [void][System.Windows.Forms.MessageBox]::Show('Select a drive.', 'ImagingUtility', 'OK', 'Warning'); return }
    $outDir = $tbOut.Text
    if ([string]::IsNullOrWhiteSpace($outDir)) { [void][System.Windows.Forms.MessageBox]::Show('Select an output folder.', 'ImagingUtility', 'OK', 'Warning'); return }
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    $outFile = Join-Path $outDir $tbFile.Text

    $exe = Get-ImagingUtilityPath
    if (-not $exe) { [void][System.Windows.Forms.MessageBox]::Show('Could not find ImagingUtility. Build/publish it first.', 'ImagingUtility', 'OK', 'Error'); return }

    $par = [int]$trkPar.Value
    if ($cbLive.Checked) {
      $parFile = Join-Path $env:TEMP ("imaging-par-" + [Guid]::NewGuid().ToString('N') + ".txt")
      Set-Content -Path $parFile -Value $par -Encoding ASCII
      $parPipe = 'ImgCtl-' + [Guid]::NewGuid().ToString('N')
    } else { $parFile = $null; $parPipe = $null }

    $opts = @{ useVss = $cbVss.Checked; resume = $cbResume.Checked; allBlocks = (-not $cbUsedOnly.Checked); parallel = $par; parFile = $parFile; parPipe = $parPipe }
    $tbLog.Clear()
    $tbLog.AppendText("Starting imaging $device -> $outFile`r`n") | Out-Null
    if (-not (Test-IsAdmin)) { $tbLog.AppendText("[WARN] Not running as Administrator; ImagingUtility will fail to open raw devices.`r`n") | Out-Null }

    $procRef = Start-Imaging -ExeOrDll $exe -Device $device -OutPath $outFile -Opts $opts -LogBox $tbLog -Separate:$($cbSeparate.Checked) -Elevate:$( -not $isAdmin )
    $btnStart.Enabled = $false; $btnStop.Enabled = $true

    # Watch for process exit
    Register-ObjectEvent -InputObject $procRef -EventName Exited -Action {
      $tbLog.Invoke([System.Action]{ $tbLog.AppendText("Process exited with code $($procRef.ExitCode)`r`n"); $btnStart.Enabled = $true; $btnStop.Enabled = $false }) | Out-Null
      if ($parFile -and (Test-Path $parFile)) { Remove-Item -Force $parFile -ErrorAction SilentlyContinue }
    } | Out-Null
    $procRef.EnableRaisingEvents = $true
  } catch {
    [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'ImagingUtility', 'OK', 'Error')
  }
})

$btnStop.Add_Click({
  try {
    if ($procRef -and -not $procRef.HasExited) { $procRef.Kill() | Out-Null }
    $btnStart.Enabled = $true; $btnStop.Enabled = $false
  } catch {}
})

[void]$form.ShowDialog()
