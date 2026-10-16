namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerClusterOptions(
    bool Enabled,
    Guid? ClusterId,
    Uri? PublicEndpoint,
    bool Bootstrap,
    int ListenPort,
    TimeSpan GossipInterval,
    TimeSpan OfflineAfter,
    string IdentityPassword,
    string DataDirectory,
    string SharedDataKey = "",
    string? TransportProxy = null)
{
    public static PeerClusterOptions From(IConfiguration configuration, string contentRoot)
    {
        var enabled = configuration.GetValue("PeerCluster:Enabled", false);
        var clusterIdValue = configuration["PeerCluster:ClusterId"];
        Guid? clusterId = Guid.TryParse(clusterIdValue, out var parsedClusterId) ? parsedClusterId : null;
        Uri? publicEndpoint = null;
        var endpointValue = configuration["PeerCluster:PublicEndpoint"];
        if (Uri.TryCreate(endpointValue, UriKind.Absolute, out var parsedEndpoint)) publicEndpoint = parsedEndpoint;
        var bootstrap = configuration.GetValue("PeerCluster:Bootstrap", false);
        var listenPort = configuration.GetValue("PeerCluster:ListenPort", 8443);
        var gossipSeconds = configuration.GetValue("PeerCluster:GossipIntervalSeconds", 3);
        var offlineSeconds = configuration.GetValue("PeerCluster:OfflineAfterSeconds", 30);
        var identityPassword = configuration["PeerCluster:IdentityPassword"] ?? string.Empty;
        var sharedDataKey = configuration["PeerCluster:SharedDataKey"] ?? string.Empty;
        if (enabled && (!TryReadKey(sharedDataKey)))
            throw new InvalidOperationException("PeerCluster:SharedDataKey doit contenir une clé aléatoire de 32 octets encodée en base64, identique sur les pairs.");
        var dataDirectory = configuration["PeerCluster:DataDirectory"]
            ?? configuration["Node:DataDirectory"]
            ?? Path.Combine(contentRoot, "data", "peer-cluster");

        if (listenPort is < 1 or > 65535) throw new InvalidOperationException("PeerCluster:ListenPort doit être compris entre 1 et 65535.");
        if (gossipSeconds is < 1 or > 60) throw new InvalidOperationException("PeerCluster:GossipIntervalSeconds doit être compris entre 1 et 60.");
        if (offlineSeconds <= gossipSeconds * 2) throw new InvalidOperationException("PeerCluster:OfflineAfterSeconds doit dépasser deux intervalles de gossip.");
        if (enabled && clusterId is null) throw new InvalidOperationException("PeerCluster:ClusterId doit être un GUID partagé par tous les pairs.");
        if (enabled && identityPassword.Length < 32) throw new InvalidOperationException("PeerCluster:IdentityPassword doit contenir au moins 32 caractères et être conservé comme secret propre à ce pair.");
        if (enabled && (publicEndpoint is null || publicEndpoint.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(publicEndpoint.UserInfo) || publicEndpoint.AbsolutePath != "/"))
            throw new InvalidOperationException("PeerCluster:PublicEndpoint doit être une URL HTTPS absolue, sans chemin ni identifiants.");

        return new(enabled, clusterId, publicEndpoint, bootstrap, listenPort, TimeSpan.FromSeconds(gossipSeconds),
            TimeSpan.FromSeconds(offlineSeconds), identityPassword, Path.GetFullPath(dataDirectory), sharedDataKey, configuration["PeerCluster:TransportProxy"]);
    }
    private static bool TryReadKey(string value)
    {
        Span<byte> key = stackalloc byte[32];
        return Convert.TryFromBase64String(value, key, out var count) && count == 32;
    }
}
