// 功能：在清單或清單項目處於作用中狀態時，接收不受中英文輸入法影響的檔案操作快捷鍵。
// 使用：DesktopViewApplicationContext 建立後提供處理回呼；回呼回傳 true 時攔截 Ctrl+C／X／V 或 Delete。
// 設定：沒有作用中清單時完全不攔截，讓 Windows 桌面或其他應用程式照常處理鍵盤。

using System.Runtime.InteropServices;
using System.Text;

namespace DesktopListTool;

internal sealed class DesktopShortcutMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private readonly HookProc _callback;
    private readonly Func<Keys, bool, bool> _pressed;
    private readonly HashSet<Keys> _suppressed = [];
    private IntPtr _hook;

    public DesktopShortcutMonitor(Func<Keys, bool, bool> pressed)
    {
        _pressed = pressed;
        _callback = HookCallback;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = (Keys)Marshal.ReadInt32(lParam);
            var message = wParam.ToInt32();
            if (message is WmKeyUp or WmSysKeyUp)
            {
                if (_suppressed.Remove(key)) return (IntPtr)1;
            }
            else if (message is WmKeyDown or WmSysKeyDown)
            {
                var control = (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
                if (IsSupportedShortcut(key, control) && _pressed(key, control))
                {
                    _suppressed.Add(key);
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    internal static bool IsSupportedShortcut(Keys key, bool control) =>
        (control && key is Keys.C or Keys.X or Keys.V) || (!control && key == Keys.Delete);

    internal static bool IsDesktopForeground()
    {
        var window = GetForegroundWindow();
        var name = new StringBuilder(128);
        while (window != IntPtr.Zero)
        {
            name.Clear();
            GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32") return true;
            window = GetParent(window);
        }
        return false;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
