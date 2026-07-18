namespace NimShare.Core;

/// <summary>
/// Marker type used by <c>IStringLocalizer&lt;SharedResources&gt;</c> to
/// bind to the shared .resx files (SharedResources.{culture}.resx).
/// The type deliberately lives in the Core assembly so it can be
/// injected into both API controllers and Razor pages.
/// </summary>
public sealed class SharedResources
{
}
