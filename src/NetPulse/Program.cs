using NetPulse.Platform;

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

        using var instance = AcquireSingleInstance(args.Contains(RestartedArgument));
        if (instance is null)
        {
            SignalRunningInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(CreateUiFont());
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception, "Unhandled");

        var open = args.FirstOrDefault(a => a.StartsWith(OpenArgumentPrefix, StringComparison.Ordinal))?[OpenArgumentPrefix.Length..];
        using (var context = new NetPulseContext(Enum.TryParse<MainTab>(open, ignoreCase: true, out var tab) ? tab : null))
        {
            Application.Run(context);
        }

        // Hand the mutex over cleanly, e.g. to the elevated copy waiting in AcquireSingleInstance.
        instance.ReleaseMutex();
        return 0;
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

    /// <summary>Asks the running instance to show its window instead of starting a second copy.</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Elevated instance: a normal process cannot signal it. Nothing else to do.
        }
    }

    /// <summary>
    /// The UI text is Chinese; Segoe UI lacks CJK glyphs, and per-character fallback mixes weights.
    /// </summary>
    private static Font CreateUiFont()
    {
        using var probe = new Font("Microsoft YaHei UI", 9f);
        return probe.Name == "Microsoft YaHei UI"
            ? new Font("Microsoft YaHei UI", 9f)
            : (Font)(SystemFonts.MessageBoxFont ?? Control.DefaultFont).Clone();
    }
}
