// 功能：在隔離輔助程序中顯示檔案總管原生右鍵選單，並維持「內容」等 Shell 視窗的訊息迴圈。
// 使用：主視窗呼叫 TryShowIsolated，可在原生選單程序結束時刷新清單；輔助程序再呼叫 TryShow。
// 設定：命令結束必定釋放滑鼠擷取；需要視窗生命週期的命令會等待其視窗關閉，主程序不載入第三方 Shell 擴充套件。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopListTool;

internal static class NativeShellContextMenu
{
    private static readonly object ProcessGate = new();
    private static readonly HashSet<string> UnsafeShellKeys = new(StringComparer.OrdinalIgnoreCase);
    private static Process? _activeMenuProcess;
    private static string? _activeMenuStatusPath;

    static NativeShellContextMenu() => Application.ApplicationExit += (_, _) => StopActiveMenuProcess();

    internal static bool ProbeIsolated(string path)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(path)) return false;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--shell-menu-probe"); start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool TryShowIsolated(IntPtr owner, string path, Point screenPoint, Action<bool>? completed = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        var shellKey = GetShellIsolationKey(path);
        lock (ProcessGate)
        {
            if (UnsafeShellKeys.Contains(shellKey))
            {
                _ = Task.Run(() => { try { completed?.Invoke(true); } catch (Exception) { } });
                return true;
            }
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith("DesktopListTool.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            StopActiveMenuProcess();
            var statusPath = Path.Combine(Path.GetTempPath(), $"DesktopListTool-shell-{Guid.NewGuid():N}.status");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--shell-menu"); start.ArgumentList.Add(path); start.ArgumentList.Add(screenPoint.X.ToString()); start.ArgumentList.Add(screenPoint.Y.ToString());
            start.ArgumentList.Add(statusPath);
            var process = Process.Start(start);
            if (process is null) return false;
            lock (ProcessGate) { _activeMenuProcess = process; _activeMenuStatusPath = statusPath; }
            _ = Task.Run(async () =>
            {
                var timedOut = false;
                var exitCode = -1;
                try
                {
                    var exitTask = process.WaitForExitAsync();
                    if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromMinutes(10))) != exitTask)
                    {
                        timedOut = true;
                        try { process.Kill(entireProcessTree: true); }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                        await process.WaitForExitAsync();
                    }
                    exitCode = process.ExitCode;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
                finally
                {
                    lock (ProcessGate) if (ReferenceEquals(_activeMenuProcess, process)) { _activeMenuProcess = null; _activeMenuStatusPath = null; }
                    var reachedMenu = File.Exists(statusPath);
                    var fallbackRequired = RequiresSafeFallback(timedOut, exitCode, reachedMenu);
                    if (fallbackRequired) lock (ProcessGate) UnsafeShellKeys.Add(shellKey);
                    try { completed?.Invoke(fallbackRequired); } catch (Exception) { }
                    try { File.Delete(statusPath); } catch (Exception) { }
                    process.Dispose();
                }
            });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static bool RequiresSafeFallback(bool timedOut, int exitCode, bool reachedMenu) =>
        !timedOut && exitCode != 0 && !reachedMenu;

    internal static string GetShellIsolationKey(string path) =>
        Directory.Exists(path) ? "<folder>" : Path.GetExtension(path).ToLowerInvariant();

    public static bool TryShow(IntPtr ignoredOwner, string path, Point screenPoint, bool display = true, string? statusPath = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        IntPtr pidl = IntPtr.Zero, menu = IntPtr.Zero;
        IntPtr previousForeground = IntPtr.Zero;
        var menuTrackingStarted = false;
        IShellFolder? parent = null; IContextMenu? context = null;
        using var owner = new ShellMenuWindow();
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0) return false;
            var iidFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidl, ref iidFolder, out parent, out var childPidl) != 0 || parent is null) return false;
            var iidMenu = typeof(IContextMenu).GUID; var children = new[] { childPidl };
            if (parent.GetUIObjectOf(owner.Handle, 1, children, ref iidMenu, IntPtr.Zero, out var menuObject) != 0 || menuObject == IntPtr.Zero) return false;
            context = (IContextMenu)Marshal.GetObjectForIUnknown(menuObject); Marshal.Release(menuObject);
            owner.Context = context;
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero || context.QueryContextMenu(menu, 0, 1, 0x7FFF, 0x00000004) < 0) return false;
            WriteStatus(statusPath, "menu-ready");
            if (!display) return true;
            menuTrackingStarted = true;
            previousForeground = GetForegroundWindow();
            SetForegroundWindow(owner.Handle);
            var command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, screenPoint.X, screenPoint.Y, owner.Handle, IntPtr.Zero);
            WriteStatus(statusPath, command > 0 ? "command-selected" : "cancelled");
            if (menuTrackingStarted)
            {
                ReleaseCapture();
                PostMessage(owner.Handle, 0x001F, IntPtr.Zero, IntPtr.Zero);
            }
            if (command > 0)
            {
                var canonicalVerb = GetCanonicalVerb(context, command - 1);
                var propertiesCommand = IsPropertiesCommand(canonicalVerb, GetMenuCommandText(menu, command));
                var invoke = new InvokeCommandInfo { Size = Marshal.SizeOf<InvokeCommandInfo>(), Window = owner.Handle, Verb = (IntPtr)(command - 1), Show = 1 };
                var invoked = propertiesCommand
                    ? TryShowPropertiesIsolated(path)
                    : context.InvokeCommand(ref invoke) >= 0;
                if (!invoked) return false;
                WriteStatus(statusPath, "command-invoked");
                if (!propertiesCommand) PumpShellCommandWindows(owner.Handle, canonicalVerb);
            }
            PostMessage(owner.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or AccessViolationException or InvalidOperationException or ExternalException) { return false; }
        finally
        {
            if (menuTrackingStarted)
            {
                ReleaseCapture();
                PostMessage(owner.Handle, 0x001F, IntPtr.Zero, IntPtr.Zero);
            }
            if (previousForeground != IntPtr.Zero && IsWindow(previousForeground)) SetForegroundWindow(previousForeground);
            owner.Context = null;
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            // 這是一次性隔離程序；讓 COM apartment 隨程序一起結束，避免有缺陷的 Shell 擴充在手動釋放時造成 fail-fast。
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static void WriteStatus(string? path, string status)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.WriteAllText(path, status); } catch (Exception) { }
    }

    public static bool TryShowPropertiesIsolated(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--shell-properties"); start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool ShowProperties(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        if (!SHObjectProperties(IntPtr.Zero, 2, path, null)) return false;
        PumpShellCommandWindows(IntPtr.Zero, "properties");
        return true;
    }

    private static void StopActiveMenuProcess()
    {
        Process? process;
        string? statusPath;
        lock (ProcessGate) { process = _activeMenuProcess; statusPath = _activeMenuStatusPath; }
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                WriteStatus(statusPath, "superseded");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
    }

    private static string? GetCanonicalVerb(IContextMenu context, uint commandOffset)
    {
        const uint GcsVerbW = 4;
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return context.GetCommandString((UIntPtr)commandOffset, GcsVerbW, IntPtr.Zero, buffer, 256) >= 0
                ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool RequiresExtendedShellLifetime(string? canonicalVerb) =>
        string.Equals(canonicalVerb, "properties", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPropertiesCommand(string? canonicalVerb, string? menuText)
    {
        if (RequiresExtendedShellLifetime(canonicalVerb)) return true;
        if (string.IsNullOrWhiteSpace(menuText)) return false;
        return menuText.Contains("內容", StringComparison.OrdinalIgnoreCase) ||
            menuText.Contains("屬性", StringComparison.OrdinalIgnoreCase) ||
            menuText.Contains("Properties", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetMenuCommandText(IntPtr menu, uint command)
    {
        var buffer = new System.Text.StringBuilder(260);
        return GetMenuString(menu, command, buffer, buffer.Capacity, 0) > 0 ? buffer.ToString() : null;
    }

    private static void PumpShellCommandWindows(IntPtr ownerHandle, string? canonicalVerb)
    {
        var discoveryLimit = RequiresExtendedShellLifetime(canonicalVerb) ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);
        var lifetimeLimit = RequiresExtendedShellLifetime(canonicalVerb) ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(15);
        var stopwatch = Stopwatch.StartNew();
        var sawWindow = false;
        while (stopwatch.Elapsed < lifetimeLimit)
        {
            Application.DoEvents();
            var hasWindow = HasVisibleHelperWindow(ownerHandle);
            sawWindow |= hasWindow;
            if (sawWindow && !hasWindow) break;
            if (!sawWindow && stopwatch.Elapsed >= discoveryLimit) break;
            Thread.Sleep(15);
        }
    }

    private static bool HasVisibleHelperWindow(IntPtr ownerHandle)
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == (uint)Environment.ProcessId && window != ownerHandle && IsWindowVisible(window))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private sealed class ShellMenuWindow : NativeWindow, IDisposable
    {
        public IContextMenu? Context { get; set; }
        public ShellMenuWindow() => CreateHandle(new CreateParams { Caption = "DesktopListTool.ShellMenuOwner", ClassName = null, Style = unchecked((int)0x80000000) });
        protected override void WndProc(ref Message m)
        {
            const int WmInitMenuPopup = 0x0117, WmDrawItem = 0x002B, WmMeasureItem = 0x002C, WmMenuChar = 0x0120;
            if (m.Msg is WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar)
            {
                try
                {
                    if (Context is IContextMenu3 menu3 && menu3.HandleMenuMsg2((uint)m.Msg, m.WParam, m.LParam, out var result) >= 0) { m.Result = result; return; }
                    if (Context is IContextMenu2 menu2 && menu2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam) >= 0) { m.Result = IntPtr.Zero; return; }
                }
                catch (COMException) { }
            }
            base.WndProc(ref m);
        }
        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bind, string name, ref uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bind, ref Guid iid, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bind, ref Guid iid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls, ref Guid iid, IntPtr reserved, out IntPtr result);
    }
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeCommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint count);
    }
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeCommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint count);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }
    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeCommandInfo info);
        [PreserveSig] int GetCommandString(UIntPtr id, uint type, IntPtr reserved, IntPtr name, uint count);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct InvokeCommandInfo { public int Size; public uint Mask; public IntPtr Window; public IntPtr Verb; public string? Parameters; public string? Directory; public int Show; public uint HotKey; public IntPtr Icon; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out IntPtr childPidl);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool SHObjectProperties(IntPtr owner, uint type, string objectName, string? propertyPage);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr menu, uint item, System.Text.StringBuilder text, int count, uint flags);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
}
