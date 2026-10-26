// 功能：在固定桌面清單不取得可見前景層級的前提下，接收選取項目的常用鍵盤快捷鍵。
// 使用：點選清單或圖示項目後自動啟用；支援 Ctrl+C、Ctrl+X、Ctrl+V 與 Delete，切換到其他視窗即關閉。
// 設定：宿主為 2x2 色鍵透明視窗，只取得鍵盤焦點；可見清單仍維持原本桌面／懸浮層級。

namespace DesktopListTool;

internal sealed class ShortcutKeyHost : Form
{
    private static readonly Color TransparentHostColor = Color.FromArgb(255, 255, 0, 255);
    private int _focusAttempts;
    private bool _focusAcquired;
    public event Action<Keys, bool>? ShortcutPressed;

    public ShortcutKeyHost(Point screenAnchor, bool floating)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(screenAnchor, new Size(2, 2)); BackColor = TransparentHostColor; TransparencyKey = TransparentHostColor;
        TopMost = floating; KeyPreview = true;
        Shown += (_, _) => BeginInvoke(AcquireKeyboardFocus);
        Deactivate += (_, _) => { if (_focusAcquired) Close(); };
        KeyDown += (_, e) =>
        {
            if (IsSupportedShortcut(e.KeyCode, e.Control))
            {
                e.SuppressKeyPress = true; e.Handled = true; ShortcutPressed?.Invoke(e.KeyCode, e.Control);
            }
        };
    }

    internal static bool IsSupportedShortcut(Keys key, bool control) =>
        (control && key is Keys.C or Keys.X or Keys.V) || (!control && key == Keys.Delete);

    private void AcquireKeyboardFocus()
    {
        if (IsDisposed || Disposing) return;
        _focusAcquired = KeyboardInputFocus.TryAcquire(this, this);
        if (_focusAcquired) return;
        if (++_focusAttempts < 3)
            BeginInvoke(AcquireKeyboardFocus);
        else
            Close();
    }
}
