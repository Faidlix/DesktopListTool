// 功能：監看桌面空白處左鍵點擊，通知所有清單清除目前選取項目。
// 使用：DesktopViewApplicationContext 建立後傳入回呼，結束程式時 Dispose；不攔截或修改滑鼠事件。
// 設定：只處理 Progman／WorkerW／桌面 Shell 視窗，不影響其他應用程式。

using System.Runtime.InteropServices;
using System.Text;

namespace DesktopListTool;

internal sealed class DesktopBlankClickMonitor : IDisposable
{
    private const int WhMouseLl = 14, WmLButtonDown = 0x0201;
    private readonly HookProc _callback;
    private readonly Action<Point> _clicked;
    private IntPtr _hook;
    public DesktopBlankClickMonitor(Action<Point> clicked)
    {
        _clicked = clicked; _callback = HookCallback;
        _hook = SetWindowsHookEx(WhMouseLl, _callback, IntPtr.Zero, 0);
    }
    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == (IntPtr)WmLButtonDown)
        {
            var data = Marshal.PtrToStructure<MouseHookData>(lParam);
            if (IsDesktopSurface(data.Point)) _clicked(new Point(data.Point.X, data.Point.Y));
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
    private static bool IsDesktopSurface(NativePoint point)
    {
        var window = WindowFromPoint(point); var name = new StringBuilder(128);
        while (window != IntPtr.Zero)
        {
            name.Clear(); GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32") return true;
            window = GetParent(window);
        }
        return false;
    }
    public void Dispose() { if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public NativePoint Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
}
