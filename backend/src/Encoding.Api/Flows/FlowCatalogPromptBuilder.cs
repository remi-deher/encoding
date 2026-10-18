using System.Text;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;

namespace Encoding.Api.Flows;

public sealed class FlowCatalogPromptBuilder(FlowComponentRegistry registry)
{
    public string BuildSystemPrompt(string mediaType)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Tu es l'architecte expert de flows multimédia pour le moteur distribué Encoding.");
        sb.AppendLine("Tu composes des flux complets, cohérents et fiables.");
        sb.AppendLine("FORMAT OBLIGATOIRE : Réponds UNIQUEMENT avec un objet JSON valide représentant le FlowDefinition (aucun texte avant ni après, pas de balises superflues).");
        sb.AppendLine();
        sb.AppendLine("RÈGLES D'OR DU MOTEUR DE FLOWS :");
        sb.AppendLine("1. Exactement UNE entrée obligatoire (ex: base-input-video ou base-input-file) sans AUCUNE connexion entrante.");
        sb.AppendLine("2. Le graphe est un DAG acyclique strict (aucun cycle). Tous les blocs doivent être reliés depuis l'entrée.");
        sb.AppendLine("3. Chaque sortie ne peut être reliée qu'une seule fois. Plusieurs sorties peuvent converger vers une même entrée.");
        sb.AppendLine("4. Respecter scrupuleusement les sorties (sourceOutputId) et les identifiants de composants du catalogue.");
        sb.AppendLine();
        sb.AppendLine("CATALOGUE DES COMPOSANTS DISPONIBLES :");

        foreach (var plugin in registry.Plugins)
        {
            var matching = plugin.Components
                .Where(c => c.SupportedFlowMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase)
                         || c.SupportedFlowMediaTypes.Contains("general", StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count == 0) continue;

            sb.AppendLine($"Plugin: {plugin.Id} (v{plugin.Version})");
            foreach (var c in matching)
            {
                var outputs = string.Join(", ", c.Outputs.Select(o => o.Id));
                var fields = string.Join(", ", c.ConfigurationSchema.Fields.Select(f => $"{f.Id} ({f.Type})"));
                sb.AppendLine($"  - id: \"{c.Id}\" (v{c.Version}) [{c.Kind}] : {c.Name}");
                sb.AppendLine($"    Description: {c.Description}");
                sb.AppendLine($"    Outputs valides: [{outputs}]");
                if (fields.Length > 0)
                {
                    sb.AppendLine($"    Champs config: [{fields}]");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("STRUCTURE DU DOCUMENT JSON ATTENDU :");
        sb.AppendLine("""
        {
          "formatVersion": 1,
          "id": "11111111-1111-1111-1111-111111111111",
          "version": 1,
          "name": "Nom du Flow",
          "mediaType": "video",
          "nodes": [
            {
              "id": "node_1",
              "name": "Entrée",
              "component": {
                "pluginId": "base",
                "pluginVersion": "1.0.0",
                "componentId": "base-input-video",
                "componentVersion": "1.0.0",
                "configurationSchemaVersion": 1
              },
              "configuration": {}
            }
          ],
          "connections": [
            {
              "id": "conn_1",
              "sourceNodeId": "node_1",
              "sourceOutputId": "output",
              "targetNodeId": "node_2"
            }
          ]
        }
        """);

        return sb.ToString();
    }
}
