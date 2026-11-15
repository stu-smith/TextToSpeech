using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace TtsStudio.Core.Engine;

public enum RuntimeComponent { Uv, Python, Packages, Model }

/// <summary>One thing setup will download. <see cref="Bytes"/> is null when the size could not be found.</summary>
public sealed record RuntimeDownload(RuntimeComponent Component, string Name, string Description, long? Bytes);

/// <summary>A locked package file that setup will download.</summary>
public sealed record PackageDownload(string Name, string Version, string Url, long? Bytes);

/// <summary>What the speech engine still needs before it can run, from <see cref="RuntimeManager.PlanAsync"/>.</summary>
public sealed class RuntimePlan(IReadOnlyList<RuntimeDownload> downloads, IReadOnlyList<PackageDownload> packages,
    bool installRuntime, bool fetchModel)
{
    public IReadOnlyList<RuntimeDownload> Downloads { get; } = downloads;
    public IReadOnlyList<PackageDownload> Packages { get; } = packages;

    /// <summary>uv, Python or the engine's packages need installing or updating.</summary>
    public bool InstallRuntime { get; } = installRuntime;

    /// <summary>The voice model is not in the local cache.</summary>
    public bool FetchModel { get; } = fetchModel;

    public bool IsComplete => !InstallRuntime && !FetchModel;
    public long TotalBytes => Downloads.Sum(d => d.Bytes ?? 0);
    public bool HasUnknownSizes => Downloads.Any(d => d.Bytes is null);
}

/// <summary>Reads uv.lock to find the package files a Windows x64 CPython 3.12 install downloads.</summary>
internal static partial class UvLock
{
    private sealed record Package(string Name, string Version, TomlTable Table);

    private static readonly Dictionary<string, string> MarkerEnvironment = new()
    {
        ["sys_platform"] = "win32",
        ["platform_system"] = "Windows",
        ["os_name"] = "nt",
        ["platform_machine"] = "AMD64",
        ["implementation_name"] = "cpython",
        ["platform_python_implementation"] = "CPython",
    };

    /// <summary>
    /// The files needed for <paramref name="root"/>'s runtime dependencies (not its dev group), skipping
    /// packages for other platforms and those whose locked version <paramref name="isInstalled"/> says is present.
    /// </summary>
    public static List<PackageDownload> RequiredFiles(string lockText, string root, Func<string, string, bool> isInstalled)
    {
        var doc = TomlSerializer.Deserialize<TomlTable>(lockText)!;
        var packages = ((TomlTableArray)doc["package"]).Select(t => new Package((string)t["name"], (string)t["version"], t)).ToList();

        var needed = new List<Package>();
        var seen = new HashSet<(string, string, string)>();
        var queue = new Queue<(Package Package, IEnumerable<string> Extras)>();
        var start = packages.First(p => p.Name == root);
        queue.Enqueue((start, []));
        seen.Add((start.Name, start.Version, ""));
        while (queue.Count > 0)
        {
            var (package, extras) = queue.Dequeue();
            needed.Add(package);
            var deps = Dependencies(package.Table, "dependencies");
            if (package.Table.TryGetValue("optional-dependencies", out var optional))
                foreach (var extra in extras)
                    deps = deps.Concat(Dependencies((TomlTable)optional, extra));
            foreach (var dep in deps)
            {
                if (dep.TryGetValue("marker", out var marker) && !MarkerApplies((string)marker)) continue;
                string name = (string)dep["name"];
                string? version = dep.TryGetValue("version", out var v) ? (string)v : null;
                var target = packages.FirstOrDefault(p => p.Name == name && (version is null || p.Version == version));
                var depExtras = dep.TryGetValue("extra", out var e) ? ((TomlArray)e).Cast<string>().Order().ToList() : [];
                // Visited per set of extras, so a package reached again with extras has those dependencies followed too.
                if (target is null || !seen.Add((target.Name, target.Version, string.Join(",", depExtras)))) continue;
                queue.Enqueue((target, depExtras));
            }
        }

        var files = new List<PackageDownload>();
        foreach (var p in needed.DistinctBy(p => (p.Name, p.Version)))
        {
            if (p.Table["source"] is TomlTable source && (source.ContainsKey("virtual") || source.ContainsKey("editable"))) continue;
            if (isInstalled(p.Name, p.Version)) continue;
            var file = PickFile(p.Table);
            if (file is not null) files.Add(new PackageDownload(p.Name, p.Version, (string)file["url"], file.TryGetValue("size", out var s) ? (long)s : null));
        }
        return files;
    }

    private static IEnumerable<TomlTable> Dependencies(TomlTable table, string key) =>
        table.TryGetValue(key, out var deps) ? ((TomlArray)deps).Cast<TomlTable>() : [];

    /// <summary>The best wheel for Windows x64 CPython 3.12, else the sdist if there are no wheels at all.</summary>
    private static TomlTable? PickFile(TomlTable package)
    {
        var wheels = package.TryGetValue("wheels", out var w) ? ((TomlArray)w).Cast<TomlTable>().ToList() : [];
        if (wheels.Count == 0) return package.TryGetValue("sdist", out var sdist) ? (TomlTable)sdist : null;
        return wheels
            .Select(wheel => (Wheel: wheel, Score: WheelScore(Uri.UnescapeDataString(Path.GetFileName(new Uri((string)wheel["url"]).AbsolutePath)))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Wheel)
            .FirstOrDefault();
    }

    /// <summary>How well a wheel's tags fit Windows x64 CPython 3.12: 0 = not installable, higher = more specific.</summary>
    internal static int WheelScore(string fileName)
    {
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('-');
        if (parts.Length < 5) return 0;
        var (pythons, abis, platforms) = (parts[^3].Split('.'), parts[^2].Split('.'), parts[^1].Split('.'));
        int platform = platforms.Contains("win_amd64") ? 2 : platforms.Contains("any") ? 1 : 0;
        int abi = abis.Contains("cp312") ? 3 : abis.Contains("abi3") ? 2 : abis.Contains("none") ? 1 : 0;
        bool python = pythons.Any(t => t is "cp312" or "py3" or "py312" || (abi == 2 && CpythonAtMost312(t)));
        return platform == 0 || abi == 0 || !python ? 0 : platform * 10 + abi;
    }

    private static bool CpythonAtMost312(string tag) =>
        tag.StartsWith("cp3") && int.TryParse(tag[3..], out int minor) && minor <= 12;

    /// <summary>
    /// Evaluates a PEP 508 marker for Windows x64 CPython 3.12. Handles "and"/"or" of simple comparisons;
    /// anything it does not understand counts as true, so sizes err towards too large.
    /// </summary>
    internal static bool MarkerApplies(string marker)
    {
        if (marker.Contains('(')) return true;
        return marker.Split(" or ").Any(any => any.Split(" and ").All(ClauseApplies));
    }

    private static bool ClauseApplies(string clause)
    {
        var m = ClauseRegex().Match(clause.Trim());
        if (!m.Success || !MarkerEnvironment.TryGetValue(m.Groups[1].Value, out var actual)) return true;
        bool equal = actual == m.Groups[3].Value;
        return m.Groups[2].Value == "==" ? equal : !equal;
    }

    [GeneratedRegex("""^(\w+)\s*(==|!=)\s*['"]([^'"]*)['"]$""")]
    private static partial Regex ClauseRegex();
}
