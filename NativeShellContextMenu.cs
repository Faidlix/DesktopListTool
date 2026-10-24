// 功能：在獨立輔助程序顯示 Windows Shell 原生右鍵選單，隔離第三方 Shell 外掛的原生崩潰。
// 使用：主視窗呼叫 TryShowIsolated；輔助程序再呼叫 TryShow，失敗不會中止桌面清單主程序。
// 設定：僅接受存在的本機檔案或資料夾；PIDL 陣列使用 LPArray 正確封送。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopListTool;

internal static class NativeShellContextMenu
{
    public static bool TryShowIsolated(IntPtr owner, string path, Point screenPoint)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith("DesktopListTool.exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add("--shell-menu");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(screenPoint.X.ToString());
            start.ArgumentList.Add(screenPoint.Y.ToString());
            start.ArgumentList.Add(owner.ToInt64().ToString());
            Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool TryShow(IntPtr owner, string path, Point screenPoint, bool display = true)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        IntPtr pidl = IntPtr.Zero;
        IShellFolder? parent = null;
        IContextMenu? context = null;
        IntPtr menu = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0)
                return false;
            var iidFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidl, ref iidFolder, out parent, out var childPidl) != 0 || parent is null)
                return false;
            var iidMenu = typeof(IContextMenu).GUID;
            var children = new[] { childPidl };
            if (parent.GetUIObjectOf(owner, 1, children, ref iidMenu, IntPtr.Zero, out var menuObject) != 0 || menuObject == IntPtr.Zero)
                return false;
            context = (IContextMenu)Marshal.GetObjectForIUnknown(menuObject);
            Marshal.Release(menuObject);
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero || context.QueryContextMenu(menu, 0, 1, 0x7FFF, 0x00000004) < 0)
                return false;
            if (!display) return true;
            SetForegroundWindow(owner);
            var command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, screenPoint.X, screenPoint.Y, owner, IntPtr.Zero);
            if (command > 0)
            {
                var invoke = new InvokeCommandInfo
                {
                    Size = Marshal.SizeOf<InvokeCommandInfo>(),
                    Window = owner,
                    Verb = (IntPtr)(command - 1),
                    Show = 1
                };
                context.InvokeCommand(ref invoke);
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return false; }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (context is not null) Marshal.FinalReleaseComObject(context);
            if (parent is not null) Marshal.FinalReleaseComObject(parent);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct InvokeCommandInfo
    {
        public int Size; public uint Mask; public IntPtr Window; public IntPtr Verb; public string? Parameters;
        public string? Directory; public int Show; public uint HotKey; public IntPtr Icon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out IntPtr childPidl);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
