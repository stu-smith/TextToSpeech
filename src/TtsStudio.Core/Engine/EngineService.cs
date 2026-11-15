using CommunityToolkit.Mvvm.ComponentModel;

namespace TtsStudio.Core.Engine;

public enum EngineState { Stopped, Installing, Starting, Ready, Failed }

/// <summary>
/// Owns the engine for a process: installs the runtime if needed, starts the worker on first use
/// and restarts it if it dies. Observable so the UI can show the state and log.
/// </summary>
public sealed partial class EngineService(RuntimeManager runtime) : ObservableObject, IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private EngineClient? _client;
    private readonly List<string> _log = [];

    public RuntimeManager Runtime { get; } = runtime;

    [ObservableProperty] public partial EngineState State { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "Engine not started";

    /// <summary>Raised for every log line (from any thread).</summary>
    public event Action<string>? LogLine;

    public IReadOnlyList<string> LogLines
    {
        get { lock (_log) return _log.ToList(); }
    }

    public EngineInfo? Info => _client?.Info;

    /// <summary>Returns a ready engine, installing and starting it if necessary.</summary>
    public async Task<EngineClient> GetAsync(CancellationToken ct = default)
    {
        if (_client is { HasExited: false }) return _client;
        await _startLock.WaitAsync(ct);
        try
        {
            if (_client is { HasExited: false }) return _client;
            if (_client is not null) await _client.DisposeAsync();
            _client = null;
            try
            {
                if (!Runtime.IsInstalled)
                {
                    SetState(EngineState.Installing, "Setting up the speech engine (first run)...");
                    await Runtime.EnsureInstalledAsync(new Progress(this), ct);
                }
                SetState(EngineState.Starting, "Loading the speech model...");
                _client = await EngineClient.StartAsync(Runtime, AddLog, ct);
                _client.Exited += () => SetState(EngineState.Stopped, "Engine stopped; it restarts on next use");
                SetState(EngineState.Ready, "Engine ready");
                return _client;
            }
            catch (OperationCanceledException)
            {
                SetState(EngineState.Stopped, "Engine start cancelled");
                throw;
            }
            catch (Exception e)
            {
                AddLog("ERROR: " + e.Message);
                SetState(EngineState.Failed, "Engine failed: " + e.Message);
                throw;
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void SetState(EngineState state, string text)
    {
        State = state;
        StatusText = text;
        AddLog(text);
    }

    /// <summary>Log lines are also appended here, for diagnosing first-run problems.</summary>
    public string LogFile => Path.Combine(Runtime.Root, "engine.log");

    private void AddLog(string line)
    {
        lock (_log)
        {
            _log.Add(line);
            if (_log.Count > 2000) _log.RemoveRange(0, 500);
            try
            {
                Directory.CreateDirectory(Runtime.Root);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
        LogLine?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _client = null;
    }

    private sealed class Progress(EngineService owner) : IProgress<string>
    {
        public void Report(string value) => owner.AddLog(value);
    }
}
