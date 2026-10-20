import { ChangeDetectionStrategy, Component, input } from '@angular/core';

const ICONS = {
  upload: 'M12 16V3m-5 5 5-5 5 5M4 15v6h16v-6',
  copy: 'M9 9h12v12H9ZM15 5V3H3v12h2',
  refresh: 'M20 7v5h-5M4 17v-5h5M6 7a7 7 0 0 1 12-2l2 3M4 16l2 3a7 7 0 0 0 12-2',
  server: 'M3 3h18v7H3Zm0 11h18v7H3ZM7 6h.01M7 17h.01',
  terminal: 'm5 7 4 5-4 5M12 17h7',
  api: 'm8 5-6 7 6 7m8-14 6 7-6 7M14 3l-4 18',
  back: 'M19 12H5m7-7-7 7 7 7',
  forward: 'M5 12h14m-7-7 7 7-7 7',
  outward: 'M7 17 17 7M7 7h10v10',
  undo: 'M9 5 4 10l5 5M4 10h10a6 6 0 0 1 0 12',
  redo: 'm15 5 5 5-5 5m5-5H10a6 6 0 0 0 0 12',
  up: 'm5 15 7-7 7 7',
  down: 'm5 9 7 7 7-7',
  left: 'm15 5-7 7 7 7',
  right: 'm9 5 7 7-7 7',
  more: 'M5 12h.01M12 12h.01M19 12h.01',
  add: 'M12 5v14M5 12h14',
  close: 'm6 6 12 12M6 18 18 6',
  play: 'm8 5 11 7-11 7Z',
  check: 'm5 12 4 4L19 6',
  save: 'M5 3h12l4 4v14H3V3Zm2 0v7h10V3M7 21v-7h10v7',
  settings: 'M4 7h16M4 17h16M8 4v6m8 4v6',
  pack: 'm3 7 9-4 9 4v10l-9 4-9-4Zm0 0 9 4 9-4M12 11v10',
  link: 'm10 13 4-4M8 16l-2 2a4 4 0 0 1-6-6l4-4a4 4 0 0 1 6 0m4 0 2-2a4 4 0 0 1 6 6l-4 4a4 4 0 0 1-6 0',
  pin: 'M9 3h6l-1 6 4 4H6l4-4Zm3 10v8',
  edit: 'm4 16 12-12 4 4L8 20H4Zm10-10 4 4',
  sort: 'M8 4v16m-4-4 4 4 4-4M16 20V4m-4 4 4-4 4 4',
} as const;
export type IconName = keyof typeof ICONS;
@Component({
  selector: 'app-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true' },
  template:
    '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" focusable="false"><path [attr.d]="paths[name()]" /></svg>',
  styles:
    ':host { display: inline-flex; width: 20px; height: 20px; flex-shrink: 0; vertical-align: middle; } svg { width: 100%; height: 100%; }',
})
export class IconComponent {
  readonly name = input.required<IconName>();
  protected readonly paths = ICONS;
}
