using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace NetPulse.Platform;

internal static class Elevation
{
    private const int ERROR_CANCELLED = 1223;

    public static bool IsAdministrator { get; } = CheckAdministrator();

    /// <summary>
    /// Starts an elevated copy (UAC prompt) that waits for this one to exit. Returns false if the user
    /// declined the prompt; the caller should exit only on true.
    /// </summary>
    public static bool TryRestartElevated(MainTab openTab)
    {
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{Program.RestartedArgument} {Program.OpenArgumentPrefix}{openTab}",
                UseShellExecute = true,
                Verb = "runas",
            });
            return process is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return false;
        }
    }

    private static bool CheckAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
