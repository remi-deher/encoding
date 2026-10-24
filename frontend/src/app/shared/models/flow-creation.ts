import type { RecordFormData } from '../ui/record-form.component';

export const FLOW_CREATION_FORM: RecordFormData = {
  title: 'Créer un flow',
  description: 'Nommez votre flow, choisissez son type et son mode de création.',
  submitLabel: 'Continuer dans l’éditeur',
  reviewTitle: 'Vérifier le nouveau flow',
  fields: [
    { id: 'name', label: 'Nom du flow', type: 'text', required: true, maxLength: 128 },
    {
      id: 'mediaType',
      label: 'Type de média',
      type: 'select',
      required: true,
      options: [
        { value: 'video', label: 'Vidéo' },
        { value: 'image', label: 'Image' },
        { value: 'music', label: 'Musique' },
        { value: 'subtitles', label: 'Sous-titres' },
      ],
    },
    {
      id: 'starter',
      label: 'Modèle ou flow vierge',
      type: 'select',
      required: true,
      filterOptionsByFieldId: 'mediaType',
      options: [
        { value: 'guided', label: 'Assistant vidéo — création guidée', mediaTypes: ['video'] },
        { value: 'blank', label: 'Flow vierge' },
        { value: 'flow-video-library', label: 'Films — H.265', mediaTypes: ['video'] },
        { value: 'flow-video-series', label: 'Séries — H.265', mediaTypes: ['video'] },
        {
          value: 'flow-video-source-check',
          label: 'Vérification de la source',
          mediaTypes: ['video'],
        },
        { value: 'flow-image-webp', label: 'Conversion WebP', mediaTypes: ['image'] },
        { value: 'flow-music-flac', label: 'Vérification FLAC', mediaTypes: ['music'] },
        {
          value: 'flow-subtitles-cleanup',
          label: 'Nettoyage des sous-titres',
          mediaTypes: ['subtitles'],
        },
      ],
    },
  ],
  steps: [
    { id: 'information', label: 'Informations', fieldIds: ['name', 'mediaType'] },
    {
      id: 'creation',
      label: 'Démarrage',
      description: 'Choisissez comment commencer votre flow.',
      fieldIds: ['starter'],
    },
  ],
  initialValues: { mediaType: 'video', starter: 'guided' },
};
