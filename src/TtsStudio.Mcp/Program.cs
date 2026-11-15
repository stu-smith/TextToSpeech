using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using TtsStudio.Core;
using TtsStudio.Core.Engine;
using TtsStudio.Mcp;

// MCP server over stdio: stdout carries the protocol, so all logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Critical); // tool errors are reported to the client already

builder.Services.AddSingleton(new EngineService(new RuntimeManager()));
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "tts-studio", Title = "TTS Studio", Version = "0.1.0" };
        o.ServerInstructions = """
            Edits TTS Studio project files (.ttsproj): text-to-speech samples whose text carries
            regions with pronunciation and prosody properties, rendered on CPU with Kokoro-82M.
            Call get_guide once for the property reference and phoneme key. Paths may be relative
            to the working directory. Samples are referenced by id or by name. Rendering starts the
            speech engine; the very first use installs a portable Python runtime and model, which
            can take several minutes.
            """ + "\n\n" + Guide.Properties;
    })
    .WithStdioServerTransport()
    .WithTools<ProjectTools>();

var app = builder.Build();
var engine = app.Services.GetRequiredService<EngineService>();
await app.RunAsync();
await engine.DisposeAsync(); // stops the Python worker if one was started
