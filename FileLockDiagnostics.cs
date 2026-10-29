// 功能：使用 Windows Restart Manager 找出占用檔案的程序，並產生不會強制終止程序的資源回收筒錯誤訊息。
// 使用：刪除至資源回收筒失敗時呼叫 CreateRecycleBinException；不需管理員權限或額外設定。

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopListTool;

internal static class FileLockDiagnostics
{
    private const int ErrorMoreData = 234;

    public static IOException CreateRecycleBinException(string path, int shellError)
    {
        var lockingProcesses = FindLockingProcesses(path);
        if (lockingProcesses.Count > 0)
            return new IOException($"無法將「{Path.GetFileName(path)}」移至資源回收筒，檔案正由 {string.Join("、", lockingProcesses)} 使用。請先關閉該程式後再試一次。");

        var detail = new Win32Exception(shellError).Message;
        return new IOException($"無法將「{Path.GetFileName(path)}」移至資源回收筒（錯誤 0x{shellError:X}：{detail}）。");
    }

    internal static IReadOnlyList<string> FindLockingProcesses(string path)
    {
        var sessionKey = new StringBuilder(32);
        var startResult = RmStartSession(out var sessionHandle, 0, sessionKey);
        if (startResult != 0) return [];
        try
        {
            var registerResult = RmRegisterResources(sessionHandle, 1, [Path.GetFullPath(path)], 0, null, 0, null);
            if (registerResult != 0) return [];

            uint needed = 0;
            uint count = 0;
            uint rebootReasons = 0;
            var result = RmGetList(sessionHandle, out needed, ref count, null, ref rebootReasons);
            if ((result != ErrorMoreData && result != 0) || needed == 0) return [];

            var processInfo = new RestartManagerProcessInfo[needed];
            count = needed;
            result = RmGetList(sessionHandle, out needed, ref count, processInfo, ref rebootReasons);
            if (result != 0) return [];

            return processInfo.Take((int)count)
                .Select(DescribeProcess)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
        finally { RmEndSession(sessionHandle); }
    }

    private static string DescribeProcess(RestartManagerProcessInfo info)
    {
        var name = info.ApplicationName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = Process.GetProcessById(info.Process.ProcessId).ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        return string.IsNullOrWhiteSpace(name) ? $"PID {info.Process.ProcessId}" : $"{name} (PID {info.Process.ProcessId})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RestartManagerUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestartManagerProcessInfo
    {
        public RestartManagerUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public uint ApplicationType;
        public uint ApplicationStatus;
        public uint TerminalSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] fileNames,
        uint applicationCount, [In] RestartManagerUniqueProcess[]? applications,
        uint serviceCount, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint processInfoNeeded, ref uint processInfoCount,
        [In, Out] RestartManagerProcessInfo[]? affectedApplications, ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);
}
