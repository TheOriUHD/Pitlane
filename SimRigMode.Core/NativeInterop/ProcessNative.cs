using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SimRigMode.NativeInterop;

/// <summary>
/// Reading a running program's full path, and resolving Start menu shortcuts. Declarations and
/// thin wrappers only; nothing here starts or ends a process.
/// </summary>
internal static class ProcessNative
{
    /// <summary>Enough to read the image path of almost any process, including elevated ones,
    /// without the rights MainModule needs.</summary>
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>The full path of a running process, or null when it has exited or is protected.</summary>
    internal static string? TryGetImagePath(int processId)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(handle); }
    }

    // ---- shortcuts ------------------------------------------------------------------------

    private const uint SLGP_RAWPATH = 0x4;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    /// <summary>The program and arguments a .lnk points at, or null when it cannot be read.
    /// Never calls Resolve, which can search the disk or show UI for a broken shortcut.</summary>
    internal static (string Target, string Arguments)? TryReadShortcut(string shortcutPath)
    {
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((IPersistFile)link).Load(shortcutPath, 0);
            var shell = (IShellLinkW)link;
            var target = new StringBuilder(1024);
            shell.GetPath(target, target.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var arguments = new StringBuilder(2048);
            shell.GetArguments(arguments, arguments.Capacity);
            return (Environment.ExpandEnvironmentVariables(target.ToString()), arguments.ToString());
        }
        catch { return null; }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); }
    }
}
