using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TtsStudio.Core.Engine;

/// <summary>
/// Installs the speech engine's Python runtime on first use, entirely under one folder
/// (default %LOCALAPPDATA%\TtsStudio\runtime) with nothing installed system-wide:
/// <list type="number">
/// <item>uv (Astral's Python package manager), a single exe downloaded from GitHub at a
///   pinned version and verified against its SHA-256;</item>
/// <item>a portable CPython 3.12 that uv downloads (python-build-standalone);</item>
/// <item>the engine's dependencies from the shipped uv.lock, using CPU-only PyTorch wheels.</item>
/// </list>
/// The Kokoro model weights (~330 MB) are fetched by the engine on its first start (into hf/).
/// </summary>
public sealed class RuntimeManager
{
    public const string UvVersion = "0.13.8";

    private static readonly Dictionary<Architecture, (string Asset, string Sha256)> UvBuilds = new()
    {
        [Architecture.X64] = ("uv-x86_64-pc-windows-msvc.zip", "529e78a9cec6cd7e3e014f7ecd8bda0c113969d4ed0f2efeedb85c30943325b8"),
        [Architecture.Arm64] = ("uv-aarch64-pc-windows-msvc.zip", "59e6d919bed1f72035e622a7c930058f08a8931c4c78ceaf9f56e7fb74dd2c48"),
    };

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
    private string Marker => Path.Combine(VenvDir, ".tts-studio-installed");

    public bool IsInstalled => File.Exists(Python) && File.Exists(Marker) && File.ReadAllText(Marker) == LockHash();

    /// <summary>Makes sure uv, Python and the engine's packages are installed and current.</summary>
    public async Task EnsureInstalledAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (IsInstalled) return;
        Directory.CreateDirectory(Root);
        // The app and the MCP server may start at the same moment; only one installs.
        using var setupLock = await AcquireLockAsync(Path.Combine(Root, "setup.lock"), log, ct);
        if (IsInstalled) return;

        if (!File.Exists(UvExe)) await DownloadUvAsync(log, ct);

        log?.Report("Installing Python and speech engine packages (first run, about 1 GB)...");
        string python = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "cpython-3.12-windows-x86_64-none" // PyTorch's CPU wheels for Windows are x64; runs under emulation.
            : "3.12";
        int code = await RunAsync(UvExe,
            ["sync", "--frozen", "--no-dev", "--no-install-project", "--project", EngineDir, "--python", python],
            UvEnvironment(), log, ct);
        if (code != 0) throw new InvalidOperationException($"Installing the engine packages failed (uv exit code {code}). See the log for details.");

        File.WriteAllText(Marker, LockHash());
        log?.Report("Speech engine runtime installed.");
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
        ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(Root, "python"),
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

    private async Task DownloadUvAsync(IProgress<string>? log, CancellationToken ct)
    {
        if (!UvBuilds.TryGetValue(RuntimeInformation.OSArchitecture, out var build))
            build = UvBuilds[Architecture.X64];
        string url = $"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{build.Asset}";
        string downloads = Path.Combine(Root, "downloads");
        Directory.CreateDirectory(downloads);
        string zip = Path.Combine(downloads, build.Asset);

        log?.Report($"Downloading uv {UvVersion} (portable Python manager)...");
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TtsStudio/0.1");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            await src.CopyToAsync(dst, ct);
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
}
