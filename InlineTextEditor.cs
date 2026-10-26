// 功能：以近乎透明的原生文字輸入宿主支援便籤與代辦原地編輯、滑鼠選字、IME 與標準剪貼簿快捷鍵。
// 使用：雙擊內容後接收輸入；Enter 確認、Esc 取消，便籤本文以 Shift+Enter 換行，失去焦點即取消。
// 設定：輸入宿主只覆蓋目前文字範圍；檔案剪貼簿貼上時自動插入顯示物件名稱的 Markdown 捷徑連結。

using System.Runtime.InteropServices;

namespace DesktopListTool;

internal sealed class InlineTextEditor : Form
{
    private readonly TextBox _editor;
    private readonly bool _multiline;
    private int _focusAttempts;
    private bool _focusAcquired;
    private bool _finished;
    public event Action<string>? Committed;
    public event Action? Canceled;
    public event Action<InlineEditSnapshot>? PreviewChanged;

    public InlineTextEditor(Rectangle screenBounds, string text, bool multiline, bool floating, Font font)
    {
        _multiline = multiline;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(screenBounds.Location, new Size(Math.Max(8, screenBounds.Width), Math.Max(8, screenBounds.Height)));
        TopMost = floating; BackColor = Color.Black; Opacity = 0.01;
        _editor = new TextBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Multiline = multiline, WordWrap = multiline,
            AcceptsReturn = multiline, AcceptsTab = false, ScrollBars = ScrollBars.None,
            Text = text, ForeColor = Color.White, BackColor = Color.Black, Font = new Font(font, font.Style)
        };
        _editor.KeyDown += HandleKeyDown;
        _editor.TextChanged += (_, _) => PublishPreview();
        _editor.KeyUp += (_, _) => PublishPreview();
        _editor.MouseDown += (_, _) => BeginInvoke(PublishPreview);
        _editor.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) BeginInvoke(PublishPreview); };
        _editor.MouseUp += (_, _) => PublishPreview();
        Controls.Add(_editor);
        Shown += (_, _) => BeginInvoke(AcquireKeyboardFocus);
        Deactivate += (_, _) => { if (_focusAcquired) Cancel(); };
        FormClosed += (_, _) => { if (!_finished) Cancel(); };
    }

    private void AcquireKeyboardFocus()
    {
        if (_finished || IsDisposed || Disposing) return;
        _editor.SelectionStart = _editor.TextLength; _editor.SelectionLength = 0;
        _focusAcquired = KeyboardInputFocus.TryAcquire(this, _editor);
        if (_focusAcquired) return;
        if (++_focusAttempts < 3)
            BeginInvoke(AcquireKeyboardFocus);
        else
            Cancel();
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Cancel(); return; }
        if (e.Control && e.KeyCode == Keys.A)
        {
            _editor.SelectAll(); e.SuppressKeyPress = true; e.Handled = true; PublishPreview(); return;
        }
        if (e.Control && e.KeyCode == Keys.V && TryCreateFileLinks(out var links))
        {
            _editor.SelectedText = links; e.SuppressKeyPress = true; e.Handled = true; PublishPreview(); return;
        }
        if (e.KeyCode != Keys.Enter) return;
        if (IsImeComposing()) return;
        if (_multiline && e.Shift) return;
        e.SuppressKeyPress = true; e.Handled = true; Commit();
    }

    private void PublishPreview() => PreviewChanged?.Invoke(new InlineEditSnapshot(
        _editor.Text,
        Math.Clamp(_editor.SelectionStart, 0, _editor.TextLength),
        Math.Clamp(_editor.SelectionLength, 0, _editor.TextLength),
        GetSelectionRectangles()));

    private IReadOnlyList<Rectangle> GetSelectionRectangles()
    {
        var start = Math.Clamp(_editor.SelectionStart, 0, _editor.TextLength);
        var end = Math.Clamp(start + _editor.SelectionLength, start, _editor.TextLength);
        if (start == end) return [];
        var lines = new Dictionary<int, (int Left, int Right)>();
        for (var index = start; index < end; index++)
        {
            var position = _editor.GetPositionFromCharIndex(index);
            var next = _editor.GetPositionFromCharIndex(Math.Min(index + 1, _editor.TextLength));
            var right = next.Y == position.Y && next.X > position.X ? next.X : _editor.ClientSize.Width;
            if (lines.TryGetValue(position.Y, out var current))
                lines[position.Y] = (Math.Min(current.Left, position.X), Math.Max(current.Right, right));
            else
                lines[position.Y] = (position.X, Math.Max(position.X + 2, right));
        }
        return lines.Select(value => new Rectangle(value.Value.Left, value.Key, Math.Max(2, value.Value.Right - value.Value.Left), Math.Max(18, _editor.Font.Height + 2))).ToArray();
    }

    private bool TryCreateFileLinks(out string links)
    {
        links = "";
        try
        {
            if (!Clipboard.ContainsFileDropList()) return false;
            var values = Clipboard.GetFileDropList().Cast<string>().Where(value => File.Exists(value) || Directory.Exists(value)).ToArray();
            if (values.Length == 0) return false;
            links = string.Join(_multiline ? Environment.NewLine : " ", values.Select(CreateFileLink));
            return true;
        }
        catch (ExternalException) { return false; }
    }

    internal static string CreateFileLink(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name)) name = new DirectoryInfo(path).Name;
        name = name.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
        var target = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        return $"[{name}]({target})";
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

    private bool IsImeComposing()
    {
        var context = ImmGetContext(_editor.Handle);
        if (context == IntPtr.Zero) return false;
        try { return ImmGetCompositionString(context, 8, IntPtr.Zero, 0) > 0; }
        finally { ImmReleaseContext(_editor.Handle, context); }
    }

    [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionString(IntPtr context, uint index, IntPtr buffer, uint length);

}

internal sealed record InlineEditSnapshot(string Text, int SelectionStart, int SelectionLength, IReadOnlyList<Rectangle> SelectionRectangles);
