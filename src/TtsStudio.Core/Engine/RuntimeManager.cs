using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TtsStudio.Core.Engine;

/// <summary>
/// Installs the speech engine's Python runtime, entirely under one folder
/// (default %LOCALAPPDATA%\TtsStudio\runtime) with nothing installed system-wide:
/// <list type="number">
/// <item>uv (Astral's Python package manager), a single exe downloaded from GitHub at a
///   pinned version and verified against its SHA-256;</item>
/// <item>a portable CPython 3.12 that uv downloads (python-build-standalone);</item>
/// <item>the engine's dependencies from the shipped uv.lock, using CPU-only PyTorch wheels;</item>
/// <item>the Kokoro model weights (~330 MB) from Hugging Face (into hf/).</item>
/// </list>
/// <see cref="PlanAsync"/> works out what is missing and how big it is, so the app can ask before
/// downloading; <see cref="InstallAsync"/> then fetches it. The MCP server installs the runtime on
/// first use with <see cref="EnsureInstalledAsync"/> and lets the engine fetch the model itself.
/// </summary>
public sealed partial class RuntimeManager
{
    public const string UvVersion = "0.13.8";

    /// <summary>The Python that uv <see cref="UvVersion"/> installs for "3.12" on x64, and its download size.</summary>
    public const string PythonVersion = "3.12.16";
    private const long PythonDownloadBytes = 22_365_213;

    public const string ModelRepo = "hexgrad/Kokoro-82M";
    private static readonly string[] ModelFiles = ["config.json", "kokoro-v1_0.pth"];

    private const string EngineProject = "ttsengine";

    private static readonly Dictionary<Architecture, (string Asset, string Sha256, long Bytes)> UvBuilds = new()
    {
        [Architecture.X64] = ("uv-x86_64-pc-windows-msvc.zip", "529e78a9cec6cd7e3e014f7ecd8bda0c113969d4ed0f2efeedb85c30943325b8", 15_529_884),
        [Architecture.Arm64] = ("uv-aarch64-pc-windows-msvc.zip", "59e6d919bed1f72035e622a7c930058f08a8931c4c78ceaf9f56e7fb74dd2c48", 15_590_791),
    };

    private static readonly HttpClient Http = CreateHttpClient();

    public RuntimeManager(string? root = null, string? engineDir = null)
    {
        Root = root
               ?? Environment.GetEnvironmentVariable("TTS_STUDIO_RUNTIME")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TtsStudio", "runtime");
        EngineDir = engineDir
                    ?? Environment.GetEnvironmentVariable("TTS_STUDIO_ENGINE")
                    ?? Path.Combine(AppContext.BaseDirectory, "engine");
    }

    public string Root { get; }
    public string EngineDir { get; }
    public string UvExe => Path.Combine(Root, "uv", "uv.exe");
    public string VenvDir => Path.Combine(Root, "venv");
    public string Python => Path.Combine(VenvDir, "Scripts", "python.exe");
    public string ModelCacheDir => Path.Combine(Root, "hf");
    private string PythonInstallDir => Path.Combine(Root, "python");
    private string ModelRepoDir => Path.Combine(ModelCacheDir, "hub", "models--" + ModelRepo.Replace("/", "--"));
    private string Marker => Path.Combine(VenvDir, ".tts-studio-installed");

    private static (string Asset, string Sha256, long Bytes) UvBuild =>
        UvBuilds.TryGetValue(RuntimeInformation.OSArchitecture, out var build) ? build : UvBuilds[Architecture.X64];

    public bool IsInstalled => File.Exists(Python) && File.Exists(Marker) && File.ReadAllText(Marker) == LockHash();

    /// <summary>Nothing left to download: the runtime is installed and the model weights cached.</summary>
    public bool IsSetUp => IsInstalled && IsModelCached;

    /// <summary>The model weights are in the local Hugging Face cache.</summary>
    public bool IsModelCached
    {
        get
        {
            string snapshots = Path.Combine(ModelRepoDir, "snapshots");
            return Directory.Exists(snapshots) &&
                   Directory.EnumerateDirectories(snapshots).Any(d => ModelFiles.All(f => File.Exists(Path.Combine(d, f))));
        }
    }

    private bool IsPythonInstalled =>
        Directory.Exists(PythonInstallDir) && Directory.EnumerateDirectories(PythonInstallDir, "cpython-3.12*-windows-x86_64-none").Any();

