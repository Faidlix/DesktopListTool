// 功能：以不可見鍵盤輸入宿主支援便籤與代辦原地編輯，畫面文字仍由原本的分層視窗繪製。
// 使用：雙擊內容後接收輸入；Enter 確認、Esc 取消，便籤本文以 Shift+Enter 換行，失去焦點即取消。
// 設定：固定桌面物件的輸入宿主保持在一般視窗後方；懸浮物件才使用置頂層級，且不建立額外捲動列。

using System.Runtime.InteropServices;

namespace DesktopListTool;

internal sealed class InlineTextEditor : Form
{
    private static readonly Color TransparentEditorColor = Color.FromArgb(255, 255, 0, 255);
    private readonly TextBox _editor;
    private readonly bool _multiline;
    private bool _finished;
    public event Action<string>? Committed;
    public event Action? Canceled;
    public event Action<string, int>? PreviewChanged;

    public InlineTextEditor(Point screenAnchor, string text, bool multiline, bool floating)
    {
        _multiline = multiline;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(screenAnchor, new Size(2, 2)); TopMost = floating; BackColor = TransparentEditorColor; TransparencyKey = TransparentEditorColor;
        _editor = new TextBox
        {
            Bounds = new Rectangle(0, 0, 1, 1), BorderStyle = BorderStyle.None, Multiline = multiline,
            AcceptsReturn = multiline, AcceptsTab = false, ScrollBars = ScrollBars.None,
            Text = text, ForeColor = TransparentEditorColor, BackColor = TransparentEditorColor
        };
        _editor.KeyDown += HandleKeyDown;
        _editor.TextChanged += (_, _) => PublishPreview();
        _editor.KeyUp += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown) PublishPreview(); };
        Controls.Add(_editor);
        Shown += (_, _) =>
        {
            _editor.Focus(); _editor.SelectionStart = _editor.TextLength; _editor.SelectionLength = 0;
            if (!floating) SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        };
        Deactivate += (_, _) => Cancel();
        FormClosed += (_, _) => { if (!_finished) Cancel(); };
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Cancel(); return; }
        if (e.KeyCode != Keys.Enter) return;
        if (_multiline && e.Shift) return;
        e.SuppressKeyPress = true; e.Handled = true; Commit();
    }

    private void PublishPreview() => PreviewChanged?.Invoke(_editor.Text, Math.Clamp(_editor.SelectionStart, 0, _editor.TextLength));

    private void Commit()
    {
        if (_finished) return; _finished = true; var value = _editor.Text; Hide(); Committed?.Invoke(value); Close();
    }
    private void Cancel()
    {
        if (_finished) return; _finished = true; Hide(); Canceled?.Invoke(); Close();
    }

    internal static bool CommitsOnKey(Keys key, bool shift, bool multiline) => key == Keys.Enter && (!multiline || !shift);
    internal static bool CancelsOnKey(Keys key) => key == Keys.Escape;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
