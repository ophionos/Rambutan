using System.Reflection;

namespace Rambutan.Integrations;

/// <summary>
/// Credentials that identify this app to outside services: the Discord
/// application id and the Last.fm API key and shared secret. They ship in the
/// binary like every desktop client's (a Last.fm secret can't be protected
/// client-side), but they stay out of the repository: the build takes them from
/// the MSBuild properties <c>RambutanDiscordAppId</c>, <c>RambutanLastFmApiKey</c>
/// and <c>RambutanLastFmSharedSecret</c>, which environment variables of the same
/// names also set. A build without them says the integration isn't available in
/// this build instead of failing at runtime.
/// </summary>
public static class IntegrationKeys
{
    private static readonly Dictionary<string, string> Metadata = typeof(IntegrationKeys).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(a => !string.IsNullOrWhiteSpace(a.Value))
        .ToDictionary(a => a.Key, a => a.Value!.Trim(), StringComparer.Ordinal);

    public static string? DiscordAppId => Get("RambutanDiscordAppId");

    public static string? LastFmApiKey => Get("RambutanLastFmApiKey");

    public static string? LastFmSharedSecret => Get("RambutanLastFmSharedSecret");

    private static string? Get(string key) => Metadata.GetValueOrDefault(key);
}
