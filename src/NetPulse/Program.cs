using NetPulse.Core;
using NetPulse.Platform;
using System.Security.AccessControl;

namespace NetPulse;

internal static class Program
{
    internal static readonly string ShowWindowEventName = $@"Local\NetPulse{AppPaths.ProfileSuffix}.ShowMainWindow";

    /// <summary>Passed to the elevated copy so it waits for this instance to exit before starting.</summary>
    internal const string RestartedArgument = "--restarted";

    /// <summary>"--open=processes" etc.: tab to show right after start.</summary>
    internal const string OpenArgumentPrefix = "--open=";

    private static readonly string InstanceMutexName = $@"Local\NetPulse{AppPaths.ProfileSuffix}.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper started by ElevatedAutostart: does its one job and exits, no UI, no instance lock.
        if (args.FirstOrDefault(a => a is ElevatedAutostart.InstallArgument or ElevatedAutostart.RemoveArgument) is { } helper)
        {
            return ElevatedAutostart.RunHelper(helper);
        }

        if (args.Contains(InstallLayout.UninstallArgument))
        {
            return RunUninstaller(args);
        }

        using var instance = AcquireSingleInstance(args.Contains(RestartedArgument));
        if (instance is null)
        {
            SignalRunningInstance();
            return 0;
        }

        var settings = InitializeUi();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception, "Unhandled");

        var open = args.FirstOrDefault(a => a.StartsWith(OpenArgumentPrefix, StringComparison.Ordinal))?[OpenArgumentPrefix.Length..];
        using (var context = new NetPulseContext(settings, Enum.TryParse<MainTab>(open, ignoreCase: true, out var tab) ? tab : null))
        {
            Application.Run(context);
        }

        // Hand the mutex over cleanly, e.g. to the elevated copy waiting in AcquireSingleInstance.
        instance.ReleaseMutex();
        return 0;
    }

    /// <summary>Language, theme and font from the settings; returns the loaded settings.</summary>
    private static AppSettings InitializeUi()
    {
        ApplicationConfiguration.Initialize();
        var isFirstRun = !File.Exists(NetPulseContext.SettingsStore.Path);
        var settings = NetPulseContext.SettingsStore.Load().Normalize();
        // Settings from before v0.3 have no language: those users have been using the Chinese UI.
        settings.Language ??= isFirstRun ? UiLanguage.Auto : UiLanguage.Chinese;
        Lang.Use(settings.Language.Value);
        Application.SetColorMode(settings.WindowTheme switch
        {
            ThemeMode.Dark => SystemColorMode.Dark,
            ThemeMode.Light => SystemColorMode.Classic,
            _ => SystemColorMode.System,
        });
        Application.SetDefaultFont(CreateUiFont());
        return settings;
    }

    /// <summary>
    /// Takes the instance lock first, so NetPulse cannot run while its files are removed. From the settings
    /// page (--restarted) the running copy is exiting, so its lock is waited for.
    /// </summary>
    private static int RunUninstaller(string[] args)
    {
        InitializeUi();
        using var instance = AcquireSingleInstance(args.Contains(RestartedArgument));
        if (instance is null)
        {
            Uninstaller.ShowStillRunning();
            return 1;
        }

        try
        {
            return Uninstaller.Run(args.Contains(Uninstaller.QuietArgument));
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    /// <summary>Returns the owned mutex, or null if another instance is running.</summary>
    private static Mutex? AcquireSingleInstance(bool waitForPrevious)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
            if (isFirstInstance)
            {
                return mutex;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The running instance is elevated; a normal process may not even open its mutex.
            return null;
        }

        if (waitForPrevious)
        {
            try
            {
                if (mutex.WaitOne(TimeSpan.FromSeconds(15)))
                {
                    return mutex;
                }
            }
            catch (AbandonedMutexException)
            {
                return mutex; // the previous instance exited without releasing it; we own it now
            }
        }

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Creates the event a second launch signals. When this copy is elevated, the default ACL only admits
    /// elevated processes, so the current user is granted access explicitly: otherwise a normal-token
    /// launch could not wake the elevated instance.
    /// </summary>
    internal static EventWaitHandle CreateShowWindowSignal()
    {
        var security = new EventWaitHandleSecurity();
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        if (user is not null)
        {
            security.AddAccessRule(new EventWaitHandleAccessRule(
                user,
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                System.Security.AccessControl.AccessControlType.Allow));
        }

        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowWindowEventName, out _, security);
    }

    /// <summary>Asks the running instance to show its window instead of starting a second copy.</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandleAcl.TryOpenExisting(ShowWindowEventName, EventWaitHandleRights.Modify, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Instance started by an older version: its event has the default ACL. Nothing else to do.
        }
    }

    /// <summary>
    /// Chinese UI: Segoe UI lacks CJK glyphs, and per-character fallback mixes weights. English uses the system font.
    /// </summary>
    private static Font CreateUiFont()
    {
        if (Lang.IsEnglish)
        {
            return (Font)(SystemFonts.MessageBoxFont ?? Control.DefaultFont).Clone();
        }

        using var probe = new Font("Microsoft YaHei UI", 9f);
        return probe.Name == "Microsoft YaHei UI"
            ? new Font("Microsoft YaHei UI", 9f)
            : (Font)(SystemFonts.MessageBoxFont ?? Control.DefaultFont).Clone();
    }
}
