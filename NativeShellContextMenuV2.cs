// 功能：在隔離輔助程序中顯示檔案總管原生右鍵選單，並轉送 Shell 延伸選單訊息避免空白或閃退。
// 使用：主視窗呼叫 TryShowIsolated，可在原生選單程序結束時刷新清單；輔助程序再呼叫 TryShow。
// 設定：僅接受存在的本機檔案或資料夾；主程序不直接載入第三方 Shell 擴充套件。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopListTool;

internal static class NativeShellContextMenu
{
    public static bool TryShowIsolated(IntPtr owner, string path, Point screenPoint, Action? completed = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith("DesktopListTool.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--shell-menu"); start.ArgumentList.Add(path); start.ArgumentList.Add(screenPoint.X.ToString()); start.ArgumentList.Add(screenPoint.Y.ToString());
            var process = Process.Start(start);
            if (process is null) return false;
            if (completed is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await process.WaitForExitAsync(); completed(); }
                    finally { process.Dispose(); }
                });
            }
            else process.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool TryShow(IntPtr ignoredOwner, string path, Point screenPoint, bool display = true)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        IntPtr pidl = IntPtr.Zero, menu = IntPtr.Zero;
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
            if (!display) return true;
            SetForegroundWindow(owner.Handle);
            var command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, screenPoint.X, screenPoint.Y, owner.Handle, IntPtr.Zero);
            if (command > 0)
            {
                var invoke = new InvokeCommandInfo { Size = Marshal.SizeOf<InvokeCommandInfo>(), Window = owner.Handle, Verb = (IntPtr)(command - 1), Show = 1 };
                context.InvokeCommand(ref invoke);
            }
            PostMessage(owner.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or AccessViolationException) { return false; }
        finally
        {
            owner.Context = null;
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (context is not null && Marshal.IsComObject(context)) Marshal.FinalReleaseComObject(context);
            if (parent is not null && Marshal.IsComObject(parent)) Marshal.FinalReleaseComObject(parent);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
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
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
