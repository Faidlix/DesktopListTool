// 功能：管理桌面清單、資料夾清單、單項清單與便籤，保存配置並協調非懸浮物件的避讓位置。
// 使用：由 --list compact|standard|large 啟動；--shell-menu／--shell-properties／--shell-menu-probe 只供隔離 Shell 程序使用。
// 設定：內容、位置、樣式與懸浮狀態依單／多螢幕配置保存於目前使用者設定。

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
            var shown = false;
            if (int.TryParse(args[2], out var x) && int.TryParse(args[3], out var y))
                shown = NativeShellContextMenu.TryShow(IntPtr.Zero, args[1], new Point(x, y), statusPath: args.Length >= 5 ? args[4] : null);
            Environment.Exit(shown ? 0 : 1);
        }

        if (args.Length >= 2 && args[0].Equals("--shell-properties", StringComparison.OrdinalIgnoreCase))
        {
            var shown = NativeShellContextMenu.ShowProperties(args[1]);
            if (!shown) MessageBox.Show("無法顯示這個項目的內容。", "內容", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(shown ? 0 : 1);
        }

        if (args.Length >= 2 && args[0].Equals("--shell-menu-probe", StringComparison.OrdinalIgnoreCase))
        {
            var shown = NativeShellContextMenu.TryShow(IntPtr.Zero, args[1], Point.Empty, display: false);
            Environment.Exit(shown ? 0 : 1);
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
            var applied = StartupManager.IsEnabled == enabled;
            Console.WriteLine(applied
                ? enabled ? "開機自動啟動已啟用" : "開機自動啟動已停用"
                : "無法變更開機自動啟動設定");
            Environment.ExitCode = applied ? 0 : 1;
            return;
        }

        var startupRun = args.Contains("--startup-run", StringComparer.OrdinalIgnoreCase);
        if (startupRun)
            WaitForDesktopShell(DesktopViewController.FindDesktopListView, Thread.Sleep);
        RunOverlay(ListItemSizeSettings.Load());
    }

    internal static bool WaitForDesktopShell(Func<IntPtr> findDesktop, Action<int> delay, int maxAttempts = 60)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (findDesktop() != IntPtr.Zero)
            {
                delay(1000);
                return true;
            }
            delay(250);
        }
        return false;
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
    private readonly DesktopShortcutMonitor _shortcutMonitor;
    private DesktopLayeredOverlayForm? _activeListOverlay;
    private Form? _activePlacementSource;
    private Rectangle _activePlacementOrigin;

    private readonly ToolStripMenuItem _startupItem;
    private bool _checkingForUpdate;
    private int _savingState;

    public DesktopViewApplicationContext(ListItemSize initialSize)
    {
        StartupManager.MigrateLegacyRegistration();
        _overlay = new DesktopLayeredOverlayForm(initialSize);
        _overlay.FormClosed += (_, _) => ExitThread();
        _overlay.AddFolderRequested += (_, _) => ChooseFolder();
        _overlay.AddNoteRequested += (_, e) => CreateNote(e.ScreenLocation, e.Kind);
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
        var pinnedFolders = PinnedFolderSettings.Load();
        foreach (var folder in pinnedFolders)
            CreateFolderOverlay(folder, save: false);
        PinnedFolderSettings.SaveSnapshot(pinnedFolders);
        _customLists.AddRange(CustomListSettings.Load());
        foreach (var customList in _customLists.ToArray())
            CreateCustomListOverlay(customList, save: false);
        CustomListSettings.Save(_customLists);
        RefreshDetachedDesktopItems();
        _notes.AddRange(DesktopNotesSettings.Load());
        NormalizeLegacyNotes(_notes);
        foreach (var note in _notes)
            ShowNote(note);
        SaveNotes();
        _shortcutMonitor = new DesktopShortcutMonitor(HandleListShortcut, ClearActiveListTarget);
        _blankClickMonitor = new DesktopBlankClickMonitor(HandleDesktopBlankClick);
        Application.ApplicationExit += HandleApplicationExit;
        SystemEvents.SessionEnding += HandleSessionEnding;
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

    internal static void NormalizeLegacyNotes(List<DesktopNoteData> notes)
    {
        var companions = new List<DesktopNoteData>();
        foreach (var note in notes.Where(value => value.Kind is null).ToArray())
        {
            var hasText = !string.IsNullOrWhiteSpace(note.Text);
            var hasTasks = note.Items.Count > 0;
            if (hasText && hasTasks)
            {
                var todo = note.CreateCompanion(DesktopNoteKind.Todo, new Point(28, 28));
                todo.Text = "";
                companions.Add(todo);
                note.Items = [];
                note.Kind = DesktopNoteKind.Note;
            }
            else
            {
                note.Kind = hasTasks ? DesktopNoteKind.Todo : DesktopNoteKind.Note;
                if (note.Kind == DesktopNoteKind.Todo) note.Text = "";
                else note.Items = [];
            }
        }
        notes.AddRange(companions);
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
        folderOverlay.AddNoteRequested += (_, e) => CreateNote(e.ScreenLocation, e.Kind);
        folderOverlay.CloseListRequested += (_, _) => CloseFolderOverlay(folderOverlay);
        AttachPlacementAndCustomListEvents(folderOverlay);
        folderOverlay.FormClosed += (_, _) => _folderOverlays.Remove(folderOverlay);
        _folderOverlays.Add(folderOverlay);
        if (save) folderOverlay.SetBoundsWithoutSaving(FindFreePlacement(folderOverlay.Bounds, folderOverlay));
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
        var existingData = _customLists.FirstOrDefault(value => value.Paths.Contains(path, StringComparer.OrdinalIgnoreCase));
        var existingOverlay = existingData is null ? null : _customOverlays.FirstOrDefault(value => value.CustomListId == existingData.Id);
        if (existingOverlay is not null) { existingOverlay.RevealFrame(); return; }
        var data = new CustomListData { Paths = [path] };
        _customLists.Add(data);
        RefreshDetachedDesktopItems();
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
        overlay.AddNoteRequested += (_, e) => CreateNote(e.ScreenLocation, e.Kind);
        overlay.CloseListRequested += (_, _) => CloseCustomListOverlay(overlay, data);
        overlay.CustomPathsChanged += (_, _) => { CustomListSettings.Save(_customLists); RefreshDetachedDesktopItems(); };
        AttachPlacementAndCustomListEvents(overlay);
        overlay.FormClosed += (_, _) => _customOverlays.Remove(overlay);
        _customOverlays.Add(overlay);
        if (save)
        {
            overlay.FitToContentMinimum();
            overlay.SetBoundsWithoutSaving(FindFreePlacement(overlay.Bounds, overlay));
        }
        OverlaySettings.SaveBounds($"custom:{data.Id:N}", overlay.Bounds);
        overlay.Show();
        if (save) CustomListSettings.Save(_customLists);
        RefreshDetachedDesktopItems();
    }

    private void CloseCustomListOverlay(DesktopLayeredOverlayForm overlay, CustomListData data)
    {
        _customOverlays.Remove(overlay);
        _customLists.Remove(data);
        CustomListSettings.Save(_customLists);
        RefreshDetachedDesktopItems();
        overlay.Close();
        overlay.Dispose();
    }

    private void RefreshDetachedDesktopItems()
    {
        _overlay.SetExcludedPaths(_customLists.SelectMany(value => value.Paths));
    }

    private void AttachPlacementAndCustomListEvents(DesktopLayeredOverlayForm overlay)
    {
        overlay.CreateCustomListRequested += (_, e) => CreateCustomList(e.Path, e.ScreenLocation);
        overlay.ResolvePlacement = (sender, candidate) => FindFreePlacement(candidate, sender);
        overlay.PlacementPreview = PreviewPlacement;
        overlay.PlacementCompleted = CompletePlacement;
        overlay.CheckUpdateRequested += async (_, _) => await CheckForUpdatesAsync(interactive: true);
        overlay.PhysicalPathsChanged += (_, _) => RefreshAllListItems();
        overlay.KeyboardTargetActivated += HandleKeyboardTargetActivated;
    }

    private void HandleKeyboardTargetActivated(object? sender, EventArgs e)
    {
        if (sender is not DesktopLayeredOverlayForm active || active.IsDisposed) return;
        _activeListOverlay = active;
        foreach (var overlay in AllListOverlays().Where(value => !ReferenceEquals(value, active) && !value.IsDisposed))
            overlay.DeactivateKeyboardTarget();
    }

    private bool HandleListShortcut(Keys key, bool control)
    {
        var active = _activeListOverlay;
        if (active is null || active.IsDisposed || !active.HasKeyboardTarget)
        {
            _activeListOverlay = null;
            return false;
        }
        return active.HandleKeyboardShortcut(key, control);
    }

    private void RefreshAllListItems()
    {
        foreach (var list in AllListOverlays().Where(value => !value.IsDisposed).ToArray())
            list.RefreshItems();
        RefreshDetachedDesktopItems();
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
            _noteForms.Any(value => !value.IsDisposed && value.Bounds.Contains(screenPoint)) ||
            AllListOverlays().Any(value => !value.IsDisposed && value.OwnsContextMenuPoint(screenPoint)) ||
            _noteForms.Any(value => !value.IsDisposed && value.OwnsContextMenuPoint(screenPoint))) return;
        try
        {
            _overlay.BeginInvoke(() =>
            {
                _activeListOverlay = null;
                NativeShellContextMenu.DismissActiveMenu();
                foreach (var overlay in AllListOverlays()) overlay.DeactivateKeyboardTarget();
            });
        }
        catch (InvalidOperationException) { }
    }

    private Rectangle FindFreePlacement(Rectangle candidate, Form? self)
    {
        if (self is not null && IsFloatingSurface(self))
            return DesktopLayeredOverlayForm.ConstrainRectangle(candidate, Screen.FromRectangle(candidate).WorkingArea);
        var workArea = Screen.FromRectangle(candidate).WorkingArea;
        candidate = DesktopLayeredOverlayForm.ConstrainRectangle(candidate, workArea);
        var occupied = AllDesktopSurfaces().Where(value => value != self && !IsFloatingSurface(value) && !value.IsDisposed)
            .Select(value => value.Bounds).ToArray();
        return FindFreeListPlacement(candidate, workArea, occupied);
    }

    private IEnumerable<Form> AllDesktopSurfaces()
    {
        foreach (var overlay in AllListOverlays()) yield return overlay;
        foreach (var note in _noteForms) yield return note;
    }

    private static bool IsFloatingSurface(Form form) => form switch
    {
        DesktopLayeredOverlayForm overlay => overlay.IsFloating,
        DesktopLayeredNoteForm note => note.IsFloating,
        _ => false
    };

    private static void SetSurfaceBoundsWithoutSaving(Form form, Rectangle bounds)
    {
        if (form is DesktopLayeredOverlayForm overlay) overlay.SetBoundsWithoutSaving(bounds);
        else if (form is DesktopLayeredNoteForm note) note.SetBoundsWithoutSaving(bounds);
    }

    private void PreviewPlacement(Form source, Rectangle candidate)
    {
        if (IsFloatingSurface(source)) return;
        if (_activePlacementSource != source)
        {
            _activePlacementSource = source;
            _activePlacementOrigin = source.Bounds;
        }
    }

    private void CompletePlacement(Form source)
    {
        if (_activePlacementSource != source) return;
        if (source.Bounds == _activePlacementOrigin)
        {
            _activePlacementSource = null;
            return;
        }
        var surfaces = AllDesktopSurfaces()
            .Where(value => value != source && !value.IsDisposed && !IsFloatingSurface(value))
            .ToArray();
        var originalBounds = surfaces.Select(value => value.Bounds).ToArray();
        var planned = PlanReleaseDisplacements(source.Bounds, Screen.FromRectangle(source.Bounds).WorkingArea, originalBounds);
        foreach (var (index, bounds) in planned)
        {
            var form = surfaces[index];
            SetSurfaceBoundsWithoutSaving(form, bounds);
            if (form is DesktopLayeredOverlayForm overlay) overlay.PersistState();
            else if (form is DesktopLayeredNoteForm note) note.PersistState();
        }
        _activePlacementSource = null;
        SaveNotes();
    }

    internal static Dictionary<int, Rectangle> PlanReleaseDisplacements(Rectangle moving, Rectangle workArea, IReadOnlyList<Rectangle> stationary)
    {
        var current = stationary.ToArray();
        var result = new Dictionary<int, Rectangle>();
        for (var index = 0; index < current.Length; index++)
        {
            if (!current[index].IntersectsWith(moving)) continue;
            var occupied = current.Where((_, otherIndex) => otherIndex != index).Append(moving).ToArray();
            var target = FindFreeListPlacement(current[index], workArea, occupied);
            current[index] = target;
            if (target != stationary[index]) result[index] = target;
        }
        return result;
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

    private void CreateNote(Point screenLocation, DesktopNoteKind kind)
    {
        using var editor = new NoteEditorDialog(kind);
        if (editor.ShowDialog() != DialogResult.OK)
            return;
        if (string.IsNullOrWhiteSpace(editor.NoteText) && editor.ChecklistItems.Count == 0)
            return;

        var note = new DesktopNoteData
        {
            Kind = kind,
            Text = editor.NoteText.TrimEnd(),
            Items = editor.ChecklistItems.ToList(),
            Bounds = new Rectangle(screenLocation.X, screenLocation.Y, 340, 260)
        };
        note.Bounds = FindFreePlacement(note.Bounds, null);
        var placement = DisplayLayoutIdentity.CapturePlacement(note.Bounds);
        note.MonitorDeviceName = placement.DeviceName; note.MonitorRelativeX = placement.RelativeX; note.MonitorRelativeY = placement.RelativeY;
        _notes.Add(note);
        ShowNote(note);
        SaveNotes();
    }

    private void ShowNote(DesktopNoteData note)
    {
        var form = new DesktopLayeredNoteForm(note);
        form.EditRequested += (_, _) => EditNote(form);
        form.DeleteRequested += (_, _) => DeleteNote(form);
        form.AddNoteRequested += (_, e) => CreateNote(e.ScreenLocation, e.Kind);
        form.NoteChanged += (_, _) => SaveNotes();
        form.InteractionActivated += (_, _) => ClearActiveListTarget();
        form.ResolvePlacement = (sender, candidate) => FindFreePlacement(candidate, sender);
        form.PlacementPreview = PreviewPlacement;
        form.PlacementCompleted = CompletePlacement;
        form.SetBoundsWithoutSaving(FindFreePlacement(form.Bounds, form));
        form.PersistState();
        _noteForms.Add(form);
        form.Show();
    }

    private void ClearActiveListTarget()
    {
        _activeListOverlay = null;
        NativeShellContextMenu.DismissActiveMenu();
        foreach (var overlay in AllListOverlays().Where(value => !value.IsDisposed))
            overlay.DeactivateKeyboardTarget();
    }

    private void EditNote(DesktopLayeredNoteForm form)
    {
        var kind = form.Data.Kind ?? DesktopNoteKind.Note;
        using var editor = new NoteEditorDialog(kind, form.Data);
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

    private void HandleApplicationExit(object? sender, EventArgs e) => PersistAllState(capturePlacement: true);

    private void HandleSessionEnding(object sender, SessionEndingEventArgs e) => PersistAllState(capturePlacement: false);

    private void PersistAllState(bool capturePlacement)
    {
        if (Interlocked.Exchange(ref _savingState, 1) == 1) return;
        try
        {
            if (capturePlacement)
            {
                foreach (var overlay in AllListOverlays().Where(value => !value.IsDisposed)) overlay.PersistState();
                foreach (var noteForm in _noteForms.Where(value => !value.IsDisposed)) noteForm.PersistState();
            }
            CustomListSettings.Save(_customLists);
            SaveNotes();
        }
        finally { Volatile.Write(ref _savingState, 0); }
    }

    private RegisteredWaitHandle RegisterCommand(EventWaitHandle commandEvent, Action action) =>
        ThreadPool.RegisterWaitForSingleObject(commandEvent, (_, _) =>
        {
            if (!_overlay.IsDisposed)
                _overlay.BeginInvoke(action);
        }, null, Timeout.Infinite, executeOnlyOnce: false);

    protected override void ExitThreadCore()
    {
        PersistAllState(capturePlacement: true);
        Application.ApplicationExit -= HandleApplicationExit;
        SystemEvents.SessionEnding -= HandleSessionEnding;
        _shortcutMonitor.Dispose();
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
    private static string StartupShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DesktopListTool.lnk");

    public static bool IsEnabled => IsStartupShortcutCurrent() || HasLegacyRegistration();

    public static void MigrateLegacyRegistration()
    {
        if (!HasLegacyRegistration() || !LegacyRegistrationTargetsCurrentExecutable() || IsStartupShortcutCurrent())
            return;
        if (CreateStartupShortcut())
            DeleteLegacyRegistration();
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (CreateStartupShortcut())
                DeleteLegacyRegistration();
            return;
        }
        DeleteStartupShortcut();
        DeleteLegacyRegistration();
    }

    private static bool IsStartupShortcutCurrent()
    {
        if (!File.Exists(StartupShortcutPath)) return false;
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;
            shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [StartupShortcutPath]);
            if (shortcut is null) return false;
            var targetPath = shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            var arguments = shortcut.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            return string.Equals(Path.GetFullPath(targetPath ?? ""), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(arguments?.Trim(), "--startup-run", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or COMException or System.Reflection.TargetInvocationException or ArgumentException)
        {
            return false;
        }
        finally { ReleaseComObject(shortcut); ReleaseComObject(shell); }
    }

    private static bool CreateStartupShortcut()
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupShortcutPath)!);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;
            shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [StartupShortcutPath]);
            if (shortcut is null) return false;
            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [Application.ExecutablePath]);
            shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, ["--startup-run"]);
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(Application.ExecutablePath) ?? ""]);
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, ["登入後啟動桌面清單"]);
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            return IsStartupShortcutCurrent();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or COMException or System.Reflection.TargetInvocationException or ArgumentException)
        {
            return false;
        }
        finally { ReleaseComObject(shortcut); ReleaseComObject(shell); }
    }

    private static bool HasLegacyRegistration() => !string.IsNullOrWhiteSpace(ReadLegacyRegistration());

    private static bool LegacyRegistrationTargetsCurrentExecutable()
    {
        var value = ReadLegacyRegistration();
        return value?.Contains(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? ReadLegacyRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    private static void DeleteLegacyRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
    }

    private static void DeleteStartupShortcut()
    {
        try { File.Delete(StartupShortcutPath); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
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
            using var profileKey = Registry.CurrentUser.OpenSubKey(DisplayLayoutIdentity.CurrentProfileKey);
            using var legacyKey = profileKey?.GetValue(ValueName) is string[] ? null : Registry.CurrentUser.OpenSubKey(SettingsKey);
            return (((profileKey?.GetValue(ValueName) ?? legacyKey?.GetValue(ValueName)) as string[]) ?? [])
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

    public static void SaveSnapshot(IEnumerable<string> folderPaths) => Save(folderPaths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    private static void Save(string[] values)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(DisplayLayoutIdentity.CurrentProfileKey);
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

            var shellProbes = 0;
            var shellDelays = new List<int>();
            Assert(Program.WaitForDesktopShell(() => ++shellProbes >= 3 ? (IntPtr)1 : IntPtr.Zero, shellDelays.Add, 4) &&
                shellDelays.SequenceEqual([250, 250, 1000]), "startup waits for Explorer desktop readiness before showing overlays");

            var workArea = new Rectangle(0, 0, 1920, 1040);
            Assert(DesktopLayeredOverlayForm.ConstrainRectangle(new Rectangle(9, 200, 400, 500), workArea).X == 9, "inside edge remains free");
            Assert(DesktopLayeredOverlayForm.ConstrainRectangle(new Rectangle(-9, 200, 400, 500), workArea).X == 0, "left overflow constrained");
            Assert(DesktopLayeredOverlayForm.ConstrainRectangle(new Rectangle(1530, 200, 400, 500), workArea).Right == 1920, "right overflow constrained");
            Assert(DesktopLayeredOverlayForm.ConstrainRectangle(new Rectangle(400, 560, 400, 500), workArea).Bottom == 1040, "bottom overflow constrained");
            Assert(OverlayStyleRendering.GetResizeCorner(new Point(2, 2), new Size(400, 300)) == OverlayResizeCorner.TopLeft && OverlayStyleRendering.GetResizeCorner(new Point(398, 298), new Size(400, 300)) == OverlayResizeCorner.BottomRight, "resize hover corners are detected around the frame");
            Assert(OverlayStyleRendering.ResizeIndicatorThickness >= 10F, "rounded resize indicator is approximately scrollbar-thumb thickness");
            using (var inputHitBrush = OverlayStyleRendering.CreateInputHitBrush(Color.DarkOrange))
                Assert(inputHitBrush.Color.A == 1, "detached list transparent surface remains mouse-interactive without becoming visible");
            Assert(OverlayAppearanceSettings.DecodeExplorerAccentColor(0xFF2A4C91).ToArgb() == Color.FromArgb(145, 76, 42).ToArgb(), "Explorer taskbar ABGR color is decoded correctly");
            using (var explorerAccent = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                if (explorerAccent?.GetValue("StartColorMenu") is int taskbarColor)
                    Assert(OverlayAppearanceSettings.GetWindowsThemeColor().ToArgb() == OverlayAppearanceSettings.DecodeExplorerAccentColor(unchecked((uint)taskbarColor)).ToArgb(), "system color follows the current Explorer taskbar color");
            Assert(DesktopLayeredOverlayForm.ShouldShowDesktopDropCursor(DragDropEffects.None, insideSource: false, overDesktop: true) &&
                !DesktopLayeredOverlayForm.ShouldShowDesktopDropCursor(DragDropEffects.None, insideSource: false, overDesktop: false), "desktop item drag uses a move cursor only over a valid desktop surface");
            Assert(OverlaySettings.GetSettingsKey("custom:smoke").Contains(DisplayLayoutIdentity.CurrentId, StringComparison.Ordinal), "list settings are isolated by display layout");
            var currentScreen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            var sampleBounds = new Rectangle(currentScreen.WorkingArea.Left + 73, currentScreen.WorkingArea.Top + 41, 360, 220);
            var samplePlacement = DisplayLayoutIdentity.CapturePlacement(sampleBounds);
            Assert(DisplayLayoutIdentity.RestoreBounds(sampleBounds, samplePlacement.DeviceName, samplePlacement.RelativeX, samplePlacement.RelativeY, sampleBounds) == sampleBounds, "monitor-relative placement round trips");
            if (Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_REGISTRY") == "1")
            {
                var stateIdentity = $"custom:smoke-{Guid.NewGuid():N}";
                try
                {
                    OverlaySettings.SaveBounds(stateIdentity, sampleBounds); OverlaySettings.SaveFloating(stateIdentity, true);
                    var savedStyle = new OverlayStyleOptions { BackgroundColor = Color.MediumPurple, BackgroundOpacity = 123, GradientOpacity = 77, FontSize = 13F, LineHeight = 44, HorizontalSpacing = 37 };
                    OverlayAppearanceSettings.SaveStyle(stateIdentity, savedStyle);
                    OverlayOrderSettings.SaveDisplayMode(stateIdentity, OverlayDisplayMode.Icons);
                    OverlayOrderSettings.SaveReferencePaths(stateIdentity, [@"C:\DesktopListTool-Smoke\reference.txt"]);
                    var loadedStyle = OverlayAppearanceSettings.LoadStyle(stateIdentity, new OverlayStyleOptions());
                    var loadedBounds = OverlaySettings.LoadBounds(stateIdentity, Rectangle.Empty); var loadedFloating = OverlaySettings.LoadFloating(stateIdentity);
                    Assert(loadedBounds == sampleBounds && loadedFloating, $"list position and floating state persist for current display layout ({loadedBounds}, floating={loadedFloating})");
                    Assert(loadedStyle.BackgroundColor.ToArgb() == Color.MediumPurple.ToArgb() && loadedStyle.BackgroundOpacity == 123 && loadedStyle.GradientOpacity == 77 && loadedStyle.LineHeight == 44 && loadedStyle.HorizontalSpacing == 37, "list style including horizontal spacing persists for current display layout");
                    Assert(OverlayOrderSettings.LoadDisplayMode(stateIdentity) == OverlayDisplayMode.Icons, "per-list icon display mode persists for current display layout");
                    Assert(OverlayOrderSettings.LoadReferencePaths(stateIdentity).SequenceEqual([@"C:\DesktopListTool-Smoke\reference.txt"], StringComparer.OrdinalIgnoreCase), "cross-list reference paths persist for current display layout");
                }
                finally
                {
                    Registry.CurrentUser.DeleteSubKeyTree(OverlaySettings.GetSettingsKey(stateIdentity), false);
                    Registry.CurrentUser.DeleteSubKeyTree(OverlaySettings.GetLegacySettingsKey(stateIdentity), false);
                }
            }
            var placed = DesktopViewApplicationContext.FindFreeListPlacement(
                new Rectangle(20, 20, 360, 240), workArea, [new Rectangle(0, 0, 500, 600)]);
            Assert(!placed.IntersectsWith(new Rectangle(0, 0, 500, 600)) && workArea.Contains(placed), "new lists avoid existing lists");
            var movingSurface = new Rectangle(100, 100, 300, 220);
            Rectangle[] stationarySurfaces = [new Rectangle(120, 120, 260, 180), new Rectangle(700, 120, 260, 180)];
            var pushed = DesktopViewApplicationContext.PlanReleaseDisplacements(movingSurface, workArea, stationarySurfaces);
            Assert(pushed.Count == 1 && pushed.TryGetValue(0, out var pushedBounds) && !pushedBounds.IntersectsWith(movingSurface) && !pushedBounds.IntersectsWith(stationarySurfaces[1]), "mouse release pushes only the conflicting list or note");
            Assert(DesktopViewApplicationContext.PlanReleaseDisplacements(new Rectangle(400, 700, 200, 150), workArea, stationarySurfaces).Count == 0, "releasing outside conflicts leaves original positions unchanged");
            Assert(AutoUpdateManager.IsNewerVersion(new Version(1, 0, 0), "v1.1.0", out var updateVersion) && updateVersion == new Version(1, 1, 0), "release version comparison");

            var desktopEntries = DesktopOverlayEntry.Load();
            Assert(desktopEntries.Any(entry => entry.IsVirtual && entry.Path == DesktopOverlayEntry.RecycleBinPath), "recycle bin appears in desktop list");
            Assert(DesktopLayeredOverlayForm.CanBeginItemDrag(true, DesktopOverlayEntry.RecycleBinPath) &&
                !DesktopLayeredOverlayForm.CanBeginItemDrag(true, "shell:unsupported"), "recycle bin alone may detach into a reference list without enabling arbitrary virtual-item moves");
            Assert(DesktopLayeredOverlayForm.IsCrossListReferenceDrop("custom:source", "custom:target", hasPhysicalTarget: false) &&
                !DesktopLayeredOverlayForm.IsCrossListReferenceDrop("custom:source", "custom:target", hasPhysicalTarget: true) &&
                !DesktopLayeredOverlayForm.IsCrossListReferenceDrop("custom:same", "custom:same", hasPhysicalTarget: false), "cross-list blank drops preserve the source while physical targets take priority");
            Assert(DesktopLayeredOverlayForm.ShouldRemoveSourceReference(DragDropEffects.Move, physicalMoveCompleted: true, droppedOutsideSource: false) &&
                DesktopLayeredOverlayForm.ShouldRemoveSourceReference(DragDropEffects.Move, physicalMoveCompleted: false, droppedOutsideSource: true) &&
                !DesktopLayeredOverlayForm.ShouldRemoveSourceReference(DragDropEffects.Copy, physicalMoveCompleted: true, droppedOutsideSource: true), "physical moves remove stale references even when overlapping lists share the drop point, while copies retain them");
            Assert(DesktopShortcutMonitor.IsSupportedShortcut(Keys.C, control: true) && DesktopShortcutMonitor.IsSupportedShortcut(Keys.X, control: true) &&
                DesktopShortcutMonitor.IsSupportedShortcut(Keys.V, control: true) && DesktopShortcutMonitor.IsSupportedShortcut(Keys.Delete, control: false) &&
                !DesktopShortcutMonitor.IsSupportedShortcut(Keys.Delete, control: true), "selected list items support input-language-independent copy, cut, paste and delete shortcuts");
            using (var recycleIcon = DesktopOverlayEntry.GetIcon(DesktopOverlayEntry.RecycleBinPath, true))
                Assert(recycleIcon is not null, "recycle bin icon available");
            Assert(DesktopOverlayEntry.LoadPaths([DesktopOverlayEntry.RecycleBinPath]).Single().IsVirtual, "custom list can contain the recycle bin reference");
            Assert(NativeShellContextMenu.RequiresExtendedShellLifetime("properties") &&
                NativeShellContextMenu.RequiresExtendedShellLifetime("PROPERTIES") &&
                !NativeShellContextMenu.RequiresExtendedShellLifetime("open"), "properties commands keep the isolated Shell message loop alive without blocking later context menus");
            Assert(NativeShellContextMenu.IsPropertiesCommand(null, "內容(&R)") &&
                NativeShellContextMenu.IsPropertiesCommand(null, "屬性") &&
                NativeShellContextMenu.IsPropertiesCommand(null, "Properties") &&
                !NativeShellContextMenu.IsPropertiesCommand("open", "開啟"), "localized Properties menu text uses the reliable Shell properties path");
            Assert(NativeShellContextMenu.RequiresSafeFallback(timedOut: false, exitCode: unchecked((int)0xC0000409), reachedMenu: false) &&
                !NativeShellContextMenu.RequiresSafeFallback(timedOut: false, exitCode: unchecked((int)0xC0000409), reachedMenu: true) &&
                !NativeShellContextMenu.RequiresSafeFallback(timedOut: false, exitCode: 0, reachedMenu: false), "Shell crashes before menu creation use the safe fallback without duplicating an already displayed menu");
            using (var overlayConstruction = new DesktopLayeredOverlayForm(ListItemSize.Standard))
            {
                Assert(overlayConstruction.Width >= overlayConstruction.MinimumSize.Width, "overlay constructs before resize callbacks");
                Assert(overlayConstruction.ContextMenuLabels.Contains("新增便籤…") && overlayConstruction.ContextMenuLabels.Contains("新增代辦清單…") && overlayConstruction.ContextMenuLabels.Contains("複製樣式") && overlayConstruction.ContextMenuLabels.Contains("貼上樣式") && overlayConstruction.ContextMenuLabels.Contains("變成桌面圖示樣式"), "list menu exposes separate note, todo, style and icon actions");
                Assert(overlayConstruction.UsesNoActivateForTest && overlayConstruction.MouseActivateResultForTest() == (IntPtr)3, "fixed lists reject mouse activation and stay in the desktop z-order");
                overlayConstruction.ActivateListForTest();
                Assert(overlayConstruction.ListSelectedForTest && overlayConstruction.HasKeyboardTarget && overlayConstruction.FrameVisibleForTest, "clicking list blank space keeps a persistent framed list paste target");
                if (overlayConstruction.EntryPaths.Count > 0)
                {
                    overlayConstruction.ActivateItemForTest(0);
                    Assert(!overlayConstruction.ListSelectedForTest && overlayConstruction.HasKeyboardTarget && !overlayConstruction.FrameVisibleForTest, "clicking an item selects only the item, without the list frame state");
                }
                overlayConstruction.DeactivateKeyboardTarget();
                Assert(!overlayConstruction.HasKeyboardTarget, "clicking the Windows desktop clears the list keyboard target");
                var detachable = overlayConstruction.EntryPaths.FirstOrDefault(value => value != DesktopOverlayEntry.RecycleBinPath);
                if (detachable is not null) { overlayConstruction.SetExcludedPaths([detachable]); Assert(!overlayConstruction.EntryPaths.Contains(detachable, StringComparer.OrdinalIgnoreCase), "detached desktop item is hidden from main list"); overlayConstruction.SetExcludedPaths([]); }
            }

            var referenceRoot = Path.Combine(Path.GetTempPath(), $"DesktopListTool-Reference-{Guid.NewGuid():N}");
            Directory.CreateDirectory(referenceRoot);
            var referenceFile = Path.Combine(referenceRoot, "original.txt");
            File.WriteAllText(referenceFile, "reference-only");
            var secondReference = Path.Combine(referenceRoot, "second.txt");
            var thirdReference = Path.Combine(referenceRoot, "third.txt");
            File.WriteAllText(secondReference, "second"); File.WriteAllText(thirdReference, "third");
            var referenced = DesktopOverlayEntry.LoadPaths([referenceFile]);
            Assert(referenced.Count == 1 && File.Exists(referenceFile), "custom list references without moving source");
            Assert(NativeShellContextMenu.ProbeIsolated(referenceFile), "isolated native shell context menu probe succeeds");
            var previousFloatingQa = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_FLOATING");
            Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_FLOATING", "1");
            try
            {
                using var detachedOverlay = new DesktopLayeredOverlayForm(ListItemSize.Standard,
                    defaultBounds: new Rectangle(currentScreen.WorkingArea.Left + 120, currentScreen.WorkingArea.Top + 120, 360, 180),
                    customList: new CustomListData { Paths = [referenceFile, secondReference, thirdReference] });
                var narrowWidth = detachedOverlay.MinimumSize.Width;
                var narrowHeight = detachedOverlay.RequiredContentHeightForTest(narrowWidth);
                var wideWidth = Math.Min(currentScreen.WorkingArea.Width - 80, narrowWidth * 3);
                var wideHeight = detachedOverlay.RequiredContentHeightForTest(wideWidth);
                Assert(detachedOverlay.AdaptiveColumnCountForTest(wideWidth) > detachedOverlay.AdaptiveColumnCountForTest(narrowWidth) && wideHeight < narrowHeight, "detached list adds horizontal columns and lowers its required height when widened");
                var normalMetrics = detachedOverlay.LayoutMetricsForTest;
                var normalColumns = detachedOverlay.AdaptiveColumnCountForTest(wideWidth);
                detachedOverlay.SetHorizontalSpacingForTest(96);
                Assert(detachedOverlay.AdaptiveColumnCountForTest(wideWidth) < normalColumns, "horizontal item spacing immediately changes list column layout");
                detachedOverlay.SetHorizontalSpacingForTest(16);
                detachedOverlay.SetTextMetricsForTest(36F, 84, 12);
                var largeMetrics = detachedOverlay.LayoutMetricsForTest;
                Assert(largeMetrics.Width > normalMetrics.Width && largeMetrics.Height > normalMetrics.Height && detachedOverlay.RequiredContentHeightForTest(narrowWidth) > narrowHeight, "font and line scaling immediately update list cell metrics and minimum range");
                detachedOverlay.SetTextMetricsForTest(10F, 36, 0);
                detachedOverlay.FitToContentMinimum();
                Assert(detachedOverlay.Width == narrowWidth && detachedOverlay.Height == narrowHeight && narrowHeight > detachedOverlay.MinimumSize.Height, "detached list initial minimum range displays all current items");
                detachedOverlay.Size = new Size(wideWidth, wideHeight);
                detachedOverlay.Show(); Application.DoEvents();
                var blankPoint = detachedOverlay.PointToScreen(new Point(detachedOverlay.ClientSize.Width - 24, detachedOverlay.ClientSize.Height / 2));
                Assert(detachedOverlay.OwnsScreenPointForTest(blankPoint), "detached list transparent blank area receives right-click and move input");
                detachedOverlay.ToggleDisplayModeForTest();
                Assert(detachedOverlay.DisplayModeForTest == OverlayDisplayMode.Icons && detachedOverlay.RequiredContentHeightForTest(wideWidth) >= detachedOverlay.MinimumSize.Height, "desktop icon mode remains inside the resizable custom-list frame");
                var fourthReference = Path.Combine(referenceRoot, "fourth.txt"); File.WriteAllText(fourthReference, "fourth");
                detachedOverlay.AddReferencePathsForTest([fourthReference]);
                Assert(detachedOverlay.EntryPaths.Contains(fourthReference, StringComparer.OrdinalIgnoreCase) && File.Exists(fourthReference), "cross-list drops add a reference without moving the original file");
                File.Delete(thirdReference); detachedOverlay.RefreshItems();
                Assert(!detachedOverlay.EntryPaths.Contains(thirdReference, StringComparer.OrdinalIgnoreCase), "a physical deletion refreshes the list immediately without a manual reload command");
                detachedOverlay.Hide();
            }
            finally { Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_FLOATING", previousFloatingQa); }
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
                HorizontalSpacing = 26,
                CornerRadius = 18,
                TextAlignment = OverlayTextAlignment.Center,
                Distribution = OverlayContentDistribution.Fill
            }, "清單"))
            {
                var controls = appearance.Controls.Cast<Control>().SelectMany(Descendants).ToArray();
                var themeOption = controls.OfType<CheckBox>().Single(value => value.Text.Contains("Windows 系統顏色"));
                Assert(themeOption.Text.Contains("Windows 系統顏色"), "system color option is visible");
                Assert(appearance.SelectedStyle.Bold && appearance.SelectedStyle.CornerRadius == 18 &&
                    appearance.SelectedStyle.TextAlignment == OverlayTextAlignment.Center && appearance.SelectedStyle.Distribution == OverlayContentDistribution.Fill && appearance.SelectedStyle.HorizontalSpacing == 26 &&
                    appearance.SelectedStyle.GradientEnabled && appearance.SelectedStyle.GradientDirection == OverlayGradientDirection.DiagonalDown && appearance.SelectedStyle.GradientMode == OverlayGradientMode.Sandwich && appearance.SelectedStyle.GradientOpacity == 140 && appearance.SelectedStyle.TextColor == Color.LightYellow,
                    "extended style values round trip");
                Assert(appearance.AcceptButton is null, "style Enter does not confirm");
                Assert(controls.OfType<SliderNumberControl>().Count() == 6, "list style includes slider controls for horizontal spacing and other persistent numeric values");
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

            using (var noteAppearance = new OverlayStyleDialog(new OverlayStyleOptions(), "便籤"))
            {
                var controls = noteAppearance.Controls.Cast<Control>().SelectMany(Descendants).ToArray();
                Assert(controls.OfType<SliderNumberControl>().Count() == 5, "note style omits the list-only horizontal-spacing slider");
            }
            using (var todoAppearance = new OverlayStyleDialog(new OverlayStyleOptions(), "代辦清單"))
            {
                var controls = todoAppearance.Controls.Cast<Control>().SelectMany(Descendants).ToArray();
                Assert(controls.OfType<SliderNumberControl>().Count() == 5, "todo style also omits the file-list-only horizontal-spacing slider");
            }

            Assert(InlineTextEditor.CommitsOnKey(Keys.Enter, shift: false, multiline: true) &&
                !InlineTextEditor.CommitsOnKey(Keys.Enter, shift: true, multiline: true) &&
                InlineTextEditor.CancelsOnKey(Keys.Escape), "inline note editing uses Enter to confirm, Shift+Enter for newline and Esc to cancel");
            using (var inlineEditor = new InlineTextEditor(new Rectangle(0, 0, 240, 90), "原樣式", true, false, Control.DefaultFont))
            {
                var textInput = inlineEditor.Controls.OfType<TextBox>().Single();
                Assert(inlineEditor.Size == new Size(240, 90) && inlineEditor.Opacity < 0.02 && textInput.ScrollBars == ScrollBars.None && textInput.WordWrap,
                    "inline note editor covers the text target for mouse selection without adding a visible system background or scrollbar");
                Assert(InlineTextEditor.CreateFileLink(@"C:\Example\測試.txt").Contains("[測試.txt](file:", StringComparison.Ordinal),
                    "pasted desktop files become display-name shortcut links");
            }

            using (var colorAlpha = new ColorWithAlphaDialog(Color.Black, 64))
            {
                var liveEvents = 0; colorAlpha.PreviewChanged += (_, _) => liveEvents++; colorAlpha.SetAlphaForTest(72);
                Assert(liveEvents == 1 && Math.Abs(colorAlpha.SelectedAlpha - 184) <= 1, "color opacity changes publish live preview events");
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
                var sameFolderCopies = DesktopLayeredOverlayForm.DuplicatePaths([file], source);
                Assert(sameFolderCopies.Count == 1 && File.Exists(sameFolderCopies[0]) && File.Exists(file) && Path.GetFileName(sameFolderCopies[0]).Contains("複製"), "ctrl drag on the same list creates a uniquely named physical copy");
                Assert(DesktopLayeredOverlayForm.PastePathsIntoDirectory([file], source, move: false) &&
                    File.Exists(Path.Combine(source, "drag - 複製 (2).txt")), "copy and paste in the source list creates the next uniquely named copy in place");
                Assert(DesktopLayeredOverlayForm.TransferPaths([file], copyTarget, copy: true), "copy reports a physical change");
                Assert(File.Exists(Path.Combine(copyTarget, "drag.txt")) && File.Exists(file), "ctrl drag copies file");
                Assert(DesktopLayeredOverlayForm.TransferPaths([file], moveTarget, copy: false), "move reports a physical change");
                Assert(File.Exists(Path.Combine(moveTarget, "drag.txt")) && !File.Exists(file), "drag moves file");
                Assert(!DesktopLayeredOverlayForm.TransferPaths([Path.Combine(moveTarget, "drag.txt")], moveTarget, copy: false), "dropping onto the same physical folder is a no-op and keeps its list reference");
                Assert(DesktopLayeredOverlayForm.HaveSameVolumeRoot(@"C:\source\file.txt", @"C:\target\file.txt") &&
                    !DesktopLayeredOverlayForm.HaveSameVolumeRoot(@"C:\source\file.txt", @"D:\target\file.txt"), "cross-volume roots are detected before moving");
                var forcedSource = Path.Combine(transferRoot, "forced-source");
                var forcedTarget = Path.Combine(transferRoot, "forced-target");
                var forcedFile = Path.Combine(forcedSource, "cross-volume.txt");
                var forcedFolder = Path.Combine(forcedSource, "cross-volume-folder");
                Directory.CreateDirectory(forcedFolder);
                File.WriteAllText(forcedFile, "safe-file-move");
                File.WriteAllText(Path.Combine(forcedFolder, "nested.txt"), "safe-folder-move");
                DesktopLayeredOverlayForm.TransferPaths([forcedFile, forcedFolder], forcedTarget, copy: false, (_, _) => false);
                Assert(File.ReadAllText(Path.Combine(forcedTarget, "cross-volume.txt")) == "safe-file-move" && !File.Exists(forcedFile), "cross-volume file move copies completely before deleting source");
                Assert(File.ReadAllText(Path.Combine(forcedTarget, "cross-volume-folder", "nested.txt")) == "safe-folder-move" && !Directory.Exists(forcedFolder), "cross-volume folder move copies completely before deleting source");
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
                Kind = DesktopNoteKind.Note,
                Text = "## 每週回顧\r\n**完成：** Markdown 顯示與安全圓角\r\n- 第一個項目\r\n- 第二個項目\r\n> 這是一段引言\r\n`inline code` 與 [連結](https://example.invalid)\r\n第七行用來確認內容超出時可捲動\r\n第八行仍需保留",
                X = 40, Y = 40, Width = 420, Height = 180, CornerRadius = 42,
                GradientEnabled = true, GradientColorArgb = Color.DarkOrange.ToArgb(), GradientOpacity = 120, GradientDirection = OverlayGradientDirection.DiagonalDown, GradientMode = OverlayGradientMode.Edges,
                GradientTransition = 60, TextColorArgb = Color.LightYellow.ToArgb()
            };
            if (Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_STRAIGHT_RESIZE") == "1") note.CornerRadius = 0;
            var markdown = MarkdownNoteRenderer.ParseLine("## **標題**與[連結](https://example.invalid)");
            Assert(markdown.HeadingLevel == 2 && markdown.Runs.Any(value => value.Style.HasFlag(FontStyle.Bold)) && markdown.Runs.Any(value => value.Link == "https://example.invalid"), "Markdown formatting and links parse");
            Assert(MarkdownNoteRenderer.ParseLine("網址 https://example.invalid/path").Runs.Any(value => value.Link == "https://example.invalid/path") &&
                MarkdownNoteRenderer.TryGetSingleLink("https://example.invalid", out _, out _), "pasted raw URLs become clickable note and todo links after confirmation");
            using (var editor = new NoteEditorDialog(DesktopNoteKind.Note, note))
            {
                Assert(editor.AcceptButton is null, "note editor Enter does not confirm");
                var descendants = editor.Controls.Cast<Control>().SelectMany(value => new[] { value }.Concat(Descendants(value))).ToArray();
                Assert(!descendants.OfType<Button>().Any(value => value.Text.Contains("新增勾選事項")), "plain note editor does not contain a checklist add button");
                var editorQa = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_EDITOR_PATH");
                if (!string.IsNullOrWhiteSpace(editorQa))
                {
                    editor.StartPosition = FormStartPosition.Manual; editor.Location = new Point(-10000, -10000); editor.Show(); Application.DoEvents();
                    using var image = new Bitmap(editor.Width, editor.Height); editor.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(editorQa); editor.Hide();
                }
            }
            using (var noteForm = new DesktopLayeredNoteForm(note))
            {
                Assert(noteForm.ContextMenuLabels.Contains("新增便籤…") && noteForm.ContextMenuLabels.Contains("新增代辦清單…") && noteForm.ContextMenuLabels.Contains("樣式…") && noteForm.ContextMenuLabels.Contains("複製樣式") && noteForm.ContextMenuLabels.Contains("貼上樣式"), "note menu exposes separate note, todo and style actions");
                Assert(noteForm.UsesNoActivateForTest && noteForm.MouseActivateResultForTest() == (IntPtr)3, "fixed notes reject mouse activation and stay in the desktop z-order");
                noteForm.RenderForTest(); Assert(!noteForm.HasAddChecklistButtonForTest, "plain note rendering has no checklist add button");
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH")))
                {
                    noteForm.SetResizeCornerForTest(OverlayResizeCorner.BottomRight);
                    noteForm.StartPosition = FormStartPosition.Manual; noteForm.Location = new Point(-10000, -10000); noteForm.Show(); Application.DoEvents();
                    Assert(noteForm.HasScrollableContent, "small note retains overflow content"); noteForm.ScrollForTest(120); Assert(noteForm.ScrollOffset > 0, "small note scrolls vertically"); noteForm.Hide();
                }
                var inlineQa = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_INLINE_NOTE_PATH");
                if (!string.IsNullOrWhiteSpace(inlineQa))
                {
                    Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH", inlineQa);
                    noteForm.SetInlineDraftForTest(note.Text + "\r\n正在以原樣式編輯");
                    Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_INLINE_NOTE_PATH", null);
                }
            }
            var todo = new DesktopNoteData { Kind = DesktopNoteKind.Todo, X = 80, Y = 80, Width = 420, Height = 240 };
            using (var todoEditor = new NoteEditorDialog(DesktopNoteKind.Todo, todo))
            {
                var descendants = todoEditor.Controls.Cast<Control>().SelectMany(value => new[] { value }.Concat(Descendants(value))).ToArray();
                var addButton = descendants.OfType<Button>().Single(value => value.Text.Contains("新增勾選事項"));
                Assert(addButton.AutoSize && addButton.Width >= addButton.PreferredSize.Width, "todo editor has an unclipped checklist add button");
                Assert(!descendants.OfType<Label>().Any(value => value.Text.Contains("Markdown")), "todo editor does not contain note text input");
            }
            using (var todoForm = new DesktopLayeredNoteForm(todo))
            {
                todoForm.ApplyEditedContent("", [new DesktopChecklistItem { Text = "已完成", Completed = true }, new DesktopChecklistItem { Text = "待完成", Completed = false }]);
                Assert(todo.Items.Count == 2 && !todo.Items[0].Completed && todo.Items[1].Completed, "completed todo item moves to bottom");
                todoForm.RenderForTest(); Assert(todoForm.HasAddChecklistButtonForTest, "todo rendering keeps the checklist add button");
                var todoQa = Environment.GetEnvironmentVariable("DESKTOP_LIST_QA_TODO_PATH");
                if (!string.IsNullOrWhiteSpace(todoQa))
                {
                    Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_NOTE_PATH", todoQa);
                    todoForm.StartPosition = FormStartPosition.Manual; todoForm.Location = new Point(-10000, -10000); todoForm.Show(); Application.DoEvents(); todoForm.Hide();
                    Environment.SetEnvironmentVariable("DESKTOP_LIST_QA_TODO_PATH", null);
                }
            }
            var legacy = new List<DesktopNoteData> { new() { Text = "舊便籤", Items = [new DesktopChecklistItem { Text = "舊代辦" }] } };
            DesktopViewApplicationContext.NormalizeLegacyNotes(legacy);
            Assert(legacy.Count == 2 && legacy.Any(value => value.Kind == DesktopNoteKind.Note && value.Text == "舊便籤" && value.Items.Count == 0) && legacy.Any(value => value.Kind == DesktopNoteKind.Todo && value.Text.Length == 0 && value.Items.Count == 1), "legacy mixed note splits into separate note and todo without data loss");
            var distributedNote = new DesktopNoteData { Kind = DesktopNoteKind.Note, Text = "第一行\r\n第二行", X = 40, Y = 40, Width = 500, Height = 420, Distribution = OverlayContentDistribution.Fill };
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
