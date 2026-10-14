import type { FlowComponentDefinition } from './flow-component';

const MEDIA: Record<FlowComponentDefinition['mediaType'], { color: string; background: string }> = {
  video: { color: '#245b9b', background: '#edf3fc' },
  image: { color: '#67419b', background: '#f3edfa' },
  audio: { color: '#146c75', background: '#e9f6f7' },
  subtitles: { color: '#875600', background: '#fff5db' },
  general: { color: '#555e6b', background: '#f0f2f5' },
};
const PLUGINS: Record<string, string> = {
  base: '#365969',
  nodes: '#315c4d',
  plex: '#805700',
  sonarr: '#14667c',
  radarr: '#755300',
  jellyfin: '#5b409e',
};
const EXTRA_COLORS = ['#365969', '#6b3fa0', '#14667c', '#8c3e76', '#805700'];
export function flowComponentColors(component: FlowComponentDefinition) {
  const hash = [...component.pluginId].reduce(
    (value, character) => (value * 31 + character.charCodeAt(0)) >>> 0,
    0,
  );
  return {
    ...MEDIA[component.mediaType],
    plugin: PLUGINS[component.pluginId] ?? EXTRA_COLORS[hash % EXTRA_COLORS.length],
  };
}
