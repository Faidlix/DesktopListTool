using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DesktopListTool;

internal sealed class DesktopListOverlayForm : Form
{
    internal const string WindowTitle = "DesktopListTool.OverlayHost";
    private static readonly Color TransparentColor = Color.Magenta;
    private readonly ListView _list = new();
    private readonly ImageList _images = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 120 };
    private readonly FileSystemWatcher? _personalWatcher;
    private readonly FileSystemWatcher? _publicWatcher;
    private bool _frameVisible;
    private ListItemSize _itemSize;

    public DesktopListOverlayForm(ListItemSize itemSize)
    {
        Text = WindowTitle;
        Name = WindowTitle;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = TransparentColor;
        TransparencyKey = TransparentColor;
        MinimumSize = new Size(220, 180);
        Bounds = OverlaySettings.LoadBounds();

        _images.ColorDepth = ColorDepth.Depth32Bit;
        _list.Dock = DockStyle.Fill;
        _list.Margin = Padding.Empty;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = TransparentColor;
        _list.ForeColor = Color.White;
        _list.View = View.Details;
        _list.HeaderStyle = ColumnHeaderStyle.None;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = true;
        _list.SmallImageList = _images;
        _list.Columns.Add("桌面項目", ClientSize.Width - 16);
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.MouseMove += (_, _) => RevealFrame();
        _list.MouseDown += ListMouseDown;
        _list.KeyDown += ListKeyDown;
        _list.Resize += (_, _) => _list.Columns[0].Width = Math.Max(80, _list.ClientSize.Width - 4);
        _list.ContextMenuStrip = BuildItemMenu();
        Controls.Add(_list);
        Padding = new Padding(8);

        _hoverTimer.Tick += (_, _) => UpdateFrameVisibility();
        _hoverTimer.Start();
        Move += (_, _) => OverlaySettings.SaveBounds(Bounds);
        ResizeEnd += (_, _) => OverlaySettings.SaveBounds(Bounds);

        _personalWatcher = CreateWatcher(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        _publicWatcher = CreateWatcher(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

        SetItemSize(itemSize);
        ReloadItems();
    }

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
        DesktopViewController.SetDesktopIconsVisible(false);
        SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    public void SetItemSize(ListItemSize size)
    {
        _itemSize = size;
        var iconSize = size switch
        {
            ListItemSize.Compact => 16,
            ListItemSize.Large => 32,
            _ => 24
        };
        var fontSize = size switch
        {
            ListItemSize.Compact => 9F,
            ListItemSize.Large => 12F,
            _ => 10F
        };
        _images.ImageSize = new Size(iconSize, iconSize);
        _list.Font = new Font("Microsoft JhengHei UI", fontSize, FontStyle.Regular);
        ListItemSizeSettings.Save(size);
        ReloadItems();
        RevealFrame();
    }

    public void RevealFrame()
    {
        if (!_frameVisible)
        {
            _frameVisible = true;
            Invalidate();
        }
    }

    private void UpdateFrameVisibility()
    {
        var expanded = Rectangle.Inflate(Bounds, 18, 18);
        var visible = expanded.Contains(Cursor.Position);
        if (visible == _frameVisible)
            return;
        _frameVisible = visible;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_frameVisible)
            return;
        using var pen = new Pen(Color.FromArgb(210, 96, 165, 250), 2F);
        e.Graphics.DrawRectangle(pen, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
        using var grip = new SolidBrush(Color.FromArgb(220, 96, 165, 250));
        e.Graphics.FillRectangle(grip, ClientSize.Width - 13, ClientSize.Height - 13, 10, 10);
    }

    protected override void WndProc(ref Message message)
    {
        const int WmNcHitTest = 0x0084;
        const int WmAppSetSize = 0x8000 + 42;
        const int WmAppClose = 0x8000 + 43;
        const int HtCaption = 2;
        const int HtBottomRight = 17;

        if (message.Msg == WmAppSetSize)
        {
            SetItemSize((ListItemSize)Math.Clamp(message.WParam.ToInt32(), 0, 2));
            return;
        }
        if (message.Msg == WmAppClose)
        {
            Close();
            return;
        }

        base.WndProc(ref message);
        if (message.Msg != WmNcHitTest || !_frameVisible)
            return;

        var screenPoint = new Point((short)(message.LParam.ToInt64() & 0xFFFF), (short)((message.LParam.ToInt64() >> 16) & 0xFFFF));
        var clientPoint = PointToClient(screenPoint);
        if (clientPoint.X >= ClientSize.Width - 16 && clientPoint.Y >= ClientSize.Height - 16)
            message.Result = (IntPtr)HtBottomRight;
        else if (clientPoint.X < 10 || clientPoint.Y < 10 || clientPoint.X > ClientSize.Width - 10 || clientPoint.Y > ClientSize.Height - 10)
            message.Result = (IntPtr)HtCaption;
    }

    private ContextMenuStrip BuildItemMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("開啟", null, (_, _) => OpenSelected());
        menu.Items.Add("在檔案總管中顯示", null, (_, _) => RevealSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("緊湊項目", null, (_, _) => SetItemSize(ListItemSize.Compact));
        menu.Items.Add("標準項目", null, (_, _) => SetItemSize(ListItemSize.Standard));
        menu.Items.Add("大型項目", null, (_, _) => SetItemSize(ListItemSize.Large));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("重新整理", null, (_, _) => ReloadItems());
        return menu;
    }

    private void ReloadItems()
    {
        var selectedPath = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;
        var entries = DesktopOverlayEntry.Load();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _images.Images.Clear();
            foreach (var entry in entries)
            {
                using var icon = DesktopOverlayEntry.GetIcon(entry.Path, entry.IsDirectory);
                _images.Images.Add(icon ?? (entry.IsDirectory ? SystemIcons.WinLogo : SystemIcons.Application));
                var item = new ListViewItem(entry.Name, _images.Images.Count - 1) { Tag = entry.Path };
                _list.Items.Add(item);
                if (string.Equals(selectedPath, entry.Path, StringComparison.OrdinalIgnoreCase))
                    item.Selected = true;
            }
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private void ListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _list.GetItemAt(e.X, e.Y) is not null)
            return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    private void ListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
            OpenSelected();
        else if (e.KeyCode == Keys.F5)
            ReloadItems();
    }

    private void OpenSelected()
    {
        var path = SelectedPath;
        if (path is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "無法開啟", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RevealSelected()
    {
        var path = SelectedPath;
        if (path is null)
            return;
        var arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    private string? SelectedPath => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    private FileSystemWatcher? CreateWatcher(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;
        var watcher = new FileSystemWatcher(path) { EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, _) => BeginInvoke(ReloadItems);
        RenamedEventHandler renamed = (_, _) => BeginInvoke(ReloadItems);
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Changed += changed;
        watcher.Renamed += renamed;
        return watcher;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hoverTimer.Dispose();
            _personalWatcher?.Dispose();
            _publicWatcher?.Dispose();
            _images.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal static class OverlayMessenger
{
    private static readonly string[] EventNames =
    [
        @"Local\DesktopListTool.Compact",
        @"Local\DesktopListTool.Standard",
        @"Local\DesktopListTool.Large",
        @"Local\DesktopListTool.Close"
    ];

    public static EventWaitHandle[] CreateCommandEvents() =>
        EventNames.Select(name => new EventWaitHandle(false, EventResetMode.AutoReset, name)).ToArray();

    public static bool TrySetSize(ListItemSize size)
    {
        return TrySignal(EventNames[(int)size]);
    }

    public static bool TryClose() => TrySignal(EventNames[3]);

    private static bool TrySignal(string name)
    {
        try
        {
            using var commandEvent = EventWaitHandle.OpenExisting(name);
            commandEvent.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}

internal static class OverlaySettings
{
    private const string SettingsKey = @"Software\DesktopListTool";

    public static Rectangle LoadBounds() => LoadBounds(null, null);

    public static Rectangle LoadBounds(string? folderPath, Rectangle? defaultBounds)
    {
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var fallback = defaultBounds ?? new Rectangle(working.Left + 24, working.Top + 24, 360, 520);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GetSettingsKey(folderPath));
            var x = ReadInt(key, "OverlayX", fallback.X);
            var y = ReadInt(key, "OverlayY", fallback.Y);
            var width = ReadInt(key, "OverlayWidth", fallback.Width);
            var height = ReadInt(key, "OverlayHeight", fallback.Height);
            var bounds = new Rectangle(x, y, Math.Max(220, width), Math.Max(180, height));
            return Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))
                ? bounds
                : fallback;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    public static void SaveBounds(Rectangle bounds) => SaveBounds(null, bounds);

    public static void SaveBounds(string? folderPath, Rectangle bounds)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GetSettingsKey(folderPath));
            key.SetValue("OverlayX", bounds.X, RegistryValueKind.DWord);
            key.SetValue("OverlayY", bounds.Y, RegistryValueKind.DWord);
            key.SetValue("OverlayWidth", bounds.Width, RegistryValueKind.DWord);
            key.SetValue("OverlayHeight", bounds.Height, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
    }

    private static int ReadInt(RegistryKey? key, string name, int fallback) => key?.GetValue(name) is int value ? value : fallback;

    internal static string GetSettingsKey(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return SettingsKey;
        var identity = folderPath.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)
            ? folderPath
            : Path.GetFullPath(folderPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $@"{SettingsKey}\Folders\{hash}";
    }
}

internal sealed record DesktopOverlayEntry(string Name, string Path, bool IsDirectory, bool IsVirtual = false)
{
    public const string RecycleBinPath = "shell:RecycleBinFolder";
    public static IReadOnlyList<DesktopOverlayEntry> Load() => Load(null);

    public static IReadOnlyList<DesktopOverlayEntry> Load(string? folderPath)
    {
        var result = new List<DesktopOverlayEntry>();
        if (string.IsNullOrWhiteSpace(folderPath))
            result.Add(new DesktopOverlayEntry("資源回收筒", RecycleBinPath, true, true));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = string.IsNullOrWhiteSpace(folderPath)
            ? new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }
            : new[] { System.IO.Path.GetFullPath(folderPath) };
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
                continue;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    if (!seen.Add(path))
                        continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;
                    var isDirectory = Directory.Exists(path);
                    var fileName = System.IO.Path.GetFileName(path);
                    var name = isDirectory ? fileName : System.IO.Path.GetFileNameWithoutExtension(fileName);
                    result.Add(new DesktopOverlayEntry(name, path, isDirectory));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return result.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<DesktopOverlayEntry> LoadPaths(IEnumerable<string> paths)
    {
        var result = new List<DesktopOverlayEntry>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(path, RecycleBinPath, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new DesktopOverlayEntry("資源回收筒", path, true, true));
                continue;
            }
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;
            var directory = Directory.Exists(path);
            var fileName = System.IO.Path.GetFileName(path);
            result.Add(new DesktopOverlayEntry(directory ? fileName : System.IO.Path.GetFileNameWithoutExtension(fileName), path, directory));
        }
        return result;
    }

    public static Icon? GetIcon(string path, bool isDirectory)
    {
        if (string.Equals(path, RecycleBinPath, StringComparison.OrdinalIgnoreCase))
            return GetRecycleBinIcon();
        const uint shgfiIcon = 0x000000100;
        const uint shgfiSmallIcon = 0x000000001;
        var result = SHGetFileInfo(path, isDirectory ? 0x10u : 0x80u, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), shgfiIcon | shgfiSmallIcon);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
            return null;
        try
        {
            return (Icon)Icon.FromHandle(info.IconHandle).Clone();
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static Icon? GetRecycleBinIcon()
    {
        const uint shgsiIcon = 0x000000100;
        const uint shgsiSmallIcon = 0x000000001;
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
        if (SHGetStockIconInfo(32, shgsiIcon | shgsiSmallIcon, ref info) != 0 || info.IconHandle == IntPtr.Zero)
            return null;
        try { return (Icon)Icon.FromHandle(info.IconHandle).Clone(); }
        finally { DestroyIcon(info.IconHandle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr IconHandle;
        public int SystemImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo fileInfo, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint identifier, uint flags, ref StockIconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
