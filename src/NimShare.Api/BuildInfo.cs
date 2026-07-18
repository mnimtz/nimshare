using System.Reflection;

namespace NimShare.Api;

/// <summary>App version, resolved from the VERSION file baked in at build time.</summary>
public static class BuildInfo
{
    private static readonly Lazy<string> _version = new(ResolveVersion);
    public static string Version => _version.Value;

    private static string ResolveVersion()
    {
        // Try the VERSION file next to the entry assembly (both in-container and dev).
        try
        {
            var dir = AppContext.BaseDirectory;
            for (var probe = new DirectoryInfo(dir); probe is not null; probe = probe.Parent)
            {
                var candidate = Path.Combine(probe.FullName, "VERSION");
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Trim();
            }
        }
        catch { /* fall through */ }
        // Assembly informational version fallback.
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
    }
}
