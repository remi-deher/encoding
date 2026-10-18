namespace Encoding.Contracts.Tools;

/// <summary>Prompt information returned during OAuth 2.0 Device Authorization Grant (RFC 8628).</summary>
public sealed record DeviceAuthPrompt(
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds = 900,
    int IntervalSeconds = 5);

/// <summary>Status of an AI tool authentication session on a node.</summary>
public sealed record DeviceAuthStatus(
    string ToolId,
    string State,
    string? AccountName = null,
    DateTimeOffset? AuthenticatedAt = null,
    string? ErrorMessage = null);

/// <summary>Response payload when starting device code authentication.</summary>
public sealed record DeviceAuthStartResponse(
    string ToolId,
    DeviceAuthPrompt Prompt);

/// <summary>Summary of an AI tool's availability and configuration on the node.</summary>
public sealed record AiToolInfo(
    string Id,
    string Name,
    string Description,
    string Category,
    bool IsInstalled,
    DeviceAuthStatus AuthStatus,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// Définition des capacités d'accélération matérielle protégées.
/// En vertu de todo-hardware-accelerated-tools-policy.md, les outils requérant ces capacités
/// doivent obligatoirement utiliser l'environnement qualifié du conteneur.
/// </summary>
public static class HardwareCapabilities
{
    public static readonly HashSet<string> ProtectedCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvenc", "nvdec", "cuda",
        "qsv", "vpl", "mfx",
        "vaapi", "vdpau",
        "amf", "videotoolbox", "vulkan", "npu"
    };

    public static bool RequiresHardwareAcceleration(IEnumerable<string>? capabilities) =>
        capabilities?.Any(c => ProtectedCapabilities.Contains(c)) ?? false;
}
