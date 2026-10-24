$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$outDir = Join-Path $root "build"
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$files = Get-ChildItem (Join-Path $root "src") -Filter *.cs | Sort-Object Name
$src = ($files | ForEach-Object { '"' + $_.FullName + '"' }) -join " "

$argLine = "/nologo /target:winexe /codepage:65001 /optimize+ /win32icon:`"$root\app.ico`" /out:`"$outDir\TuneForge.exe`" " +
           $src + " /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $csc
$psi.Arguments = $argLine
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($psi)
$stdout = $p.StandardOutput.ReadToEnd()
$stderr = $p.StandardError.ReadToEnd()
$p.WaitForExit()
Write-Output $stdout
Write-Output $stderr
Write-Output ("EXIT=" + $p.ExitCode)
if (Test-Path "$outDir\TuneForge.exe") {
    Write-Output ("SIZE=" + (Get-Item "$outDir\TuneForge.exe").Length)
}
