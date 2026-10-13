using Encoding.Contracts.Nodes;
using System.Text.RegularExpressions;

namespace Encoding.Api.Nodes;

public sealed record NodePresenceOptions(TimeSpan OfflineAfter);

public static class NodeHeartbeatValidator
{
    private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.Ordinal)
    {
        "video", "image", "audio", "subtitles", "general"
    };
    private static readonly Regex PairingKeyPattern = new("^[0-9A-F]{16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Dictionary<string, string[]> Validate(NodeHeartbeatRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128)
            errors["name"] = ["Le nom est obligatoire et limité à 128 caractères."];
        if (request.Roles is null || request.Roles.Count == 0 || request.Roles.Distinct().Count() != request.Roles.Count)
            errors["roles"] = ["Déclarez au moins un rôle sans doublon."];
        if (request.MaxConcurrentTasks < 0 || request.MaxConcurrentTasks > 1024)
            errors["maxConcurrentTasks"] = ["La limite doit être comprise entre 0 et 1024."];
        if (request.Capabilities is null || request.Capabilities.Count > 256)
            errors["capabilities"] = ["La liste des capacités est obligatoire et limitée à 256 éléments."];
        else
        {
            for (var index = 0; index < request.Capabilities.Count; index++)
            {
                var capability = request.Capabilities[index];
                var prefix = $"capabilities[{index}]";
                if (string.IsNullOrWhiteSpace(capability.ToolId) || capability.ToolId.Length > 128)
                    errors[$"{prefix}.toolId"] = ["L'identifiant de l'outil est obligatoire et limité à 128 caractères."];
                if (string.IsNullOrWhiteSpace(capability.Version) || capability.Version.Length > 128)
                    errors[$"{prefix}.version"] = ["La version est obligatoire et limitée à 128 caractères."];
                if (capability.MediaTypes is null || capability.MediaTypes.Any(type => !SupportedMediaTypes.Contains(type)))
                    errors[$"{prefix}.mediaTypes"] = ["Un ou plusieurs types de média ne sont pas pris en charge."];
                if (capability.AccelerationTypes is null || capability.AccelerationTypes.Any(string.IsNullOrWhiteSpace))
                    errors[$"{prefix}.accelerationTypes"] = ["Les types d'accélération doivent être renseignés."];
            }
        }
        return errors;
    }

    public static Dictionary<string, string[]> Validate(NodeEnrollmentRequest request)
    {
        var errors = Validate(new NodeHeartbeatRequest(request.Name, request.Roles, request.MaxConcurrentTasks, request.Capabilities));
        if (request.Id == Guid.Empty) errors["id"] = ["L’identifiant du nœud doit être un UUID non vide."];
        if (string.IsNullOrWhiteSpace(request.NodeToken) || request.NodeToken.Length is < 40 or > 256)
            errors["nodeToken"] = ["L’identité locale du nœud est invalide."];
        return errors;
    }

    public static Dictionary<string, string[]> Validate(CreateNodeEnrollmentRequest request)
    {
        if (request.Node is null)
            return new Dictionary<string, string[]> { ["node"] = ["Les informations du nœud sont obligatoires."] };
        var errors = Validate(request.Node);
        if (request.PairingKey is null || !PairingKeyPattern.IsMatch(request.PairingKey))
            errors["pairingKey"] = ["La clé de jumelage doit contenir 16 caractères hexadécimaux majuscules."];
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ApproveNodeEnrollmentRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.PairingKey is null || !PairingKeyPattern.IsMatch(request.PairingKey))
            errors["pairingKey"] = ["La clé de jumelage doit contenir 16 caractères hexadécimaux majuscules."];
        return errors;
    }
}
