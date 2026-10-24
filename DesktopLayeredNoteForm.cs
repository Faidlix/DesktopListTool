// 功能：以與桌面清單相同的透明框樣式顯示便籤、多段文字與可勾選事項。
// 使用：空白處拖曳移動、右下角縮放、勾選方框切換完成；右鍵可編輯、調色或刪除。
// 設定：位置、大小、顏色、透明度與內容由 DesktopNotesSettings 自動保存。

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopListTool;

internal sealed class DesktopLayeredNoteForm : Form
{
    private const int PaddingSize = 14;
    private readonly DesktopNoteData _data;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 100 };
    private readonly Font _font = new("Microsoft JhengHei UI", 11F);
    private readonly Font _completedFont = new("Microsoft JhengHei UI", 11F, FontStyle.Strikeout);
    private readonly List<(DesktopChecklistItem Item, Rectangle Bounds)> _checkBounds = [];
    private bool _frameVisible;
    private Color _backgroundColor;
    private byte _backgroundOpacity;
    private bool _followTheme;
    private bool _isFloating;
    private readonly ToolStripMenuItem _floatingItem;

    public event EventHandler? EditRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? NoteChanged;

    public DesktopLayeredNoteForm(DesktopNoteData data)
    {
        _data = data;
        Text = $"DesktopListTool.Note.{data.Id:N}";
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(240, 140);
        Bounds = DesktopLayeredOverlayForm.ConstrainRectangle(data.Bounds, Screen.FromRectangle(data.Bounds).WorkingArea);
        _followTheme = data.FollowThemeColor;
        var savedColor = Color.FromArgb(data.BackgroundColorArgb);
        _backgroundColor = _followTheme ? OverlayAppearanceSettings.GetWindowsThemeColor() : savedColor;
        _backgroundOpacity = data.BackgroundOpacity;
        _isFloating = data.IsFloating;
        _frameVisible = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_SHOW_FRAME") == "1";

        _menu = new ContextMenuStrip();
        _menu.Items.Add("編輯便籤內容", null, (_, _) => EditRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("背景顏色與透明度…", null, (_, _) => EditBackground());
        _menu.Items.Add("恢復完全透明", null, (_, _) => SetBackground(_backgroundColor, 0, false, save: true));
        _floatingItem = new ToolStripMenuItem(_isFloating ? "回到桌面" : "變成懸浮視窗");
        _floatingItem.Click += (_, _) => { if (_isFloating) ReturnToDesktop(); else MakeFloating(); };
        _menu.Items.Add(_floatingItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("刪除便籤", null, (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty));

        MouseMove += (_, _) => RevealFrame();
        MouseDown += HandleMouseDown;
        MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) EditRequested?.Invoke(this, EventArgs.Empty); };
        _hoverTimer.Tick += (_, _) => UpdateFrameVisibility();
        _hoverTimer.Start();
        SystemEvents.UserPreferenceChanged += HandleUserPreferenceChanged;
    }

    public DesktopNoteData Data => _data;
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExLayered = 0x00080000;
            const int WsExToolWindow = 0x00000080;
            const int WsOverlappedWindow = 0x00CF0000;
            const int WsPopup = unchecked((int)0x80000000);
            const int WsExWindowEdge = 0x00000100;
            var value = base.CreateParams;
            value.Style = (value.Style & ~WsOverlappedWindow) | WsPopup;
            value.ExStyle &= ~WsExWindowEdge;
            value.ExStyle |= WsExLayered | WsExToolWindow;
            return value;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RenderLayer();
        TopMost = _isFloating;
        SetWindowPos(Handle, _isFloating ? new IntPtr(-1) : new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RenderLayer();
    }

    public void ApplyEditedContent(string text, IEnumerable<DesktopChecklistItem> items)
    {
        _data.Text = text.TrimEnd();
        _data.Items = items.OrderBy(item => item.Completed).ToList();
        RenderLayer();
        NoteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleMouseDown(object? sender, MouseEventArgs e)
    {
        RevealFrame();
        if (e.Button == MouseButtons.Left && _isFloating && FloatingReturnButtonBounds.Contains(e.Location))
        {
            ReturnToDesktop();
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            _menu.Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;
        var hit = _checkBounds.FirstOrDefault(value => value.Bounds.Contains(e.Location));
        if (hit.Item is not null)
        {
            hit.Item.Completed = !hit.Item.Completed;
            _data.Items = _data.Items.OrderBy(value => value.Completed).ToList();
            RenderLayer();
            NoteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    private void RenderLayer()
    {
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var background = new SolidBrush(Color.FromArgb(Math.Max(1, (int)_backgroundOpacity), _backgroundColor));
            graphics.FillRectangle(background, ClientRectangle);
            DrawContent(graphics);
            if (_frameVisible)
            {
                using var pen = new Pen(Color.FromArgb(225, 96, 165, 250), 2F);
                graphics.DrawRectangle(pen, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
                using var grip = new SolidBrush(Color.FromArgb(235, 96, 165, 250));
                graphics.FillRectangle(grip, ClientSize.Width - 14, ClientSize.Height - 14, 11, 11);
            }
            if (_isFloating)
                DrawFloatingReturnButton(graphics);
        }
        var qaPath = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH");
        if (!string.IsNullOrWhiteSpace(qaPath))
        {
            try { bitmap.Save(qaPath, ImageFormat.Png); Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH", null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var qaMetaPath = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_META_PATH");
        if (!string.IsNullOrWhiteSpace(qaMetaPath))
        {
            try
            {
                File.WriteAllText(qaMetaPath, $"Bounds={Bounds}; ClientSize={ClientSize}; DataBounds={_data.Bounds}; Dpi={DeviceDpi}");
                Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_META_PATH", null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        ApplyLayeredBitmap(bitmap);
    }

    private void DrawContent(Graphics graphics)
    {
        _checkBounds.Clear();
        var width = Math.Max(1, ClientSize.Width - PaddingSize * 2);
        var y = _isFloating ? 48 : PaddingSize;
        var availableHeight = Math.Max(20, ClientSize.Height - y - PaddingSize);
        var checklistHeight = Math.Min(_data.Items.Count * 31, availableHeight);
        if (!string.IsNullOrWhiteSpace(_data.Text))
        {
            using var textBrush = new SolidBrush(Color.White);
            using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            var format = new StringFormat { Trimming = StringTrimming.EllipsisWord };
            var measured = graphics.MeasureString(_data.Text, _font, width, format);
            var maximumBodyHeight = _data.Items.Count == 0
                ? availableHeight
                : Math.Max(20, availableHeight - checklistHeight - 10);
            var bodyHeight = Math.Min((int)Math.Ceiling(measured.Height) + 2, maximumBodyHeight);
            graphics.DrawString(_data.Text, _font, shadowBrush, new RectangleF(PaddingSize + 1, y + 1, width, bodyHeight), format);
            graphics.DrawString(_data.Text, _font, textBrush, new RectangleF(PaddingSize, y, width, bodyHeight), format);
            y += bodyHeight + 10;
        }
        foreach (var item in _data.Items.OrderBy(value => value.Completed))
        {
            if (y + 30 > ClientSize.Height - 12)
                break;
            var row = new Rectangle(PaddingSize, y, width, 30);
            var box = new Rectangle(row.X + 2, row.Y + 6, 17, 17);
            using var boxPen = new Pen(Color.White, 1.8F);
            graphics.DrawRectangle(boxPen, box);
            if (item.Completed)
            {
                using var checkPen = new Pen(Color.FromArgb(115, 205, 255), 2.2F);
                graphics.DrawLines(checkPen, [new Point(box.Left + 3, box.Top + 9), new Point(box.Left + 7, box.Bottom - 3), new Point(box.Right - 2, box.Top + 3)]);
            }
            using var textBrush = new SolidBrush(Color.White);
            using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            var font = item.Completed ? _completedFont : _font;
            graphics.DrawString(item.Text, font, shadowBrush, row.X + 27, row.Y + 4);
            graphics.DrawString(item.Text, font, textBrush, row.X + 26, row.Y + 3);
            _checkBounds.Add((item, row));
            y += 31;
        }
    }

    private void EditBackground()
    {
        var original = (_backgroundColor, _backgroundOpacity, _followTheme);
        using var dialog = new OverlayAppearanceDialog(_backgroundColor, _backgroundOpacity, _followTheme);
        dialog.PreviewChanged += (_, _) => SetBackground(dialog.SelectedColor, dialog.SelectedOpacity, dialog.FollowThemeColor, false);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SetBackground(dialog.SelectedColor, dialog.SelectedOpacity, dialog.FollowThemeColor, true);
        else
            SetBackground(original._backgroundColor, original._backgroundOpacity, original._followTheme, false);
    }

    private void SetBackground(Color color, byte opacity, bool followTheme, bool save)
    {
        _followTheme = followTheme;
        _backgroundColor = followTheme ? OverlayAppearanceSettings.GetWindowsThemeColor() : Color.FromArgb(color.R, color.G, color.B);
        _backgroundOpacity = opacity;
        if (save)
        {
            _data.BackgroundColorArgb = _backgroundColor.ToArgb();
            _data.BackgroundOpacity = opacity;
            _data.FollowThemeColor = followTheme;
            NoteChanged?.Invoke(this, EventArgs.Empty);
        }
        RenderLayer();
    }

    private Rectangle FloatingReturnButtonBounds => new(Math.Max(12, ClientSize.Width - 116), 8, 98, 28);

    private void MakeFloating()
    {
        _isFloating = true;
        _data.IsFloating = true;
        _floatingItem.Text = "回到桌面";
        TopMost = true;
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        RenderLayer();
        NoteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReturnToDesktop()
    {
        _isFloating = false;
        _data.IsFloating = false;
        _floatingItem.Text = "變成懸浮視窗";
        TopMost = false;
        SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        RenderLayer();
        NoteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DrawFloatingReturnButton(Graphics graphics)
    {
        using var background = new SolidBrush(Color.FromArgb(225, 30, 41, 59));
        graphics.FillRoundedRectangle(background, FloatingReturnButtonBounds, 6);
        using var brush = new SolidBrush(Color.White);
        using var font = new Font("Microsoft JhengHei UI", 9F, FontStyle.Bold);
        const string text = "回到桌面";
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, brush,
            FloatingReturnButtonBounds.X + (FloatingReturnButtonBounds.Width - size.Width) / 2,
            FloatingReturnButtonBounds.Y + (FloatingReturnButtonBounds.Height - size.Height) / 2);
    }

    private void RevealFrame() { if (!_frameVisible) { _frameVisible = true; RenderLayer(); } }

    private void UpdateFrameVisibility()
    {
        var visible = Rectangle.Inflate(Bounds, 18, 18).Contains(Cursor.Position) || _menu.Visible;
        if (visible == _frameVisible) return;
        _frameVisible = visible;
        RenderLayer();
    }

    private void HandleUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_followTheme || IsDisposed) return;
        _backgroundColor = OverlayAppearanceSettings.GetWindowsThemeColor();
        _data.BackgroundColorArgb = _backgroundColor.ToArgb();
        if (IsHandleCreated) BeginInvoke(RenderLayer);
    }

    protected override void WndProc(ref Message message)
    {
        const int WmNcHitTest = 0x0084, WmMoving = 0x0216, WmExitSizeMove = 0x0232;
        if (message.Msg == WmMoving && message.LParam != IntPtr.Zero)
        {
            var bounds = Marshal.PtrToStructure<NativeRect>(message.LParam).ToRectangle();
            var constrained = DesktopLayeredOverlayForm.ConstrainRectangle(bounds, Screen.FromRectangle(bounds).WorkingArea);
            Marshal.StructureToPtr(NativeRect.FromRectangle(constrained), message.LParam, false);
        }
        base.WndProc(ref message);
        if (message.Msg == WmExitSizeMove)
        {
            Bounds = DesktopLayeredOverlayForm.ConstrainRectangle(Bounds, Screen.FromRectangle(Bounds).WorkingArea);
            _data.Bounds = Bounds;
            NoteChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (message.Msg == WmNcHitTest && _frameVisible)
        {
            var screen = new Point((short)(message.LParam.ToInt64() & 0xFFFF), (short)((message.LParam.ToInt64() >> 16) & 0xFFFF));
            var client = PointToClient(screen);
            if (client.X >= ClientSize.Width - 18 && client.Y >= ClientSize.Height - 18) message.Result = (IntPtr)17;
            else if (client.X < 10 || client.Y < 10 || client.X > ClientSize.Width - 10 || client.Y > ClientSize.Height - 10) message.Result = (IntPtr)2;
        }
    }

    private void ApplyLayeredBitmap(Bitmap bitmap)
    {
        var screenDc = GetDC(IntPtr.Zero); var memoryDc = CreateCompatibleDC(screenDc);
        var handle = bitmap.GetHbitmap(Color.FromArgb(0)); var previous = SelectObject(memoryDc, handle);
        try
        {
            var destination = new NativePoint(Left, Top); var source = new NativePoint(0, 0);
            var size = new NativeSize(bitmap.Width, bitmap.Height);
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2);
        }
        finally { SelectObject(memoryDc, previous); DeleteObject(handle); DeleteDC(memoryDc); ReleaseDC(IntPtr.Zero, screenDc); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTimer.Dispose(); _menu.Dispose(); _font.Dispose(); _completedFont.Dispose();
            SystemEvents.UserPreferenceChanged -= HandleUserPreferenceChanged;
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize(int w, int h) { public int Width = w; public int Height = h; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); public static NativeRect FromRectangle(Rectangle value) => new() { Left=value.Left, Top=value.Top, Right=value.Right, Bottom=value.Bottom }; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, int colorKey, ref BlendFunction blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
