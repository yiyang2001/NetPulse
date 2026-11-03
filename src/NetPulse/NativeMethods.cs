using System.Runtime.InteropServices;

namespace NetPulse;

internal static partial class NativeMethods
{
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int HTCAPTION = 2;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUNDSMALL = 3;

    // QUERY_USER_NOTIFICATION_STATE values that mean a full-screen app or presentation is in front.
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Full path of a running process's exe, or null (exited, or not even limited query rights).</summary>
    public static string? ProcessImagePath(int processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[32_768];
            var size = buffer.Length;
            unsafe
            {
                fixed (char* name = buffer)
                {
                    return QueryFullProcessImageName(process, 0, name, ref size) ? new string(buffer, 0, size) : null;
                }
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(nint hWnd, string? subAppName, string? subIdList);

    /// <summary>
    /// WinForms dark mode leaves combo boxes and spin boxes light; the "DarkMode_CFD" visual style
    /// (the one Explorer's own dropdowns use) draws them dark.
    /// </summary>
    public static void UseDarkControlTheme(nint hWnd) => _ = SetWindowTheme(hWnd, "DarkMode_CFD", null);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong32(nint hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr64(nint hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong32(nint hWnd, int index, int value);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr64(nint hWnd, int index, nint value);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    public static int GetExStyle(nint hWnd) =>
        nint.Size == 8 ? (int)GetWindowLongPtr64(hWnd, GWL_EXSTYLE) : GetWindowLong32(hWnd, GWL_EXSTYLE);

    public static void SetExStyle(nint hWnd, int style)
    {
        if (nint.Size == 8)
        {
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, style);
        }
        else
        {
            SetWindowLong32(hWnd, GWL_EXSTYLE, style);
        }
    }

    /// <summary>Asks Windows 11 for small rounded corners; harmless no-op on Windows 10.</summary>
    public static void TryUseRoundedCorners(nint hWnd)
    {
        var preference = DWMWCP_ROUNDSMALL;
        _ = DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    public static bool IsFullscreenAppActive() =>
        SHQueryUserNotificationState(out var state) == 0
        && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
}
