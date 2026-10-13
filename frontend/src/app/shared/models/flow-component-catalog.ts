import type { FlowComponentDefinition, FlowComponentOutput } from './flow-component';
import { MEDIA_TOOL_ACTIONS } from './media-tool-actions';
import { FLOW_CONTROL_COMPONENTS } from './flow-control-components';

const output = (id: string, label: string, description: string): FlowComponentOutput => ({
  id,
  label,
  description,
});
const COMMON_OUTPUTS = [
  output('success', 'Suite', 'Le composant a terminé son traitement.'),
  output('error', 'Erreur', 'Le composant n’a pas pu terminer son traitement.'),
];
export const FLOW_COMPONENT_CATALOG: readonly FlowComponentDefinition[] = [
  ...FLOW_CONTROL_COMPONENTS,
  ...MEDIA_TOOL_ACTIONS,
  {
    id: 'base-condition',
    name: 'Tester une condition',
    description: 'Évalue une règle et dirige le flow selon le résultat.',
    kind: 'condition',
    mediaType: 'general',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: [
      output('yes', 'Oui', 'La condition est satisfaite.'),
      output('no', 'Non', 'La condition n’est pas satisfaite.'),
      output('error', 'Erreur', 'La condition n’a pas pu être évaluée.'),
    ],
    configurationSchema: {
      version: 1,
      fields: [
        {
          id: 'expression',
          type: 'text',
          label: 'Expression',
          description: 'Règle à évaluer pour choisir la sortie.',
          placeholder: 'Ex. codec vidéo = HEVC',
          required: true,
          multiline: true,
        },
      ],
    },
  },
  {
    id: 'base-variable',
    name: 'Définir une variable',
    description: 'Crée ou met à jour une valeur réutilisable dans le flow.',
    kind: 'variable',
    mediaType: 'general',
    pluginId: 'base',
    pluginName: 'Base',
    outputs: COMMON_OUTPUTS,
    configurationSchema: {
      version: 1,
      fields: [
        { id: 'name', label: 'Nom de la variable', type: 'text', required: true },
        { id: 'value', label: 'Valeur', type: 'text', required: true },
      ],
    },
  },
  {
    id: 'plex-refresh-library',
    name: 'Actualiser la bibliothèque Plex',
    description: 'Demande à Plex d’actualiser les éléments traités.',
    kind: 'action',
    mediaType: 'video',
    pluginId: 'plex',
    pluginName: 'Plex',
    outputs: COMMON_OUTPUTS,
  },
  {
    id: 'sonarr-refresh-series',
    name: 'Actualiser une série Sonarr',
    description: 'Synchronise le statut de la série après traitement.',
    kind: 'action',
    mediaType: 'video',
    pluginId: 'sonarr',
    pluginName: 'Sonarr',
    outputs: COMMON_OUTPUTS,
  },
  {
    id: 'radarr-refresh-movie',
    name: 'Actualiser un film Radarr',
    description: 'Synchronise le statut du film après traitement.',
    kind: 'action',
    mediaType: 'video',
    pluginId: 'radarr',
    pluginName: 'Radarr',
    outputs: COMMON_OUTPUTS,
  },
  {
    id: 'jellyfin-refresh-library',
    name: 'Actualiser la bibliothèque Jellyfin',
    description: 'Demande à Jellyfin d’actualiser les éléments traités.',
    kind: 'action',
    mediaType: 'video',
    pluginId: 'jellyfin',
    pluginName: 'Jellyfin',
    outputs: COMMON_OUTPUTS,
  },
];
