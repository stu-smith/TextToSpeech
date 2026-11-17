using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TtsStudio.App.Services;

/// <summary>Whether an MCP client's config starts this copy of the MCP server.</summary>
public enum McpRegistrationState
{
    /// <summary>No entry for the server, or no config file yet.</summary>
    NotRegistered,

    /// <summary>An entry that starts this copy of the server.</summary>
    Registered,

    /// <summary>An entry that starts some other path: an old or moved copy of the app.</summary>
    Elsewhere,

    /// <summary>The file exists but is not JSON that can be read; it is never written over.</summary>
    Unreadable,
}

public sealed record McpRegistration(McpRegistrationState State, string? Command = null);

/// <summary>
/// Adds the MCP server (TtsStudio.Mcp.exe, shipped next to the app) to MCP clients. Claude Code is
/// changed through its own CLI (<c>claude mcp add</c>), since that is the supported way and it
/// rewrites its config file constantly; Claude Desktop has no CLI, so its config file is edited
/// directly. Both files are read directly to show the current state.
/// </summary>
public static class AgentIntegration
{
    public const string ServerName = "tts-studio";

    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The command a client runs: the MCP server next to this exe, wherever it is now.</summary>
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "TtsStudio.Mcp.exe");

    public static string CommandLine => $"\"{ExePath}\"";

    /// <summary>The server's entry as an <c>mcpServers</c> snippet, for clients configured by hand.</summary>
    public static string Snippet =>
        new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = Entry() } }.ToJsonString(Indented);

    // ---------------------------------------------------------------- Claude Code

    /// <summary>Where Claude Code keeps user-scope servers: <c>~/.claude.json</c>, or under CLAUDE_CONFIG_DIR.</summary>
    public static string ClaudeCodeConfigPath =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, ".claude.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public static string ClaudeCodeCommand => $"claude mcp add --scope user {ServerName} -- \"{ExePath}\"";

    public static McpRegistration ClaudeCodeState => Read(ClaudeCodeConfigPath);

    /// <summary>
    /// The <c>claude</c> CLI: on the PATH, or where the native installer and npm put it, since an app
    /// started from the Start menu may not see a PATH change made after sign-in.
    /// </summary>
    public static string? FindClaudeCli()
    {
        string[] names = ["claude.exe", "claude.cmd", "claude.bat"];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (string dir in dirs)
        foreach (string name in names)
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; skip it.
            }
        }
        return null;
    }

    /// <summary>Adds, or re-points, the user-scope entry. Returns null on success, or what went wrong.</summary>
    public static async Task<string?> AddToClaudeCodeAsync(string cli)
    {
        // `mcp add` refuses a name that exists, so an entry for a moved copy goes first.
        if (ClaudeCodeState.State != McpRegistrationState.NotRegistered)
            await RunAsync(cli, "mcp", "remove", ServerName, "--scope", "user");

        var (exitCode, output) = await RunAsync(cli, "mcp", "add", "--scope", "user", ServerName, "--", ExePath);
        return exitCode == 0 ? null : Failure("claude mcp add", exitCode, output);
    }

    public static async Task<string?> RemoveFromClaudeCodeAsync(string cli)
    {
        var (exitCode, output) = await RunAsync(cli, "mcp", "remove", ServerName, "--scope", "user");
        return exitCode == 0 || ClaudeCodeState.State == McpRegistrationState.NotRegistered
            ? null
            : Failure("claude mcp remove", exitCode, output);
    }

    // ---------------------------------------------------------------- Claude Desktop

    /// <summary>
    /// Claude Desktop's config file. The Microsoft Store build keeps its app data in a virtualised
    /// folder under its package, so that one is used when it exists.
    /// </summary>
    public static string ClaudeDesktopConfigPath => Path.Combine(
        ClaudeDesktopFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude"),
        "claude_desktop_config.json");

    /// <summary>True once Claude Desktop has run on this account and made its settings folder.</summary>
    public static bool IsClaudeDesktopInstalled => ClaudeDesktopFolder is not null;

    public static McpRegistration ClaudeDesktopState => Read(ClaudeDesktopConfigPath);

    public static string? AddToClaudeDesktop() => Edit(ClaudeDesktopConfigPath, servers => servers[ServerName] = Entry());

    public static string? RemoveFromClaudeDesktop() =>
        File.Exists(ClaudeDesktopConfigPath) ? Edit(ClaudeDesktopConfigPath, servers => servers.Remove(ServerName)) : null;

    private static string? ClaudeDesktopFolder
    {
        get
        {
            try
            {
                string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                if (Directory.Exists(packages))
                    foreach (string package in Directory.EnumerateDirectories(packages, "Claude_*"))
                    {
                        string folder = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                        if (Directory.Exists(folder)) return folder;
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Fall back to the ordinary location.
            }
            string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
            return Directory.Exists(roaming) ? roaming : null;
        }
    }

    // ---------------------------------------------------------------- config files

    private static JsonObject Entry() => new() { ["command"] = ExePath, ["args"] = new JsonArray() };

    /// <summary>Reads the <c>mcpServers</c> entry that Claude Code's and Claude Desktop's config files share the shape of.</summary>
    private static McpRegistration Read(string configPath)
    {
        if (!File.Exists(configPath)) return new(McpRegistrationState.NotRegistered);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(configPath), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(McpRegistrationState.Unreadable);
        }
        if (root is null) return new(McpRegistrationState.Unreadable);
        if (root["mcpServers"] is not JsonObject servers || servers[ServerName] is not JsonObject entry)
            return new(McpRegistrationState.NotRegistered);

        string? command = entry["command"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        return command is not null && SamePath(command, ExePath)
            ? new(McpRegistrationState.Registered, command)
            : new(McpRegistrationState.Elsewhere, command);
    }

    /// <summary>
    /// Changes the <c>mcpServers</c> section, keeping everything else in the file as it was and
    /// leaving a file that cannot be read alone. Returns null on success, or why nothing changed.
    /// </summary>
    private static string? Edit(string configPath, Action<JsonObject> change)
    {
        try
        {
            JsonObject root = [];
            if (File.Exists(configPath))
            {
                string text = File.ReadAllText(configPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) is not JsonObject parsed)
                        return $"{configPath} does not hold a JSON object, so it was left alone.";
                    root = parsed;
                }
            }

            if (root["mcpServers"] is not JsonObject servers)
            {
                if (root["mcpServers"] is not null) return $"'mcpServers' in {configPath} is not an object, so the file was left alone.";
                root["mcpServers"] = servers = [];
            }
            change(servers);

            if (Path.GetDirectoryName(configPath) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            // Via a temporary file, so a failed write cannot cost the client its config.
            string temp = configPath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Indented));
            File.Move(temp, configPath, overwrite: true);
            return null;
        }
        catch (JsonException e)
        {
            return $"{configPath} is not valid JSON ({e.Message}), so it was left alone.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{configPath} could not be written: {e.Message}";
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            a = Path.GetFullPath(a);
            b = Path.GetFullPath(b);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Compare as written.
        }
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- CLI

    /// <summary>Runs the CLI without a console window. A .cmd shim (an npm install) has to go through cmd.exe.</summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(string cli, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (cli.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = cli;
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
        }
        else
        {
            // /s takes everything between the outer quotes as the command line, quoting intact.
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = $"/d /s /c \"{string.Join(' ', arguments.Prepend(cli).Select(Quote))}\"";
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{cli} did not start.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(CliTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (-1, $"{Path.GetFileName(cli)} did not finish within {CliTimeout.TotalSeconds:0} seconds.");
            }
            return (process.ExitCode, (await output + "\n" + await error).Trim());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, e.Message);
        }
    }

    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '&' or '|' or '<' or '>' or '^' or '(' or ')')
            ? argument
            : $"\"{argument}\"";

    private static string Failure(string command, int exitCode, string output) =>
        output.Length > 0 ? $"{command} failed: {output}" : $"{command} failed (exit code {exitCode}).";
}
