// 功能：以可自訂透明樣式分別顯示可捲動 Markdown 便籤或代辦清單，支援懸浮圖釘且不遮住內容。
// 使用：空白處拖曳、本文或代辦事項連點兩下原地編輯、滑鼠滾輪捲動；便籤不顯示代辦新增按鈕。
// 設定：位置、種類、內容與樣式逐張保存；固定物件不啟用也不浮到一般視窗前，懸浮物件才允許置頂。

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Diagnostics;
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
    private bool _editingBody;
    private Guid? _editingChecklistItemId;
    private string? _editingText;
    private int _editingCaretIndex;
    private int _editingSelectionLength;
    private readonly List<Rectangle> _editingSelectionBounds = [];
    private readonly List<(RectangleF Bounds, string Target)> _linkBounds = [];
    private Rectangle _bodyTextBounds;
    private bool _frameVisible;
    private OverlayResizeCorner _resizeHoverCorner;
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
    public event EventHandler? InteractionActivated;
    internal Func<DesktopLayeredNoteForm, Rectangle, Rectangle>? ResolvePlacement;
    internal Action<Form, Rectangle>? PlacementPreview;
    internal Action<Form>? PlacementCompleted;

    public DesktopLayeredNoteForm(DesktopNoteData data)
    {
        _data = data;
        Text = $"DesktopListTool.Note.{data.Id:N}";
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(240, 140);
        var primary = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Bounds = DisplayLayoutIdentity.RestoreBounds(data.Bounds, data.MonitorDeviceName, data.MonitorRelativeX, data.MonitorRelativeY,
            new Rectangle(primary.Left + 40, primary.Top + 40, data.Bounds.Width, data.Bounds.Height));
        CapturePlacement();
        _style = StyleFromData(data);
        _followTheme = _style.FollowThemeColor;
        _backgroundColor = _followTheme ? OverlayAppearanceSettings.GetWindowsThemeColor() : _style.BackgroundColor;
        _backgroundOpacity = _style.BackgroundOpacity;
        RecreateFonts();
        _isFloating = data.IsFloating;
        _frameVisible = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_SHOW_FRAME") == "1";

        _menu = new ContextMenuStrip();
        _menu.Items.Add("新增便籤…", null, (_, _) => AddNoteRequested?.Invoke(this, new NoteRequestedEventArgs(_lastContextScreenPoint, DesktopNoteKind.Note)));
        _menu.Items.Add("新增代辦清單…", null, (_, _) => AddNoteRequested?.Invoke(this, new NoteRequestedEventArgs(_lastContextScreenPoint, DesktopNoteKind.Todo)));
        _menu.Items.Add(_data.Kind == DesktopNoteKind.Todo ? "編輯代辦清單" : "編輯便籤內容", null, (_, _) => EditRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("樣式…", null, (_, _) => EditStyle());
        _menu.Items.Add("複製樣式", null, (_, _) => OverlayStyleClipboard.Copy(_style));
        var pasteStyle = new ToolStripMenuItem("貼上樣式");
        pasteStyle.Click += (_, _) => { if (OverlayStyleClipboard.Paste() is { } style) ApplyStyle(style, true); };
        _menu.Items.Add(pasteStyle);
        _floatingItem = new ToolStripMenuItem(_isFloating ? "回到桌面" : "變成懸浮視窗");
        _floatingItem.Click += (_, _) => { if (_isFloating) ReturnToDesktop(); else MakeFloating(); };
        _menu.Items.Add(_floatingItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_data.Kind == DesktopNoteKind.Todo ? "刪除代辦清單" : "刪除便籤", null, (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty));
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
    private bool IsTodo => _data.Kind == DesktopNoteKind.Todo;
    public bool IsFloating => _isFloating;
    internal IReadOnlyList<string> ContextMenuLabels => _menu.Items.Cast<ToolStripItem>().Select(value => value.Text ?? "").ToArray();
    internal bool HasScrollableContent => _contentHeight > SafeContentBounds.Height;
    internal int ScrollOffset => _scrollOffset;
    internal int LastDistributionExtra => _lastDistributionExtra;
    internal bool UsesNoActivateForTest => (CreateParams.ExStyle & 0x08000000) != 0;
    internal IntPtr MouseActivateResultForTest() { var message = Message.Create(Handle, 0x0021, IntPtr.Zero, IntPtr.Zero); WndProc(ref message); return message.Result; }
    internal bool HasAddChecklistButtonForTest => !_addChecklistBounds.IsEmpty;
    internal void SetResizeCornerForTest(OverlayResizeCorner corner) { _resizeHoverCorner = corner; _frameVisible = true; RenderLayer(); }
    internal void RenderForTest() { _ = Handle; RenderLayer(); }
    internal void ScrollForTest(int delta)
    {
        _scrollOffset += delta; ClampScroll(); RenderLayer();
    }
    internal void SetInlineDraftForTest(string text)
    {
        _editingBody = true; _editingChecklistItemId = null; _editingText = text; _editingCaretIndex = text.Length; _frameVisible = true;
        var qaPath = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH");
        Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH", null);
        _scrollOffset = 0; RenderLayer();
        if (!string.IsNullOrWhiteSpace(qaPath)) Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH", qaPath);
        _scrollOffset = Math.Max(0, _contentHeight - SafeContentBounds.Height); RenderLayer();
    }
    public void PersistState() => CapturePlacement();
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExLayered = 0x00080000;
            const int WsExToolWindow = 0x00000080;
            const int WsExNoActivate = 0x08000000;
            const int WsOverlappedWindow = 0x00CF0000;
            const int WsPopup = unchecked((int)0x80000000);
            const int WsExWindowEdge = 0x00000100;
            var value = base.CreateParams;
            value.Style = (value.Style & ~WsOverlappedWindow) | WsPopup;
            value.ExStyle &= ~WsExWindowEdge;
            value.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
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
        InteractionActivated?.Invoke(this, EventArgs.Empty);
        if (!_isFloating) EnsureDesktopZOrder();
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
        if (_inlineEditor is null)
        {
            var link = _linkBounds.LastOrDefault(value => value.Bounds.Contains(e.Location));
            if (!string.IsNullOrWhiteSpace(link.Target))
            {
                OpenLinkTarget(link.Target);
                return;
            }
        }
        if (ScrollbarThumbBounds.Contains(e.Location))
        {
            _scrollDragging = true;
            _scrollDragStartY = e.Y;
            _scrollDragStartOffset = _scrollOffset;
            return;
        }
        if (IsTodo && _addChecklistBounds.Contains(e.Location))
        {
            var item = new DesktopChecklistItem(); _data.Items.Insert(0, item); RenderLayer(); BeginInlineChecklistEdit(item, true);
            return;
        }
        var hit = IsTodo ? _checkBounds.FirstOrDefault(value => value.Bounds.Contains(e.Location)) : default;
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
        UpdateResizeHover(e.Location);
        Cursor = _inlineEditor is null && _linkBounds.Any(value => value.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
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
        var hit = IsTodo ? _checkBounds.FirstOrDefault(value => value.Bounds.Contains(e.Location)) : default;
        if (hit.Item is not null)
        {
            _checkClickTimer.Stop(); _pendingCheckItem = null; BeginInlineChecklistEdit(hit.Item, false); return;
        }
        if (!IsTodo && !_addChecklistBounds.Contains(e.Location)) BeginInlineBodyEdit();
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
                OverlayStyleRendering.DrawResizeIndicator(graphics, frameBounds, _style.CornerRadius, _resizeHoverCorner);
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
        _checkTextBounds.Clear(); _linkBounds.Clear(); _addChecklistBounds = Rectangle.Empty;
        var safe = SafeContentBounds;
        var originalState = graphics.Save();
        graphics.SetClip(safe);
        if (_editingSelectionBounds.Count > 0)
        {
            using var selectionBrush = new SolidBrush(Color.FromArgb(125, 37, 99, 235));
            foreach (var bounds in _editingSelectionBounds) graphics.FillRectangle(selectionBrush, bounds);
        }
        var width = Math.Max(1, safe.Width);
        var y = safe.Top - _scrollOffset;
        var itemStep = Math.Max(_style.LineHeight, (int)Math.Ceiling(_font.GetHeight(graphics)) + 6) + _style.LineSpacing;
        _lastDistributionExtra = 0;
        var bodyTop = y;
        var bodyText = _editingBody ? _editingText ?? "" : _data.Text;
        if (!IsTodo && !string.IsNullOrWhiteSpace(bodyText))
        {
            using var textBrush = new SolidBrush(_style.TextColor);
            using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
            y = DrawMarkdownText(graphics, bodyText, safe.Left, y, width, shadowBrush, textBrush) + 10;
            if (_editingBody && _editingSelectionLength == 0) DrawBodyCaret(graphics, bodyText[..Math.Clamp(_editingCaretIndex, 0, bodyText.Length)], safe, bodyTop);
        }
        else if (!IsTodo && _editingBody)
        {
            using var caretPen = new Pen(Color.FromArgb(235, 96, 205, 255), 2F);
            graphics.DrawLine(caretPen, safe.Left, bodyTop, safe.Left, bodyTop + Math.Max(18, _font.Height));
        }
        _bodyTextBounds = Rectangle.FromLTRB(safe.Left, bodyTop, safe.Right, Math.Min(safe.Bottom, Math.Max(bodyTop + Math.Max(itemStep, 32), y)));
        var ordered = IsTodo ? _data.Items.OrderBy(value => value.Completed).ToList() : [];
        if (IsTodo && ordered.Count == 0)
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
            var itemText = _editingChecklistItemId == item.Id ? _editingText ?? "" : item.Text;
            var displayText = itemText;
            string? linkTarget = null;
            if (_editingChecklistItemId != item.Id && MarkdownNoteRenderer.TryGetSingleLink(itemText, out var linkText, out var target))
            {
                displayText = linkText;
                linkTarget = target;
            }
            Font? linkFont = null;
            var drawFont = font;
            if (linkTarget is not null)
            {
                linkFont = new Font(font, font.Style | FontStyle.Underline);
                drawFont = linkFont;
            }
            DrawSimpleText(graphics, displayText, drawFont, itemBounds, shadowBrush, textBrush);
            if (linkTarget is not null)
            {
                var linkWidth = Math.Min(itemBounds.Width, graphics.MeasureString(displayText, drawFont, PointF.Empty, StringFormat.GenericTypographic).Width);
                _linkBounds.Add((new RectangleF(itemBounds.Location, new SizeF(linkWidth, Math.Max(drawFont.Height, itemBounds.Height))), linkTarget));
            }
            linkFont?.Dispose();
            if (_editingChecklistItemId == item.Id && _editingSelectionLength == 0) DrawChecklistCaret(graphics, itemText[..Math.Clamp(_editingCaretIndex, 0, itemText.Length)], font, itemBounds);
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
                if (line.Count > 0 && line[^1].Style == run.Style && line[^1].IsCode == run.IsCode && line[^1].Link == run.Link)
                    line[^1] = line[^1] with { Text = line[^1].Text + character };
                else line.Add(new MarkdownRun(character, run.Style, run.IsCode, run.Link));
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
            var runStart = cursor;
            foreach (var character in value.Run.Text.Select(ch => ch.ToString()))
            {
                var characterWidth = graphics.MeasureString(character, value.Font, PointF.Empty, StringFormat.GenericTypographic).Width;
                if (value.Run.IsCode) { using var codeBackground = new SolidBrush(Color.FromArgb(75, 255, 255, 255)); graphics.FillRectangle(codeBackground, new RectangleF(cursor - 2, y, characterWidth + 4, value.Font.Height + 2)); }
                graphics.DrawString(character, value.Font, shadow, cursor + 1, y + 1, StringFormat.GenericTypographic);
                graphics.DrawString(character, value.Font, foreground, cursor, y, StringFormat.GenericTypographic);
                cursor += characterWidth + extra;
            }
            if (!string.IsNullOrWhiteSpace(value.Run.Link))
                _linkBounds.Add((new RectangleF(runStart, y, Math.Max(2, cursor - runStart), Math.Max(value.Font.Height, _style.LineHeight)), value.Run.Link));
            value.Font.Dispose();
        }
    }

    private static void OpenLinkTarget(string target)
    {
        try
        {
            var value = target;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is "http" or "https") value = uri.AbsoluteUri;
                else if (uri.IsFile) value = uri.LocalPath;
                else return;
            }
            if (!File.Exists(value) && !Directory.Exists(value) && !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UriFormatException)
        {
            MessageBox.Show(ex.Message, "無法開啟連結", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private void DrawBodyCaret(Graphics graphics, string text, Rectangle bounds, int top)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var caretY = top;
        List<MarkdownRun> lastWrapped = [];
        Font? lastBaseFont = null;
        try
        {
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var source = lines[lineIndex];
                var parsed = MarkdownNoteRenderer.ParseLine(source);
                lastBaseFont?.Dispose(); lastBaseFont = CreateMarkdownFont(parsed.HeadingLevel, FontStyle.Regular);
                var wrapped = WrapRuns(graphics, BuildRuns(parsed), lastBaseFont, bounds.Width);
                if (wrapped.Count == 0) wrapped.Add([]);
                var step = Math.Max(_style.LineHeight, (int)Math.Ceiling(lastBaseFont.GetHeight(graphics)) + 4) + _style.LineSpacing;
                if (lineIndex < lines.Length - 1) caretY += wrapped.Count * step;
                else { caretY += Math.Max(0, wrapped.Count - 1) * step; lastWrapped = wrapped[^1]; }
            }
            var width = 0F;
            foreach (var run in lastWrapped)
            {
                using var font = CreateRunFont(lastBaseFont ?? _font, run);
                width += graphics.MeasureString(run.Text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
            }
            var x = _style.TextAlignment switch
            {
                OverlayTextAlignment.Center => bounds.Left + Math.Max(0, (bounds.Width - width) / 2) + width,
                OverlayTextAlignment.Right => bounds.Right,
                _ => bounds.Left + Math.Min(bounds.Width, width)
            };
            using var pen = new Pen(Color.FromArgb(235, 96, 205, 255), 2F);
            graphics.DrawLine(pen, x, caretY, x, caretY + Math.Max(18, lastBaseFont?.Height ?? _font.Height));
        }
        finally { lastBaseFont?.Dispose(); }
    }

    private void DrawChecklistCaret(Graphics graphics, string text, Font font, RectangleF bounds)
    {
        var width = graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        var x = _style.TextAlignment switch
        {
            OverlayTextAlignment.Center => bounds.Left + Math.Max(0, (bounds.Width - width) / 2) + width,
            OverlayTextAlignment.Right => bounds.Right,
            _ => bounds.Left + Math.Min(bounds.Width, width)
        };
        using var pen = new Pen(Color.FromArgb(235, 96, 205, 255), 2F);
        graphics.DrawLine(pen, x, bounds.Top, x, bounds.Top + Math.Max(18, font.Height));
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
        _editingBody = true; _editingChecklistItemId = null; _editingText = _data.Text; _editingCaretIndex = _data.Text.Length; _editingSelectionLength = 0; _editingSelectionBounds.Clear(); RenderEditingAtEnd();
        var local = Rectangle.Intersect(_bodyTextBounds, SafeContentBounds);
        if (local.Width <= 0 || local.Height <= 0) local = SafeContentBounds;
        var editor = new InlineTextEditor(RectangleToScreen(local), _data.Text, true, _isFloating, _font);
        _inlineEditor = editor;
        editor.PreviewChanged += snapshot =>
        {
            if (!ReferenceEquals(_inlineEditor, editor)) return;
            ApplyInlineSnapshot(snapshot, local);
            if (snapshot.SelectionStart == snapshot.Text.Length && snapshot.SelectionLength == 0) RenderEditingAtEnd(); else RenderLayer();
        };
        editor.Committed += value => { if (!ReferenceEquals(_inlineEditor, editor)) return; _data.Text = value.TrimEnd(); _scrollOffset = 0; FinishInlineEdit(editor); NoteChanged?.Invoke(this, EventArgs.Empty); };
        editor.Canceled += () => { if (ReferenceEquals(_inlineEditor, editor)) FinishInlineEdit(editor); };
        editor.Show();
        if (!_isFloating) EnsureDesktopZOrder();
    }

    private void BeginInlineChecklistEdit(DesktopChecklistItem item, bool removeOnCancel)
    {
        CloseInlineEditor(); _editingBody = false; _editingChecklistItemId = item.Id; _editingText = item.Text; _editingCaretIndex = item.Text.Length; _editingSelectionLength = 0; _editingSelectionBounds.Clear(); RenderLayer();
        if (!_checkTextBounds.TryGetValue(item.Id, out var local)) local = new Rectangle(SafeContentBounds.Left + 30, SafeContentBounds.Top, Math.Max(80, SafeContentBounds.Width - 30), Math.Max(28, _style.LineHeight));
        var editor = new InlineTextEditor(RectangleToScreen(local), item.Text, false, _isFloating, item.Completed ? _completedFont : _font);
        _inlineEditor = editor;
        editor.PreviewChanged += snapshot => { if (ReferenceEquals(_inlineEditor, editor)) { ApplyInlineSnapshot(snapshot, local); RenderLayer(); } };
        editor.Committed += value => { if (!ReferenceEquals(_inlineEditor, editor)) return; item.Text = value.Trim(); if (item.Text.Length == 0) _data.Items.Remove(item); _data.Items = _data.Items.OrderBy(value => value.Completed).ToList(); FinishInlineEdit(editor); NoteChanged?.Invoke(this, EventArgs.Empty); };
        editor.Canceled += () => { if (!ReferenceEquals(_inlineEditor, editor)) return; if (removeOnCancel) _data.Items.Remove(item); FinishInlineEdit(editor); if (removeOnCancel) NoteChanged?.Invoke(this, EventArgs.Empty); };
        editor.Show();
        if (!_isFloating) EnsureDesktopZOrder();
    }

    private void FinishInlineEdit(InlineTextEditor editor)
    {
        if (ReferenceEquals(_inlineEditor, editor)) _inlineEditor = null;
        _editingBody = false; _editingChecklistItemId = null; _editingText = null; _editingCaretIndex = 0; _editingSelectionLength = 0; _editingSelectionBounds.Clear(); RenderLayer();
    }
    private void ApplyInlineSnapshot(InlineEditSnapshot snapshot, Rectangle localBounds)
    {
        _editingText = snapshot.Text;
        _editingCaretIndex = snapshot.SelectionStart;
        _editingSelectionLength = snapshot.SelectionLength;
        _editingSelectionBounds.Clear();
        _editingSelectionBounds.AddRange(snapshot.SelectionRectangles.Select(bounds => new Rectangle(
            localBounds.Left + bounds.Left,
            localBounds.Top + bounds.Top,
            Math.Min(bounds.Width, Math.Max(1, localBounds.Right - localBounds.Left - bounds.Left)),
            Math.Min(bounds.Height, Math.Max(1, localBounds.Bottom - localBounds.Top - bounds.Top))))
            .Where(bounds => bounds.Width > 0 && bounds.Height > 0));
    }
    private void RenderEditingAtEnd()
    {
        _scrollOffset = 0; RenderLayer();
        _scrollOffset = Math.Max(0, _contentHeight - SafeContentBounds.Height); RenderLayer();
    }
    private void CloseInlineEditor()
    {
        if (_inlineEditor is { IsDisposed: false } editor) editor.Close();
        _inlineEditor = null; _editingBody = false; _editingChecklistItemId = null; _editingText = null; _editingCaretIndex = 0; _editingSelectionLength = 0; _editingSelectionBounds.Clear(); RenderLayer();
    }
    private bool IsResizeCorner(Point point) => (point.X < 18 || point.X >= ClientSize.Width - 18) && (point.Y < 18 || point.Y >= ClientSize.Height - 18);

    private void EditStyle()
    {
        var original = _style.Clone();
        using var dialog = new OverlayStyleDialog(_style.Clone(), IsTodo ? "代辦清單" : "便籤");
        dialog.PreviewChanged += (_, _) => ApplyStyle(dialog.SelectedStyle, true);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            ApplyStyle(dialog.SelectedStyle, true);
        else
            ApplyStyle(original, true);
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
        if (ResolvePlacement is not null)
            Bounds = ResolvePlacement(this, Bounds);
        SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        RenderLayer();
        NoteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureDesktopZOrder() => SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

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
        var corner = visible ? OverlayStyleRendering.GetResizeCorner(PointToClient(Cursor.Position), ClientSize) : OverlayResizeCorner.None;
        if (visible == _frameVisible && corner == _resizeHoverCorner) return;
        _frameVisible = visible;
        _resizeHoverCorner = corner;
        RenderLayer();
    }

    private void UpdateResizeHover(Point point)
    {
        var corner = OverlayStyleRendering.GetResizeCorner(point, ClientSize);
        if (corner == _resizeHoverCorner) return;
        _resizeHoverCorner = corner; RenderLayer();
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
        const int WmNcHitTest = 0x0084, WmMouseActivate = 0x0021, WmMoving = 0x0216, WmSizing = 0x0214, WmExitSizeMove = 0x0232;
        if (message.Msg == WmMouseActivate && !_isFloating)
        {
            message.Result = (IntPtr)3;
            EnsureDesktopZOrder();
            return;
        }
        if (message.Msg == WmMoving && message.LParam != IntPtr.Zero)
        {
            var bounds = Marshal.PtrToStructure<NativeRect>(message.LParam).ToRectangle();
            var constrained = DesktopLayeredOverlayForm.ConstrainRectangle(bounds, Screen.FromRectangle(bounds).WorkingArea);
            if (!_isFloating)
                PlacementPreview?.Invoke(this, constrained);
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
            PlacementCompleted?.Invoke(this);
            CapturePlacement();
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

    private void CapturePlacement()
    {
        _data.Bounds = Bounds;
        var placement = DisplayLayoutIdentity.CapturePlacement(Bounds);
        _data.MonitorDeviceName = placement.DeviceName;
        _data.MonitorRelativeX = placement.RelativeX;
        _data.MonitorRelativeY = placement.RelativeY;
    }

    internal void SetBoundsWithoutSaving(Rectangle bounds)
    {
        Bounds = DesktopLayeredOverlayForm.ConstrainRectangle(bounds, Screen.FromRectangle(bounds).WorkingArea);
        RenderLayer();
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
