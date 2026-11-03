using NetPulse.Core;

namespace NetPulse;

/// <summary>
/// Runs speed tests and keeps their history. Shared by the Speed test tab and the optional schedule, so
/// the two never run at once and both add to the same history. Used from the UI thread only.
/// </summary>
internal sealed class SpeedTestService
{
    private readonly JsonFileStore<SpeedTestHistory> _store = new(AppPaths.SpeedTestFile, () => new SpeedTestHistory());

    public SpeedTestService() => History = _store.Load();

    public SpeedTestHistory History { get; }

    public bool IsBusy { get; private set; }

    /// <summary>A result was added to <see cref="History"/>.</summary>
    public event EventHandler? ResultAdded;

    /// <summary>Runs one test and records it. Throws <see cref="InvalidOperationException"/> if one is already running.</summary>
    public async Task<SpeedTestResult> RunAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("A speed test is already running.");
        }

        IsBusy = true;
        try
        {
            using var runner = new SpeedTestRunner();
            var result = await runner.RunAsync(progress, cancellationToken);
            History.Add(result);
            Save();
            ResultAdded?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Save()
    {
        try
        {
            _store.Save(History);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex, "SaveSpeedTestHistory");
        }
    }
}
