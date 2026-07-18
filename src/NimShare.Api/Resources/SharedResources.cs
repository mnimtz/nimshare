// The marker type deliberately lives in the ROOT namespace (NimShare.Api),
// not in NimShare.Api.Resources. The ResourceManagerStringLocalizer builds
// the resource base name as "{RootNamespace}.{ResourcesPath}.{TypeName}"
// which for us resolves to "NimShare.Api.Resources.SharedResources" —
// exactly the embedded-resource name our .resx files compile to.
// Moving it into NimShare.Api.Resources would double the "Resources" segment.
namespace NimShare.Api;

public sealed class SharedResources
{
}
