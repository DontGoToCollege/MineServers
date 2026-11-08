# Builds MineServers as one portable exe.
# Usage:  .\publish.ps1        -> runs the tests, then MineServers.exe (+ Versions\Archive\MineServers-<version>.exe)
#                                  A running MineServers is closed (worlds saved) and restarted on the new version.
#         .\publish.ps1 -Run   -> same, and starts it even if it wasn't running
# Refuses a version that's already archived: raise <Version> in src\MineServers\MineServers.csproj first.
param([switch]$Run)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\MineServers\MineServers.csproj"
$out  = Join-Path $root "dist"
# The x64 SDK; the dotnet on PATH can be the x86 one, which has no SDK on some PCs.
$dotnet = if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") { "$env:ProgramFiles\dotnet\dotnet.exe" } else { "dotnet" }

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$archive = Join-Path $root "Versions\Archive\MineServers-$version.exe"
if (Test-Path $archive) { throw "Version $version is already archived. Raise <Version> in the csproj, then publish again." }

& $dotnet test (Join-Path $root "tests\MineServers.Tests")
if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was built." }

& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Get-ChildItem $out -File | Where-Object { $_.Name -ne "MineServers.exe" } | Remove-Item -Force

$exe = Join-Path $out "MineServers.exe"
New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
Copy-Item $exe $archive
Write-Host ("Built MineServers {0} ({1} MB), copy kept in Versions\Archive" -f $version, [math]::Round((Get-Item $exe).Length / 1MB, 1)) -ForegroundColor Green

# A running MineServers is closed so the new version can replace it (the owner's standing wish), then started again.
# It closes the way its Close button does: worlds are saved and servers stopped, never killed mid-save,
# and the new version starts those servers again.
$top = Join-Path $root "MineServers.exe"
$running = @(Get-Process MineServers -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $top })
foreach ($p in $running) {
    try {
        [System.Threading.EventWaitHandle]::OpenExisting("Local\MineServers-Close-$($p.Id)").Set() | Out-Null
        Write-Host "Asked MineServers to save its worlds and close..."
    } catch {
        # Versions before 1.2.0 have no update signal: close it normally (it asks before stopping servers).
        Write-Host "Closing MineServers. If it asks about running servers, click Yes." -ForegroundColor Yellow
        $p.CloseMainWindow() | Out-Null
    }
    if (-not $p.WaitForExit(180000)) { throw "MineServers didn't close within 3 minutes, so it wasn't replaced." }
}
Copy-Item $exe $top
Write-Host "MineServers.exe is now $version." -ForegroundColor Green
if ($running.Count -gt 0 -or $Run) {
    # Started through Windows (not as this script's child) so it keeps running after the build.
    Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$top`""; CurrentDirectory = $root } | Out-Null
}
