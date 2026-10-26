// 功能：讓不可見的快捷鍵／原地編輯宿主取得鍵盤前景與輸入焦點，而不改變清單或便籤本體的桌面層級。
// 使用：宿主顯示並回到訊息迴圈後呼叫 TryAcquire，傳入宿主表單及實際接收按鍵的控制項。
// 設定：只操作 2x2 透明輸入宿主；不調整可見桌面物件的 Z-order。

using System.Runtime.InteropServices;

namespace DesktopListTool;

internal static class KeyboardInputFocus
{
    public static bool TryAcquire(Form host, Control target)
    {
        if (host.IsDisposed || target.IsDisposed || !host.IsHandleCreated)
            return false;

        SetForegroundWindow(host.Handle);
        host.Activate();
        target.Select();
        target.Focus();
        SetFocus(target.Handle);
        return GetForegroundWindow() == host.Handle && (target.Focused || host.ContainsFocus);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