    /// <summary>Works out what setup would download, with sizes. Sizes that need the network and cannot be found are null.</summary>
    public async Task<RuntimePlan> PlanAsync(CancellationToken ct = default)
    {
        var downloads = new List<RuntimeDownload>();
        var packages = new List<PackageDownload>();
        bool installRuntime = !IsInstalled;
        if (installRuntime)
        {
            if (!File.Exists(UvExe))
                downloads.Add(new(RuntimeComponent.Uv, $"uv {UvVersion}", "Python package manager", UvBuild.Bytes));
            if (!IsPythonInstalled)
                downloads.Add(new(RuntimeComponent.Python, $"Python {PythonVersion}", "Portable Python, private to TTS Studio", PythonDownloadBytes));

            var installed = InstalledPackages();
            packages = UvLock.RequiredFiles(await File.ReadAllTextAsync(Path.Combine(EngineDir, "uv.lock"), ct), EngineProject,
                (name, version) => installed.Contains((NormalizeName(name), version)));
            // PyTorch's index does not record file sizes in the lock, so ask the server.
            for (int i = 0; i < packages.Count; i++)
                if (packages[i].Bytes is null)
                    packages[i] = packages[i] with { Bytes = await RemoteSizeAsync(packages[i].Url, ct) };
            if (packages.Count > 0)
            {
                string detail = $"{packages.Count} packages" + (packages.Any(p => p.Name == "torch") ? ", including PyTorch (CPU)" : "");
                long? bytes = packages.All(p => p.Bytes is not null) ? packages.Sum(p => p.Bytes!.Value) : null;
                downloads.Add(new(RuntimeComponent.Packages, "Speech engine packages", detail, bytes));
            }
        }

        bool fetchModel = !IsModelCached;
        if (fetchModel)
        {
            long? bytes = 0;
            foreach (var file in ModelFiles)
                bytes += await RemoteSizeAsync($"https://huggingface.co/{ModelRepo}/resolve/main/{file}", ct);
            downloads.Add(new(RuntimeComponent.Model, "Kokoro-82M voice model", "Speech model weights from Hugging Face", bytes));
        }
        return new RuntimePlan(downloads, packages, installRuntime, fetchModel);
    }

    /// <summary>Downloads and installs everything in <paramref name="plan"/>, reporting overall progress from 0 to 1.</summary>
    public async Task InstallAsync(RuntimePlan plan, IProgress<string>? log = null, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var tracker = new DownloadTracker(plan, progress);
        if (plan.InstallRuntime) await InstallRuntimeAsync(log, tracker, ct);
        if (plan.FetchModel) await FetchModelAsync(log, tracker, ct);
        progress?.Report(1);
    }

