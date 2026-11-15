using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TtsStudio.Core.Engine;

/// <summary>
/// A running engine worker (python -m ttsengine.worker), spoken to with JSON lines over stdio.
/// Requests are answered in order; the worker handles one at a time.
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly TaskCompletionSource<EngineInfo> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId;
    private int _exited;

    private static readonly JsonSerializerOptions LineOptions = new(ProjectFile.JsonOptions) { WriteIndented = false };

    public event Action<string>? Log;
    public event Action? Exited;

    public EngineInfo? Info { get; private set; }
    public bool HasExited => _process.HasExited;

    private EngineClient(Process process) => _process = process;

    public static async Task<EngineClient> StartAsync(RuntimeManager runtime, Action<string>? log = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(runtime.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = runtime.EngineDir,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("ttsengine.worker");
        foreach (var (k, v) in runtime.WorkerEnvironment()) psi.Environment[k] = v;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var client = new EngineClient(process);
        if (log is not null) client.Log += log;
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) client.Log?.Invoke(e.Data); };
        process.Exited += (_, _) => client.OnExited();
        process.Start();
        process.BeginErrorReadLine();
        _ = Task.Run(client.ReadLoopAsync);

        using var reg = ct.Register(() => client._ready.TrySetCanceled(ct));
        try
        {
            client.Info = await client._ready.Task;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }

    public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default) =>
        CallAsync<SynthesisResult>("synthesize", request, ct);

    public Task<PhonemizeResult> PhonemizeAsync(string text, string voice, IDictionary<string, string>? lexicon = null, CancellationToken ct = default) =>
        CallAsync<PhonemizeResult>("phonemize", new { text, voice, lexicon = lexicon ?? new Dictionary<string, string>() }, ct);

    private async Task<T> CallAsync<T>(string method, object parameters, CancellationToken ct)
    {
        if (_process.HasExited) throw new EngineException("The speech engine has stopped.");
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        string line = JsonSerializer.Serialize(new { id, method, @params = parameters }, LineOptions);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var node = await tcs.Task;
        return node.Deserialize<T>(ProjectFile.JsonOptions) ?? throw new EngineException($"Empty reply to {method}.");
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch (JsonException) { Log?.Invoke(line); continue; }
                if (msg is null) continue;

                if (msg["event"]?.GetValue<string>() is { } ev)
                {
                    if (ev == "ready") _ready.TrySetResult(msg.Deserialize<EngineInfo>(ProjectFile.JsonOptions)!);
                    else if (msg["message"]?.GetValue<string>() is { } text) Log?.Invoke(text);
                    continue;
                }
                if (msg["id"]?.GetValue<int>() is not int id || !_pending.TryRemove(id, out var tcs)) continue;
                if (msg["error"] is { } err)
                    tcs.TrySetException(new EngineException(err["message"]?.GetValue<string>() ?? "Engine error"));
                else
                    tcs.TrySetResult(msg["result"]);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        OnExited();
    }

    private void OnExited()
    {
        if (Interlocked.Exchange(ref _exited, 1) == 1) return;
        var error = new EngineException("The speech engine stopped unexpectedly. See the engine log for details.");
        _ready.TrySetException(error);
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(error);
        Exited?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_process.HasExited)
        {
            _process.Dispose();
            return;
        }
        try
        {
            _process.StandardInput.Close(); // the worker exits at end of input
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        _process.Dispose();
    }
}
