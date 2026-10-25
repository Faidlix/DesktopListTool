// 功能：保存由桌面項目拖出後建立的自訂參照清單，不移動或複製原始檔案。
// 使用：由 DesktopViewApplicationContext 自動建立、讀取、更新與關閉清單。
// 設定：清單 ID 與項目路徑依目前單／多螢幕配置，以 JSON 保存於 HKCU\Software\DesktopListTool\Layouts。

using System.Text.Json;
using Microsoft.Win32;

namespace DesktopListTool;

internal sealed class CustomListData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<string> Paths { get; set; } = [];
}

internal static class CustomListSettings
{
    private const string SettingsKey = @"Software\DesktopListTool";
    private const string ValueName = "CustomListsJson";

    public static List<CustomListData> Load()
    {
        try
        {
            var qaJson = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_CUSTOM_LISTS_JSON");
            if (!string.IsNullOrWhiteSpace(qaJson))
                return JsonSerializer.Deserialize<List<CustomListData>>(qaJson) ?? [];
            using var profileKey = Registry.CurrentUser.OpenSubKey(DisplayLayoutIdentity.CurrentProfileKey);
            using var legacyKey = profileKey?.GetValue(ValueName) is string ? null : Registry.CurrentUser.OpenSubKey(SettingsKey);
            var json = (profileKey?.GetValue(ValueName) ?? legacyKey?.GetValue(ValueName)) as string;
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<CustomListData>>(json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or System.Security.SecurityException or UnauthorizedAccessException) { return []; }
    }

    public static void Save(IEnumerable<CustomListData> lists)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(DisplayLayoutIdentity.CurrentProfileKey);
            var values = lists.Where(value => value.Paths.Count > 0).ToArray();
            key.SetValue(ValueName, JsonSerializer.Serialize(values), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }
}
