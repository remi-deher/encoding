import type { FlowComponentDefinition } from './flow-component';

const TYPES: Record<FlowComponentDefinition['kind'], { color: string; background: string }> = {
  input: { color: '#16634d', background: '#e8f5ee' },
  action: { color: '#245b9b', background: '#edf3fc' },
  condition: { color: '#875600', background: '#fff5db' },
  variable: { color: '#67419b', background: '#f3edfa' },
  script: { color: '#8c3e76', background: '#f9edf5' },
  preparation: { color: '#146c75', background: '#e9f6f7' },
  execution: { color: '#925016', background: '#fff2e5' },
  terminal: { color: '#555e6b', background: '#f0f2f5' },
};
const PLUGINS: Record<string, string> = {
  base: '#365969',
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
    ...TYPES[component.kind],
    plugin: PLUGINS[component.pluginId] ?? EXTRA_COLORS[hash % EXTRA_COLORS.length],
  };
}
