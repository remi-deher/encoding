import { Injectable, signal } from '@angular/core';
@Injectable({ providedIn: 'root' })
export class PageTitle {
  readonly current = signal<{
    readonly owner: object;
    readonly url: string;
    readonly title: string;
  } | null>(null);
  set(owner: object, url: string, title: string): void {
    this.current.set({ owner, url, title });
  }
  clear(owner: object): void {
    if (this.current()?.owner === owner) this.current.set(null);
  }
}
