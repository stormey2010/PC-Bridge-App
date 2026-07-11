param([string]$Runtime = "win-x64", [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts\publish"
dotnet publish (Join-Path $root "windows-agent\src\PcBridge.Agent.App\PcBridge.Agent.App.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $publish "app")
dotnet publish (Join-Path $root "windows-agent\src\PcBridge.Agent.Service\PcBridge.Agent.Service.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $publish "service")
dotnet publish (Join-Path $root "windows-agent\src\PcBridge.Agent.SessionHelper\PcBridge.Agent.SessionHelper.csproj") -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $publish "service")
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) { throw "Inno Setup 6 is required. Install it, then rerun installer/build.ps1." }
& $iscc (Join-Path $PSScriptRoot "pc-bridge-agent.iss")
