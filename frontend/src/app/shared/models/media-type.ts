export type MediaType = 'video' | 'image' | 'music' | 'subtitles';

export interface MediaTypeOption {
  readonly value: MediaType;
  readonly label: string;
}

export const MEDIA_TYPE_OPTIONS: readonly MediaTypeOption[] = [
  { value: 'video', label: 'Vidéo' },
  { value: 'image', label: 'Image' },
  { value: 'music', label: 'Musique' },
  { value: 'subtitles', label: 'Sous-titres' },
];
