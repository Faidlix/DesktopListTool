using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopListTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length >= 4 && args[0].Equals("--shell-drop", StringComparison.OrdinalIgnoreCase))
        {
            Environment.ExitCode = NativeShellDropTarget.TryDrop(args[1], args.Skip(3).ToArray(), args[2].Equals("copy", StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
            return;
        }

        if (args.Length >= 4 && args[0].Equals("--shell-menu", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(args[2], out var x) && int.TryParse(args[3], out var y))
                NativeShellContextMenu.TryShow(IntPtr.Zero, args[1], new Point(x, y));
            return;
        }

        if (args.Length >= 2 && args[0].Equals("--watchdog", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(args[1], out var processId)) RestoreDesktopAfterProcessExit(processId);
            return;
        }

        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = SmokeTests.Run();
            return;
        }

        if (args.Contains("--notes-count", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"NOTES_COUNT={DesktopNotesSettings.Load().Count}");
            return;
        }

        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            var index = Array.FindIndex(args, value => value.Equals("--list", StringComparison.OrdinalIgnoreCase));
            var size = index >= 0 && index + 1 < args.Length
                ? ListItemSizeSettings.Parse(args[index + 1])
                : ListItemSizeSettings.Load();
            RunOverlay(size);
            return;
        }

        if (args.Contains("--icons", StringComparer.OrdinalIgnoreCase))
        {
            OverlayMessenger.TryClose();
            DesktopViewController.SetDesktopIconsVisible(true);
            var success = DesktopViewController.TrySetIconView(out var message);
            Console.WriteLine(message);
            Environment.ExitCode = success ? 0 : 1;
            return;
        }

        if (args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
        {
            var index = Array.FindIndex(args, value => value.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            var action = index >= 0 && index + 1 < args.Length ? args[index + 1] : "toggle";
            var enabled = action.ToLowerInvariant() switch
            {
                "enable" => true,
                "disable" => false,
                _ => !StartupManager.IsEnabled
            };
            StartupManager.SetEnabled(enabled);
            Console.WriteLine(enabled ? "開機自動啟動已啟用" : "開機自動啟動已停用");
            return;
        }

        var startupRun = args.Contains("--startup-run", StringComparer.OrdinalIgnoreCase);
        RunOverlay(ListItemSizeSettings.Load());
    }

    private static void RunOverlay(ListItemSize size)
    {
        using var mutex = new Mutex(true, @"Local\DesktopListTool.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (OverlayMessenger.TrySetSize(size))
                    return;
                Thread.Sleep(50);
            }
            return;
        }

        StartCrashWatchdog();
        Application.Run(new DesktopViewApplicationContext(size));
    }

    private static void RestoreDesktopAfterProcessExit(int processId)
    {
        try { Process.GetProcessById(processId).WaitForExit(); }
        catch (ArgumentException) { }
        DesktopViewController.SetDesktopIconsVisible(true);
        DesktopViewController.TrySetIconView(out _);
    }

    private static void StartCrashWatchdog()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith("DesktopListTool.exe", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--watchdog");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

internal sealed class DesktopViewApplicationContext : ApplicationContext
{
    private readonly DesktopLayeredOverlayForm _overlay;
    private readonly List<DesktopLayeredOverlayForm> _folderOverlays = [];
    private readonly List<DesktopLayeredOverlayForm> _customOverlays = [];
    private readonly List<CustomListData> _customLists = [];
    private readonly List<DesktopLayeredNoteForm> _noteForms = [];
    private readonly List<DesktopNoteData> _notes = [];
    private readonly NotifyIcon _trayIcon;
    private readonly EventWaitHandle[] _commandEvents;
    private readonly RegisteredWaitHandle[] _registeredWaits;
    private readonly DesktopBlankClickMonitor _blankClickMonitor;

    private readonly ToolStripMenuItem _startupItem;
    private bool _checkingForUpdate;

    public DesktopViewApplicationContext(ListItemSize initialSize)
    {
        _overlay = new DesktopLayeredOverlayForm(initialSize);
        _overlay.FormClosed += (_, _) => ExitThread();
        _overlay.AddFolderRequested += (_, _) => ChooseFolder();
        _overlay.AddNoteRequested += (_, e) => CreateNote(e.ScreenLocation);
        AttachPlacementAndCustomListEvents(_overlay);
        _commandEvents = OverlayMessenger.CreateCommandEvents();
        _registeredWaits =
        [
            RegisterCommand(_commandEvents[0], () => SetAllItemSizes(ListItemSize.Compact)),
            RegisterCommand(_commandEvents[1], () => SetAllItemSizes(ListItemSize.Standard)),
            RegisterCommand(_commandEvents[2], () => SetAllItemSizes(ListItemSize.Large)),
            RegisterCommand(_commandEvents[3], () => ExitThread())
        ];

        var menu = new ContextMenuStrip();
        menu.Items.Add("緊湊清單", null, (_, _) => SetAllItemSizes(ListItemSize.Compact));
        menu.Items.Add("標準清單", null, (_, _) => SetAllItemSizes(ListItemSize.Standard));
        menu.Items.Add("大型清單", null, (_, _) => SetAllItemSizes(ListItemSize.Large));
        menu.Items.Add("新增資料夾清單…", null, (_, _) => ChooseFolder());
        menu.Items.Add("切回圖示模式", null, (_, _) => ExitThread());
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("開機自動啟動")
        {
            Checked = StartupManager.IsEnabled,
            CheckOnClick = false
        };
        _startupItem.Click += (_, _) =>
        {
            StartupManager.SetEnabled(!StartupManager.IsEnabled);
            _startupItem.Checked = StartupManager.IsEnabled;
        };
        menu.Items.Add(_startupItem);
        menu.Items.Add("結束", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "桌面顯示模式",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => _overlay.RevealFrame();
        _overlay.Show();
        foreach (var folder in PinnedFolderSettings.Load())
            CreateFolderOverlay(folder, save: false);
        _customLists.AddRange(CustomListSettings.Load());
        foreach (var customList in _customLists.ToArray())
            CreateCustomListOverlay(customList, save: false);
        _notes.AddRange(DesktopNotesSettings.Load());
        foreach (var note in _notes)
            ShowNote(note);
        _blankClickMonitor = new DesktopBlankClickMonitor(HandleDesktopBlankClick);
        WriteQaLayoutIfRequested();
        if (AutoUpdateManager.IsEnabled)
            _overlay.BeginInvoke(async () => { await Task.Delay(2500); await CheckForUpdatesAsync(interactive: false); });
    }

    private void SetAllItemSizes(ListItemSize size)
    {
        _overlay.SetItemSize(size);
        foreach (var folderOverlay in _folderOverlays)
            folderOverlay.SetItemSize(size);
        foreach (var customOverlay in _customOverlays)
            customOverlay.SetItemSize(size);
    }

    private void WriteQaLayoutIfRequested()
    {
        var path = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_LAYOUT_PATH");
        if (string.IsNullOrWhiteSpace(path))
            return;
        var layout = AllListOverlays().Select(value => new
        {
            value.Bounds.X, value.Bounds.Y, value.Bounds.Width, value.Bounds.Height,
            value.IsFloating, value.IsCustomList, value.FolderPath
        }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(layout));
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "選擇要釘到桌面的資料夾清單",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            CreateFolderOverlay(dialog.SelectedPath, save: true);
    }

    private void CreateFolderOverlay(string folderPath, bool save)
    {
        if (!Directory.Exists(folderPath))
        {
            if (save)
                MessageBox.Show("找不到選取的資料夾。", "桌面清單", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var fullPath = Path.GetFullPath(folderPath);
        var existing = _folderOverlays.FirstOrDefault(item => string.Equals(item.FolderPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.RevealFrame();
            return;
        }

        var offset = 36 * (_folderOverlays.Count + 1);
        var defaultBounds = new Rectangle(_overlay.Left + offset, _overlay.Top + offset, Math.Max(360, _overlay.Width), Math.Min(640, _overlay.Height));
        var folderOverlay = new DesktopLayeredOverlayForm(ListItemSizeSettings.Load(), fullPath, defaultBounds);
        folderOverlay.AddFolderRequested += (_, _) => ChooseFolder();
        folderOverlay.AddNoteRequested += (_, e) => CreateNote(e.ScreenLocation);
        folderOverlay.CloseListRequested += (_, _) => CloseFolderOverlay(folderOverlay);
        AttachPlacementAndCustomListEvents(folderOverlay);
        folderOverlay.FormClosed += (_, _) => _folderOverlays.Remove(folderOverlay);
        _folderOverlays.Add(folderOverlay);
        folderOverlay.SetBoundsWithoutSaving(FindFreePlacement(folderOverlay.Bounds, folderOverlay));
        OverlaySettings.SaveBounds(fullPath, folderOverlay.Bounds);
        folderOverlay.Show();
        if (save)
            PinnedFolderSettings.Add(fullPath);
    }

    private void CloseFolderOverlay(DesktopLayeredOverlayForm overlay)
    {
        if (overlay.FolderPath is not null)
            PinnedFolderSettings.Remove(overlay.FolderPath);
        _folderOverlays.Remove(overlay);
        overlay.Close();
        overlay.Dispose();
    }

    private void CreateCustomList(string path, Point screenLocation)
    {
        if (!File.Exists(path) && !Directory.Exists(path) && path != DesktopOverlayEntry.RecycleBinPath)
            return;
        var data = new CustomListData { Paths = [path] };
        _customLists.Add(data);
        var defaultBounds = new Rectangle(screenLocation.X - 24, screenLocation.Y - 24, 360, 180);
        CreateCustomListOverlay(data, save: true, defaultBounds);
    }

    private void CreateCustomListOverlay(CustomListData data, bool save, Rectangle? defaultBounds = null)
    {
        data.Paths = data.Paths.Where(path => File.Exists(path) || Directory.Exists(path) || path == DesktopOverlayEntry.RecycleBinPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (data.Paths.Count == 0)
        {
            _customLists.Remove(data);
            if (save) CustomListSettings.Save(_customLists);
            return;
        }
        var overlay = new DesktopLayeredOverlayForm(ListItemSizeSettings.Load(), defaultBounds: defaultBounds, customList: data);
        overlay.AddFolderRequested += (_, _) => ChooseFolder();
        overlay.AddNoteRequested += (_, e) => CreateNote(e.ScreenLocation);
        overlay.CloseListRequested += (_, _) => CloseCustomListOverlay(overlay, data);
        overlay.CustomPathsChanged += (_, _) => CustomListSettings.Save(_customLists);
        AttachPlacementAndCustomListEvents(overlay);
        overlay.FormClosed += (_, _) => _customOverlays.Remove(overlay);
        _customOverlays.Add(overlay);
        overlay.SetBoundsWithoutSaving(FindFreePlacement(overlay.Bounds, overlay));
        OverlaySettings.SaveBounds($"custom:{data.Id:N}", overlay.Bounds);
        overlay.Show();
        if (save) CustomListSettings.Save(_customLists);
    }

    private void CloseCustomListOverlay(DesktopLayeredOverlayForm overlay, CustomListData data)
    {
        _customOverlays.Remove(overlay);
        _customLists.Remove(data);
        CustomListSettings.Save(_customLists);
        overlay.Close();
        overlay.Dispose();
    }

    private void AttachPlacementAndCustomListEvents(DesktopLayeredOverlayForm overlay)
    {
        overlay.CreateCustomListRequested += (_, e) => CreateCustomList(e.Path, e.ScreenLocation);
        overlay.ResolvePlacement = (sender, candidate) => FindFreePlacement(candidate, sender);
        overlay.CheckUpdateRequested += async (_, _) => await CheckForUpdatesAsync(interactive: true);
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_checkingForUpdate || _overlay.IsDisposed) return;
        _checkingForUpdate = true;
        try
        {
            if (await AutoUpdateManager.CheckAndPromptAsync(_overlay, interactive))
                ExitThread();
        }
        finally { _checkingForUpdate = false; }
    }

    private IEnumerable<DesktopLayeredOverlayForm> AllListOverlays()
    {
        yield return _overlay;
        foreach (var overlay in _folderOverlays) yield return overlay;
        foreach (var overlay in _customOverlays) yield return overlay;
    }

    private void HandleDesktopBlankClick(Point screenPoint)
    {
        if (_overlay.IsDisposed || AllListOverlays().Any(value => !value.IsDisposed && value.Bounds.Contains(screenPoint)) ||
            _noteForms.Any(value => !value.IsDisposed && value.Bounds.Contains(screenPoint))) return;
        try { _overlay.BeginInvoke(() => { foreach (var overlay in AllListOverlays()) overlay.ClearSelectionFromDesktopClick(); }); }
        catch (InvalidOperationException) { }
    }

    private Rectangle FindFreePlacement(Rectangle candidate, DesktopLayeredOverlayForm self)
    {
        if (self.IsFloating)
            return DesktopLayeredOverlayForm.ConstrainRectangle(candidate, Screen.FromRectangle(candidate).WorkingArea);
        var workArea = Screen.FromRectangle(candidate).WorkingArea;
        candidate = DesktopLayeredOverlayForm.ConstrainRectangle(candidate, workArea);
        var occupied = AllListOverlays().Where(value => value != self && !value.IsFloating && !value.IsDisposed)
            .Select(value => value.Bounds).ToArray();
        return FindFreeListPlacement(candidate, workArea, occupied);
    }

    internal static Rectangle FindFreeListPlacement(Rectangle candidate, Rectangle workArea, IReadOnlyCollection<Rectangle> occupied)
    {
        candidate = DesktopLayeredOverlayForm.ConstrainRectangle(candidate, workArea);
        if (!occupied.Any(value => value.IntersectsWith(candidate)))
            return candidate;

        var candidates = new List<Rectangle>();
        foreach (var other in occupied)
        {
            candidates.Add(new Rectangle(other.Right + 8, candidate.Y, candidate.Width, candidate.Height));
            candidates.Add(new Rectangle(other.Left - candidate.Width - 8, candidate.Y, candidate.Width, candidate.Height));
            candidates.Add(new Rectangle(candidate.X, other.Bottom + 8, candidate.Width, candidate.Height));
            candidates.Add(new Rectangle(candidate.X, other.Top - candidate.Height - 8, candidate.Width, candidate.Height));
        }
        var nearestEdge = candidates
            .Select(value => DesktopLayeredOverlayForm.ConstrainRectangle(value, workArea))
            .Where(value => !occupied.Any(other => other.IntersectsWith(value)))
            .OrderBy(value => Math.Abs(value.X - candidate.X) + Math.Abs(value.Y - candidate.Y))
            .Select(value => (Rectangle?)value)
            .FirstOrDefault();
        if (nearestEdge is Rectangle edge)
            return edge;
        Rectangle? best = null;
        var bestDistance = int.MaxValue;
        for (var y = workArea.Top; y <= workArea.Bottom - candidate.Height; y += 24)
        {
            for (var x = workArea.Left; x <= workArea.Right - candidate.Width; x += 24)
            {
                var value = new Rectangle(x, y, candidate.Width, candidate.Height);
                if (occupied.Any(other => other.IntersectsWith(value))) continue;
                var distance = Math.Abs(x - candidate.X) + Math.Abs(y - candidate.Y);
                if (distance >= bestDistance) continue;
                best = value;
                bestDistance = distance;
            }
        }
        return best ?? candidate;
    }

    private void CreateNote(Point screenLocation)
    {
        using var editor = new NoteEditorDialog();
        if (editor.ShowDialog() != DialogResult.OK)
            return;
        if (string.IsNullOrWhiteSpace(editor.NoteText) && editor.ChecklistItems.Count == 0)
            return;

        var note = new DesktopNoteData
        {
            Text = editor.NoteText.TrimEnd(),
            Items = editor.ChecklistItems.ToList(),
            Bounds = new Rectangle(screenLocation.X, screenLocation.Y, 340, 260)
        };
        note.Bounds = DesktopLayeredOverlayForm.ConstrainRectangle(note.Bounds, Screen.FromPoint(screenLocation).WorkingArea);
        _notes.Add(note);
        ShowNote(note);
        SaveNotes();
    }

    private void ShowNote(DesktopNoteData note)
    {
        var form = new DesktopLayeredNoteForm(note);
        form.EditRequested += (_, _) => EditNote(form);
        form.DeleteRequested += (_, _) => DeleteNote(form);
        form.AddNoteRequested += (_, e) => CreateNote(e.ScreenLocation);
        form.NoteChanged += (_, _) => SaveNotes();
        _noteForms.Add(form);
        form.Show();
    }

    private void EditNote(DesktopLayeredNoteForm form)
    {
        using var editor = new NoteEditorDialog(form.Data);
        if (editor.ShowDialog(form) != DialogResult.OK)
            return;
        form.ApplyEditedContent(editor.NoteText, editor.ChecklistItems);
        SaveNotes();
    }

    private void DeleteNote(DesktopLayeredNoteForm form)
    {
        _noteForms.Remove(form);
        _notes.Remove(form.Data);
        form.Close();
        form.Dispose();
        SaveNotes();
    }

    private void SaveNotes() => DesktopNotesSettings.Save(_notes);

    private RegisteredWaitHandle RegisterCommand(EventWaitHandle commandEvent, Action action) =>
        ThreadPool.RegisterWaitForSingleObject(commandEvent, (_, _) =>
        {
            if (!_overlay.IsDisposed)
                _overlay.BeginInvoke(action);
        }, null, Timeout.Infinite, executeOnlyOnce: false);

    protected override void ExitThreadCore()
    {
        _blankClickMonitor.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        foreach (var wait in _registeredWaits)
            wait.Unregister(null);
        foreach (var commandEvent in _commandEvents)
            commandEvent.Dispose();
        DesktopViewController.SetDesktopIconsVisible(true);
        DesktopViewController.TrySetIconView(out _);
        if (!_overlay.IsDisposed)
            _overlay.Dispose();
        foreach (var folderOverlay in _folderOverlays.ToArray())
            folderOverlay.Dispose();
        _folderOverlays.Clear();
        foreach (var customOverlay in _customOverlays.ToArray())
            customOverlay.Dispose();
        _customOverlays.Clear();
        foreach (var noteForm in _noteForms.ToArray())
            noteForm.Dispose();
        _noteForms.Clear();
        base.ExitThreadCore();
    }
}

internal sealed class DesktopViewForm : Form
{
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };

    public DesktopViewForm()
    {
        Text = "桌面顯示模式";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ClientSize = new Size(520, 292);
        BackColor = Color.FromArgb(246, 248, 252);
        Font = new Font("Microsoft JhengHei UI", 10F);
        Icon = SystemIcons.Application;

        BuildUi();
        RefreshStatus();
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
    }

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "選擇桌面顯示模式",
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 41, 59),
            AutoSize = true,
            Location = new Point(28, 24)
        };
        var description = new Label
        {
            Text = "直接切換 Windows 原生桌面，不會移動、重新命名或刪除任何圖示。",
            ForeColor = Color.FromArgb(71, 85, 105),
            AutoSize = true,
            Location = new Point(30, 66)
        };

        var listButton = new Button
        {
            Text = "☰  清單模式",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            Size = new Size(218, 74),
            Location = new Point(30, 108)
        };
        StylePrimaryButton(listButton);
        listButton.Click += (_, _) => ApplyListView();

        var iconButton = new Button
        {
            Text = "▦  圖示模式",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            Size = new Size(218, 74),
            Location = new Point(272, 108)
        };
        StyleSecondaryButton(iconButton);
        iconButton.Click += (_, _) => ApplyIconView();

        var iconHint = new Label
        {
            Text = "切回後可照常用桌面右鍵選擇大／中／小圖示",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(272, 188)
        };
        var divider = new Panel
        {
            BackColor = Color.FromArgb(226, 232, 240),
            Location = new Point(30, 222),
            Size = new Size(460, 1)
        };
        _status.AutoSize = true;
        _status.ForeColor = Color.FromArgb(71, 85, 105);
        _status.Location = new Point(30, 244);

        Controls.AddRange([title, description, listButton, iconButton, iconHint, divider, _status]);
    }

    private static void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.FromArgb(37, 99, 235);
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
    }

    private static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        button.BackColor = Color.White;
        button.ForeColor = Color.FromArgb(30, 41, 59);
        button.Cursor = Cursors.Hand;
    }

    internal void ApplyListView()
    {
        if (DesktopViewController.TrySetListView(out var message))
            SetSuccess("桌面已切換為清單模式。");
        else
            ShowError(message);
        RefreshStatus();
    }

    internal void ApplyIconView()
    {
        if (DesktopViewController.TrySetIconView(out var message))
            SetSuccess("桌面已切回圖示模式。");
        else
            ShowError(message);
        RefreshStatus();
    }

    private void SetSuccess(string message)
    {
        _status.Text = message;
        _status.ForeColor = Color.FromArgb(22, 101, 52);
    }

    private void ShowError(string message)
    {
        _status.Text = message;
        _status.ForeColor = Color.FromArgb(185, 28, 28);
        MessageBox.Show(this, message, "桌面顯示模式", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void RefreshStatus()
    {
        var view = DesktopViewController.GetCurrentView();
        _status.Text = view switch
        {
            DesktopView.List => "目前模式：清單",
            DesktopView.Icon => "目前模式：圖示",
            DesktopView.Details => "目前模式：詳細資料",
            DesktopView.SmallIcon => "目前模式：小圖示",
            DesktopView.Tile => "目前模式：並排",
            _ => "目前模式：無法讀取"
        };
        _status.ForeColor = view == DesktopView.Unknown
            ? Color.FromArgb(185, 28, 28)
            : Color.FromArgb(71, 85, 105);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}

internal enum DesktopView
{
    Unknown = -1,
    Icon = 1,
    SmallIcon = 2,
    List = 3,
    Details = 4,
    Thumbnail = 5,
    Tile = 6,
    Content = 8
}

internal enum ListItemSize
{
    Compact,
    Standard,
    Large
}

internal static class ListItemSizeSettings
{
    private const string SettingsKey = @"Software\DesktopListTool";

    public static ListItemSize Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "compact" => ListItemSize.Compact,
        "large" => ListItemSize.Large,
        _ => ListItemSize.Standard
    };

    public static ListItemSize Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return Parse(key?.GetValue("ListItemSize") as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return ListItemSize.Standard;
        }
    }

    public static void Save(ListItemSize size)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue("ListItemSize", size.ToString().ToLowerInvariant(), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopListTool";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{Application.ExecutablePath}\" --startup-run", RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class PinnedFolderSettings
{
    private const string SettingsKey = @"Software\DesktopListTool";
    private const string ValueName = "PinnedFolders";

    public static IReadOnlyList<string> Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return (key?.GetValue(ValueName) as string[] ?? [])
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Add(string folderPath)
    {
        var values = Load().Append(Path.GetFullPath(folderPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Save(values);
    }

    public static void Remove(string folderPath)
    {
        var fullPath = Path.GetFullPath(folderPath);
        Save(Load().Where(value => !string.Equals(value, fullPath, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private static void Save(string[] values)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            if (values.Length == 0)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            else
                key.SetValue(ValueName, values, RegistryValueKind.MultiString);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class DesktopViewController
{
    private const int LvmFirst = 0x1000;
    private const int LvmSetView = LvmFirst + 142;
    private const int LvmGetView = LvmFirst + 143;
    private const int LvmGetColumnWidth = LvmFirst + 29;
    private const int LvmSetColumnWidth = LvmFirst + 30;
    private const int WmCommand = 0x0111;
    private const int FcidmShellViewBigIcon = 0x7029;
    private const int FcidmShellViewList = 0x702B;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwErase = 0x0004;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    public static DesktopView GetCurrentView()
    {
        if (TryGetFolderView(out var folderView, out var releaseObjects))
        {
            try
            {
                return folderView.GetCurrentViewMode(out var mode) >= 0 && Enum.IsDefined(typeof(DesktopView), (int)mode)
                    ? (DesktopView)mode
                    : DesktopView.Unknown;
            }
            finally
            {
                ReleaseComObjects(releaseObjects);
            }
        }

        var desktop = FindDesktopListView();
        if (desktop == IntPtr.Zero)
            return DesktopView.Unknown;
        var listViewMode = SendMessage(desktop, LvmGetView, IntPtr.Zero, IntPtr.Zero).ToInt32();
        return listViewMode switch
        {
            0 => DesktopView.Icon,
            1 => DesktopView.Details,
            2 => DesktopView.SmallIcon,
            3 => DesktopView.List,
            4 => DesktopView.Tile,
            _ => DesktopView.Unknown
        };
    }

    public static bool TrySetListView(out string message) => TrySetListView(ListItemSizeSettings.Load(), out message);

    public static bool TrySetListView(ListItemSize size, out string message)
    {
        if (!TrySetView(DesktopView.List, out message))
            return false;

        var desktop = FindDesktopListView();
        if (desktop == IntPtr.Zero)
        {
            message = "清單模式已切換，但找不到桌面清單以套用項目大小。";
            return false;
        }

        var dpi = GetDpiForWindow(desktop);
        if (dpi == 0)
            dpi = 96;
        var logicalWidth = size switch
        {
            ListItemSize.Compact => 140,
            ListItemSize.Large => 340,
            _ => 220
        };
        var width = (int)Math.Round(logicalWidth * dpi / 96d);
        SendMessage(desktop, LvmSetColumnWidth, IntPtr.Zero, (IntPtr)width);
        var actualWidth = SendMessage(desktop, LvmGetColumnWidth, IntPtr.Zero, IntPtr.Zero).ToInt32();
        if (Math.Abs(actualWidth - width) > 2)
        {
            message = $"清單模式已切換，但項目大小未套用（預期 {width}px，實際 {actualWidth}px）。";
            return false;
        }

        ListItemSizeSettings.Save(size);
        message = $"完成（清單項目寬度 {actualWidth}px）";
        return true;
    }

    public static bool TrySetIconView(out string message) => TrySetView(DesktopView.Icon, out message);

    public static bool SetDesktopIconsVisible(bool visible)
    {
        var desktop = FindDesktopListView();
        return desktop != IntPtr.Zero && ShowWindow(desktop, visible ? 5 : 0);
    }

    internal static bool TrySetView(DesktopView view, out string message)
    {
        if (!TryGetFolderView(out var folderView, out var releaseObjects))
        {
            return TrySetViaShellCommand(view, out message);
        }

        int result;
        try
        {
            result = folderView.SetCurrentViewMode((uint)view);
        }
        finally
        {
            ReleaseComObjects(releaseObjects);
        }

        var desktop = FindDesktopListView();
        if (desktop != IntPtr.Zero)
            RedrawWindow(desktop, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);

        var actual = GetCurrentView();
        if (result < 0 || actual != view)
        {
            message = $"Windows 未接受顯示模式切換（錯誤 0x{result:X8}，目前模式：{actual}）。";
            return false;
        }

        message = "完成";
        return true;
    }

    private static bool TrySetViaShellCommand(DesktopView view, out string message)
    {
        var desktopList = FindDesktopListView();
        if (desktopList == IntPtr.Zero)
        {
            message = "找不到 Windows 桌面視窗，請確認檔案總管正在執行。";
            return false;
        }

        var command = view switch
        {
            DesktopView.List => FcidmShellViewList,
            DesktopView.Icon => FcidmShellViewBigIcon,
            _ => 0
        };
        if (command == 0)
        {
            message = "此顯示模式無法使用 Shell 命令切換。";
            return false;
        }

        var shellView = GetParent(desktopList);
        SendMessage(shellView, WmCommand, (IntPtr)command, IntPtr.Zero);
        RedrawWindow(desktopList, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow);
        var actual = GetCurrentView();
        if (actual != view)
        {
            message = $"Windows 未接受顯示模式切換（目前模式：{actual}）。";
            return false;
        }

        message = "完成";
        return true;
    }

    private static bool TryGetFolderView(out IFolderView folderView, out List<object> releaseObjects)
    {
        folderView = null!;
        releaseObjects = [];
        try
        {
            var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            if (shellWindowsType is null || Activator.CreateInstance(shellWindowsType) is not IShellWindows shellWindows)
                return false;
            releaseObjects.Add(shellWindows);

            object location = 0;
            object root = 0;
            var desktopDispatch = shellWindows.FindWindowSW(ref location, ref root, 8, out _, 1);
            if (desktopDispatch is not IComServiceProvider serviceProvider)
                return false;
            releaseObjects.Add(desktopDispatch);

            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var browserInterface = new Guid("000214E2-0000-0000-C000-000000000046");
            var queryResult = serviceProvider.QueryService(ref service, ref browserInterface, out var browserObject);
            if (queryResult < 0 || browserObject is not IShellBrowser browser)
                return false;
            releaseObjects.Add(browserObject);

            var viewResult = browser.QueryActiveShellView(out var shellViewPointer);
            if (viewResult < 0 || shellViewPointer == IntPtr.Zero)
                return false;
            try
            {
                var folderViewInterface = typeof(IFolderView).GUID;
                var qiResult = Marshal.QueryInterface(shellViewPointer, in folderViewInterface, out var folderViewPointer);
                if (qiResult < 0 || folderViewPointer == IntPtr.Zero)
                    return false;
                try
                {
                    var folderViewObject = Marshal.GetObjectForIUnknown(folderViewPointer);
                    folderView = (IFolderView)folderViewObject;
                    releaseObjects.Add(folderViewObject);
                    return true;
                }
                finally
                {
                    Marshal.Release(folderViewPointer);
                }
            }
            finally
            {
                Marshal.Release(shellViewPointer);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            ReleaseComObjects(releaseObjects);
            releaseObjects = [];
            return false;
        }
    }

    private static void ReleaseComObjects(IEnumerable<object> objects)
    {
        foreach (var value in objects.Reverse())
        {
            if (Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
    }

    internal static IntPtr FindDesktopListView()
    {
        var programManager = FindWindow("Progman", null);
        var shellView = programManager == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(programManager, IntPtr.Zero, "SHELLDLL_DefView", null);

        if (shellView == IntPtr.Zero)
        {
            EnumWindows((topWindow, _) =>
            {
                var candidate = FindWindowEx(topWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (candidate == IntPtr.Zero)
                    return true;
                shellView = candidate;
                return false;
            }, IntPtr.Zero);
        }

        return shellView == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle, IntPtr updateRegion, uint flags);
}

[ComImport]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    [DispId(200)]
    object FindWindowSW(ref object location, ref object locationRoot, int windowClass, out int windowHandle, int options);
}

[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object result);
}

[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp(bool enterMode);
    [PreserveSig] int InsertMenusSB(IntPtr sharedMenu, IntPtr menuWidths);
    [PreserveSig] int SetMenuSB(IntPtr sharedMenu, IntPtr oleMenuReserved, IntPtr activeObject);
    [PreserveSig] int RemoveMenusSB(IntPtr sharedMenu);
    [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string statusText);
    [PreserveSig] int EnableModelessSB(bool enable);
    [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort commandId);
    [PreserveSig] int BrowseObject(IntPtr itemIdList, uint flags);
    [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
    [PreserveSig] int GetControlWindow(uint controlId, out IntPtr window);
    [PreserveSig] int SendControlMsg(uint controlId, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    [PreserveSig] int QueryActiveShellView(out IntPtr shellView);
}

[ComImport]
[Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView
{
    [PreserveSig] int GetCurrentViewMode(out uint viewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
}

internal static class SmokeTests
{
    public static int Run()
    {
        try
        {
            using var list = new ListView { View = View.LargeIcon };
            _ = list.Handle;
            var before = SendMessage(list.Handle, 0x1000 + 143, IntPtr.Zero, IntPtr.Zero).ToInt32();
            var setResult = SendMessage(list.Handle, 0x1000 + 142, (IntPtr)(int)DesktopView.List, IntPtr.Zero).ToInt32();
            var after = SendMessage(list.Handle, 0x1000 + 143, IntPtr.Zero, IntPtr.Zero).ToInt32();

            Assert(before == 0, "initial icon view");
            Assert(setResult != 0, "LVM_SETVIEW result");
            Assert(after == (int)DesktopView.List, "list view applied");

            var desktopHandle = DesktopViewController.FindDesktopListView();
            Assert(desktopHandle != IntPtr.Zero, "desktop list view found");
            var current = DesktopViewController.GetCurrentView();
            Assert(current != DesktopView.Unknown, "desktop view readable");

            var workArea = new Rectangle(0, 0, 1920, 1040);
            Assert(DesktopLayeredOverlayForm.ConstrainRectangle(new Rectangle(9, 200, 400, 500), workArea).X == 9, "inside edge remains free");
            Assert(DesktopLayeredOverlayForm.ConstrainRectangle(new Rectangle(-9, 200, 400, 500), workArea).X == 0, "left overflow constrained");
            Assert(DesktopLayeredOverlayForm.ConstrainRectangle(new Rectangle(1530, 200, 400, 500), workArea).Right == 1920, "right overflow constrained");
            Assert(DesktopLayeredOverlayForm.ConstrainRectangle(new Rectangle(400, 560, 400, 500), workArea).Bottom == 1040, "bottom overflow constrained");
            var placed = DesktopViewApplicationContext.FindFreeListPlacement(
                new Rectangle(20, 20, 360, 240), workArea, [new Rectangle(0, 0, 500, 600)]);
            Assert(!placed.IntersectsWith(new Rectangle(0, 0, 500, 600)) && workArea.Contains(placed), "new lists avoid existing lists");
            Assert(AutoUpdateManager.IsNewerVersion(new Version(1, 0, 0), "v1.1.0", out var updateVersion) && updateVersion == new Version(1, 1, 0), "release version comparison");

            var desktopEntries = DesktopOverlayEntry.Load();
            Assert(desktopEntries.Any(entry => entry.IsVirtual && entry.Path == DesktopOverlayEntry.RecycleBinPath), "recycle bin appears in desktop list");
            using (var recycleIcon = DesktopOverlayEntry.GetIcon(DesktopOverlayEntry.RecycleBinPath, true))
                Assert(recycleIcon is not null, "recycle bin icon available");
            using (var overlayConstruction = new DesktopLayeredOverlayForm(ListItemSize.Standard))
            {
                Assert(overlayConstruction.Width >= overlayConstruction.MinimumSize.Width, "overlay constructs before resize callbacks");
                Assert(overlayConstruction.ContextMenuLabels.Contains("複製樣式") && overlayConstruction.ContextMenuLabels.Contains("貼上樣式"), "list menu exposes style copy and paste");
            }

            var referenceRoot = Path.Combine(Path.GetTempPath(), $"DesktopListTool-Reference-{Guid.NewGuid():N}");
            Directory.CreateDirectory(referenceRoot);
            var referenceFile = Path.Combine(referenceRoot, "original.txt");
            File.WriteAllText(referenceFile, "reference-only");
            var referenced = DesktopOverlayEntry.LoadPaths([referenceFile]);
            Assert(referenced.Count == 1 && File.Exists(referenceFile), "custom list references without moving source");
            Assert(NativeShellContextMenu.TryShow(IntPtr.Zero, referenceFile, Point.Empty, display: false), "native shell context menu marshaling");
            Directory.Delete(referenceRoot, recursive: true);

            using (var appearance = new OverlayStyleDialog(new OverlayStyleOptions
            {
                BackgroundColor = Color.Black,
                BackgroundOpacity = 80,
                FollowThemeColor = false,
                GradientEnabled = true,
                GradientColor = Color.DarkOrange,
                GradientOpacity = 140,
                GradientTransition = 65,
                GradientDirection = OverlayGradientDirection.DiagonalDown,
                GradientMode = OverlayGradientMode.Sandwich,
                TextColor = Color.LightYellow,
                FontSize = 12,
                Bold = true,
                LineHeight = 38,
                LineSpacing = 4,
                CornerRadius = 18,
                TextAlignment = OverlayTextAlignment.Center,
                Distribution = OverlayContentDistribution.Fill
            }, "清單"))
            {
                var controls = appearance.Controls.Cast<Control>().SelectMany(Descendants).ToArray();
                var themeOption = controls.OfType<CheckBox>().Single(value => value.Text.Contains("Windows 系統顏色"));
                Assert(themeOption.Text.Contains("Windows 系統顏色"), "system color option is visible");
                Assert(appearance.SelectedStyle.Bold && appearance.SelectedStyle.CornerRadius == 18 &&
                    appearance.SelectedStyle.TextAlignment == OverlayTextAlignment.Center && appearance.SelectedStyle.Distribution == OverlayContentDistribution.Fill &&
                    appearance.SelectedStyle.GradientEnabled && appearance.SelectedStyle.GradientDirection == OverlayGradientDirection.DiagonalDown && appearance.SelectedStyle.GradientMode == OverlayGradientMode.Sandwich && appearance.SelectedStyle.GradientOpacity == 140 && appearance.SelectedStyle.TextColor == Color.LightYellow,
                    "extended style values round trip");
                Assert(appearance.AcceptButton is null, "style Enter does not confirm");
                Assert(controls.OfType<SliderNumberControl>().Count() == 5, "all persistent numeric style values use sliders and opacity is integrated with colors");
                Assert(!controls.OfType<Panel>().Any(value => value.Height == 190), "redundant style preview is removed");
                Assert(controls.OfType<ButtonChoice<OverlayTextAlignment>>().Any() && controls.OfType<ButtonChoice<OverlayContentDistribution>>().Any() && controls.OfType<ButtonChoice<FontStyle>>().Any(), "alignment distribution and weight use buttons");
                var fontChoices = controls.OfType<ComboBox>().Single(value => value.DrawMode == DrawMode.OwnerDrawFixed).Items.Cast<FontChoice>().ToArray();
                Assert(!fontChoices.SkipWhile(value => value.SupportsChinese).Any(value => value.SupportsChinese), "unsupported Chinese fonts are sorted last");
                var fontCombo = controls.OfType<ComboBox>().Single(value => value.DrawMode == DrawMode.OwnerDrawFixed);
                var weightButtons = controls.OfType<ButtonChoice<FontStyle>>().Single();
                var noBold = FontFamily.Families.FirstOrDefault(value => value.IsStyleAvailable(FontStyle.Regular) && !value.IsStyleAvailable(FontStyle.Bold));
                if (noBold is not null) { fontCombo.SelectedItem = fontChoices.First(value => value.Name == noBold.Name); Assert(!weightButtons.IsOptionVisible(FontStyle.Bold), "unsupported font weight button is hidden"); }
                OverlayStyleClipboard.Copy(appearance.SelectedStyle); Assert(OverlayStyleClipboard.Paste()?.TextColor == Color.LightYellow, "style copy and paste preserves full style");
                var appearanceQa = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_APPEARANCE_PATH");
                if (!string.IsNullOrWhiteSpace(appearanceQa))
                {
                    appearance.StartPosition = FormStartPosition.Manual;
                    appearance.Location = new Point(-10000, -10000);
                    appearance.Show();
                    Application.DoEvents();
                    using var image = new Bitmap(appearance.Width, appearance.Height);
                    appearance.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    image.Save(appearanceQa);
                    appearance.Hide();
                }
            }

            var transferRoot = Path.Combine(Path.GetTempPath(), $"DesktopListTool-Smoke-{Guid.NewGuid():N}");
            try
            {
                var source = Path.Combine(transferRoot, "source");
                var copyTarget = Path.Combine(transferRoot, "copy");
                var moveTarget = Path.Combine(transferRoot, "move");
                Directory.CreateDirectory(source);
                var file = Path.Combine(source, "drag.txt");
                File.WriteAllText(file, "drag-copy-move");
                DesktopLayeredOverlayForm.TransferPaths([file], copyTarget, copy: true);
                Assert(File.Exists(Path.Combine(copyTarget, "drag.txt")) && File.Exists(file), "ctrl drag copies file");
                DesktopLayeredOverlayForm.TransferPaths([file], moveTarget, copy: false);
                Assert(File.Exists(Path.Combine(moveTarget, "drag.txt")) && !File.Exists(file), "drag moves file");
                Assert(DesktopLayeredOverlayForm.IsFolderDropTarget(new Rectangle(10, 18, 300, 26), new Point(295, 30)), "folder row center accepts drop");
                Assert(NativeShellDropTarget.IsArchivePath(Path.Combine(moveTarget, "test.zip")) == false, "missing archive is not a drop target");
                var archive = Path.Combine(moveTarget, "test.zip"); using (System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create)) { }
                Assert(NativeShellDropTarget.IsArchivePath(archive), "archive extension is a drop target");
                var zipSource = Path.Combine(moveTarget, "zip-source.txt"); File.WriteAllText(zipSource, "zip");
                Assert(NativeShellDropTarget.TryDrop(archive, [zipSource], copy: true), "isolated archive handler adds files");
                using (var zip = System.IO.Compression.ZipFile.OpenRead(archive)) Assert(zip.GetEntry("zip-source.txt") is not null, "zip entry is written before helper exits");
                object? shell = null, shortcut = null;
                try
                {
                    var shellType = Type.GetTypeFromProgID("WScript.Shell")!; shell = Activator.CreateInstance(shellType)!;
                    var shortcutPath = Path.Combine(transferRoot, "folder.lnk");
                    shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
                    shortcut!.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [moveTarget]);
                    shortcut.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                    Assert(NativeShellDropTarget.TryResolveFolderShortcut(shortcutPath, out var resolved) && Path.GetFullPath(resolved) == Path.GetFullPath(moveTarget), "folder shortcuts resolve as drop targets");
                }
                finally
                {
                    if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                    if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                }
                var resized = DesktopLayeredOverlayForm.ConstrainSizingRectangle(new Rectangle(-20, -20, 500, 400), new Rectangle(0, 0, 1920, 1040), 4, new Size(240, 140));
                Assert(resized.Left == 0 && resized.Top == 0 && resized.Right == 480 && resized.Bottom == 380, "top-left resizing keeps opposite corner fixed and stays onscreen");
            }
            finally
            {
                if (Directory.Exists(transferRoot))
                    Directory.Delete(transferRoot, recursive: true);
            }

            var note = new DesktopNoteData
            {
                Text = "## 每週回顧\r\n**完成：** Markdown 顯示與安全圓角\r\n- 第一個項目\r\n- 第二個項目\r\n> 這是一段引言\r\n`inline code` 與 [連結](https://example.invalid)\r\n第七行用來確認內容超出時可捲動\r\n第八行仍需保留",
                X = 40, Y = 40, Width = 420, Height = 180, CornerRadius = 42,
                GradientEnabled = true, GradientColorArgb = Color.DarkOrange.ToArgb(), GradientOpacity = 120, GradientDirection = OverlayGradientDirection.DiagonalDown, GradientMode = OverlayGradientMode.Edges,
                GradientTransition = 60, TextColorArgb = Color.LightYellow.ToArgb()
            };
            var markdown = MarkdownNoteRenderer.ParseLine("## **標題**與[連結](https://example.invalid)");
            Assert(markdown.HeadingLevel == 2 && markdown.Runs.Any(value => value.Style.HasFlag(FontStyle.Bold)) && markdown.Runs.Any(value => value.Style.HasFlag(FontStyle.Underline)), "Markdown formatting parses");
            using (var editor = new NoteEditorDialog(note))
            {
                Assert(editor.AcceptButton is null, "note editor Enter does not confirm");
                var descendants = editor.Controls.Cast<Control>().SelectMany(value => new[] { value }.Concat(Descendants(value))).ToArray();
                Assert(descendants.OfType<SplitContainer>().Any(), "note editor text and checklist ranges are adjustable");
                var addButton = descendants.OfType<Button>().Single(value => value.Text.Contains("新增勾選事項"));
                Assert(addButton.AutoSize && addButton.Width >= addButton.PreferredSize.Width, "add checklist label is not clipped");
                var editorQa = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_EDITOR_PATH");
                if (!string.IsNullOrWhiteSpace(editorQa))
                {
                    editor.StartPosition = FormStartPosition.Manual; editor.Location = new Point(-10000, -10000); editor.Show(); Application.DoEvents();
                    using var image = new Bitmap(editor.Width, editor.Height); editor.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(editorQa); editor.Hide();
                }
            }
            using (var noteForm = new DesktopLayeredNoteForm(note))
            {
                Assert(noteForm.ContextMenuLabels.Contains("新增便籤…") && noteForm.ContextMenuLabels.Contains("樣式…") && noteForm.ContextMenuLabels.Contains("複製樣式") && noteForm.ContextMenuLabels.Contains("貼上樣式"), "note menu exposes note and style actions");
                noteForm.ApplyEditedContent(note.Text,
                [
                    new DesktopChecklistItem { Text = "已完成", Completed = true },
                    new DesktopChecklistItem { Text = "待完成", Completed = false }
                ]);
                Assert(note.Items.Count == 2 && !note.Items[0].Completed && note.Items[1].Completed, "completed checklist moves to bottom");
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH")))
                {
                    noteForm.StartPosition = FormStartPosition.Manual; noteForm.Location = new Point(-10000, -10000); noteForm.Show(); Application.DoEvents();
                    Assert(noteForm.HasScrollableContent, "small note retains overflow content"); noteForm.ScrollForTest(120); Assert(noteForm.ScrollOffset > 0, "small note scrolls vertically"); noteForm.Hide();
                }
            }
            var distributedNote = new DesktopNoteData { Text = "第一行\r\n第二行", X = 40, Y = 40, Width = 500, Height = 420, Distribution = OverlayContentDistribution.Fill };
            using (var distributedForm = new DesktopLayeredNoteForm(distributedNote))
            {
                distributedForm.RenderForTest();
                Assert(distributedForm.LastDistributionExtra == 0, "note distribution no longer changes vertical spacing");
            }

            Console.WriteLine($"DESKTOP_VIEW_SMOKE_PASSED CURRENT={current}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"DESKTOP_VIEW_SMOKE_FAILED: {ex}");
            return 1;
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {name}");
    }

    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
