# Fullscreen overlay test window (borderless, topmost, covers primary screen).
# Shows for N seconds so NotifyHub.FullscreenWatcher can detect it. ASCII only.
param(
    [int]$Seconds = 8
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$f.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$f.TopMost = $true
$f.BackColor = [System.Drawing.Color]::FromArgb(20, 20, 28)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = [Math]::Max(1, $Seconds) * 1000
$timer.Add_Tick({ $f.Close() })
$timer.Start()

[System.Windows.Forms.Application]::Run($f)
Write-Output "fullscreen-test window closed after ${Seconds}s"
