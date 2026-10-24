// 功能：建立、顯示、編輯與保存桌面便籤，支援 Markdown、多段文字及會自動移到底部的勾選事項。
// 使用：在清單空白處右鍵選「新增便籤」；Enter 換行，只有點「確定」才保存，Esc 取消。
// 設定：便籤右鍵可編輯或刪除；位置、大小、內容與勾選狀態保存於目前使用者登錄設定。

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopListTool;

internal sealed class DesktopNoteData
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public List<DesktopChecklistItem> Items { get; set; } = [];
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 320;
    public int Height { get; set; } = 240;
    public int BackgroundColorArgb { get; set; } = Color.FromArgb(30, 41, 59).ToArgb();
    public byte BackgroundOpacity { get; set; } = 96;
    public bool FollowThemeColor { get; set; } = true;
    public bool GradientEnabled { get; set; }
    public int GradientColorArgb { get; set; } = Color.FromArgb(37, 99, 235).ToArgb();
    public int GradientTransition { get; set; } = 50;
    public OverlayGradientDirection GradientDirection { get; set; }
    public int TextColorArgb { get; set; } = Color.White.ToArgb();
    public bool IsFloating { get; set; }
    public string FontFamily { get; set; } = "Microsoft JhengHei UI";
    public float FontSize { get; set; } = 11F;
    public bool FontBold { get; set; }
    public int LineHeight { get; set; } = 30;
    public int LineSpacing { get; set; }
    public int CornerRadius { get; set; }
    public OverlayTextAlignment TextAlignment { get; set; }
    public OverlayContentDistribution Distribution { get; set; }

    public Rectangle Bounds
    {
        get => new(X, Y, Math.Max(240, Width), Math.Max(140, Height));
        set { X = value.X; Y = value.Y; Width = value.Width; Height = value.Height; }
    }
}

internal static class NoteEditorSettings
{
    private const string KeyPath = @"Software\DesktopListTool";
    public static int LoadSplitter()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return Math.Clamp(Convert.ToInt32(key?.GetValue("NoteEditorSplitter", 220) ?? 220), 120, 520); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or FormatException) { return 220; }
    }
    public static void SaveSplitter(int value)
    {
        try { using var key = Registry.CurrentUser.CreateSubKey(KeyPath); key.SetValue("NoteEditorSplitter", value, RegistryValueKind.DWord); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }
}

internal sealed class DesktopChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public bool Completed { get; set; }
}

