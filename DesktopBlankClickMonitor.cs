// 功能：監看清單外部的左右鍵點擊，讓作用中清單及其鍵盤快捷鍵目標立即解除。
// 使用：DesktopViewApplicationContext 建立後傳入回呼，結束程式時 Dispose；不攔截或修改滑鼠事件。
// 設定：略過 Windows 快顯選單視窗，避免在選取右鍵命令前先把選單關閉。

using System.Runtime.InteropServices;
using System.Text;

namespace DesktopListTool;

internal sealed class DesktopBlankClickMonitor : IDisposable
{
    private const int WhMouseLl = 14, WmLButtonDown = 0x0201, WmRButtonDown = 0x0204;
    private readonly HookProc _callback;
    private readonly Action<Point> _clicked;
    private IntPtr _hook;
    public DesktopBlankClickMonitor(Action<Point> clicked)
    {
        _clicked = clicked; _callback = HookCallback;
        _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
    }
    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)WmLButtonDown || wParam == (IntPtr)WmRButtonDown))
        {
            var data = Marshal.PtrToStructure<MouseHookData>(lParam);
            if (!IsPopupMenuSurface(data.Point)) _clicked(new Point(data.Point.X, data.Point.Y));
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
    private static bool IsPopupMenuSurface(NativePoint point)
    {
        var window = WindowFromPoint(point); var name = new StringBuilder(128);
        while (window != IntPtr.Zero)
        {
            name.Clear(); GetClassName(window, name, name.Capacity);
            if (name.ToString() == "#32768") return true;
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
