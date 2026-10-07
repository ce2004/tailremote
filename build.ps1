# Builds TailRemote into bin\arm64 and bin\x64, even while it is running.
#
# A running exe cannot be overwritten, but it can be moved: the running copy is
# moved to %LOCALAPPDATA%\TailRemote\old (emptied by the next start), the new
# one is copied into its place, the old copy is closed (it saves whether it was
# hosting or connected) and the new one starts with --resume to carry on.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1            both
#   powershell -ExecutionPolicy Bypass -File build.ps1 arm64      one
param([string[]]$Arch = @('arm64', 'x64'))
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$oldDir = Join-Path $env:LOCALAPPDATA 'TailRemote\old'

foreach ($a in $Arch) {
    $pub = "obj\publish\$a"
    & $dotnet publish TailRemote.csproj -c Release -r "win-$a" -o $pub -p:BaseOutputPath=obj\pubout\ --nologo -v:q
    if ($LASTEXITCODE) { exit $LASTEXITCODE }

    $dest = Join-Path $PSScriptRoot "bin\$a"
    $exe = Join-Path $dest 'TailRemote.exe'
    New-Item -ItemType Directory -Force $dest | Out-Null

    $running = @(Get-Process TailRemote -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    if (Test-Path $exe) {
        New-Item -ItemType Directory -Force $oldDir | Out-Null
        Move-Item $exe (Join-Path $oldDir ("TailRemote-$a-" + [DateTime]::Now.Ticks + '.exe'))
    }
    Copy-Item "$pub\TailRemote.exe" $exe

    foreach ($p in $running) {
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(5000)) { $p.Kill(); $p.WaitForExit() }
    }
    if ($running.Count -gt 0) {
        Start-Process $exe -ArgumentList '--resume'
        "$a built, installed and restarted: $exe"
    }
    else { "$a built and installed: $exe" }

    # The replaced copy has exited by now: delete it, and the folder once empty.
    if (Test-Path $oldDir) {
        Get-ChildItem $oldDir -File | ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }
        if (-not (Get-ChildItem $oldDir)) { Remove-Item $oldDir -Force }
        $parent = Split-Path $oldDir
        if ((Test-Path $parent) -and -not (Get-ChildItem $parent)) { Remove-Item $parent -Force }
    }
}
