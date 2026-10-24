using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SFHTrainer.Injection;

/// <summary>目标进程信息。</summary>
public sealed record TargetProcess(int Pid, string ExeName, string? WindowTitle);

/// <summary>注入结果（含具体 Win32 错误码）。</summary>
public sealed record InjectResult(
    bool Success,
    string Stage,
    int Win32Error,
    nint LoadLibraryResult,
    string Message);

/// <summary>
/// 进程查找 + 经典 LoadLibrary 注入：
/// OpenProcess → VirtualAllocEx → WriteProcessMemory（UTF-16 DLL 路径）→ CreateRemoteThread(LoadLibraryW)。
/// </summary>
public static class ProcessInjector
{
    /// <summary>目标进程名（不带 .exe，Process.ProcessName 的形态）。</summary>
    public const string TargetProcessName = "Strike Force Heroes";

    const uint ProcessAllAccess = 0x001F0FFF;
    const uint ProcessInjectRights = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400; // CREATE_THREAD|VM_OPERATION|VM_WRITE|VM_READ|QUERY_INFORMATION
    const uint MemCommit = 0x00001000;
    const uint MemReserve = 0x00002000;
    const uint MemRelease = 0x00008000;
    const uint PageReadWrite = 0x04;
    const uint WaitTimeout = 0x00000102;

    /// <summary>按进程名找目标进程；未启动返回 null。</summary>
    public static TargetProcess? FindTarget()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return null; }

        foreach (var p in all)
        {
            try
            {
                if (!p.ProcessName.Equals(TargetProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? title = null;
                try { title = p.MainWindowTitle; } catch { }
                var pid = p.Id;
                var name = p.ProcessName + ".exe";
                return new TargetProcess(pid, name, title);
            }
            catch
            {
                // 某些系统进程会拒绝访问，跳过即可。
            }
            finally
            {
                p.Dispose();
            }
        }

        return null;
    }

    /// <summary>把 dllPath 注入到 pid。失败时返回带 Win32 错误码的结果，不抛异常。</summary>
    public static InjectResult Inject(int pid, string dllPath)
    {
        if (!File.Exists(dllPath))
            return Fail("check-dll", 2, $"找不到代理 DLL：{dllPath}");

        if (!Environment.Is64BitProcess)
            return Fail("check-host", 0, "本程序不是 64 位进程，无法向 64 位游戏注入。请使用 x64 版本的 SFHTrainer.exe。");

        nint hProcess = Native.OpenProcess(ProcessAllAccess, false, pid);
        if (hProcess == 0)
        {
            var allAccessError = Marshal.GetLastWin32Error();
            // 权限更窄的一组权限偶尔能过；成功则继续，失败则报原始错误码。
            hProcess = Native.OpenProcess(ProcessInjectRights, false, pid);
            if (hProcess == 0)
            {
                var error = Marshal.GetLastWin32Error();
                var hint = error == 5
                    ? "拒绝访问（错误 5）。请以管理员身份运行本程序；若游戏由 Steam 以管理员启动，本程序也必须提升权限。"
                    : $"OpenProcess 失败，Win32 错误 {error}：{new Win32Exception(error).Message}";
                return Fail("OpenProcess", allAccessError != 0 ? allAccessError : error, hint);
            }
        }

        try
        {
            // 位数检查：32 位目标无法被 64 位宿主用同一份 kernel32 地址注入。
            if (Native.IsWow64Process(hProcess, out var isWow64) && isWow64)
                return Fail("check-arch", 0, "目标是 32 位进程，本程序为 64 位，无法注入。");

            var dllBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");

            var remoteBuffer = Native.VirtualAllocEx(
                hProcess, 0, (nuint)dllBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remoteBuffer == 0)
            {
                var error = Marshal.GetLastWin32Error();
                return Fail("VirtualAllocEx", error,
                    $"VirtualAllocEx 失败，Win32 错误 {error}：{new Win32Exception(error).Message}");
            }

            try
            {
                if (!Native.WriteProcessMemory(hProcess, remoteBuffer, dllBytes, (nuint)dllBytes.Length, out var written)
                    || (long)written != dllBytes.Length)
                {
                    var error = Marshal.GetLastWin32Error();
                    return Fail("WriteProcessMemory", error,
                        $"WriteProcessMemory 失败，Win32 错误 {error}：{new Win32Exception(error).Message}");
                }

                var kernel32 = Native.GetModuleHandleW("kernel32.dll");
                if (kernel32 == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    return Fail("GetModuleHandle", error, $"GetModuleHandle(\"kernel32.dll\") 失败，Win32 错误 {error}。");
                }

                var loadLibrary = Native.GetProcAddress(kernel32, "LoadLibraryW");
                if (loadLibrary == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    return Fail("GetProcAddress", error, $"找不到 LoadLibraryW，Win32 错误 {error}。");
                }

                var hThread = Native.CreateRemoteThread(hProcess, 0, 0, loadLibrary, remoteBuffer, 0, out _);
                if (hThread == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    return Fail("CreateRemoteThread", error,
                        $"CreateRemoteThread 失败，Win32 错误 {error}：{new Win32Exception(error).Message}");
                }

                try
                {
                    var wait = Native.WaitForSingleObject(hThread, 15_000);
                    if (wait == WaitTimeout)
                        return Fail("WaitForSingleObject", 0,
                            "远程线程 15 秒内没有返回：DLL 可能正在初始化或已卡住，请检查游戏是否响应。");

                    if (!Native.GetExitCodeThread(hThread, out var exitCode))
                    {
                        var error = Marshal.GetLastWin32Error();
                        return Fail("GetExitCodeThread", error,
                            $"GetExitCodeThread 失败，Win32 错误 {error}。");
                    }

                    if (exitCode == 0)
                        return Fail("LoadLibraryW", 0,
                            "LoadLibraryW 返回 0：DLL 加载失败。常见原因：DLL 位数不匹配（需要 x64）、"
                            + "缺少依赖 DLL，或 sfh_agent.dll 路径不可读。");

                    return new InjectResult(true, "LoadLibraryW", 0, (nint)(uint)exitCode,
                        $"注入成功：已把 {Path.GetFileName(dllPath)} 载入游戏进程（HMODULE 0x{(uint)exitCode:X}）。");
                }
                finally
                {
                    Native.CloseHandle(hThread);
                }
            }
            finally
            {
                Native.VirtualFreeEx(hProcess, remoteBuffer, 0, MemRelease);
            }
        }
        finally
        {
            Native.CloseHandle(hProcess);
        }
    }

    static InjectResult Fail(string stage, int error, string message) => new(false, stage, error, 0, message);

    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint CreateRemoteThread(
            nint process, nint threadAttributes, nuint stackSize,
            nint startAddress, nint parameter, uint creationFlags, out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeThread(nint thread, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        internal static extern nint GetProcAddress(nint module, string procedureName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWow64Process(nint process, [MarshalAs(UnmanagedType.Bool)] out bool isWow64);
    }
}
