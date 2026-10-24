# 功能：建置、測試並封裝「桌面清單模式」為單一 Windows x64 執行檔。
# 用法：在專案目錄執行 .\build.ps1；成品位於 artifacts\DesktopListTool-win-x64.zip。
# 設定：需安裝 .NET 10 SDK，不需要系統管理員權限。

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$cliHome = Join-Path $projectRoot '.dotnet-cli'
$publishDir = Join-Path $projectRoot 'publish\win-x64'
$artifactsDir = Join-Path $projectRoot 'artifacts'
$zipPath = Join-Path $artifactsDir 'DesktopListTool-win-x64.zip'

$env:DOTNET_CLI_HOME = $cliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

dotnet build (Join-Path $projectRoot 'DesktopListTool.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

dotnet run --project (Join-Path $projectRoot 'DesktopListTool.csproj') -c Release --no-build -- --smoke-test
if ($LASTEXITCODE -ne 0) { throw 'Smoke test failed.' }

dotnet publish (Join-Path $projectRoot 'DesktopListTool.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force
Get-Item -LiteralPath $zipPath | Select-Object FullName, Length, LastWriteTime
