using EfLens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Short flags: eflens --provider sqlite --connection "Data Source=app.db"
builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
{
    ["--provider"] = $"{EfLensOptions.SectionName}:{nameof(EfLensOptions.Provider)}",
    ["--connection"] = $"{EfLensOptions.SectionName}:{nameof(EfLensOptions.ConnectionString)}",
    ["--max-rows"] = $"{EfLensOptions.SectionName}:{nameof(EfLensOptions.MaxRows)}",
    ["--timeout"] = $"{EfLensOptions.SectionName}:{nameof(EfLensOptions.QueryTimeoutSeconds)}",
});

// stdout belongs to the MCP protocol: every log line goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var options = builder.Configuration.GetSection(EfLensOptions.SectionName).Get<EfLensOptions>() ?? new EfLensOptions();

builder.Services.AddEfLens(options);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithEfLensTools();

await builder.Build().RunAsync();
