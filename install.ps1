# 功能：安裝「桌面清單模式」到目前使用者的 LocalAppData，並在開始功能表建立捷徑。
# 用法：先執行 .\build.ps1，再執行 .\install.ps1；不需要系統管理員權限。
# 設定：安裝位置固定為 %LOCALAPPDATA%\DesktopListTool，可重複執行以更新。

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$sourceDir = Join-Path $projectRoot 'publish\win-x64'
$installDir = Join-Path $env:LOCALAPPDATA 'DesktopListTool'
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenuDir '桌面清單.lnk'

if (-not (Test-Path -LiteralPath (Join-Path $sourceDir 'DesktopListTool.exe'))) {
    throw '找不到發佈成品，請先執行 build.ps1。'
}

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $installDir -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installDir 'DesktopListTool.exe'
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = '切換 Windows 桌面的清單與圖示顯示模式'
$shortcut.Save()

$menuKey = 'HKCU:\Software\Classes\DesktopBackground\Shell\DesktopListTool'
$oldCommandKey = Join-Path $menuKey 'command'
if (Test-Path $oldCommandKey) {
    Remove-Item -LiteralPath $oldCommandKey -Recurse -Force
}
New-Item -Path $menuKey -Force | Out-Null
Set-ItemProperty -Path $menuKey -Name 'MUIVerb' -Value '桌面清單模式'
Set-ItemProperty -Path $menuKey -Name 'Icon' -Value (Join-Path $installDir 'DesktopListTool.exe')
$commandIds = @(
    'DesktopListTool.Compact',
    'DesktopListTool.Standard',
    'DesktopListTool.Large',
    'DesktopListTool.Icons',
    'DesktopListTool.StartupOn',
    'DesktopListTool.StartupOff'
)
Set-ItemProperty -Path $menuKey -Name 'SubCommands' -Value ($commandIds -join ';')

$commandStore = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell'
function Set-DesktopListCommand {
    param([string]$Id, [string]$Label, [string]$Arguments)
    $itemKey = Join-Path $commandStore $Id
    $itemCommandKey = Join-Path $itemKey 'command'
    New-Item -Path $itemCommandKey -Force | Out-Null
    Set-Item -Path $itemKey -Value $Label
    Set-ItemProperty -Path $itemKey -Name 'Icon' -Value (Join-Path $installDir 'DesktopListTool.exe')
    Set-Item -Path $itemCommandKey -Value ('"{0}" {1}' -f (Join-Path $installDir 'DesktopListTool.exe'), $Arguments)
}

Set-DesktopListCommand 'DesktopListTool.Compact' '清單：緊湊項目' '--list compact'
Set-DesktopListCommand 'DesktopListTool.Standard' '清單：標準項目' '--list standard'
Set-DesktopListCommand 'DesktopListTool.Large' '清單：大型項目' '--list large'
Set-DesktopListCommand 'DesktopListTool.Icons' '切回 Windows 圖示' '--icons'
Set-DesktopListCommand 'DesktopListTool.StartupOn' '啟用開機自動啟動' '--startup enable'
Set-DesktopListCommand 'DesktopListTool.StartupOff' '停用開機自動啟動' '--startup disable'

Get-Item -LiteralPath (Join-Path $installDir 'DesktopListTool.exe'), $shortcutPath |
    Select-Object FullName, Length, LastWriteTime
