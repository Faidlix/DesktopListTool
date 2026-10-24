// 功能：解析資料夾捷徑，並在獨立 DesktopListTool 程序中交由 Windows Shell 把檔案加入壓縮檔。
// 使用：清單拖曳到資料夾捷徑或 ZIP/RAR/7Z 中央；主程式以 --shell-drop 啟動隔離程序。
// 設定：Ctrl 拖曳保留來源；Shell 壓縮處理器若不支援該格式，隔離程序會回傳失敗而不影響桌面清單。

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace DesktopListTool;

internal static class NativeShellDropTarget
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2" };

    public static bool IsArchivePath(string path) => File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path));

    public static bool TryResolveFolderShortcut(string path, out string folder)
    {
        folder = "";
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        object? shell = null, shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell"); if (type is null) return false;
            shell = Activator.CreateInstance(type); if (shell is null) return false;
            shortcut = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
            var target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)) return false;
            folder = Path.GetFullPath(target); return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or System.Reflection.TargetInvocationException) { return false; }
        finally { ReleaseCom(shortcut); ReleaseCom(shell); }
    }

    public static bool TryDropIsolated(string target, IReadOnlyList<string> paths, bool copy)
    {
        try
        {
            var executable = Environment.ProcessPath; if (string.IsNullOrWhiteSpace(executable)) return false;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--shell-drop"); start.ArgumentList.Add(target); start.ArgumentList.Add(copy ? "copy" : "move");
            foreach (var path in paths) start.ArgumentList.Add(path);
            return Process.Start(start) is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public static bool TryDrop(string target, IReadOnlyList<string> paths, bool copy)
    {
        if (Path.GetExtension(target).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return TryAddToZip(target, paths);
        object? shell = null, folder = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application"); if (type is null) return false;
            shell = Activator.CreateInstance(type); if (shell is null) return false;
            folder = shell.GetType().InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, [target]);
            if (folder is null) return false;
            const int noConfirmation = 0x10; const int noErrorUi = 0x400; const int renameOnCollision = 0x8;
            foreach (var path in paths)
                folder.GetType().InvokeMember("CopyHere", System.Reflection.BindingFlags.InvokeMethod, null, folder, [path, noConfirmation | noErrorUi | renameOnCollision]);
            Thread.Sleep(4000);
            if (!copy)
            {
                // Shell 壓縮命名空間的 CopyHere 為非同步；保留來源可避免尚未完成時誤刪資料。
                // 「移動」提示只適用資料夾目標，壓縮檔一律安全地加入副本。
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or System.Reflection.TargetInvocationException) { return false; }
        finally { ReleaseCom(folder); ReleaseCom(shell); }
    }

    private static bool TryAddToZip(string target, IReadOnlyList<string> paths)
    {
        try
        {
            using var archive = ZipFile.Open(target, ZipArchiveMode.Update);
            foreach (var path in paths)
            {
                if (File.Exists(path)) AddFile(archive, path, Path.GetFileName(path));
                else if (Directory.Exists(path))
                {
                    var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        AddFile(archive, file, Path.Combine(rootName, Path.GetRelativePath(path, file)).Replace('\\', '/'));
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    private static void AddFile(ZipArchive archive, string source, string requestedName)
    {
        var name = requestedName; var stem = Path.GetFileNameWithoutExtension(requestedName); var extension = Path.GetExtension(requestedName); var index = 2;
        while (archive.GetEntry(name) is not null) name = $"{stem} ({index++}){extension}";
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var input = File.OpenRead(source); using var output = entry.Open(); input.CopyTo(output);
    }

    private static void ReleaseCom(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
