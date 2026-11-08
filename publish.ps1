# Builds MineServers as one portable exe.
# Usage:  .\publish.ps1        -> runs the tests, then MineServers.exe (+ Versions\Archive\MineServers-<version>.exe)
#         .\publish.ps1 -Run   -> same, then starts it
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

# Windows can't replace an exe that's running.
Get-Process MineServers -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root*" } | Stop-Process -Force

& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Get-ChildItem $out -File | Where-Object { $_.Name -ne "MineServers.exe" } | Remove-Item -Force

$exe = Join-Path $out "MineServers.exe"
New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
Copy-Item $exe $archive
Copy-Item $exe (Join-Path $root "MineServers.exe")
Write-Host ("Done: MineServers.exe {0} ({1} MB), copy kept in Versions\Archive" -f $version, [math]::Round((Get-Item $exe).Length / 1MB, 1)) -ForegroundColor Green
if ($Run) { Start-Process (Join-Path $root "MineServers.exe") }
