import { DOCUMENT } from '@angular/common';
import { Injectable, computed, inject, signal } from '@angular/core';

export type ThemeMode = 'light' | 'dark';
export type ThemePalette = 'forest' | 'ocean' | 'iris' | 'rose';

const THEME_STORAGE_KEY = 'encoding.theme';
const PALETTE_STORAGE_KEY = 'encoding.palette';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly mode = signal<ThemeMode>(this.initialMode());
  private readonly selectedPalette = signal<ThemePalette>(this.initialPalette());

  readonly current = this.mode.asReadonly();
  readonly isDark = computed(() => this.mode() === 'dark');
  readonly palette = this.selectedPalette.asReadonly();

  constructor() {
    this.apply(this.mode(), this.palette());
  }

  toggle(): void {
    this.set(this.isDark() ? 'light' : 'dark');
  }

  set(mode: ThemeMode): void {
    this.mode.set(mode);
    this.apply(mode, this.palette());
    try {
      localStorage.setItem(THEME_STORAGE_KEY, mode);
    } catch {
      // Stockage indisponible : le choix reste valable pour la session.
    }
  }

  setPalette(palette: ThemePalette): void {
    this.selectedPalette.set(palette);
    this.apply(this.mode(), palette);
    try {
      localStorage.setItem(PALETTE_STORAGE_KEY, palette);
    } catch {
      // Stockage indisponible : le choix reste valable pour la session.
    }
  }

  private initialMode(): ThemeMode {
    try {
      const stored = localStorage.getItem(THEME_STORAGE_KEY);
      if (stored === 'light' || stored === 'dark') return stored;
    } catch {
      // Stockage indisponible : repli sur la préférence système.
    }
    return this.document.defaultView?.matchMedia?.('(prefers-color-scheme: dark)').matches
      ? 'dark'
      : 'light';
  }

  private initialPalette(): ThemePalette {
    try {
      const stored = localStorage.getItem(PALETTE_STORAGE_KEY);
      if (stored === 'forest' || stored === 'ocean' || stored === 'iris' || stored === 'rose') {
        return stored;
      }
    } catch {
      // Repli sur la palette Forêt quand le stockage est indisponible.
    }
    return 'forest';
  }

  private apply(mode: ThemeMode, palette: ThemePalette): void {
    this.document.documentElement.dataset['theme'] = mode;
    this.document.documentElement.dataset['palette'] = palette;
  }
}
