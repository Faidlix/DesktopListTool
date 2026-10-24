// 功能：在清單檔案項目上顯示 Windows Shell 原生右鍵選單並執行所選命令。
// 使用：DesktopLayeredOverlayForm 在項目右鍵時呼叫 TryShow；失敗時回退工具選單。
// 設定：僅接受存在的本機檔案或資料夾，不處理虛擬 Shell 項目。

using System.Runtime.InteropServices;

namespace DesktopListTool;

internal static class NativeShellContextMenu
{
    public static bool TryShow(IntPtr owner, string path, Point screenPoint)
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
        [PreserveSig] int GetAttributesOf(uint count, IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count, IntPtr[] pidls, ref Guid iid, IntPtr reserved, out IntPtr result);
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