internal static class DesktopNotesSettings
{
    private const string SettingsKey = @"Software\DesktopListTool";
    private const string ValueName = "DesktopNotesJson";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static List<DesktopNoteData> Load()
    {
        try
        {
            var qaJson = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_NOTES_JSON");
            if (!string.IsNullOrWhiteSpace(qaJson))
                return JsonSerializer.Deserialize<List<DesktopNoteData>>(qaJson, JsonOptions) ?? [];
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            var json = key?.GetValue(ValueName) as string;
            return string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<DesktopNoteData>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<DesktopNoteData> notes)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            var values = notes.ToArray();
            if (values.Length == 0)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            else
                key.SetValue(ValueName, JsonSerializer.Serialize(values, JsonOptions), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class DesktopNoteForm : Form
{
    private readonly DesktopNoteData _data;
    private readonly Label _body = new();
    private readonly FlowLayoutPanel _items = new();
    private readonly Panel _titleBar = new();
    private readonly ContextMenuStrip _menu = new();
    private bool _rebuilding;

    public event EventHandler? EditRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? NoteChanged;

    public DesktopNoteForm(DesktopNoteData data)
    {
        _data = data;
        Text = $"DesktopListTool.Note.{data.Id:N}";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Constrain(data.Bounds);
        MinimumSize = new Size(240, 140);
        BackColor = Color.FromArgb(255, 248, 190);
        ForeColor = Color.FromArgb(35, 35, 35);
        Font = new Font("Microsoft JhengHei UI", 10F);

        _titleBar.Height = 25;
        _titleBar.Dock = DockStyle.Top;
        _titleBar.BackColor = Color.FromArgb(235, 210, 116);
        _titleBar.Cursor = Cursors.SizeAll;
        _titleBar.MouseDown += BeginDrag;

        _body.AutoEllipsis = false;
        _body.Location = new Point(12, 36);
        _body.MouseDown += BeginDrag;
        _body.DoubleClick += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);

        _items.FlowDirection = FlowDirection.TopDown;
        _items.WrapContents = false;
        _items.AutoScroll = true;
        _items.BackColor = Color.Transparent;

        _menu.Items.Add("編輯便籤內容", null, (_, _) => EditRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("刪除便籤", null, (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty));
        ContextMenuStrip = _menu;
        _titleBar.ContextMenuStrip = _menu;
        _body.ContextMenuStrip = _menu;
        _items.ContextMenuStrip = _menu;
        Controls.AddRange([_items, _body, _titleBar]);
        Resize += (_, _) => LayoutContent();
        RebuildContent();
    }

    public DesktopNoteData Data => _data;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x00000080;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    public void ApplyEditedContent(string text, IEnumerable<DesktopChecklistItem> items)
    {
        _data.Text = text.TrimEnd();
        _data.Items = items.OrderBy(item => item.Completed).ToList();
        RebuildContent();
        NoteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildContent()
    {
        _rebuilding = true;
        _body.Text = _data.Text;
        foreach (var control in _items.Controls.Cast<Control>().ToArray())
            control.Dispose();
        _items.Controls.Clear();
        _data.Items = _data.Items.OrderBy(item => item.Completed).ToList();
        foreach (var item in _data.Items)
        {
            var check = new CheckBox
            {
                AutoSize = false,
                Width = Math.Max(180, ClientSize.Width - 28),
                Height = 30,
                Text = item.Text,
                Checked = item.Completed,
                Font = new Font(Font, item.Completed ? FontStyle.Strikeout : FontStyle.Regular),
                ContextMenuStrip = _menu,
                Tag = item
            };
            check.CheckedChanged += ChecklistChanged;
            _items.Controls.Add(check);
        }
        _rebuilding = false;
        LayoutContent();
    }

    private void ChecklistChanged(object? sender, EventArgs e)
    {
        if (_rebuilding || sender is not CheckBox { Tag: DesktopChecklistItem item } check)
            return;
        item.Completed = check.Checked;
        _data.Items = _data.Items.OrderBy(value => value.Completed).ToList();
        RebuildContent();
        NoteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LayoutContent()
    {
        var bodyWidth = Math.Max(80, ClientSize.Width - 24);
        var proposed = new Size(bodyWidth, int.MaxValue);
        var bodyHeight = string.IsNullOrWhiteSpace(_body.Text)
            ? 0
            : TextRenderer.MeasureText(_body.Text, Font, proposed, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4;
        _body.Size = new Size(bodyWidth, bodyHeight);
        _items.Location = new Point(8, 40 + bodyHeight);
        _items.Size = new Size(Math.Max(80, ClientSize.Width - 16), Math.Max(40, ClientSize.Height - _items.Top - 8));
        foreach (Control control in _items.Controls)
            control.Width = Math.Max(80, _items.ClientSize.Width - 8);
    }

    private void BeginDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    protected override void WndProc(ref Message message)
    {
        const int WmMoving = 0x0216;
        const int WmExitSizeMove = 0x0232;
        if (message.Msg == WmMoving && message.LParam != IntPtr.Zero)
        {
            var native = Marshal.PtrToStructure<NativeRect>(message.LParam);
            var constrained = Constrain(native.ToRectangle());
            Marshal.StructureToPtr(NativeRect.FromRectangle(constrained), message.LParam, false);
        }
        base.WndProc(ref message);
        if (message.Msg == WmExitSizeMove)
        {
            Bounds = Constrain(Bounds);
            _data.Bounds = Bounds;
            NoteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static Rectangle Constrain(Rectangle bounds)
    {
        var workingArea = Screen.FromRectangle(bounds).WorkingArea;
        return DesktopLayeredOverlayForm.ConstrainRectangle(bounds, workingArea);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Control control in _items.Controls)
                control.Font?.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
        public static NativeRect FromRectangle(Rectangle value) => new() { Left = value.Left, Top = value.Top, Right = value.Right, Bottom = value.Bottom };
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

internal sealed class NoteEditorDialog : Form
{
    private readonly TextBox _body = new();
    private readonly FlowLayoutPanel _taskRows = new();
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.None, Panel1MinSize = 100, Panel2MinSize = 120, SplitterWidth = 7 };

    public NoteEditorDialog(DesktopNoteData? existing = null)
    {
        Text = existing is null ? "新增便籤" : "編輯便籤";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(560, 480);
        ClientSize = new Size(760, 640);
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft JhengHei UI", 10F);

        var bodyPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 8) };
        var bodyLabel = new Label { Text = "文字內容（支援 Markdown，Enter 換行）", AutoSize = true, Dock = DockStyle.Top };
        _body.Multiline = true;
        _body.AcceptsReturn = true;
        _body.AcceptsTab = true;
        _body.ScrollBars = ScrollBars.Vertical;
        _body.Text = existing?.Text ?? "";
        _body.Dock = DockStyle.Fill;
        _body.Margin = new Padding(0, 8, 0, 0);
        _body.KeyDown += EditorKeyDown;
        bodyPanel.Controls.Add(_body);
        bodyPanel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        bodyPanel.Controls.Add(bodyLabel);

        var tasksPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 8) };
        var tasksHeader = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, ColumnCount = 2 };
        tasksHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tasksHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tasksLabel = new Label { Text = "勾選事項", AutoSize = true, Anchor = AnchorStyles.Left };
        var addTask = new Button { Text = "＋ 新增勾選事項", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(190, 34), Anchor = AnchorStyles.Right };
        addTask.Click += (_, _) => AddTaskRow(null);
        tasksHeader.Controls.Add(tasksLabel, 0, 0); tasksHeader.Controls.Add(addTask, 1, 0);
        _taskRows.Dock = DockStyle.Fill;
        _taskRows.FlowDirection = FlowDirection.TopDown;
        _taskRows.WrapContents = false;
        _taskRows.AutoScroll = true;
        tasksPanel.Controls.Add(_taskRows); tasksPanel.Controls.Add(tasksHeader);
        _split.Panel1.Controls.Add(bodyPanel); _split.Panel2.Controls.Add(tasksPanel);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(90, 34) };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Size = new Size(90, 34) };
        buttons.Controls.AddRange([cancel, ok]);
        CancelButton = cancel;
        Controls.Add(_split); Controls.Add(buttons);

        foreach (var item in existing?.Items ?? [])
            AddTaskRow(item);
        Shown += (_, _) =>
        {
            _split.SplitterDistance = Math.Clamp(NoteEditorSettings.LoadSplitter(), _split.Panel1MinSize, Math.Max(_split.Panel1MinSize, _split.Height - _split.Panel2MinSize));
            _body.Focus();
        };
        FormClosed += (_, _) => NoteEditorSettings.SaveSplitter(_split.SplitterDistance);
    }

    public string NoteText => _body.Text;

    public IReadOnlyList<DesktopChecklistItem> ChecklistItems => _taskRows.Controls
        .OfType<Panel>()
        .Select(panel => new DesktopChecklistItem
        {
            Id = panel.Tag is DesktopChecklistItem original ? original.Id : Guid.NewGuid(),
            Text = panel.Controls.OfType<TextBox>().First().Text.Trim(),
            Completed = panel.Tag is DesktopChecklistItem existing && existing.Completed
        })
        .Where(item => item.Text.Length > 0)
        .ToArray();

    private void AddTaskRow(DesktopChecklistItem? item)
    {
        var panel = new Panel { Width = Math.Max(420, _taskRows.ClientSize.Width - 28), Height = 42, Tag = item, Margin = new Padding(0, 2, 0, 2) };
        var input = new TextBox { Text = item?.Text ?? "", Location = new Point(0, 4), Width = panel.Width - 48, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
        input.KeyDown += EditorKeyDown;
        var remove = new Button { Text = "×", Location = new Point(panel.Width - 40, 2), Size = new Size(36, 30), Anchor = AnchorStyles.Right | AnchorStyles.Top };
        remove.Click += (_, _) => { _taskRows.Controls.Remove(panel); panel.Dispose(); };
        panel.Controls.AddRange([input, remove]);
        _taskRows.Controls.Add(panel);
        input.Focus();
    }

    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
        else if (e.KeyCode == Keys.Enter && sender != _body)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
    }
}
