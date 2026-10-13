namespace Encoding.NodeAgent;

public sealed record NodeAgentOptions(
    Guid Id,
    string Name,
    IReadOnlyList<Encoding.Contracts.Nodes.NodeRole> Roles,
    int MaxConcurrentTasks,
    Uri ApiBaseUri,
    string ApiToken,
    TimeSpan HeartbeatInterval)
{
    public static NodeAgentOptions FromConfiguration(IConfiguration configuration)
    {
        var id = configuration.GetValue<Guid>("Node:Id");
        if (id == Guid.Empty)
            throw new InvalidOperationException("Node:Id doit contenir un identifiant UUID stable.");

        var name = configuration["Node:Name"]?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new InvalidOperationException("Node:Name est obligatoire et limité à 128 caractères.");

        var roles = (configuration["Node:Roles"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<Encoding.Contracts.Nodes.NodeRole>(value, ignoreCase: true, out var role)
                ? role
                : throw new InvalidOperationException($"Rôle de nœud inconnu : {value}."))
            .Distinct()
            .ToArray();
        if (roles.Length == 0)
            throw new InvalidOperationException("Node:Roles doit contenir au moins le rôle server ou worker.");

        var maxConcurrentTasks = configuration.GetValue("Node:MaxConcurrentTasks", 1);
        if (maxConcurrentTasks is < 0 or > 1024)
            throw new InvalidOperationException("Node:MaxConcurrentTasks doit être compris entre 0 et 1024.");

        var apiUrl = configuration["Cluster:ApiBaseUrl"];
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var apiBaseUri)
            || (apiBaseUri.Scheme != Uri.UriSchemeHttp && apiBaseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Cluster:ApiBaseUrl doit être une URL HTTP ou HTTPS absolue.");

        var apiToken = configuration["Cluster:NodeApiToken"];
        if (string.IsNullOrWhiteSpace(apiToken))
            throw new InvalidOperationException("Cluster:NodeApiToken est obligatoire.");

        var intervalSeconds = configuration.GetValue("Node:HeartbeatIntervalSeconds", 10);
        if (intervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("Node:HeartbeatIntervalSeconds doit être compris entre 1 et 300.");

        return new NodeAgentOptions(
            id,
            name,
            roles,
            maxConcurrentTasks,
            apiBaseUri.AbsoluteUri.EndsWith('/') ? apiBaseUri : new Uri(apiBaseUri.AbsoluteUri + '/'),
            apiToken,
            TimeSpan.FromSeconds(intervalSeconds));
    }
}
