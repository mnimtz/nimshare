using NimShare.Api.Controllers;

namespace NimShare.Api.Views.Shared;

/// <summary>ViewModel für die Partial-View
/// <c>_LandingSignerBadge.cshtml</c>. Enthält die Signer-Metadaten
/// (aus dem Landing-Model) plus die dazu passende Public-Cert-Download-URL
/// (die je nach Landing entweder /s/{slug}/signer-cert.cer oder
/// /u/{slug}/signer-cert.cer ist).</summary>
public record LandingSignerBadgeVm(LandingSignerInfo Info, string CertDownloadUrl);
