// 功能：辨識目前單／多螢幕配置，並以螢幕裝置名稱與相對座標恢復清單及便籤位置。
// 使用：設定存取時使用 CurrentProfileKey；視窗建立與移動後呼叫 RestoreBounds／CapturePlacement。
// 設定：配置識別忽略螢幕排列座標，螢幕重新排列時仍會依各螢幕工作區相對位置恢復。

using System.Security.Cryptography;
using System.Text;

namespace DesktopListTool;

internal static class DisplayLayoutIdentity
{
    public static string CurrentId
    {
        get
        {
            var description = string.Join("|", Screen.AllScreens
                .Select(screen => $"{screen.DeviceName}:{screen.Bounds.Width}x{screen.Bounds.Height}:{screen.WorkingArea.Width}x{screen.WorkingArea.Height}")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description)))[..16];
        }
    }

    public static string CurrentProfileKey => $@"Software\DesktopListTool\Layouts\{CurrentId}";

    public static Rectangle RestoreBounds(Rectangle saved, string? monitorDeviceName, int? relativeX, int? relativeY, Rectangle fallback)
    {
        var screen = Screen.AllScreens.FirstOrDefault(value => value.DeviceName.Equals(monitorDeviceName, StringComparison.OrdinalIgnoreCase));
        if (screen is not null && relativeX is int x && relativeY is int y)
        {
            var restored = new Rectangle(screen.WorkingArea.Left + x, screen.WorkingArea.Top + y, saved.Width, saved.Height);
            return DesktopLayeredOverlayForm.ConstrainRectangle(restored, screen.WorkingArea);
        }
        if (Screen.AllScreens.Any(value => value.WorkingArea.IntersectsWith(saved)))
            return DesktopLayeredOverlayForm.ConstrainRectangle(saved, Screen.FromRectangle(saved).WorkingArea);
        var primary = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return DesktopLayeredOverlayForm.ConstrainRectangle(fallback, primary);
    }

    public static (string DeviceName, int RelativeX, int RelativeY) CapturePlacement(Rectangle bounds)
    {
        var screen = Screen.FromRectangle(bounds);
        return (screen.DeviceName, bounds.Left - screen.WorkingArea.Left, bounds.Top - screen.WorkingArea.Top);
    }
}
