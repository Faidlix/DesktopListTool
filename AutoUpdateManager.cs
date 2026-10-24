// 功能：從 Faidlix/DesktopListTool 的 GitHub Releases 檢查、下載並安全套用新版。
// 使用：清單空白處右鍵切換「自動更新」或按「立即檢查更新」；自動模式在程式啟動後檢查一次。
// 設定：啟用狀態保存於 HKCU；只有使用者確認後才下載，替換失敗會保留目前安裝版本。

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopListTool;

internal sealed record UpdateRelease(Version Version, string Tag, string DownloadUrl, string Notes);

internal static class AutoUpdateManager
{
    private const string SettingsKey = @"Software\DesktopListTool";
    private const string EnabledValue = "AutoUpdateEnabled";
    private const string LatestReleaseApi = "https://api.github.com/repos/Faidlix/DesktopListTool/releases/latest";
    private const string AssetName = "DesktopListTool-win-x64.zip";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
                return key?.GetValue(EnabledValue) is int value && value != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue(EnabledValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    public static async Task<bool> CheckAndPromptAsync(IWin32Window owner, bool interactive)
    {
        try
        {
            var release = await GetLatestReleaseAsync();
            if (release is null)
            {
                if (interactive) MessageBox.Show(owner, "GitHub Releases 尚未提供可下載的 Windows 版本。", "檢查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            if (release.Version <= current)
            {
                if (interactive) MessageBox.Show(owner, $"目前已是最新版（{current.ToString(3)}）。", "檢查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            var detail = string.IsNullOrWhiteSpace(release.Notes) ? "" : $"\n\n{TrimNotes(release.Notes)}";
            if (MessageBox.Show(owner, $"發現新版 {release.Tag}，是否下載並安裝？{detail}", "桌面清單更新",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return false;
            await DownloadAndLaunchUpdaterAsync(release);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            if (interactive) MessageBox.Show(owner, $"檢查或下載更新失敗，現有版本不會被變更。\n\n{ex.Message}", "更新失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    internal static bool IsNewerVersion(Version current, string tag, out Version available)
    {
        available = new Version(0, 0);
        if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed) || parsed is null) return false;
        available = parsed;
        return available > current;
    }

    private static async Task<UpdateRelease?> GetLatestReleaseAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopListTool/1.1");
        using var response = await client.GetAsync(LatestReleaseApi);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewerVersion(new Version(0, 0), tag, out var version)) return null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (!string.IsNullOrWhiteSpace(url)) return new UpdateRelease(version, tag, url, notes);
        }
        return null;
    }

    private static async Task DownloadAndLaunchUpdaterAsync(UpdateRelease release)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !processPath.EndsWith("DesktopListTool.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("目前不是安裝版執行檔，請先執行 install.ps1。 ");
        var root = Path.Combine(Path.GetTempPath(), $"DesktopListTool-Update-{Guid.NewGuid():N}");
        var zip = Path.Combine(root, AssetName);
        var extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(root);
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopListTool/1.1");
            await using var input = await client.GetStreamAsync(release.DownloadUrl);
            await using var output = File.Create(zip);
            await input.CopyToAsync(output);
        }
        ZipFile.ExtractToDirectory(zip, extracted);
        var replacement = Path.Combine(extracted, "DesktopListTool.exe");
        if (!File.Exists(replacement)) throw new InvalidDataException("更新壓縮檔內找不到 DesktopListTool.exe。");

        var script = Path.Combine(Path.GetTempPath(), $"DesktopListTool-Updater-{Guid.NewGuid():N}.ps1");
        var scriptText = """
# 功能：等待 DesktopListTool 結束後替換已下載的新版本，完成後重新啟動並清理暫存檔。
# 使用：由 DesktopListTool 自動呼叫；參數依序為程序 ID、解壓目錄、安裝目錄、執行檔與暫存根目錄。
# 設定：更新失敗時不刪除既有安裝；腳本僅操作傳入的 DesktopListTool 安裝與暫存路徑。
param([int]$ProcessId,[string]$Source,[string]$Destination,[string]$Executable,[string]$TempRoot)
$ErrorActionPreference='Stop'
try {
    Wait-Process -Id $ProcessId -Timeout 30 -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
    Start-Process -FilePath $Executable -ArgumentList '--list','standard' -WindowStyle Hidden
    Start-Sleep -Milliseconds 800
    Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    Add-Content -LiteralPath (Join-Path $env:TEMP 'DesktopListTool-update-error.log') -Value $_.Exception.ToString()
}
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
""";
        await File.WriteAllTextAsync(script, scriptText);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(extracted);
        start.ArgumentList.Add(AppContext.BaseDirectory); start.ArgumentList.Add(processPath); start.ArgumentList.Add(root);
        Process.Start(start);
    }

    private static string TrimNotes(string notes)
    {
        var value = notes.Replace("\r", "").Trim();
        return value.Length <= 600 ? value : value[..600] + "…";
    }
}
