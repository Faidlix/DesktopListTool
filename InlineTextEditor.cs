// 功能：在便籤原位置覆蓋無邊框文字輸入區，支援本文與單一勾選事項的直接編輯。
// 使用：便籤本文或勾選事項雙擊開啟；Enter 確認、Esc 取消，本文以 Shift+Enter 換行。
// 設定：由 DesktopLayeredNoteForm 提供畫面座標、字型、文字顏色與是否允許多行；背景以色鍵透明保留便籤原樣式。

namespace DesktopListTool;

internal sealed class InlineTextEditor : Form
{
    private static readonly Color TransparentEditorColor = Color.FromArgb(255, 255, 0, 255);
    private readonly TextBox _editor;
    private readonly bool _multiline;
    private bool _finished;
    public event Action<string>? Committed;
    public event Action? Canceled;

    public InlineTextEditor(Rectangle screenBounds, string text, Font font, Color foreground, bool multiline)
    {
        _multiline = multiline;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds; TopMost = true; BackColor = TransparentEditorColor; TransparencyKey = TransparentEditorColor;
        _editor = new TextBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Multiline = multiline,
            AcceptsReturn = multiline, AcceptsTab = false, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            Text = text, Font = new Font(font, font.Style), ForeColor = foreground, BackColor = TransparentEditorColor
        };
        _editor.KeyDown += HandleKeyDown;
        Controls.Add(_editor);
        Shown += (_, _) => { _editor.Focus(); _editor.SelectionStart = _editor.TextLength; _editor.SelectionLength = 0; };
        Deactivate += (_, _) => Cancel();
        FormClosed += (_, _) => { if (!_finished) Cancel(); _editor.Font.Dispose(); };
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Cancel(); return; }
        if (e.KeyCode != Keys.Enter) return;
        if (_multiline && e.Shift) return;
        e.SuppressKeyPress = true; e.Handled = true; Commit();
    }

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
}
