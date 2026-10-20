namespace TaxIntelligenceStudio.Infrastructure.Sources;

internal static class AuthoritativeSourcePolicy
{
    private static readonly string[] AllowedHosts =
    [
        "irs.gov",
        "www.irs.gov",
        "treasury.gov",
        "www.treasury.gov",
        "federalregister.gov",
        "www.federalregister.gov"
    ];

    public static bool IsAllowedHttps(Uri sourceUrl) =>
        sourceUrl.Scheme == Uri.UriSchemeHttps &&
        sourceUrl.IsDefaultPort &&
        string.IsNullOrEmpty(sourceUrl.UserInfo) &&
        AllowedHosts.Contains(sourceUrl.IdnHost, StringComparer.OrdinalIgnoreCase);
}
