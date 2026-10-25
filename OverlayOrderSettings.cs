// 功能：保存每個桌面／資料夾清單的排序方式與使用者拖曳後的自訂順序。
// 使用：由 DesktopLayeredOverlayForm 自動讀寫；不需獨立執行。
// 設定：資料存於 HKCU\Software\DesktopListTool 及其 Folders 子機碼。

using Microsoft.Win32;

namespace DesktopListTool;

internal enum OverlaySortMode { Custom, Name, Type, Size, Modified }
internal enum OverlayDisplayMode { List, Icons }

internal static class OverlayOrderSettings
{
    public static OverlayDisplayMode LoadDisplayMode(string? identity)
    {
        try
        {
            using var profileKey = Registry.CurrentUser.OpenSubKey(OverlaySettings.GetSettingsKey(identity));
            using var legacyKey = profileKey?.GetValue("DisplayMode") is string ? null : Registry.CurrentUser.OpenSubKey(OverlaySettings.GetLegacySettingsKey(identity));
            var value = (profileKey?.GetValue("DisplayMode") ?? legacyKey?.GetValue("DisplayMode")) as string;
            return Enum.TryParse<OverlayDisplayMode>(value, out var mode) ? mode : OverlayDisplayMode.List;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return OverlayDisplayMode.List; }
    }

    public static void SaveDisplayMode(string? identity, OverlayDisplayMode mode)
    {
        try { using var key = Registry.CurrentUser.CreateSubKey(OverlaySettings.GetSettingsKey(identity)); key.SetValue("DisplayMode", mode.ToString(), RegistryValueKind.String); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    public static (OverlaySortMode Mode, bool Ascending) LoadSort(string? folderPath)
    {
        try
        {
            using var profileKey = Registry.CurrentUser.OpenSubKey(OverlaySettings.GetSettingsKey(folderPath));
            using var legacyKey = profileKey?.GetValue("SortMode") is string ? null : Registry.CurrentUser.OpenSubKey(OverlaySettings.GetLegacySettingsKey(folderPath));
            var key = profileKey?.GetValue("SortMode") is string ? profileKey : legacyKey;
            var mode = Enum.TryParse<OverlaySortMode>(key?.GetValue("SortMode") as string, out var parsed)
                ? parsed : OverlaySortMode.Name;
            var ascending = key?.GetValue("SortAscending") is not int value || value != 0;
            return (mode, ascending);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return (OverlaySortMode.Name, true);
        }
    }

    public static void SaveSort(string? folderPath, OverlaySortMode mode, bool ascending)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(OverlaySettings.GetSettingsKey(folderPath));
            key.SetValue("SortMode", mode.ToString(), RegistryValueKind.String);
            key.SetValue("SortAscending", ascending ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    public static IReadOnlyList<string> LoadCustomOrder(string? folderPath)
    {
        try
        {
            using var profileKey = Registry.CurrentUser.OpenSubKey(OverlaySettings.GetSettingsKey(folderPath));
            using var legacyKey = profileKey?.GetValue("CustomOrder") is string[] ? null : Registry.CurrentUser.OpenSubKey(OverlaySettings.GetLegacySettingsKey(folderPath));
            return (profileKey?.GetValue("CustomOrder") ?? legacyKey?.GetValue("CustomOrder")) as string[] ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return []; }
    }

    public static void SaveCustomOrder(string? folderPath, IEnumerable<string> paths)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(OverlaySettings.GetSettingsKey(folderPath));
            key.SetValue("CustomOrder", paths.ToArray(), RegistryValueKind.MultiString);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }
}
