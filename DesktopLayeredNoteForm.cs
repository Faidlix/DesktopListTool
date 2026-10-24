// 功能：以可自訂透明樣式顯示可捲動 Markdown 與勾選事項，支援懸浮圖釘且不遮住內容。
// 使用：空白處拖曳、本文或事項連點兩下原地編輯、滑鼠滾輪捲動；四角縮放，右鍵可調整樣式或切換懸浮。
// 設定：位置、內容、字型、行高、行距、背景與圓角由 DesktopNotesSettings 逐張保存。

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
    private readonly System.Windows.Forms.Timer _checkClickTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private Font _font = null!;
    private Font _completedFont = null!;
    private readonly List<(DesktopChecklistItem Item, Rectangle Bounds)> _checkBounds = [];
    private readonly Dictionary<Guid, Rectangle> _checkTextBounds = [];
    private Rectangle _addChecklistBounds;
    private DesktopChecklistItem? _pendingCheckItem;
    private InlineTextEditor? _inlineEditor;
    private bool _frameVisible;
    private Color _backgroundColor;
    private byte _backgroundOpacity;
    private bool _followTheme;
    private bool _isFloating;
    private readonly ToolStripMenuItem _floatingItem;
    private OverlayStyleOptions _style = null!;
    private Point _lastContextScreenPoint;
    private int _scrollOffset;
    private int _contentHeight;
    private bool _scrollDragging;
    private int _scrollDragStartY;
    private int _scrollDragStartOffset;
    private bool _movePending;
    private Point _moveStart;
    private int _lastDistributionExtra;

    public event EventHandler? EditRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? NoteChanged;
    public event EventHandler<NoteRequestedEventArgs>? AddNoteRequested;

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
        _style = StyleFromData(data);
        _followTheme = _style.FollowThemeColor;
        _backgroundColor = _followTheme ? OverlayAppearanceSettings.GetWindowsThemeColor() : _style.BackgroundColor;
        _backgroundOpacity = _style.BackgroundOpacity;
        RecreateFonts();
        _isFloating = data.IsFloating;
        _frameVisible = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_SHOW_FRAME") == "1";

        _menu = new ContextMenuStrip();
        _menu.Items.Add("新增便籤…", null, (_, _) => AddNoteRequested?.Invoke(this, new NoteRequestedEventArgs(_lastContextScreenPoint)));
        _menu.Items.Add("編輯便籤內容", null, (_, _) => EditRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("樣式…", null, (_, _) => EditStyle());
        _menu.Items.Add("複製樣式", null, (_, _) => OverlayStyleClipboard.Copy(_style));
        var pasteStyle = new ToolStripMenuItem("貼上樣式");
        pasteStyle.Click += (_, _) => { if (OverlayStyleClipboard.Paste() is { } style) ApplyStyle(style, true); };
        _menu.Items.Add(pasteStyle);
        _floatingItem = new ToolStripMenuItem(_isFloating ? "回到桌面" : "變成懸浮視窗");
        _floatingItem.Click += (_, _) => { if (_isFloating) ReturnToDesktop(); else MakeFloating(); };
        _menu.Items.Add(_floatingItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("刪除便籤", null, (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty));
        _menu.Opening += (_, _) => pasteStyle.Enabled = OverlayStyleClipboard.HasStyle;

        MouseMove += HandleMouseMove;
        MouseUp += (_, _) => { _scrollDragging = false; _movePending = false; };
        MouseWheel += HandleMouseWheel;
        MouseDown += HandleMouseDown;
        MouseDoubleClick += HandleMouseDoubleClick;
        _hoverTimer.Tick += (_, _) => UpdateFrameVisibility();
        _checkClickTimer.Tick += (_, _) => CommitPendingCheckToggle();
        _hoverTimer.Start();
        SystemEvents.UserPreferenceChanged += HandleUserPreferenceChanged;
    }

    public DesktopNoteData Data => _data;
    internal IReadOnlyList<string> ContextMenuLabels => _menu.Items.Cast<ToolStripItem>().Select(value => value.Text ?? "").ToArray();
    internal bool HasScrollableContent => _contentHeight > SafeContentBounds.Height;
    internal int ScrollOffset => _scrollOffset;
    internal int LastDistributionExtra => _lastDistributionExtra;
    internal void RenderForTest() { _ = Handle; RenderLayer(); }
    internal void ScrollForTest(int delta)
    {
        _scrollOffset += delta; ClampScroll(); RenderLayer();
    }
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
        if (_style is null) return;
        ClampScroll();
        RenderLayer();
    }

    public void ApplyEditedContent(string text, IEnumerable<DesktopChecklistItem> items)
    {
        _data.Text = text.TrimEnd();
        _data.Items = items.OrderBy(item => item.Completed).ToList();
        _scrollOffset = 0;
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
            _lastContextScreenPoint = PointToScreen(e.Location);
            _menu.Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;
        if (ScrollbarThumbBounds.Contains(e.Location))
        {
            _scrollDragging = true;
            _scrollDragStartY = e.Y;
            _scrollDragStartOffset = _scrollOffset;
            return;
        }
        if (_addChecklistBounds.Contains(e.Location))
        {
            var item = new DesktopChecklistItem(); _data.Items.Insert(0, item); RenderLayer(); BeginInlineChecklistEdit(item, true);
            return;
        }
        var hit = _checkBounds.FirstOrDefault(value => value.Bounds.Contains(e.Location));
        if (hit.Item is not null)
        {
            _pendingCheckItem = hit.Item;
            _checkClickTimer.Stop(); _checkClickTimer.Start();
            return;
        }
        _movePending = true;
        _moveStart = e.Location;
    }

    private void HandleMouseMove(object? sender, MouseEventArgs e)
    {
        RevealFrame();
        if (_movePending && e.Button == MouseButtons.Left && Math.Abs(e.X - _moveStart.X) + Math.Abs(e.Y - _moveStart.Y) >= SystemInformation.DragSize.Width / 2)
        {
            _movePending = false; ReleaseCapture(); SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero); return;
        }
        if (!_scrollDragging) return;
        var track = ScrollbarTrackBounds;
        var thumb = ScrollbarThumbBounds;
        var travel = Math.Max(1, track.Height - thumb.Height);
        var maximum = Math.Max(0, _contentHeight - SafeContentBounds.Height);
        _scrollOffset = Math.Clamp(_scrollDragStartOffset + (int)Math.Round((e.Y - _scrollDragStartY) * maximum / (double)travel), 0, maximum);
        RenderLayer();
    }

    private void HandleMouseWheel(object? sender, MouseEventArgs e)
    {
        _scrollOffset -= Math.Sign(e.Delta) * Math.Max(30, _style.LineHeight * 3);
        ClampScroll();
        RenderLayer();
    }

    private void HandleMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || FloatingReturnButtonBounds.Contains(e.Location) || ScrollbarThumbBounds.Contains(e.Location) || IsResizeCorner(e.Location)) return;
        _movePending = false;
        var hit = _checkBounds.FirstOrDefault(value => value.Bounds.Contains(e.Location));
        if (hit.Item is not null)
        {
            _checkClickTimer.Stop(); _pendingCheckItem = null; BeginInlineChecklistEdit(hit.Item, false); return;
        }
        if (!_addChecklistBounds.Contains(e.Location)) BeginInlineBodyEdit();
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
            using var background = OverlayStyleRendering.CreateBackgroundBrush(ClientRectangle, _style, _backgroundColor);
            if (_style.CornerRadius > 0)
                graphics.FillRoundedRectangle(background, ClientRectangle, Math.Min(_style.CornerRadius, Math.Min(ClientSize.Width, ClientSize.Height) / 2));
            else graphics.FillRectangle(background, ClientRectangle);
            DrawContent(graphics);
            if (_frameVisible)
            {
                using var pen = new Pen(Color.FromArgb(225, 96, 165, 250), 2F);
                var frameBounds = new Rectangle(1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
                if (_style.CornerRadius > 0) graphics.DrawRoundedRectangle(pen, frameBounds, Math.Min(_style.CornerRadius, Math.Min(frameBounds.Width, frameBounds.Height) / 2));
                else graphics.DrawRectangle(pen, frameBounds);
                using var grip = new SolidBrush(Color.FromArgb(235, 96, 165, 250));
                graphics.FillRectangle(grip, 3, 3, 10, 10); graphics.FillRectangle(grip, ClientSize.Width - 13, 3, 10, 10);
                graphics.FillRectangle(grip, 3, ClientSize.Height - 13, 10, 10); graphics.FillRectangle(grip, ClientSize.Width - 13, ClientSize.Height - 13, 10, 10);
                DrawScrollbar(graphics);
            }
            if (_isFloating && _frameVisible)
                DrawPinButton(graphics);
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
        _checkTextBounds.Clear(); _addChecklistBounds = Rectangle.Empty;
        var safe = SafeContentBounds;
        var originalState = graphics.Save();
        graphics.SetClip(safe);
        var width = Math.Max(1, safe.Width);
        var y = safe.Top - _scrollOffset;
        var itemStep = Math.Max(_style.LineHeight, (int)Math.Ceiling(_font.GetHeight(graphics)) + 6) + _style.LineSpacing;
        _lastDistributionExtra = 0;
        if (!string.IsNullOrWhiteSpace(_data.Text))
        {
            using var textBrush = new SolidBrush(_style.TextColor);
            using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            y = DrawMarkdownText(graphics, _data.Text, safe.Left, y, width, shadowBrush, textBrush) + 10;
        }
        var ordered = _data.Items.OrderBy(value => value.Completed).ToList();
        if (ordered.Count == 0)
        {
            _addChecklistBounds = new Rectangle(safe.Left, y, Math.Max(28, itemStep), itemStep);
            DrawAddButton(graphics, _addChecklistBounds);
            y += itemStep;
        }
        for (var itemIndex = 0; itemIndex < ordered.Count; itemIndex++)
        {
            var item = ordered[itemIndex];
            var row = new Rectangle(safe.Left, y, width, itemStep);
            var font = item.Completed ? _completedFont : _font;
            var boxSize = Math.Max(18, (int)Math.Ceiling(font.GetHeight(graphics)) + 3);
            var leading = row.X;
            if (itemIndex == 0) { _addChecklistBounds = new Rectangle(leading, row.Y, boxSize + 6, row.Height); DrawAddButton(graphics, _addChecklistBounds); leading = _addChecklistBounds.Right + 2; }
            var box = new Rectangle(leading, row.Y + Math.Max(0, (row.Height - boxSize) / 2), boxSize, boxSize);
            using var boxPen = new Pen(_style.TextColor, 1.8F);
            graphics.DrawRectangle(boxPen, box);
            if (item.Completed)
            {
                using var checkPen = new Pen(Color.FromArgb(115, 205, 255), 2.2F);
                graphics.DrawLines(checkPen, [new Point(box.Left + 3, box.Top + 9), new Point(box.Left + 7, box.Bottom - 3), new Point(box.Right - 2, box.Top + 3)]);
            }
            using var textBrush = new SolidBrush(_style.TextColor);
            using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            var itemBounds = new RectangleF(box.Right + 8, row.Y + Math.Max(0, (row.Height - font.Height) / 2F), Math.Max(1, row.Right - box.Right - 10), row.Height);
            DrawSimpleText(graphics, item.Text, font, itemBounds, shadowBrush, textBrush);
            _checkBounds.Add((item, row));
            _checkTextBounds[item.Id] = Rectangle.Ceiling(itemBounds);
            y += itemStep;
        }
        _contentHeight = Math.Max(safe.Height, y + _scrollOffset - safe.Top);
        graphics.Restore(originalState);
        ClampScroll();
    }

    private int DrawMarkdownText(Graphics graphics, string text, int x, int y, int width, Brush shadow, Brush foreground)
    {
        var currentY = y;
        var sourceLines = text.Replace("\r", "").Split('\n');
        var naturalSteps = new List<int>();
        foreach (var source in sourceLines)
        {
            var parsed = MarkdownNoteRenderer.ParseLine(source);
            using var lineFont = CreateMarkdownFont(parsed.HeadingLevel, FontStyle.Regular);
            var step = Math.Max(_style.LineHeight, (int)Math.Ceiling(lineFont.GetHeight(graphics)) + 4) + _style.LineSpacing;
            naturalSteps.Add(step);
        }
        for (var lineIndex = 0; lineIndex < sourceLines.Length; lineIndex++)
        {
            var parsed = MarkdownNoteRenderer.ParseLine(sourceLines[lineIndex]);
            var runs = BuildRuns(parsed);
            using var baseFont = CreateMarkdownFont(parsed.HeadingLevel, FontStyle.Regular);
            var lines = WrapRuns(graphics, runs, baseFont, width);
            foreach (var line in lines)
            {
                DrawRunLine(graphics, line, parsed.HeadingLevel, x, currentY, width, shadow, foreground);
                currentY += naturalSteps[lineIndex];
            }
            if (lines.Count == 0) currentY += naturalSteps[lineIndex];
        }
        return currentY;
    }

    private (int Rows, int Height) MeasureMarkdownLayout(Graphics graphics, string text, int width)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0);
        var rows = 0; var height = 0;
        foreach (var source in text.Replace("\r", "").Split('\n'))
        {
            var parsed = MarkdownNoteRenderer.ParseLine(source); using var font = CreateMarkdownFont(parsed.HeadingLevel, FontStyle.Regular);
            var count = Math.Max(1, WrapRuns(graphics, BuildRuns(parsed), font, width).Count);
            var step = Math.Max(_style.LineHeight, (int)Math.Ceiling(font.GetHeight(graphics)) + 4) + _style.LineSpacing;
            rows += count; height += count * step;
        }
        return (rows, height);
    }

    private static List<MarkdownRun> BuildRuns(MarkdownLine parsed)
    {
        var runs = new List<MarkdownRun>();
        if (parsed.Checked is bool check) runs.Add(new MarkdownRun(check ? "☑ " : "☐ ", check ? FontStyle.Strikeout : FontStyle.Regular));
        else if (parsed.Prefix.Length > 0) runs.Add(new MarkdownRun(parsed.Prefix, FontStyle.Regular));
        runs.AddRange(parsed.Runs); return runs;
    }

    private List<List<MarkdownRun>> WrapRuns(Graphics graphics, IReadOnlyList<MarkdownRun> runs, Font baseFont, int width)
    {
        var result = new List<List<MarkdownRun>>();
        var line = new List<MarkdownRun>();
        var used = 0F;
        foreach (var run in runs)
        {
            using var font = CreateRunFont(baseFont, run);
            foreach (var character in run.Text.Select(value => value.ToString()))
            {
                var measured = graphics.MeasureString(character, font, PointF.Empty, StringFormat.GenericTypographic).Width;
                if (used + measured > width && line.Count > 0) { result.Add(line); line = []; used = 0; }
                if (line.Count > 0 && line[^1].Style == run.Style && line[^1].IsCode == run.IsCode)
                    line[^1] = line[^1] with { Text = line[^1].Text + character };
                else line.Add(new MarkdownRun(character, run.Style, run.IsCode));
                used += measured;
            }
        }
        if (line.Count > 0) result.Add(line);
        return result;
    }

    private void DrawRunLine(Graphics graphics, IReadOnlyList<MarkdownRun> runs, int heading, int x, int y, int width, Brush shadow, Brush foreground)
    {
        var sizes = new List<(MarkdownRun Run, Font Font, float Width)>();
        using var baseFont = CreateMarkdownFont(heading, FontStyle.Regular);
        foreach (var run in runs) { var font = CreateRunFont(baseFont, run); sizes.Add((run, font, graphics.MeasureString(run.Text, font, PointF.Empty, StringFormat.GenericTypographic).Width)); }
        var total = sizes.Sum(value => value.Width);
        var fill = _style.Distribution == OverlayContentDistribution.Fill && runs.Sum(value => value.Text.Length) > 1;
        var gaps = Math.Max(1, runs.Sum(value => value.Text.Length) - 1); var extra = fill ? Math.Max(0, width - total) / gaps : 0F;
        var cursor = fill ? x : _style.TextAlignment switch { OverlayTextAlignment.Center => x + Math.Max(0, (width - total) / 2), OverlayTextAlignment.Right => x + Math.Max(0, width - total), _ => x };
        foreach (var value in sizes)
        {
            foreach (var character in value.Run.Text.Select(ch => ch.ToString()))
            {
                var characterWidth = graphics.MeasureString(character, value.Font, PointF.Empty, StringFormat.GenericTypographic).Width;
                if (value.Run.IsCode) { using var codeBackground = new SolidBrush(Color.FromArgb(75, 255, 255, 255)); graphics.FillRectangle(codeBackground, new RectangleF(cursor - 2, y, characterWidth + 4, value.Font.Height + 2)); }
                graphics.DrawString(character, value.Font, shadow, cursor + 1, y + 1, StringFormat.GenericTypographic);
                graphics.DrawString(character, value.Font, foreground, cursor, y, StringFormat.GenericTypographic);
                cursor += characterWidth + extra;
            }
            value.Font.Dispose();
        }
    }

    private void DrawSimpleText(Graphics graphics, string text, Font font, RectangleF bounds, Brush shadow, Brush foreground)
    {
        var widths = text.Select(value => graphics.MeasureString(value.ToString(), font, PointF.Empty, StringFormat.GenericTypographic).Width).ToArray();
        var total = widths.Sum(); var fill = _style.Distribution == OverlayContentDistribution.Fill && widths.Length > 1;
        var extra = fill ? Math.Max(0, bounds.Width - total) / (widths.Length - 1) : 0F;
        var cursor = fill ? bounds.Left : _style.TextAlignment switch { OverlayTextAlignment.Center => bounds.Left + Math.Max(0, (bounds.Width - total) / 2), OverlayTextAlignment.Right => bounds.Right - total, _ => bounds.Left };
        for (var i = 0; i < text.Length; i++) { var value = text[i].ToString(); graphics.DrawString(value, font, shadow, cursor + 1, bounds.Top + 1, StringFormat.GenericTypographic); graphics.DrawString(value, font, foreground, cursor, bounds.Top, StringFormat.GenericTypographic); cursor += widths[i] + extra; }
    }

    private void DrawAddButton(Graphics graphics, Rectangle bounds)
    {
        using var pen = new Pen(_style.TextColor, 2F); var cx = bounds.Left + bounds.Width / 2; var cy = bounds.Top + bounds.Height / 2;
        graphics.DrawLine(pen, cx - 6, cy, cx + 6, cy); graphics.DrawLine(pen, cx, cy - 6, cx, cy + 6);
    }

    private Font CreateMarkdownFont(int heading, FontStyle extra)
    {
        var size = heading > 0 ? _style.FontSize + Math.Max(1, 7 - heading) : _style.FontSize;
        var style = (_style.Bold || heading > 0 ? FontStyle.Bold : FontStyle.Regular) | extra;
        try { return new Font(_style.FontFamily, size, style); } catch { return new Font("Microsoft JhengHei UI", size, style); }
    }
    private static Font CreateRunFont(Font baseFont, MarkdownRun run)
    {
        var family = run.IsCode ? FontFamily.GenericMonospace : baseFont.FontFamily;
        try { return new Font(family, baseFont.Size, baseFont.Style | run.Style); } catch { return new Font(baseFont, baseFont.Style); }
    }
    private StringFormat CreateTextFormat() => new() { Alignment = _style.TextAlignment switch { OverlayTextAlignment.Center => StringAlignment.Center, OverlayTextAlignment.Right => StringAlignment.Far, _ => StringAlignment.Near }, Trimming = StringTrimming.EllipsisCharacter };

    private Rectangle SafeContentBounds
    {
        get
        {
            var inset = Math.Max(PaddingSize, _style.CornerRadius + 4);
            var rightInset = inset + (_isFloating ? 32 : 0);
            return Rectangle.FromLTRB(inset, inset, Math.Max(inset + 1, ClientSize.Width - rightInset), Math.Max(inset + 1, ClientSize.Height - inset));
        }
    }
    private Rectangle ScrollbarTrackBounds { get { var safe = SafeContentBounds; return new Rectangle(Math.Max(2, ClientSize.Width - 9), safe.Top, 5, safe.Height); } }
    private Rectangle ScrollbarThumbBounds
    {
        get
        {
            var track = ScrollbarTrackBounds; if (_contentHeight <= SafeContentBounds.Height) return Rectangle.Empty;
            var height = Math.Max(24, (int)Math.Round(track.Height * SafeContentBounds.Height / (double)_contentHeight));
            var maximum = Math.Max(1, _contentHeight - SafeContentBounds.Height);
            var y = track.Top + (int)Math.Round((track.Height - height) * _scrollOffset / (double)maximum);
            return new Rectangle(track.X - 2, y, 9, height);
        }
    }
    private void DrawScrollbar(Graphics graphics)
    {
        var thumb = ScrollbarThumbBounds; if (thumb.IsEmpty) return;
        using var trackBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255)); using var thumbBrush = new SolidBrush(Color.FromArgb(210, 96, 165, 250));
        graphics.FillRoundedRectangle(trackBrush, ScrollbarTrackBounds, 3); graphics.FillRoundedRectangle(thumbBrush, thumb, 4);
    }
    private void ClampScroll() => _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _contentHeight - SafeContentBounds.Height));

    private void CommitPendingCheckToggle()
    {
        _checkClickTimer.Stop(); var item = _pendingCheckItem; _pendingCheckItem = null;
        if (item is null || !_data.Items.Contains(item)) return;
        item.Completed = !item.Completed; _data.Items = _data.Items.OrderBy(value => value.Completed).ToList(); RenderLayer(); NoteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginInlineBodyEdit()
    {
        CloseInlineEditor();
        var safe = SafeContentBounds; var screen = RectangleToScreen(safe);
        _inlineEditor = new InlineTextEditor(screen, _data.Text, _font, _style.TextColor, EditorBackgroundColor(), true);
        _inlineEditor.Committed += value => { _data.Text = value.TrimEnd(); _scrollOffset = 0; _inlineEditor = null; RenderLayer(); NoteChanged?.Invoke(this, EventArgs.Empty); };
        _inlineEditor.Canceled += () => _inlineEditor = null;
        _inlineEditor.Show(this);
    }

    private void BeginInlineChecklistEdit(DesktopChecklistItem item, bool removeOnCancel)
    {
        CloseInlineEditor(); RenderLayer();
        if (!_checkTextBounds.TryGetValue(item.Id, out var local)) local = new Rectangle(SafeContentBounds.Left + 30, SafeContentBounds.Top, Math.Max(80, SafeContentBounds.Width - 30), Math.Max(28, _style.LineHeight));
        var screen = RectangleToScreen(local);
        _inlineEditor = new InlineTextEditor(screen, item.Text, _font, _style.TextColor, EditorBackgroundColor(), false);
        _inlineEditor.Committed += value => { item.Text = value.Trim(); if (item.Text.Length == 0) _data.Items.Remove(item); _data.Items = _data.Items.OrderBy(value => value.Completed).ToList(); _inlineEditor = null; RenderLayer(); NoteChanged?.Invoke(this, EventArgs.Empty); };
        _inlineEditor.Canceled += () => { if (removeOnCancel) _data.Items.Remove(item); _inlineEditor = null; RenderLayer(); if (removeOnCancel) NoteChanged?.Invoke(this, EventArgs.Empty); };
        _inlineEditor.Show(this);
    }

    private Color EditorBackgroundColor() => Color.FromArgb(255, _backgroundColor.R, _backgroundColor.G, _backgroundColor.B);
    private void CloseInlineEditor() { if (_inlineEditor is { IsDisposed: false } editor) editor.Close(); _inlineEditor = null; }
    private bool IsResizeCorner(Point point) => (point.X < 18 || point.X >= ClientSize.Width - 18) && (point.Y < 18 || point.Y >= ClientSize.Height - 18);

    private void EditStyle()
    {
        var original = _style.Clone();
        using var dialog = new OverlayStyleDialog(_style.Clone(), "便籤");
        dialog.PreviewChanged += (_, _) => ApplyStyle(dialog.SelectedStyle, false);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            ApplyStyle(dialog.SelectedStyle, true);
        else
            ApplyStyle(original, false);
    }

    private void ApplyStyle(OverlayStyleOptions style, bool save)
    {
        _style = style.Clone();
        _followTheme = _style.FollowThemeColor;
        _backgroundColor = _followTheme ? OverlayAppearanceSettings.GetWindowsThemeColor() : _style.BackgroundColor;
        _backgroundOpacity = _style.BackgroundOpacity;
        RecreateFonts();
        if (save)
        {
            _data.BackgroundColorArgb = _backgroundColor.ToArgb();
            _data.BackgroundOpacity = _style.BackgroundOpacity;
            _data.FollowThemeColor = _style.FollowThemeColor;
            _data.GradientEnabled = _style.GradientEnabled;
            _data.GradientColorArgb = _style.GradientColor.ToArgb();
            _data.GradientOpacity = _style.GradientOpacity;
            _data.GradientTransition = _style.GradientTransition;
            _data.GradientDirection = _style.GradientDirection;
            _data.GradientMode = _style.GradientMode;
            _data.TextColorArgb = _style.TextColor.ToArgb();
            _data.FontFamily = _style.FontFamily;
            _data.FontSize = _style.FontSize;
            _data.FontBold = _style.Bold;
            _data.LineHeight = _style.LineHeight;
            _data.LineSpacing = _style.LineSpacing;
            _data.CornerRadius = _style.CornerRadius;
            _data.TextAlignment = _style.TextAlignment;
            _data.Distribution = _style.Distribution;
            NoteChanged?.Invoke(this, EventArgs.Empty);
        }
        RenderLayer();
    }

    private Rectangle FloatingReturnButtonBounds => new(Math.Max(4, ClientSize.Width - 34), 4, 28, 28);

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

    private void DrawPinButton(Graphics graphics)
    {
        using var background = new SolidBrush(Color.FromArgb(225, 30, 41, 59));
        graphics.FillRoundedRectangle(background, FloatingReturnButtonBounds, 6);
        using var pen = new Pen(Color.White, 2F);
        var centerX = FloatingReturnButtonBounds.Left + FloatingReturnButtonBounds.Width / 2;
        graphics.DrawEllipse(pen, centerX - 5, FloatingReturnButtonBounds.Top + 5, 10, 7);
        graphics.DrawLine(pen, centerX - 6, FloatingReturnButtonBounds.Top + 14, centerX + 6, FloatingReturnButtonBounds.Top + 14);
        graphics.DrawLine(pen, centerX, FloatingReturnButtonBounds.Top + 12, centerX, FloatingReturnButtonBounds.Bottom - 5);
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
        _style.BackgroundColor = _backgroundColor;
        _data.BackgroundColorArgb = _backgroundColor.ToArgb();
        if (IsHandleCreated) BeginInvoke(RenderLayer);
    }

    protected override void WndProc(ref Message message)
    {
        const int WmNcHitTest = 0x0084, WmMoving = 0x0216, WmSizing = 0x0214, WmExitSizeMove = 0x0232;
        if (message.Msg == WmMoving && message.LParam != IntPtr.Zero)
        {
            var bounds = Marshal.PtrToStructure<NativeRect>(message.LParam).ToRectangle();
            var constrained = DesktopLayeredOverlayForm.ConstrainRectangle(bounds, Screen.FromRectangle(bounds).WorkingArea);
            Marshal.StructureToPtr(NativeRect.FromRectangle(constrained), message.LParam, false);
        }
        if (message.Msg == WmSizing && message.LParam != IntPtr.Zero)
        {
            var bounds = Marshal.PtrToStructure<NativeRect>(message.LParam).ToRectangle();
            var constrained = DesktopLayeredOverlayForm.ConstrainSizingRectangle(bounds, Screen.FromRectangle(bounds).WorkingArea, message.WParam.ToInt32(), MinimumSize);
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
            if (client.X < 18 && client.Y < 18) message.Result = (IntPtr)13;
            else if (client.X >= ClientSize.Width - 18 && client.Y < 18) message.Result = (IntPtr)14;
            else if (client.X < 18 && client.Y >= ClientSize.Height - 18) message.Result = (IntPtr)16;
            else if (client.X >= ClientSize.Width - 18 && client.Y >= ClientSize.Height - 18) message.Result = (IntPtr)17;
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

    private static OverlayStyleOptions StyleFromData(DesktopNoteData data) => new()
    {
        BackgroundColor = Color.FromArgb(data.BackgroundColorArgb),
        BackgroundOpacity = data.BackgroundOpacity,
        FollowThemeColor = data.FollowThemeColor,
        GradientEnabled = data.GradientEnabled,
        GradientColor = Color.FromArgb(data.GradientColorArgb),
        GradientOpacity = data.GradientOpacity,
        GradientTransition = data.GradientTransition,
        GradientDirection = data.GradientDirection,
        GradientMode = data.GradientMode,
        TextColor = Color.FromArgb(data.TextColorArgb),
        FontFamily = data.FontFamily,
        FontSize = data.FontSize,
        Bold = data.FontBold,
        LineHeight = data.LineHeight,
        LineSpacing = data.LineSpacing,
        CornerRadius = data.CornerRadius,
        TextAlignment = data.TextAlignment,
        Distribution = data.Distribution
    };

    private void RecreateFonts()
    {
        _font?.Dispose();
        _completedFont?.Dispose();
        _font = OverlayStyleDialog.CreateFont(_style);
        try { _completedFont = new Font(_font, _font.Style | FontStyle.Strikeout); }
        catch (ArgumentException) { _completedFont = new Font("Microsoft JhengHei UI", _style.FontSize, FontStyle.Strikeout); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseInlineEditor(); _checkClickTimer.Dispose(); _hoverTimer.Dispose(); _menu.Dispose(); _font.Dispose(); _completedFont.Dispose();
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
