using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Encoding.Contracts.Auth;

namespace Encoding.Api.Infrastructure;

/// <summary>
/// Adresses IPv4 des interfaces actives vues par ce processus, pour aider à repérer le nœud sur le réseau.
/// Dans un conteneur en réseau « bridge », ce sont les adresses du conteneur, pas celle de l'hôte : l'information est
/// observée, jamais présentée comme l'adresse de joignabilité du nœud (celle-ci reste l'URL publique ou annoncée).
/// </summary>
public static class LocalNetworkAddresses
{
    public const int MaximumAddresses = 16;

    /// <summary>Ponts virtuels côté hôte (Docker, libvirt) : jamais l'adresse par laquelle le réseau local joint le nœud.</summary>
    private static readonly string[] VirtualBridgePrefixes = ["docker", "br-", "veth", "virbr", "cni", "flannel", "cali", "lxcbr"];

    public static IReadOnlyList<SetupNodeAddress> Current()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses.Select(unicast => (nic.Name, unicast.Address)));
            return Select(candidates);
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            // Best-effort : l'absence d'adresse n'empêche pas l'installation.
            return [];
        }
    }

    /// <summary>Filtre déterministe : IPv4 unicast hors boucle locale, lien local et ponts virtuels ; dédoublonné et borné.</summary>
    public static IReadOnlyList<SetupNodeAddress> Select(IEnumerable<(string Interface, IPAddress Address)> candidates) =>
        candidates
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(item.Address)
                && !IsLinkLocal(item.Address)
                && !IsVirtualBridge(item.Interface))
            .DistinctBy(item => item.Address)
            .OrderBy(item => item.Interface, StringComparer.Ordinal)
            .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
            .Take(MaximumAddresses)
            .Select(item => new SetupNodeAddress(item.Interface, item.Address.ToString()))
            .ToArray();

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsVirtualBridge(string name) =>
        VirtualBridgePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