    /// <summary>Makes sure uv, Python and the engine's packages are installed and current.</summary>
    public async Task EnsureInstalledAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!IsInstalled) await InstallRuntimeAsync(log, null, ct);
    }

    private async Task InstallRuntimeAsync(IProgress<string>? log, DownloadTracker? tracker, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        // The app and the MCP server may start at the same moment; only one installs.
        using var setupLock = await AcquireLockAsync(Path.Combine(Root, "setup.lock"), log, ct);
        if (IsInstalled) return;

        if (!File.Exists(UvExe)) await DownloadUvAsync(log, tracker, ct);
        tracker?.Complete(RuntimeComponent.Uv);

        log?.Report("Installing Python and the speech engine packages...");
        string python = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "cpython-3.12-windows-x86_64-none" // PyTorch's CPU wheels for Windows are x64; runs under emulation.
            : "3.12";
        var uvLog = new LineSink(line =>
        {
            log?.Report(line);
            tracker?.OnUvLine(line);
        });
        int code = await RunAsync(UvExe,
            ["sync", "--frozen", "--no-dev", "--no-install-project", "--project", EngineDir, "--python", python],
            UvEnvironment(), uvLog, ct);
        if (code != 0) throw new InvalidOperationException($"Installing the engine packages failed (uv exit code {code}). See the log for details.");

        File.WriteAllText(Marker, LockHash());
        tracker?.Complete(RuntimeComponent.Python);
        tracker?.Complete(RuntimeComponent.Packages);
        log?.Report("Speech engine runtime installed.");
    }

    /// <summary>Fetches the model weights into the Hugging Face cache, where the engine's own loader finds them.</summary>
    private async Task FetchModelAsync(IProgress<string>? log, DownloadTracker tracker, CancellationToken ct)
    {
        log?.Report("Downloading the Kokoro voice model...");
        var env = WorkerEnvironment();
        env["HF_HUB_DISABLE_XET"] = "1"; // plain HTTP writes the file as it arrives, so its growth shows progress
        env["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
        string files = string.Join(", ", ModelFiles.Select(f => $"'{f}'"));
        string script = $"from huggingface_hub import hf_hub_download\nfor f in [{files}]: hf_hub_download('{ModelRepo}', f)";

        long before = DirectorySize(ModelRepoDir);
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poll = Task.Run(async () =>
        {
            while (!pollCts.IsCancellationRequested)
            {
                tracker.Set(RuntimeComponent.Model, DirectorySize(ModelRepoDir) - before);
                try { await Task.Delay(300, pollCts.Token); } catch (OperationCanceledException) { }
            }
        }, CancellationToken.None);
        int code;
        try
        {
            code = await RunAsync(Python, ["-c", script], env, log, ct);
        }
        finally
        {
            await pollCts.CancelAsync();
            await poll;
        }
        if (code != 0) throw new InvalidOperationException($"Downloading the voice model failed (exit code {code}). See the log for details.");
        tracker.Complete(RuntimeComponent.Model);
        log?.Report("Voice model downloaded.");
    }

    /// <summary>Environment for the engine worker process.</summary>
    public Dictionary<string, string> WorkerEnvironment() => new()
    {
        ["PYTHONPATH"] = EngineDir,
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUTF8"] = "1",
        ["PYTHONNOUSERSITE"] = "1",
        ["HF_HOME"] = ModelCacheDir,
        ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
        ["HF_HUB_DISABLE_TELEMETRY"] = "1",
        ["TQDM_DISABLE"] = "1",
    };

    private Dictionary<string, string> UvEnvironment() => new()
    {
        ["UV_PYTHON_INSTALL_DIR"] = PythonInstallDir,
        ["UV_CACHE_DIR"] = Path.Combine(Root, "cache"),
        ["UV_PROJECT_ENVIRONMENT"] = VenvDir,
        ["UV_PYTHON_PREFERENCE"] = "only-managed",
        ["UV_NO_CONFIG"] = "1",
        ["UV_NO_PROGRESS"] = "1",
    };

    private string LockHash()
    {
        using var sha = SHA256.Create();
        foreach (var name in new[] { "uv.lock", "pyproject.toml" })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(EngineDir, name));
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>(normalized name, version) of each package in the venv.</summary>
    private HashSet<(string, string)> InstalledPackages()
    {
        var result = new HashSet<(string, string)>();
        string sitePackages = Path.Combine(VenvDir, "Lib", "site-packages");
        if (!Directory.Exists(sitePackages)) return result;
        foreach (var dir in Directory.EnumerateDirectories(sitePackages, "*.dist-info"))
        {
            string stem = Path.GetFileName(dir)[..^".dist-info".Length];
            int dash = stem.IndexOf('-');
            if (dash > 0) result.Add((NormalizeName(stem[..dash]), stem[(dash + 1)..]));
        }
        return result;
    }

    private static string NormalizeName(string name) => NameSeparators().Replace(name, "_").ToLowerInvariant();

    [GeneratedRegex("[-_.]+")]
    private static partial Regex NameSeparators();

    private static long DirectorySize(string dir) =>
        Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f =>
            {
                try
                {
                    f.Refresh(); // the enumerated size of a file still being written lags behind on NTFS
                    return f.Length;
                }
                catch (IOException)
                {
                    return 0;
                }
            })
            : 0;

    private static HttpClient CreateHttpClient()
    {
        // Redirects are followed by hand: Hugging Face gives the file size (X-Linked-Size) on the first response.
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TtsStudio/0.1");
        return http;
    }

    /// <summary>The size of the file at <paramref name="url"/>, or null if it cannot be found (e.g. offline).</summary>
    private static async Task<long?> RemoteSizeAsync(string url, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(url);
            for (int hop = 0; hop < 6; hop++)
            {
                using var resp = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), ct);
                if (resp.Headers.TryGetValues("X-Linked-Size", out var linked) && long.TryParse(linked.FirstOrDefault(), out long size))
                    return size;
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
                {
                    uri = new Uri(uri, location);
                    continue;
                }
                return resp.StatusCode == HttpStatusCode.OK ? resp.Content.Headers.ContentLength : null;
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task DownloadUvAsync(IProgress<string>? log, DownloadTracker? tracker, CancellationToken ct)
    {
        var build = UvBuild;
        string downloads = Path.Combine(Root, "downloads");
        Directory.CreateDirectory(downloads);
        string zip = Path.Combine(downloads, build.Asset);

        log?.Report($"Downloading uv {UvVersion} (portable Python manager)...");
        var uri = new Uri($"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{build.Asset}");
        HttpResponseMessage resp;
        for (int hop = 0; ; hop++)
        {
            resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is < 300 or >= 400 || resp.Headers.Location is null || hop == 5) break;
            uri = new Uri(uri, resp.Headers.Location);
            resp.Dispose();
        }
        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                tracker?.Set(RuntimeComponent.Uv, total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(zip))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!actual.Equals(build.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidOperationException($"The uv download failed verification (SHA-256 {actual}); not using it.");
        }

        string uvDir = Path.GetDirectoryName(UvExe)!;
        Directory.CreateDirectory(uvDir);
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                entry.ExtractToFile(Path.Combine(uvDir, entry.Name), overwrite: true);
        }
        File.Delete(zip);
        if (!File.Exists(UvExe)) throw new InvalidOperationException("The uv download did not contain uv.exe.");
    }

    private static async Task<IDisposable> AcquireLockAsync(string path, IProgress<string>? log, CancellationToken ct)
    {
        bool reported = false;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                if (!reported) log?.Report("Waiting for another TTS Studio process to finish installing the engine...");
                reported = true;
                await Task.Delay(1000, ct);
            }
        }
    }

    internal static async Task<int> RunAsync(string exe, IEnumerable<string> args, IDictionary<string, string> env,
        IProgress<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Never let the child inherit our stdin: in the MCP server that is the protocol pipe,
            // and uv's build subprocesses (e.g. for sdist-only packages) hang waiting on it.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report(e.Data); };
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return p.ExitCode;
    }

    private sealed class LineSink(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>Turns per-component byte counts and uv's output into overall progress.</summary>
    private sealed partial class DownloadTracker(RuntimePlan plan, IProgress<double>? progress)
    {
        private readonly Dictionary<RuntimeComponent, long> _done = [];
        private readonly Dictionary<string, long> _packageBytes = plan.Packages
            .GroupBy(p => NormalizeName(p.Name)).ToDictionary(g => g.Key, g => g.Sum(p => p.Bytes ?? 0));
        private readonly long _total = Math.Max(1, plan.TotalBytes);

        private long SizeOf(RuntimeComponent c) => plan.Downloads.FirstOrDefault(d => d.Component == c)?.Bytes ?? 0;

        public void Set(RuntimeComponent component, long bytes)
        {
            lock (_done) _done[component] = Math.Clamp(bytes, 0, SizeOf(component));
            Report();
        }

        public void Complete(RuntimeComponent component) => Set(component, SizeOf(component));

        /// <summary>
        /// uv prints "Downloaded &lt;name&gt;" as each large file finishes (smaller ones go unmentioned) and
        /// "Prepared N packages" once everything is downloaded.
        /// </summary>
        public void OnUvLine(string line)
        {
            var m = DownloadedLine().Match(line);
            if (m.Success)
            {
                string name = m.Groups[1].Value;
                if (name.StartsWith("cpython-", StringComparison.Ordinal)) Complete(RuntimeComponent.Python);
                else if (_packageBytes.TryGetValue(NormalizeName(name), out long bytes))
                {
                    long done;
                    lock (_done) done = _done.GetValueOrDefault(RuntimeComponent.Packages);
                    Set(RuntimeComponent.Packages, done + bytes);
                }
            }
            else if (line.StartsWith("Prepared ", StringComparison.Ordinal))
            {
                Complete(RuntimeComponent.Python);
                Complete(RuntimeComponent.Packages);
            }
        }

        private void Report()
        {
            long done;
            lock (_done) done = _done.Values.Sum();
            progress?.Report(Math.Min(1, (double)done / _total));
        }

        [GeneratedRegex(@"^\s*Downloaded (\S+)")]
        private static partial Regex DownloadedLine();
    }
}
