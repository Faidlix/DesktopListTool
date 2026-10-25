// 功能：以逐像素透明視窗顯示桌面項目、小圖示與可獨立保存的完整字型／間距／圓角樣式。
// 使用：由 DesktopListTool 的 --list compact|standard|large 入口建立；位置、範圍與項目尺寸自動保存。
// 設定：自訂清單會完整容納項目；項目可排序、跨磁碟安全移動、按 Ctrl 複製，或拖到桌面建立清單。

using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopListTool;

internal sealed class DesktopLayeredOverlayForm : Form
{
    private const int ContentPadding = 10;
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 100 };
    private readonly List<LayeredEntry> _entries = [];
    private readonly HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher? _personalWatcher;
    private readonly FileSystemWatcher? _publicWatcher;
    private readonly string? _folderPath;
    private readonly CustomListData? _customList;
    private ContextMenuStrip? _menu;
    private Font _itemFont = null!;
    private ListItemSize _itemSize;
    private int _iconSize;
    private int _rowHeight;
    private int _columnWidth;
    private int _scrollOffset;
    private int _hoveredIndex = -1;
    private int _selectedIndex = -1;
    private bool _frameVisible;
    private OverlayResizeCorner _resizeHoverCorner;
    private Color _backgroundColor;
    private byte _backgroundOpacity;
    private bool _followThemeColor;
    private OverlayStyleOptions _style = null!;
    private Point _lastContextMenuScreenPoint;
    private bool _isFloating;
    private Point _dragStart;
    private int _dragCandidateIndex = -1;
    private int _dropFolderIndex = -1;
    private string? _dropTargetPath;
    private bool _dropIsArchive;
    private int _dropInsertIndex = -1;
    private OverlaySortMode _sortMode;
    private bool _sortAscending;
    private OverlayDisplayMode _displayMode;
    private List<LayeredEntry>? _dragOriginalOrder;
    private bool _dragCancelled;
    private bool _dropIsCopy;
    private Point _lastDragClientPoint;

    public event EventHandler? AddFolderRequested;
    public event EventHandler? CloseListRequested;
    public event EventHandler<NoteRequestedEventArgs>? AddNoteRequested;
    public event EventHandler<CustomListRequestedEventArgs>? CreateCustomListRequested;
    public event EventHandler? CustomPathsChanged;
    public event EventHandler? PlacementChanged;
    public event EventHandler? CheckUpdateRequested;
    internal Action<Form, Rectangle>? PlacementPreview;
    internal Action<Form>? PlacementCompleted;
    public string? FolderPath => _folderPath;
    public bool IsCustomList => _customList is not null;
    public Guid? CustomListId => _customList?.Id;
    public bool IsFloating => _isFloating;
    internal IReadOnlyList<string> ContextMenuLabels => _menu?.Items.Cast<ToolStripItem>().Select(value => value.Text ?? "").ToArray() ?? [];
    internal IReadOnlyList<string> EntryPaths => _entries.Select(value => value.Path).ToArray();
    internal bool OwnsScreenPointForTest(Point screenPoint) => WindowFromPoint(screenPoint) == Handle;
    internal void SetResizeCornerForTest(OverlayResizeCorner corner) { _resizeHoverCorner = corner; _frameVisible = true; RenderLayer(); }
    internal Func<DesktopLayeredOverlayForm, Rectangle, Rectangle>? ResolvePlacement;
    private string? SettingsIdentity => _customList is null ? _folderPath : $"custom:{_customList.Id:N}";

    public DesktopLayeredOverlayForm(ListItemSize itemSize, string? folderPath = null, Rectangle? defaultBounds = null, CustomListData? customList = null)
    {
        _customList = customList;
        _folderPath = string.IsNullOrWhiteSpace(folderPath) ? null : Path.GetFullPath(folderPath);
        Text = DesktopListOverlayForm.WindowTitle;
        Name = DesktopListOverlayForm.WindowTitle;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = OverlaySettings.LoadBounds(SettingsIdentity, defaultBounds);
        DoubleBuffered = false;
        AllowDrop = true;
        (_sortMode, _sortAscending) = OverlayOrderSettings.LoadSort(SettingsIdentity);
        _displayMode = OverlayOrderSettings.LoadDisplayMode(SettingsIdentity);
        _style = OverlayAppearanceSettings.LoadStyle(SettingsIdentity, DefaultStyle(itemSize));
        var qaStyleJson = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_STYLE_JSON");
        if (!string.IsNullOrWhiteSpace(qaStyleJson))
        {
            try { _style = System.Text.Json.JsonSerializer.Deserialize<OverlayStyleOptions>(qaStyleJson) ?? _style; }
            catch (System.Text.Json.JsonException) { }
        }
        _backgroundColor = _style.FollowThemeColor ? OverlayAppearanceSettings.GetWindowsThemeColor() : _style.BackgroundColor;
        _backgroundOpacity = _style.BackgroundOpacity;
        _followThemeColor = _style.FollowThemeColor;
        _frameVisible = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_SHOW_FRAME") == "1";
        _isFloating = OverlaySettings.LoadFloating(SettingsIdentity) || Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_FLOATING") == "1";
        SystemEvents.UserPreferenceChanged += HandleUserPreferenceChanged;

        _menu = BuildContextMenu();
        _hoverTimer.Tick += (_, _) => UpdateFrameVisibility();
        _hoverTimer.Start();
        MouseMove += HandleMouseMove;
        MouseLeave += (_, _) => SetHoveredIndex(-1);
        MouseDown += HandleMouseDown;
        MouseDoubleClick += HandleMouseDoubleClick;
        MouseWheel += HandleMouseWheel;
        KeyDown += HandleKeyDown;
        DragEnter += HandleDragEnter;
        DragOver += HandleDragOver;
        DragLeave += (_, _) => ClearDropFeedback();
        DragDrop += HandleDragDrop;
        GiveFeedback += HandleGiveFeedback;
        QueryContinueDrag += (_, e) => _dragCancelled |= e.EscapePressed;

        _personalWatcher = _customList is null ? CreateWatcher(_folderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) : null;
        _publicWatcher = _folderPath is null && _customList is null
            ? CreateWatcher(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory))
            : null;

        SetItemSize(itemSize);
        ReloadItems();
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
            var parameters = base.CreateParams;
            parameters.Style = (parameters.Style & ~WsOverlappedWindow) | WsPopup;
            parameters.ExStyle &= ~WsExWindowEdge;
            parameters.ExStyle |= WsExLayered | WsExToolWindow;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_folderPath is null)
            DesktopViewController.SetDesktopIconsVisible(false);
        RenderLayer();
        if (_isFloating)
        {
            TopMost = true;
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        else
        {
            SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_style is null) return;
        ClampScroll();
        RenderLayer();
    }

    public void SetItemSize(ListItemSize size)
    {
        _itemSize = size;
        _iconSize = size switch
        {
            ListItemSize.Compact => 16,
            ListItemSize.Large => 32,
            _ => 24
        };
        _itemFont?.Dispose();
        _itemFont = OverlayStyleDialog.CreateFont(_style);
        _rowHeight = Math.Max(Math.Max(_iconSize + 12, _style.LineHeight), (int)Math.Ceiling(_itemFont.GetHeight()) + 8) + _style.LineSpacing;
        _columnWidth = CalculateColumnWidth();
        ListItemSizeSettings.Save(size);
        UpdateMinimumSize();
        ClampScroll();
        RevealFrame();
        RenderLayer();
    }

    private static OverlayStyleOptions DefaultStyle(ListItemSize size) => new()
    {
        BackgroundColor = Color.Black,
        BackgroundOpacity = 0,
        GradientOpacity = 0,
        FontSize = size switch { ListItemSize.Compact => 9F, ListItemSize.Large => 12F, _ => 10F },
        LineHeight = size switch { ListItemSize.Compact => 28, ListItemSize.Large => 46, _ => 36 }
    };

    public void RevealFrame()
    {
        if (_frameVisible)
            return;
        _frameVisible = true;
        RenderLayer();
    }

    public void SetExcludedPaths(IEnumerable<string> paths)
    {
        _excludedPaths.Clear();
        foreach (var path in paths.Where(value => !string.IsNullOrWhiteSpace(value)))
            _excludedPaths.Add(path == DesktopOverlayEntry.RecycleBinPath ? path : Path.GetFullPath(path));
        ReloadItems();
    }

    public void PersistState()
    {
        OverlaySettings.SaveBounds(SettingsIdentity, Bounds);
        OverlaySettings.SaveFloating(SettingsIdentity, _isFloating);
        OverlayAppearanceSettings.SaveStyle(SettingsIdentity, _style);
    }

    private void ReloadItems()
    {
        var selectedPath = SelectedEntry?.Path;
        foreach (var entry in _entries)
            entry.Icon.Dispose();
        _entries.Clear();

        var sourceEntries = _customList is null
            ? DesktopOverlayEntry.Load(_folderPath)
            : DesktopOverlayEntry.LoadPaths(_customList.Paths);
        if (_customList is null && _folderPath is null && _excludedPaths.Count > 0)
            sourceEntries = sourceEntries.Where(entry => !_excludedPaths.Contains(entry.Path)).ToArray();
        foreach (var entry in OrderEntries(sourceEntries))
        {
            var icon = DesktopOverlayEntry.GetIcon(entry.Path, entry.IsDirectory, _displayMode == OverlayDisplayMode.Icons) ??
                       (Icon)(entry.IsDirectory ? SystemIcons.WinLogo : SystemIcons.Application).Clone();
            var info = entry.IsDirectory || entry.IsVirtual ? null : new FileInfo(entry.Path);
            _entries.Add(new LayeredEntry(entry.Name, entry.Path, entry.IsDirectory, entry.IsVirtual, icon,
                entry.IsDirectory ? 0 : SafeFileLength(info), entry.IsVirtual ? DateTime.MinValue : SafeLastWrite(entry.Path)));
        }

        _selectedIndex = selectedPath is null
            ? -1
            : _entries.FindIndex(entry => string.Equals(entry.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        _columnWidth = CalculateColumnWidth();
        UpdateMinimumSize();
        ClampScroll();
        RenderLayer();
    }

    private void UpdateMinimumSize()
    {
        if (_itemFont is null)
            return;
        var widestText = _entries.Count == 0
            ? 120
            : _entries.Max(entry => TextRenderer.MeasureText(entry.Name, _itemFont, Size.Empty, TextFormatFlags.NoPadding).Width);
        var screenWidth = Screen.FromRectangle(Bounds).WorkingArea.Width;
        var minimumWidth = _displayMode == OverlayDisplayMode.Icons
            ? Math.Clamp(IconCellWidth + ContentPadding * 2, 140, Math.Max(140, screenWidth - 80))
            : Math.Clamp(widestText + _iconSize + 42, 220, Math.Max(220, screenWidth - 80));
        var minimumHeight = (_displayMode == OverlayDisplayMode.Icons ? IconCellHeight : _rowHeight) + ContentPadding * 2;
        MinimumSize = new Size(minimumWidth, minimumHeight);
        if (Width < minimumWidth)
            Width = minimumWidth;
        var requiredHeight = _customList is null ? minimumHeight : GetRequiredCustomHeight(Width);
        if (Height < requiredHeight)
            Height = requiredHeight;
    }

    internal void FitToContentMinimum()
    {
        if (_customList is null) return;
        Size = new Size(MinimumSize.Width, GetRequiredCustomHeight(MinimumSize.Width));
        RenderLayer();
    }

    private int CalculateColumnWidth()
    {
        if (_itemFont is null || _entries.Count == 0)
            return 220;
        var widestText = _entries.Max(entry => TextRenderer.MeasureText(entry.Name, _itemFont, Size.Empty, TextFormatFlags.NoPadding).Width);
        return Math.Clamp(widestText + _iconSize + 42, 180, 520);
    }

    private bool UseAdaptiveCustomLayout => _customList is not null;
    private bool UseIconDisplay => _displayMode == OverlayDisplayMode.Icons;
    private bool UseHorizontalLayout => !UseAdaptiveCustomLayout && ClientSize.Width >= ClientSize.Height * 1.35;

    private int ColumnPitch => _columnWidth + _style.HorizontalSpacing;
    private int IconDisplaySize => _itemSize switch { ListItemSize.Compact => 32, ListItemSize.Large => 64, _ => 48 };
    private int IconCellWidth => _itemSize switch { ListItemSize.Compact => 96, ListItemSize.Large => 156, _ => 126 };
    private int IconCellHeight => IconDisplaySize + Math.Max(34, (int)Math.Ceiling(_itemFont.GetHeight()) * 2) + 18 + _style.LineSpacing;
    private int IconColumnPitch => IconCellWidth + _style.HorizontalSpacing;
    private int GetIconColumnCount(int width) => Math.Max(1, Math.Max(1, width - ContentPadding * 2) / Math.Max(1, IconColumnPitch));
    private int GetIconRowCount(int width) => Math.Max(1, (int)Math.Ceiling(_entries.Count / (double)GetIconColumnCount(width)));

    private int GetCustomColumnCount(int width) => UseIconDisplay ? GetIconColumnCount(width) : Math.Max(1, Math.Max(1, width - ContentPadding * 2) / Math.Max(1, ColumnPitch));
    private int GetCustomRowCount(int width) => UseIconDisplay ? GetIconRowCount(width) : Math.Max(1, (int)Math.Ceiling(_entries.Count / (double)GetCustomColumnCount(width)));
    private int GetRequiredCustomHeight(int width)
    {
        var step = UseIconDisplay ? IconCellHeight : LayoutRowHeight;
        var minimum = step + ContentPadding * 2;
        var desired = GetCustomRowCount(width) * step + ContentPadding * 2;
        return Math.Clamp(desired, minimum, Math.Max(minimum, Screen.FromRectangle(Bounds).WorkingArea.Height - 80));
    }

    internal int AdaptiveColumnCountForTest(int width) => GetCustomColumnCount(width);
    internal int RequiredContentHeightForTest(int width) => GetRequiredCustomHeight(width);
    internal Size LayoutMetricsForTest => new(_columnWidth, _rowHeight);
    internal OverlayDisplayMode DisplayModeForTest => _displayMode;
    internal void SetTextMetricsForTest(float fontSize, int lineHeight, int lineSpacing)
    {
        var style = _style.Clone();
        style.FontSize = fontSize; style.LineHeight = lineHeight; style.LineSpacing = lineSpacing;
        ApplyStyle(style, save: false);
    }
    internal void SetHorizontalSpacingForTest(int spacing)
    {
        var style = _style.Clone(); style.HorizontalSpacing = spacing; ApplyStyle(style, save: false);
    }
    internal void ToggleDisplayModeForTest()
    {
        _displayMode = _displayMode == OverlayDisplayMode.List ? OverlayDisplayMode.Icons : OverlayDisplayMode.List;
        _scrollOffset = 0; ReloadItems();
    }

    private int RowsPerColumn => Math.Max(1, (ClientSize.Height - ContentPadding * 2) / Math.Max(1, _rowHeight));
    private int LayoutRowHeight => _rowHeight;

    private void HandleMouseMove(object? sender, MouseEventArgs e)
    {
        RevealFrame();
        UpdateResizeHover(e.Location);
        SetHoveredIndex(GetEntryIndexAt(e.Location));
        if (e.Button == MouseButtons.Left && _dragCandidateIndex >= 0 &&
            Math.Abs(e.X - _dragStart.X) + Math.Abs(e.Y - _dragStart.Y) >= SystemInformation.DragSize.Width / 2)
            BeginItemDrag(_dragCandidateIndex);
    }

    private void HandleMouseDown(object? sender, MouseEventArgs e)
    {
        RevealFrame();
        if (e.Button == MouseButtons.Left && _isFloating && FloatingReturnButtonBounds.Contains(e.Location))
        {
            ReturnToDesktop();
            return;
        }
        var index = GetEntryIndexAt(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            _lastContextMenuScreenPoint = PointToScreen(e.Location);
            if (index >= 0)
                SelectIndex(index);
            else
                ClearSelection();
            if (index >= 0 && NativeShellContextMenu.TryShowIsolated(Handle, _entries[index].Path, _lastContextMenuScreenPoint))
                return;
            _menu?.Show(this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;
        if (index >= 0)
        {
            SelectIndex(index);
            _dragCandidateIndex = index;
            _dragStart = e.Location;
            return;
        }

        _dragCandidateIndex = -1;
        ClearSelection();
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    private void HandleMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        var index = GetEntryIndexAt(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0)
        {
            SelectIndex(index);
            OpenSelected();
        }
    }

    private void HandleMouseWheel(object? sender, MouseEventArgs e)
    {
        if (UseIconDisplay)
            _scrollOffset -= Math.Sign(e.Delta) * IconCellHeight * 2;
        else if (UseHorizontalLayout)
            _scrollOffset -= Math.Sign(e.Delta) * ColumnPitch;
        else
            _scrollOffset -= Math.Sign(e.Delta) * _rowHeight * 3;
        ClampScroll();
        SetHoveredIndex(GetEntryIndexAt(PointToClient(Cursor.Position)));
        RenderLayer();
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
            OpenSelected();
        else if (e.KeyCode == Keys.F5)
            ReloadItems();
        else if (e.Control && e.KeyCode == Keys.C)
            CopySelectedToClipboard();
    }

    private void SelectIndex(int index)
    {
        if (_selectedIndex == index)
            return;
        _selectedIndex = index;
        RenderLayer();
    }

    private void ClearSelection()
    {
        if (_selectedIndex < 0)
            return;
        _selectedIndex = -1;
        RenderLayer();
    }

    public void ClearSelectionFromDesktopClick() => ClearSelection();

    private void SetHoveredIndex(int index)
    {
        if (_hoveredIndex == index)
            return;
        _hoveredIndex = index;
        RenderLayer();
    }

    private int GetEntryIndexAt(Point point)
    {
        if (point.X < ContentPadding || point.X >= ClientSize.Width - ContentPadding ||
            point.Y < ContentPadding || point.Y >= ClientSize.Height - ContentPadding)
            return -1;
        int index;
        if (UseIconDisplay)
        {
            var contentX = point.X - ContentPadding;
            var contentY = point.Y - ContentPadding + _scrollOffset;
            var column = contentX / Math.Max(1, IconColumnPitch);
            if (column >= GetIconColumnCount(ClientSize.Width) || contentX % Math.Max(1, IconColumnPitch) >= IconCellWidth) return -1;
            var row = contentY / Math.Max(1, IconCellHeight);
            index = row * GetIconColumnCount(ClientSize.Width) + column;
        }
        else if (UseAdaptiveCustomLayout)
        {
            var contentX = point.X - ContentPadding;
            var contentY = point.Y - ContentPadding + _scrollOffset;
            var column = contentX / Math.Max(1, ColumnPitch);
            if (column >= GetCustomColumnCount(ClientSize.Width) || contentX % Math.Max(1, ColumnPitch) >= _columnWidth) return -1;
            var row = contentY / Math.Max(1, LayoutRowHeight);
            index = row * GetCustomColumnCount(ClientSize.Width) + column;
        }
        else if (UseHorizontalLayout)
        {
            var contentX = point.X - ContentPadding + _scrollOffset;
            var column = contentX / Math.Max(1, ColumnPitch);
            if (contentX % Math.Max(1, ColumnPitch) >= _columnWidth) return -1;
            var row = (point.Y - ContentPadding) / Math.Max(1, LayoutRowHeight);
            index = column * RowsPerColumn + row;
        }
        else
        {
            var contentY = point.Y - ContentPadding + _scrollOffset;
            index = contentY / Math.Max(1, LayoutRowHeight);
        }
        return index >= 0 && index < _entries.Count ? index : -1;
    }

    private void ClampScroll()
    {
        int maximum;
        if (UseIconDisplay)
        {
            var viewportHeight = Math.Max(0, ClientSize.Height - ContentPadding * 2);
            maximum = Math.Max(0, GetIconRowCount(ClientSize.Width) * IconCellHeight - viewportHeight);
        }
        else if (UseAdaptiveCustomLayout)
        {
            var viewportHeight = Math.Max(0, ClientSize.Height - ContentPadding * 2);
            maximum = Math.Max(0, GetCustomRowCount(ClientSize.Width) * LayoutRowHeight - viewportHeight);
        }
        else if (UseHorizontalLayout)
        {
            var columns = (int)Math.Ceiling(_entries.Count / (double)RowsPerColumn);
            var viewportWidth = Math.Max(0, ClientSize.Width - ContentPadding * 2);
            maximum = Math.Max(0, columns * ColumnPitch - viewportWidth);
        }
        else
        {
            var viewportHeight = Math.Max(0, ClientSize.Height - ContentPadding * 2);
            maximum = Math.Max(0, _entries.Count * LayoutRowHeight - viewportHeight);
        }
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maximum);
    }

    private void UpdateFrameVisibility()
    {
        var visible = Rectangle.Inflate(Bounds, 18, 18).Contains(Cursor.Position) || _menu?.Visible == true;
        var corner = visible ? OverlayStyleRendering.GetResizeCorner(PointToClient(Cursor.Position), ClientSize) : OverlayResizeCorner.None;
        if (visible == _frameVisible && corner == _resizeHoverCorner)
            return;
        _frameVisible = visible;
        _resizeHoverCorner = corner;
        if (!visible)
            _hoveredIndex = -1;
        RenderLayer();
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
            using var inputHitBrush = _customList is null ? null : OverlayStyleRendering.CreateInputHitBrush(_backgroundColor);
            if (inputHitBrush is not null)
            {
                if (_style.CornerRadius > 0)
                    graphics.FillRoundedRectangle(inputHitBrush, ClientRectangle, Math.Min(_style.CornerRadius, Math.Min(ClientSize.Width, ClientSize.Height) / 2));
                else
                    graphics.FillRectangle(inputHitBrush, ClientRectangle);
            }
            using var hitTargetBrush = OverlayStyleRendering.CreateBackgroundBrush(ClientRectangle, _style, _backgroundColor);
            if (_style.CornerRadius > 0)
                graphics.FillRoundedRectangle(hitTargetBrush, ClientRectangle, Math.Min(_style.CornerRadius, Math.Min(ClientSize.Width, ClientSize.Height) / 2));
            else
                graphics.FillRectangle(hitTargetBrush, ClientRectangle);

            if (UseIconDisplay)
                DrawIconEntries(graphics);
            else if (UseAdaptiveCustomLayout)
                DrawAdaptiveCustomEntries(graphics);
            else if (UseHorizontalLayout)
                DrawHorizontalEntries(graphics);
            else
                DrawVerticalEntries(graphics);
            DrawDropIndicator(graphics);
            DrawFolderDropHint(graphics);

            if (_frameVisible)
            {
                using var framePen = new Pen(Color.FromArgb(225, 96, 165, 250), 2F);
                var frameBounds = new Rectangle(1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
                if (_style.CornerRadius > 0) graphics.DrawRoundedRectangle(framePen, frameBounds, Math.Min(_style.CornerRadius, Math.Min(frameBounds.Width, frameBounds.Height) / 2));
                else graphics.DrawRectangle(framePen, frameBounds);
                OverlayStyleRendering.DrawResizeIndicator(graphics, frameBounds, _style.CornerRadius, _resizeHoverCorner);
            }
            if (_isFloating)
                DrawFloatingReturnButton(graphics);
        }
        var qaPath = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_PATH");
        if (!string.IsNullOrWhiteSpace(qaPath) && _entries.Count > 0)
        {
            try
            {
                bitmap.Save(qaPath, ImageFormat.Png);
                Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_PATH", null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        ApplyLayeredBitmap(bitmap);
    }

    private void UpdateResizeHover(Point point)
    {
        var corner = OverlayStyleRendering.GetResizeCorner(point, ClientSize);
        if (corner == _resizeHoverCorner) return;
        _resizeHoverCorner = corner; RenderLayer();
    }

    private void DrawVerticalEntries(Graphics graphics)
    {
        var step = LayoutRowHeight;
        var firstIndex = _scrollOffset / step;
        var y = ContentPadding - (_scrollOffset % step);
        for (var index = firstIndex; index < _entries.Count && y < ClientSize.Height - ContentPadding; index++, y += step)
            DrawEntry(graphics, _entries[index], index, new Rectangle(ContentPadding, y, Math.Max(1, ClientSize.Width - ContentPadding * 2), step));
    }

    private void DrawHorizontalEntries(Graphics graphics)
    {
        var rows = RowsPerColumn;
        for (var index = 0; index < _entries.Count; index++)
        {
            var column = index / rows;
            var row = index % rows;
            var x = ContentPadding + column * ColumnPitch - _scrollOffset;
            if (x + _columnWidth < ContentPadding || x >= ClientSize.Width - ContentPadding)
                continue;
            var y = ContentPadding + row * LayoutRowHeight;
            DrawEntry(graphics, _entries[index], index, new Rectangle(x, y, _columnWidth, LayoutRowHeight));
        }
    }

    private void DrawAdaptiveCustomEntries(Graphics graphics)
    {
        var columns = GetCustomColumnCount(ClientSize.Width);
        for (var index = 0; index < _entries.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var y = ContentPadding + row * LayoutRowHeight - _scrollOffset;
            if (y + LayoutRowHeight < ContentPadding || y >= ClientSize.Height - ContentPadding) continue;
            var x = ContentPadding + column * ColumnPitch;
            DrawEntry(graphics, _entries[index], index, new Rectangle(x, y, _columnWidth, LayoutRowHeight));
        }
    }

    private void DrawIconEntries(Graphics graphics)
    {
        var columns = GetIconColumnCount(ClientSize.Width);
        using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
        using var textBrush = new SolidBrush(_style.TextColor);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter };
        for (var index = 0; index < _entries.Count; index++)
        {
            var row = index / columns; var column = index % columns;
            var bounds = new Rectangle(ContentPadding + column * IconColumnPitch,
                ContentPadding + row * IconCellHeight - _scrollOffset, IconCellWidth, IconCellHeight);
            if (bounds.Bottom < ContentPadding || bounds.Top >= ClientSize.Height - ContentPadding) continue;
            if (index == _hoveredIndex || index == _selectedIndex)
            {
                using var selection = new SolidBrush(index == _selectedIndex ? Color.FromArgb(135, 37, 99, 235) : Color.FromArgb(80, 71, 85, 105));
                graphics.FillRoundedRectangle(selection, bounds, 8);
            }
            var iconBounds = new Rectangle(bounds.Left + (bounds.Width - IconDisplaySize) / 2, bounds.Top + 6, IconDisplaySize, IconDisplaySize);
            graphics.DrawIcon(_entries[index].Icon, iconBounds);
            var textBounds = new RectangleF(bounds.Left + 3, iconBounds.Bottom + 5, bounds.Width - 6, bounds.Bottom - iconBounds.Bottom - 7);
            var shadowBounds = textBounds; shadowBounds.Offset(1, 1);
            graphics.DrawString(_entries[index].Name, _itemFont, shadowBrush, shadowBounds, format);
            graphics.DrawString(_entries[index].Name, _itemFont, textBrush, textBounds, format);
            if (index == _dropFolderIndex)
            {
                using var targetPen = new Pen(Color.FromArgb(245, 96, 165, 250), 2F);
                graphics.DrawRectangle(targetPen, Rectangle.Inflate(bounds, -1, -1));
            }
        }
    }

    private void DrawEntry(Graphics graphics, LayeredEntry entry, int index, Rectangle rowBounds)
    {
        var visualBounds = GetVisualEntryBounds(entry, rowBounds);
        if (index == _hoveredIndex || index == _selectedIndex)
        {
            using var rowBrush = new SolidBrush(index == _selectedIndex
                ? Color.FromArgb(135, 37, 99, 235)
                : Color.FromArgb(80, 71, 85, 105));
            graphics.FillRoundedRectangle(rowBrush, visualBounds, 6);
        }

        var iconY = visualBounds.Y + (visualBounds.Height - _iconSize) / 2;
        graphics.DrawIcon(entry.Icon, new Rectangle(visualBounds.X + 6, iconY, _iconSize, _iconSize));
        var textX = visualBounds.X + _iconSize + 14;
        var textY = visualBounds.Y + (visualBounds.Height - _itemFont.GetHeight(graphics)) / 2F;
        using var shadowBrush = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
        using var textBrush = new SolidBrush(_style.TextColor);
        DrawEntryText(graphics, entry.Name, textX, textY, Math.Max(1, visualBounds.Right - textX - 5), shadowBrush, textBrush);
        if (index == _dropFolderIndex)
        {
            using var targetPen = new Pen(Color.FromArgb(245, 96, 165, 250), 2F);
            graphics.DrawRectangle(targetPen, Rectangle.Inflate(visualBounds, -1, -1));
        }
    }

    private void DrawEntryText(Graphics graphics, string text, float x, float y, float width, Brush shadow, Brush foreground)
    {
        if (_style.Distribution != OverlayContentDistribution.Fill || text.Length < 2)
        {
            graphics.DrawString(text, _itemFont, shadow, x + 1, y + 1); graphics.DrawString(text, _itemFont, foreground, x, y); return;
        }
        var widths = text.Select(value => graphics.MeasureString(value.ToString(), _itemFont, PointF.Empty, StringFormat.GenericTypographic).Width).ToArray();
        var extra = Math.Max(0, width - widths.Sum()) / (text.Length - 1); var cursor = x;
        for (var i = 0; i < text.Length; i++) { var value = text[i].ToString(); graphics.DrawString(value, _itemFont, shadow, cursor + 1, y + 1, StringFormat.GenericTypographic); graphics.DrawString(value, _itemFont, foreground, cursor, y, StringFormat.GenericTypographic); cursor += widths[i] + extra; }
    }

    private Rectangle GetVisualEntryBounds(LayeredEntry entry, Rectangle rowBounds)
    {
        var textWidth = TextRenderer.MeasureText(entry.Name, _itemFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        var width = Math.Min(rowBounds.Width, Math.Max(_iconSize + 24, textWidth + _iconSize + 26));
        var x = _style.TextAlignment switch
        {
            OverlayTextAlignment.Center => rowBounds.Left + Math.Max(0, (rowBounds.Width - width) / 2),
            OverlayTextAlignment.Right => rowBounds.Right - width,
            _ => rowBounds.Left
        };
        return new Rectangle(x, rowBounds.Y, width, rowBounds.Height);
    }

    private void DrawDropIndicator(Graphics graphics)
    {
        if (_dropInsertIndex < 0 || _dropFolderIndex >= 0)
            return;
        using var pen = new Pen(Color.FromArgb(245, 96, 165, 250), 3F);
        if (_entries.Count == 0)
        {
            graphics.DrawLine(pen, ContentPadding, ContentPadding, ClientSize.Width - ContentPadding, ContentPadding);
            return;
        }
        var index = Math.Clamp(_dropInsertIndex, 0, _entries.Count);
        var reference = GetEntryBounds(Math.Min(index, _entries.Count - 1));
        var y = index >= _entries.Count ? reference.Bottom : reference.Top;
        graphics.DrawLine(pen, reference.Left + 3, y, reference.Right - 3, y);
    }

    private void DrawFolderDropHint(Graphics graphics)
    {
        if (_dropFolderIndex < 0 || _dropFolderIndex >= _entries.Count)
            return;
        var text = _dropIsArchive ? $"→ 加入壓縮檔 {_entries[_dropFolderIndex].Name}" : $"→ {(_dropIsCopy ? "複製至" : "移動至")} {_entries[_dropFolderIndex].Name}";
        using var font = new Font("Microsoft JhengHei UI", 10F);
        var size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
        var x = Math.Clamp(_lastDragClientPoint.X + 18, 4, Math.Max(4, ClientSize.Width - size.Width - 22));
        var y = Math.Clamp(_lastDragClientPoint.Y + 18, 4, Math.Max(4, ClientSize.Height - size.Height - 14));
        var bounds = new Rectangle(x, y, size.Width + 16, size.Height + 10);
        using var background = new SolidBrush(Color.FromArgb(245, 250, 250, 250));
        using var border = new Pen(Color.FromArgb(100, 100, 100));
        using var textBrush = new SolidBrush(Color.FromArgb(15, 62, 180));
        graphics.FillRectangle(background, bounds);
        graphics.DrawRectangle(border, bounds);
        graphics.DrawString(text, font, textBrush, bounds.X + 8, bounds.Y + 5);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("開啟", null, (_, _) => OpenSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("緊湊項目", null, (_, _) => SetItemSize(ListItemSize.Compact));
        menu.Items.Add("標準項目", null, (_, _) => SetItemSize(ListItemSize.Standard));
        menu.Items.Add("大型項目", null, (_, _) => SetItemSize(ListItemSize.Large));
        var displayModeItem = new ToolStripMenuItem(_displayMode == OverlayDisplayMode.List ? "變成桌面圖示樣式" : "變回清單樣式");
        displayModeItem.Click += (_, _) => ToggleDisplayMode();
        menu.Items.Add(displayModeItem);
        var sortMenu = new ToolStripMenuItem("排序方式");
        sortMenu.DropDownItems.Add("自訂順序", null, (_, _) => SetSortMode(OverlaySortMode.Custom));
        sortMenu.DropDownItems.Add("名稱", null, (_, _) => SetSortMode(OverlaySortMode.Name));
        sortMenu.DropDownItems.Add("類型", null, (_, _) => SetSortMode(OverlaySortMode.Type));
        sortMenu.DropDownItems.Add("大小", null, (_, _) => SetSortMode(OverlaySortMode.Size));
        sortMenu.DropDownItems.Add("修改日期", null, (_, _) => SetSortMode(OverlaySortMode.Modified));
        menu.Items.Add(sortMenu);
        menu.Items.Add("反轉排序", null, (_, _) => { _sortAscending = !_sortAscending; SaveSortAndReload(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("樣式…", null, (_, _) => EditStyle());
        menu.Items.Add("複製樣式", null, (_, _) => OverlayStyleClipboard.Copy(_style));
        var pasteStyle = new ToolStripMenuItem("貼上樣式");
        pasteStyle.Click += (_, _) => { if (OverlayStyleClipboard.Paste() is { } style) ApplyStyle(style, save: true); };
        menu.Items.Add(pasteStyle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("新增資料夾清單…", null, (_, _) => AddFolderRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("新增便籤…", null, (_, _) => AddNoteRequested?.Invoke(this, new NoteRequestedEventArgs(_lastContextMenuScreenPoint)));
        menu.Items.Add("變成懸浮視窗", null, (_, _) => MakeFloating());
        if (_folderPath is not null || _customList is not null)
            menu.Items.Add("關閉清單", null, (_, _) => CloseListRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        var autoUpdate = new ToolStripMenuItem("自動更新") { CheckOnClick = false, Checked = AutoUpdateManager.IsEnabled };
        autoUpdate.Click += (_, _) =>
        {
            AutoUpdateManager.SetEnabled(!AutoUpdateManager.IsEnabled);
            autoUpdate.Checked = AutoUpdateManager.IsEnabled;
        };
        menu.Items.Add(autoUpdate);
        menu.Items.Add("立即檢查更新", null, (_, _) => CheckUpdateRequested?.Invoke(this, EventArgs.Empty));
        menu.Opening += (_, _) =>
        {
            autoUpdate.Checked = AutoUpdateManager.IsEnabled; pasteStyle.Enabled = OverlayStyleClipboard.HasStyle;
            displayModeItem.Text = _displayMode == OverlayDisplayMode.List ? "變成桌面圖示樣式" : "變回清單樣式";
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("重新整理", null, (_, _) => ReloadItems());
        return menu;
    }

    private void ToggleDisplayMode()
    {
        _displayMode = _displayMode == OverlayDisplayMode.List ? OverlayDisplayMode.Icons : OverlayDisplayMode.List;
        _scrollOffset = 0;
        OverlayOrderSettings.SaveDisplayMode(SettingsIdentity, _displayMode);
        ReloadItems();
    }

    private void EditStyle()
    {
        var original = _style.Clone();
        using var dialog = new OverlayStyleDialog(_style.Clone(), "清單");
        dialog.PreviewChanged += (_, _) => ApplyStyle(dialog.SelectedStyle, save: true);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            ApplyStyle(dialog.SelectedStyle, save: true);
        else
            ApplyStyle(original, save: true);
    }

    private void ApplyStyle(OverlayStyleOptions style, bool save)
    {
        _style = style.Clone();
        _followThemeColor = _style.FollowThemeColor;
        var effectiveColor = _followThemeColor ? OverlayAppearanceSettings.GetWindowsThemeColor() : _style.BackgroundColor;
        _backgroundColor = Color.FromArgb(effectiveColor.R, effectiveColor.G, effectiveColor.B);
        _backgroundOpacity = _style.BackgroundOpacity;
        _itemFont?.Dispose();
        _itemFont = OverlayStyleDialog.CreateFont(_style);
        _rowHeight = Math.Max(Math.Max(_iconSize + 12, _style.LineHeight), (int)Math.Ceiling(_itemFont.GetHeight()) + 8) + _style.LineSpacing;
        _columnWidth = CalculateColumnWidth();
        UpdateMinimumSize();
        ClampScroll();
        if (save)
            OverlayAppearanceSettings.SaveStyle(SettingsIdentity, _style);
        RenderLayer();
    }

    private Rectangle FloatingReturnButtonBounds => new(Math.Max(4, ClientSize.Width - 34), 4, 28, 28);

    private void MakeFloating()
    {
        _isFloating = true;
        OverlaySettings.SaveFloating(SettingsIdentity, true);
        TopMost = true;
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        RenderLayer();
    }

    private void ReturnToDesktop()
    {
        _isFloating = false;
        OverlaySettings.SaveFloating(SettingsIdentity, false);
        TopMost = false;
        if (ResolvePlacement is not null)
            Bounds = ResolvePlacement(this, Bounds);
        OverlaySettings.SaveBounds(SettingsIdentity, Bounds);
        SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        RenderLayer();
    }

    private void DrawFloatingReturnButton(Graphics graphics)
    {
        using var background = new SolidBrush(Color.FromArgb(225, 30, 41, 59));
        graphics.FillRoundedRectangle(background, FloatingReturnButtonBounds, 6);
        using var pen = new Pen(Color.White, 2F);
        var centerX = FloatingReturnButtonBounds.Left + FloatingReturnButtonBounds.Width / 2;
        graphics.DrawEllipse(pen, centerX - 5, FloatingReturnButtonBounds.Top + 5, 10, 7);
        graphics.DrawLine(pen, centerX - 6, FloatingReturnButtonBounds.Top + 14, centerX + 6, FloatingReturnButtonBounds.Top + 14);
        graphics.DrawLine(pen, centerX, FloatingReturnButtonBounds.Top + 12, centerX, FloatingReturnButtonBounds.Bottom - 5);
    }

    private void HandleUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_followThemeColor || IsDisposed)
            return;
        _backgroundColor = OverlayAppearanceSettings.GetWindowsThemeColor();
        _style.BackgroundColor = _backgroundColor;
        if (IsHandleCreated)
            BeginInvoke(RenderLayer);
    }

    private void OpenSelected()
    {
        var entry = SelectedEntry;
        if (entry is null)
            return;
        try
        {
            if (entry.IsVirtual)
                Process.Start(new ProcessStartInfo("explorer.exe", entry.Path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "無法開啟", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private LayeredEntry? SelectedEntry => _selectedIndex >= 0 && _selectedIndex < _entries.Count ? _entries[_selectedIndex] : null;

    private IEnumerable<DesktopOverlayEntry> OrderEntries(IEnumerable<DesktopOverlayEntry> source)
    {
        var entries = source.ToList();
        if (_sortMode == OverlaySortMode.Custom)
        {
            var order = OverlayOrderSettings.LoadCustomOrder(SettingsIdentity);
            var ranks = order.Select((path, index) => (path, index))
                .ToDictionary(value => value.path, value => value.index, StringComparer.OrdinalIgnoreCase);
            return entries.OrderBy(entry => ranks.TryGetValue(entry.Path, out var rank) ? rank : int.MaxValue)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase);
        }
        Func<DesktopOverlayEntry, object> key = _sortMode switch
        {
            OverlaySortMode.Type => entry => entry.IsDirectory ? "" : Path.GetExtension(entry.Path),
            OverlaySortMode.Size => entry => entry.IsDirectory ? -1L : SafeFileLength(new FileInfo(entry.Path)),
            OverlaySortMode.Modified => entry => SafeLastWrite(entry.Path),
            _ => entry => entry.Name
        };
        var ordered = _sortAscending
            ? entries.OrderBy(key).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            : entries.OrderByDescending(key).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase);
        return ordered;
    }

    private static long SafeFileLength(FileInfo? info)
    {
        try { return info?.Exists == true ? info.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private void SetSortMode(OverlaySortMode mode)
    {
        _sortMode = mode;
        SaveSortAndReload();
    }

    private void SaveSortAndReload()
    {
        OverlayOrderSettings.SaveSort(SettingsIdentity, _sortMode, _sortAscending);
        ReloadItems();
    }

    private void CopySelectedToClipboard()
    {
        if (SelectedEntry is not { } entry || entry.IsVirtual)
            return;
        var files = new StringCollection { entry.Path };
        Clipboard.SetFileDropList(files);
    }

    private void BeginItemDrag(int index)
    {
        if (index < 0 || index >= _entries.Count)
            return;
        _dragCandidateIndex = -1;
        _dragCancelled = false;
        _dragOriginalOrder = _entries.ToList();
        var data = new DataObject();
        if (!CanBeginItemDrag(_entries[index].IsVirtual, _entries[index].Path))
            return;
        data.SetData("DesktopListTool.SourcePath", _entries[index].Path);
        var effect = DoDragDrop(data, DragDropEffects.Move | DragDropEffects.Copy);
        if (effect == DragDropEffects.None && _dragOriginalOrder is not null)
        {
            _entries.Clear();
            _entries.AddRange(_dragOriginalOrder);
            RenderLayer();
        }
        _dragOriginalOrder = null;
        var dropPoint = Cursor.Position;
        ClearDropFeedback();
        if (effect == DragDropEffects.None && !_dragCancelled && !Bounds.Contains(dropPoint) && IsDesktopSurfaceAt(dropPoint))
            CreateCustomListRequested?.Invoke(this, new CustomListRequestedEventArgs(_entries[index].Path, dropPoint));
        if (effect != DragDropEffects.None)
        {
            if (_customList is not null && effect == DragDropEffects.Move && !Bounds.Contains(dropPoint))
            {
                _customList.Paths.RemoveAll(path => string.Equals(path, _entries[index].Path, StringComparison.OrdinalIgnoreCase));
                var becameEmpty = _customList.Paths.Count == 0;
                CustomPathsChanged?.Invoke(this, EventArgs.Empty);
                if (becameEmpty) { CloseListRequested?.Invoke(this, EventArgs.Empty); return; }
            }
            ReloadItems();
        }
    }

    private void HandleGiveFeedback(object? sender, GiveFeedbackEventArgs e)
    {
        var point = Cursor.Position;
        if (ShouldShowDesktopDropCursor(e.Effect, Bounds.Contains(point), IsDesktopSurfaceAt(point)))
        {
            e.UseDefaultCursors = false;
            Cursor.Current = Cursors.SizeAll;
            return;
        }
        e.UseDefaultCursors = true;
    }

    internal static bool ShouldShowDesktopDropCursor(DragDropEffects effect, bool insideSource, bool overDesktop) =>
        effect == DragDropEffects.None && !insideSource && overDesktop;

    internal static bool CanBeginItemDrag(bool isVirtual, string path) =>
        !isVirtual || string.Equals(path, DesktopOverlayEntry.RecycleBinPath, StringComparison.OrdinalIgnoreCase);

    private void HandleDragEnter(object? sender, DragEventArgs e) => UpdateDragTarget(e);

    private void HandleDragOver(object? sender, DragEventArgs e) => UpdateDragTarget(e);

    private void UpdateDragTarget(DragEventArgs e)
    {
        if (!e.Data!.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent("DesktopListTool.SourcePath"))
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        var client = PointToClient(new Point(e.X, e.Y));
        _lastDragClientPoint = client;
        var index = GetEntryIndexAt(client);
        var sourcePath = e.Data.GetData("DesktopListTool.SourcePath") as string;
        var sourceIsVirtual = string.Equals(sourcePath, DesktopOverlayEntry.RecycleBinPath, StringComparison.OrdinalIgnoreCase);
        _dropTargetPath = null; _dropIsArchive = false; _dropFolderIndex = -1;
        if (!sourceIsVirtual && index >= 0 && !_entries[index].IsVirtual && !string.Equals(sourcePath, _entries[index].Path, StringComparison.OrdinalIgnoreCase) && IsFolderDropZone(client, index))
        {
            var candidate = _entries[index];
            if (candidate.IsDirectory) _dropTargetPath = candidate.Path;
            else if (NativeShellDropTarget.TryResolveFolderShortcut(candidate.Path, out var shortcutFolder)) _dropTargetPath = shortcutFolder;
            else if (NativeShellDropTarget.IsArchivePath(candidate.Path)) { _dropTargetPath = candidate.Path; _dropIsArchive = true; }
            if (_dropTargetPath is not null) _dropFolderIndex = index;
        }
        _dropInsertIndex = _dropFolderIndex >= 0 ? -1 : GetInsertionIndex(client);
        _dropIsCopy = (e.KeyState & 8) != 0;
        if (sourceIsVirtual && _customList is null && !_entries.Any(value => string.Equals(value.Path, sourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            e.Effect = DragDropEffects.None;
            RenderLayer();
            return;
        }
        e.Effect = _dropIsArchive || _dropIsCopy ? DragDropEffects.Copy : DragDropEffects.Move;
        RenderLayer();
    }

    private bool IsFolderDropZone(Point point, int index)
    {
        var bounds = GetEntryBounds(index);
        var folderZone = Rectangle.Inflate(bounds, 0, -Math.Max(4, bounds.Height / 5));
        return IsFolderDropTarget(folderZone, point);
    }

    internal static bool IsFolderDropTarget(Rectangle bounds, Point point) => bounds.Contains(point);

    private int GetInsertionIndex(Point point)
    {
        var index = GetEntryIndexAt(point);
        if (index < 0)
            return _entries.Count;
        var bounds = GetEntryBounds(index);
        return UseIconDisplay
            ? (point.X < bounds.Left + bounds.Width / 2 ? index : index + 1)
            : UseAdaptiveCustomLayout
            ? (point.X < bounds.Left + bounds.Width / 2 ? index : index + 1)
            : UseHorizontalLayout
            ? (point.Y < bounds.Top + bounds.Height / 2 ? index : index + 1)
            : (point.Y < bounds.Top + bounds.Height / 2 ? index : index + 1);
    }

    private Rectangle GetEntryBounds(int index)
    {
        if (UseIconDisplay)
        {
            var columns = GetIconColumnCount(ClientSize.Width);
            var row = index / columns; var column = index % columns;
            return new Rectangle(ContentPadding + column * IconColumnPitch,
                ContentPadding + row * IconCellHeight - _scrollOffset, IconCellWidth, IconCellHeight);
        }
        if (UseAdaptiveCustomLayout)
        {
            var columns = GetCustomColumnCount(ClientSize.Width);
            var row = index / columns;
            var column = index % columns;
            return new Rectangle(ContentPadding + column * ColumnPitch,
                ContentPadding + row * LayoutRowHeight - _scrollOffset, _columnWidth, LayoutRowHeight);
        }
        if (UseHorizontalLayout)
        {
            var column = index / RowsPerColumn;
            var row = index % RowsPerColumn;
            return new Rectangle(ContentPadding + column * ColumnPitch - _scrollOffset,
                ContentPadding + row * LayoutRowHeight, _columnWidth, LayoutRowHeight);
        }
        return new Rectangle(ContentPadding, ContentPadding + index * LayoutRowHeight - _scrollOffset,
            Math.Max(1, ClientSize.Width - ContentPadding * 2), LayoutRowHeight);
    }

    private void HandleDragDrop(object? sender, DragEventArgs e)
    {
        var paths = e.Data?.GetData(DataFormats.FileDrop) as string[];
        if (paths is not { Length: > 0 } && e.Data?.GetData("DesktopListTool.SourcePath") is string sourcePath)
            paths = [sourcePath];
        if (paths is not { Length: > 0 })
            return;
        try
        {
            if (_dropFolderIndex >= 0)
            {
                if (_dropTargetPath is null) return;
                if (_dropIsArchive)
                {
                    if (!NativeShellDropTarget.TryDropIsolated(_dropTargetPath, paths, e.Effect == DragDropEffects.Copy)) throw new IOException("無法啟動壓縮檔的系統拖放處理程式。");
                }
                else TransferPaths(paths, _dropTargetPath, e.Effect == DragDropEffects.Copy);
            }
            else if (paths.Length == 1 && e.Data!.GetDataPresent("DesktopListTool.SourcePath") &&
                     _entries.Any(item => string.Equals(item.Path, paths[0], StringComparison.OrdinalIgnoreCase)))
            {
                var sourceIndex = _entries.FindIndex(item => string.Equals(item.Path, paths[0], StringComparison.OrdinalIgnoreCase));
                var destination = Math.Clamp(_dropInsertIndex, 0, _entries.Count);
                var item = _entries[sourceIndex];
                _entries.RemoveAt(sourceIndex);
                if (destination > sourceIndex) destination--;
                _entries.Insert(Math.Clamp(destination, 0, _entries.Count), item);
                _selectedIndex = _entries.IndexOf(item);
                _sortMode = OverlaySortMode.Custom;
                OverlayOrderSettings.SaveSort(SettingsIdentity, _sortMode, true);
                OverlayOrderSettings.SaveCustomOrder(SettingsIdentity, _entries.Select(value => value.Path));
                SyncCustomPaths();
            }
            else if (_customList is not null)
            {
                foreach (var path in paths.Where(path => !_customList.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                    _customList.Paths.Add(path);
                CustomPathsChanged?.Invoke(this, EventArgs.Empty);
                ReloadItems();
            }
            else
            {
                TransferPaths(paths, _folderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), e.Effect == DragDropEffects.Copy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "無法完成拖曳", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ClearDropFeedback();
            RenderLayer();
        }
    }

    internal static void TransferPaths(IEnumerable<string> paths, string destinationDirectory, bool copy,
        Func<string, string, bool>? sameVolumeResolver = null)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var source in paths)
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(source) && Path.GetFullPath(destination).StartsWith(Path.GetFullPath(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能把資料夾移動或複製到它自己的子資料夾內。");
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"目標已存在：{destination}");
            if (Directory.Exists(source))
            {
                if (copy) CopyDirectory(source, destination);
                else MovePath(source, destination, isDirectory: true, sameVolumeResolver);
            }
            else if (copy) File.Copy(source, destination);
            else MovePath(source, destination, isDirectory: false, sameVolumeResolver);
        }
    }

    private static void MovePath(string source, string destination, bool isDirectory,
        Func<string, string, bool>? sameVolumeResolver)
    {
        var sameVolume = sameVolumeResolver?.Invoke(source, destination) ?? HaveSameVolumeRoot(source, destination);
        if (sameVolume)
        {
            if (isDirectory) Directory.Move(source, destination); else File.Move(source, destination);
            return;
        }

        var copyCompleted = false;
        try
        {
            if (isDirectory) CopyDirectory(source, destination); else File.Copy(source, destination);
            copyCompleted = true;
            if (isDirectory) Directory.Delete(source, recursive: true); else File.Delete(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!copyCompleted) TryRemoveIncompleteDestination(destination, isDirectory);
            var message = copyCompleted
                ? $"已完整複製到目的地，但無法刪除來源；目的地副本已保留：{destination}"
                : $"跨磁碟移動失敗，來源未刪除：{source}";
            throw new IOException(message, ex);
        }
    }

    internal static bool HaveSameVolumeRoot(string source, string destination)
    {
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));
        return !string.IsNullOrWhiteSpace(sourceRoot) &&
               string.Equals(sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   destinationRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void TryRemoveIncompleteDestination(string destination, bool isDirectory)
    {
        try
        {
            if (isDirectory && Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            else if (!isDirectory && File.Exists(destination)) File.Delete(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private void ClearDropFeedback()
    {
        _dropFolderIndex = -1;
        _dropTargetPath = null;
        _dropIsArchive = false;
        _dropInsertIndex = -1;
        _dropIsCopy = false;
        if (IsHandleCreated) RenderLayer();
    }

    private static bool IsDesktopSurfaceAt(Point screenPoint)
    {
        var window = WindowFromPoint(screenPoint);
        var buffer = new System.Text.StringBuilder(128);
        while (window != IntPtr.Zero)
        {
            buffer.Clear();
            GetClassName(window, buffer, buffer.Capacity);
            var name = buffer.ToString();
            if (name is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32")
                return true;
            window = GetParent(window);
        }
        return false;
    }

    private FileSystemWatcher? CreateWatcher(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;
        var watcher = new FileSystemWatcher(path) { EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, _) => ScheduleReload();
        RenamedEventHandler renamed = (_, _) => ScheduleReload();
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Changed += changed;
        watcher.Renamed += renamed;
        return watcher;
    }

    private void ScheduleReload()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(ReloadItems); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    protected override void WndProc(ref Message message)
    {
        const int WmNcHitTest = 0x0084;
        const int WmMoving = 0x0216;
        const int WmSizing = 0x0214;
        const int WmExitSizeMove = 0x0232;
        const int HtCaption = 2;
        const int HtBottomRight = 17;
        if (message.Msg == WmMoving && message.LParam != IntPtr.Zero)
        {
            var movingBounds = Marshal.PtrToStructure<NativeRect>(message.LParam).ToRectangle();
            movingBounds = ConstrainToScreen(movingBounds);
            if (!_isFloating)
                PlacementPreview?.Invoke(this, movingBounds);
            Marshal.StructureToPtr(NativeRect.FromRectangle(movingBounds), message.LParam, false);
        }
        if (message.Msg == WmSizing && message.LParam != IntPtr.Zero)
        {
            var sizingBounds = Marshal.PtrToStructure<NativeRect>(message.LParam).ToRectangle();
            sizingBounds = ConstrainSizingRectangle(sizingBounds, Screen.FromRectangle(sizingBounds).WorkingArea, message.WParam.ToInt32(), MinimumSize);
            if (_customList is not null)
                sizingBounds = ConstrainCustomContentSizing(sizingBounds, Screen.FromRectangle(sizingBounds).WorkingArea, message.WParam.ToInt32());
            Marshal.StructureToPtr(NativeRect.FromRectangle(sizingBounds), message.LParam, false);
        }

        base.WndProc(ref message);

        if (message.Msg == WmExitSizeMove)
        {
            Bounds = ConstrainToScreen(Bounds);
            PlacementCompleted?.Invoke(this);
            OverlaySettings.SaveBounds(SettingsIdentity, Bounds);
            PlacementChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (message.Msg != WmNcHitTest || !_frameVisible)
            return;

        var screenPoint = new Point((short)(message.LParam.ToInt64() & 0xFFFF), (short)((message.LParam.ToInt64() >> 16) & 0xFFFF));
        var clientPoint = PointToClient(screenPoint);
        if (clientPoint.X < 18 && clientPoint.Y < 18) message.Result = (IntPtr)13;
        else if (clientPoint.X >= ClientSize.Width - 18 && clientPoint.Y < 18) message.Result = (IntPtr)14;
        else if (clientPoint.X < 18 && clientPoint.Y >= ClientSize.Height - 18) message.Result = (IntPtr)16;
        else if (clientPoint.X >= ClientSize.Width - 18 && clientPoint.Y >= ClientSize.Height - 18) message.Result = (IntPtr)HtBottomRight;
        else if (clientPoint.X < 10 || clientPoint.Y < 10 || clientPoint.X > ClientSize.Width - 10 || clientPoint.Y > ClientSize.Height - 10)
            message.Result = (IntPtr)HtCaption;
    }

    private Rectangle ConstrainToScreen(Rectangle bounds)
    {
        var workArea = Screen.FromRectangle(bounds).WorkingArea;
        return ConstrainRectangle(bounds, workArea);
    }

    public void SetBoundsWithoutSaving(Rectangle bounds)
    {
        Bounds = ConstrainToScreen(bounds);
        RenderLayer();
    }

    private void SyncCustomPaths()
    {
        if (_customList is null)
            return;
        _customList.Paths = _entries.Select(value => value.Path).ToList();
        CustomPathsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static Rectangle ConstrainRectangle(Rectangle bounds, Rectangle workArea)
    {
        var maximumX = Math.Max(workArea.Left, workArea.Right - bounds.Width);
        var maximumY = Math.Max(workArea.Top, workArea.Bottom - bounds.Height);
        bounds.X = Math.Clamp(bounds.X, workArea.Left, maximumX);
        bounds.Y = Math.Clamp(bounds.Y, workArea.Top, maximumY);
        return bounds;
    }

    internal static Rectangle ConstrainSizingRectangle(Rectangle bounds, Rectangle workArea, int edge, Size minimum)
    {
        var left = bounds.Left; var top = bounds.Top; var right = bounds.Right; var bottom = bounds.Bottom;
        var sizesLeft = edge is 1 or 4 or 7; var sizesTop = edge is 3 or 4 or 5;
        if (sizesLeft) left = Math.Max(workArea.Left, Math.Min(left, right - minimum.Width)); else right = Math.Min(workArea.Right, Math.Max(right, left + minimum.Width));
        if (sizesTop) top = Math.Max(workArea.Top, Math.Min(top, bottom - minimum.Height)); else bottom = Math.Min(workArea.Bottom, Math.Max(bottom, top + minimum.Height));
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private Rectangle ConstrainCustomContentSizing(Rectangle bounds, Rectangle workArea, int edge)
    {
        var requiredHeight = GetRequiredCustomHeight(bounds.Width);
        if (bounds.Height >= requiredHeight) return bounds;
        var sizesTop = edge is 3 or 4 or 5;
        if (sizesTop) bounds.Y = bounds.Bottom - requiredHeight;
        bounds.Height = requiredHeight;
        return ConstrainRectangle(bounds, workArea);
    }

    private void ApplyLayeredBitmap(Bitmap bitmap)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(memoryDc, bitmapHandle);
        try
        {
            var destination = new NativePoint(Left, Top);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(bitmap.Width, bitmap.Height);
            var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTimer.Dispose();
            _personalWatcher?.Dispose();
            _publicWatcher?.Dispose();
            _menu?.Dispose();
            _itemFont?.Dispose();
            SystemEvents.UserPreferenceChanged -= HandleUserPreferenceChanged;
            foreach (var entry in _entries)
                entry.Icon.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record LayeredEntry(string Name, string Path, bool IsDirectory, bool IsVirtual, Icon Icon, long Size, DateTime Modified);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height) { public int Width = width; public int Height = height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        public static NativeRect FromRectangle(Rectangle value) => new()
        {
            Left = value.Left,
            Top = value.Top,
            Right = value.Right,
            Bottom = value.Bottom
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, int colorKey, ref BlendFunction blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maximum);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
}

internal sealed class NoteRequestedEventArgs(Point screenLocation) : EventArgs
{
    public Point ScreenLocation { get; } = screenLocation;
}

internal sealed class CustomListRequestedEventArgs(string path, Point screenLocation) : EventArgs
{
    public string Path { get; } = path;
    public Point ScreenLocation { get; } = screenLocation;
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }
}
