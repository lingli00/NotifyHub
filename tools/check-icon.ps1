# Verify the exe has an embedded icon and export it for visual check.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\NotifyHub.exe'
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
if ($null -eq $icon) { throw 'no icon' }
$out = Join-Path $env:TEMP 'notifyhub-icon-check.png'
$icon.ToBitmap().Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "icon OK: $($icon.Width)x$($icon.Height) -> $out"
