using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using EfLens.Providers;

namespace EfLens.Ef;

/// <summary>Reads the EF Core model of a compiled app. One implementation; an interface so tools can be tested.</summary>
public interface IEfModelSource
{
    /// <exception cref="EfModelException">No assembly configured, no DbContext, or it cannot be created.</exception>
    Task<EfModelInfo> GetModelAsync(string? contextName, CancellationToken cancellationToken = default);
}

/// <summary>Problems loading the app's model. The message is written for the agent.</summary>
public sealed class EfModelException(string message) : EfLensException(message);

/// <summary>
/// Loads the target app into its own collectible <see cref="AssemblyLoadContext"/>, resolved with the app's
/// <c>.deps.json</c>, so the app's EF Core version is used rather than one bundled with EfLens. A small probe
/// assembly is loaded into that same context, builds the model and returns it as JSON. Assemblies are loaded
/// from memory, so the app's build output is never locked and every call sees the latest build.
/// </summary>
public sealed class EfModelLoader(EfLensOptions options) : IEfModelSource
{
    private const string ProbeAssemblyName = "EfLens.ModelProbe";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A DbContext factory may read appsettings.json from the current directory, like `dotnet ef` expects;
    // the directory is process-wide, so loads are serialized.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<EfModelInfo> GetModelAsync(string? contextName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.EfAssembly))
            throw new EfModelException("No EF Core assembly configured. Start EfLens with --ef-assembly path/to/YourApp.dll.");

        var assemblyPath = Path.GetFullPath(options.EfAssembly);
        if (!File.Exists(assemblyPath))
            throw new EfModelException($"'{assemblyPath}' does not exist. Build the app first, or fix --ef-assembly.");

        await Gate.WaitAsync(cancellationToken);
        try
        {
            // Model building is synchronous and CPU-bound; keep it off the protocol loop.
            return await Task.Run(() => Probe(assemblyPath, contextName ?? NullIfBlank(options.EfContext)), cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static EfModelInfo Probe(string assemblyPath, string? contextName)
    {
        var context = new TargetLoadContext(assemblyPath);
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(assemblyPath)!;

            var target = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(assemblyPath));
            var probe = context.LoadFromAssemblyName(new AssemblyName(ProbeAssemblyName));
            var describe = probe.GetType("EfLens.Ef.ModelProbe", throwOnError: true)!.GetMethod("Describe")!;

            string json;
            try
            {
                json = (string)describe.Invoke(null, [target, contextName])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw Explain(ex.InnerException);
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new EfModelException(error.GetString()!);

            return document.RootElement.GetProperty("model").Deserialize<EfModelInfo>(Json)!;
        }
        catch (Exception ex) when (ex is not EfModelException)
        {
            throw Explain(ex);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            context.Unload();
        }
    }

    /// <summary>Turns load failures into something the agent (and the developer) can act on.</summary>
    private static EfModelException Explain(Exception ex) => ex switch
    {
        FileNotFoundException { FileName: { } file } when file.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) =>
            new($"The app does not reference {new AssemblyName(file).Name}. EfLens needs a relational EF Core provider (10 or later)."),
        FileNotFoundException { FileName: { } file } =>
            new($"Could not load {new AssemblyName(file).Name}. Point --ef-assembly at the app's build output (bin/...), where its dependencies are."),
        MissingMethodException or TypeLoadException =>
            new($"The app's EF Core version is not supported ({ex.Message}). EfLens supports EF Core 10 and later."),
        _ => new($"Creating the DbContext failed: {ex.GetType().Name}: {ex.Message}"),
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class TargetLoadContext(string assemblyPath) : AssemblyLoadContext($"eflens:{Path.GetFileName(assemblyPath)}", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == ProbeAssemblyName)
                return LoadFromBytes(Path.Combine(AppContext.BaseDirectory, ProbeAssemblyName + ".dll"));

            if (_resolver.ResolveAssemblyToPath(name) is { } path)
                return LoadFromBytes(path);

            // Shared frameworks: Microsoft.NETCore.App comes from the default context (return null);
            // ASP.NET Core assemblies (e.g. for IdentityDbContext) are looked up next to it.
            return SharedFramework.FindAspNetCore(name) is { } aspNet ? LoadFromBytes(aspNet) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;

        private Assembly LoadFromBytes(string path)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            return LoadFromStream(stream);
        }
    }
}

internal static class SharedFramework
{
    /// <summary>
    /// <c>dotnet/shared/Microsoft.NETCore.App/10.0.x</c> is where the runtime lives; ASP.NET Core sits beside it as
    /// <c>Microsoft.AspNetCore.App/10.0.x</c>. Picks the highest installed version with the same major.
    /// </summary>
    internal static string? FindAspNetCore(AssemblyName name)
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var sharedDirectory = runtimeDirectory is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(runtimeDirectory));
        var aspNetRoot = sharedDirectory is null ? null : Path.Combine(sharedDirectory, "Microsoft.AspNetCore.App");
        if (aspNetRoot is null || !Directory.Exists(aspNetRoot))
            return null;

        var major = Environment.Version.Major;
        var versionDirectory = Directory.GetDirectories(aspNetRoot)
            .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
            .Where(d => d.Version?.Major == major)
            .OrderByDescending(d => d.Version)
            .Select(d => d.Path)
            .FirstOrDefault();

        var candidate = versionDirectory is null ? null : Path.Combine(versionDirectory, name.Name + ".dll");
        return candidate is not null && File.Exists(candidate) ? candidate : null;
    }
}
