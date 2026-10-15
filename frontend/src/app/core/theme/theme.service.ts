import { DOCUMENT } from '@angular/common';
import { Injectable, computed, inject, signal } from '@angular/core';

export type ThemeMode = 'light' | 'dark';

const STORAGE_KEY = 'encoding.theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly mode = signal<ThemeMode>(this.initialMode());

  readonly current = this.mode.asReadonly();
  readonly isDark = computed(() => this.mode() === 'dark');

  constructor() {
    this.apply(this.mode());
  }

  toggle(): void {
    this.set(this.isDark() ? 'light' : 'dark');
  }

  set(mode: ThemeMode): void {
    this.mode.set(mode);
    this.apply(mode);
    try {
      localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      // Stockage indisponible : le choix reste valable pour la session.
    }
  }

  private initialMode(): ThemeMode {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (stored === 'light' || stored === 'dark') return stored;
    } catch {
      // Stockage indisponible : repli sur la préférence système.
    }
    return this.document.defaultView?.matchMedia?.('(prefers-color-scheme: dark)').matches
      ? 'dark'
      : 'light';
  }

  private apply(mode: ThemeMode): void {
    this.document.documentElement.dataset['theme'] = mode;
  }
}
