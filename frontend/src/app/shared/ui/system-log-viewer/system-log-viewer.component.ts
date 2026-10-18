import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  input,
  OnDestroy,
  OnInit,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { retry, Subscription, tap } from 'rxjs';
import { SystemLogEntry, SystemLogFilterQuery, SystemLogLevel } from '../../models/system-logs';
import { ClipboardService } from '../../services/clipboard.service';
import { FileDownload } from '../../services/file-download';
import { NotificationService } from '../../services/notification.service';
import { SystemLogsService } from '../../services/system-logs.service';

@Component({
  selector: 'app-system-log-viewer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatMenuModule],
  templateUrl: './system-log-viewer.component.html',
  styleUrl: './system-log-viewer.component.scss',
})
export class SystemLogViewerComponent implements OnInit, OnDestroy {
  private readonly systemLogsService = inject(SystemLogsService);
  private readonly clipboard = inject(ClipboardService);
  private readonly fileDownload = inject(FileDownload);
  private readonly notifications = inject(NotificationService);

  readonly nodeId = input<string | null>(null);
  readonly title = input<string>('Journaux système');
  readonly showToolbar = input<boolean>(true);
  readonly maxBufferSize = input<number>(1000);

  protected readonly terminalContainer = viewChild<ElementRef<HTMLElement>>('terminalContainer');

  protected readonly selectedLevel = signal<SystemLogLevel | 'All'>('All');
  protected readonly selectedCategory = signal<string>('');
  protected readonly searchText = signal<string>('');
  protected readonly availableCategories = signal<readonly string[]>([]);
  protected readonly displayedEntries = signal<readonly SystemLogEntry[]>([]);
  protected readonly isPaused = signal<boolean>(false);
  protected readonly pendingCount = signal<number>(0);
  protected readonly autoScrollEnabled = signal<boolean>(true);
  protected readonly isConnected = signal<boolean>(false);
  protected readonly isLoadingHistory = signal<boolean>(false);
  protected readonly expandedStackTraces = signal<ReadonlySet<string>>(new Set());

  private pendingBuffer: SystemLogEntry[] = [];
  private streamSub: Subscription | null = null;
  private historySub: Subscription | null = null;

  readonly levelOptions: readonly { value: SystemLogLevel | 'All'; label: string }[] = [
    { value: 'All', label: 'Tous' },
    { value: 'Information', label: 'Info+' },
    { value: 'Warning', label: 'Avert.+' },
    { value: 'Error', label: 'Erreurs+' },
    { value: 'Critical', label: 'Critiques' },
  ];

  ngOnInit(): void {
    this.loadCategories();
    this.reloadLogsAndStream();
  }

  ngOnDestroy(): void {
    this.closeStream();
    this.historySub?.unsubscribe();
  }

  loadCategories(): void {
    this.systemLogsService.getCategories(this.nodeId()).subscribe({
      next: (categories) => this.availableCategories.set(categories),
      error: () => {},
    });
  }

  setLevel(level: SystemLogLevel | 'All'): void {
    if (this.selectedLevel() === level) return;
    this.selectedLevel.set(level);
    this.reloadLogsAndStream();
  }

  onCategoryChange(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.selectedCategory.set(value);
    this.reloadLogsAndStream();
  }

  onSearchInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.searchText.set(value);
  }

  clearSearch(): void {
    this.searchText.set('');
  }

  togglePause(): void {
    if (this.isPaused()) {
      if (this.pendingBuffer.length > 0) {
        const merged = [...this.displayedEntries(), ...this.pendingBuffer];
        const trimmed = merged.slice(-this.maxBufferSize());
        this.displayedEntries.set(trimmed);
        this.pendingBuffer = [];
        this.pendingCount.set(0);
      }
      this.isPaused.set(false);
      if (this.autoScrollEnabled()) {
        setTimeout(() => this.scrollToBottom(), 0);
      }
    } else {
      this.isPaused.set(true);
    }
  }

  clearLogs(): void {
    this.displayedEntries.set([]);
    this.pendingBuffer = [];
    this.pendingCount.set(0);
  }

  async copyLogs(): Promise<void> {
    const text = this.serializeLogs();
    const copied = await this.clipboard.copy(text);
    if (copied) {
      this.notifications.show(
        `${this.displayedEntries().length} log(s) copié(s) dans le presse-papier.`,
      );
    } else {
      this.notifications.show('Impossible de copier les logs dans le presse-papier.');
    }
  }

  exportTxt(): void {
    const text = this.serializeLogs();
    const dateStr = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
    const name = `system-logs-${dateStr}.txt`;
    this.fileDownload.save(new Blob([text], { type: 'text/plain;charset=utf-8' }), name);
  }

  exportJson(): void {
    const json = JSON.stringify(this.displayedEntries(), null, 2);
    const dateStr = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-');
    const name = `system-logs-${dateStr}.json`;
    this.fileDownload.save(new Blob([json], { type: 'application/json;charset=utf-8' }), name);
  }

  toggleStackTrace(id: string): void {
    const current = new Set(this.expandedStackTraces());
    if (current.has(id)) {
      current.delete(id);
    } else {
      current.add(id);
    }
    this.expandedStackTraces.set(current);
  }

  onConsoleScroll(): void {
    const el = this.terminalContainer()?.nativeElement;
    if (!el) return;
    const isNearBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= 40;
    this.autoScrollEnabled.set(isNearBottom);
  }

  scrollToBottom(): void {
    const el = this.terminalContainer()?.nativeElement;
    if (el) {
      el.scrollTop = el.scrollHeight;
      this.autoScrollEnabled.set(true);
    }
  }

  formatTime(isoString: string): string {
    try {
      const d = new Date(isoString);
      if (isNaN(d.getTime())) return isoString;
      const h = String(d.getHours()).padStart(2, '0');
      const m = String(d.getMinutes()).padStart(2, '0');
      const s = String(d.getSeconds()).padStart(2, '0');
      const ms = String(d.getMilliseconds()).padStart(3, '0');
      return `${h}:${m}:${s}.${ms}`;
    } catch {
      return isoString;
    }
  }

  levelBadge(level: SystemLogLevel): { text: string; cssClass: string } {
    switch (level) {
      case 'Critical':
        return { text: 'CRIT', cssClass: 'badge-crit' };
      case 'Error':
        return { text: 'ERR', cssClass: 'badge-err' };
      case 'Warning':
        return { text: 'WARN', cssClass: 'badge-warn' };
      case 'Information':
        return { text: 'INFO', cssClass: 'badge-info' };
      case 'Debug':
        return { text: 'DEBUG', cssClass: 'badge-debug' };
      case 'Trace':
        return { text: 'TRACE', cssClass: 'badge-trace' };
      default:
        return { text: 'LOG', cssClass: 'badge-info' };
    }
  }

  highlightParts(text: string, search: string): readonly { text: string; match: boolean }[] {
    if (!search || !text) return [{ text, match: false }];
    const escaped = search.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const regex = new RegExp(`(${escaped})`, 'gi');
    const tokens = text.split(regex);
    return tokens
      .filter((t) => t.length > 0)
      .map((t) => ({ text: t, match: t.toLowerCase() === search.toLowerCase() }));
  }

  private serializeLogs(): string {
    return this.displayedEntries()
      .map((e) => {
        const prefix = `[${e.timestamp}] [${e.level.toUpperCase()}] [${e.category}]${e.nodeName ? ' [@' + e.nodeName + ']' : ''}`;
        const msg = `${prefix} ${e.message}`;
        return e.exception ? `${msg}\n${e.exception}` : msg;
      })
      .join('\n');
  }

  private reloadLogsAndStream(): void {
    this.closeStream();
    this.historySub?.unsubscribe();
    this.displayedEntries.set([]);
    this.pendingBuffer = [];
    this.pendingCount.set(0);

    const minLevel =
      this.selectedLevel() === 'All' ? undefined : (this.selectedLevel() as SystemLogLevel);
    const category = this.selectedCategory() || undefined;
    const nodeId = this.nodeId() || undefined;

    const query: SystemLogFilterQuery = {
      minLevel,
      category,
      nodeId,
      limit: 200,
    };

    this.isLoadingHistory.set(true);
    this.historySub = this.systemLogsService.getLogs(query).subscribe({
      next: (res) => {
        const unique = new Map(
          [...res.entries, ...this.displayedEntries()].map((entry) => [entry.id, entry]),
        );
        const entries = [...unique.values()].sort((a, b) => a.timestamp.localeCompare(b.timestamp));
        this.displayedEntries.set(entries.slice(-this.maxBufferSize()));
        this.isLoadingHistory.set(false);
        if (this.autoScrollEnabled()) {
          setTimeout(() => this.scrollToBottom(), 0);
        }
      },
      error: () => {
        this.notifications.show(
          'Impossible de charger les journaux système. Vérifiez votre accès administrateur.',
        );
        this.isLoadingHistory.set(false);
      },
    });

    this.openStream({ minLevel, category, nodeId });
  }

  private openStream(filter: {
    minLevel?: SystemLogLevel;
    category?: string;
    nodeId?: string;
  }): void {
    this.isConnected.set(false);
    this.streamSub = this.systemLogsService
      .createLogStream(filter, (connected) => this.isConnected.set(connected))
      .pipe(tap({ error: () => this.isConnected.set(false) }), retry({ delay: 3000 }))
      .subscribe({
        next: (entry) => {
          this.isConnected.set(true);
          if (this.isPaused()) {
            if (
              this.pendingBuffer.some((item) => item.id === entry.id) ||
              this.displayedEntries().some((item) => item.id === entry.id)
            )
              return;
            this.pendingBuffer.push(entry);
            this.pendingBuffer = this.pendingBuffer.slice(-this.maxBufferSize());
            this.pendingCount.set(this.pendingBuffer.length);
          } else {
            const current = this.displayedEntries();
            if (current.some((item) => item.id === entry.id)) return;
            const next = [...current, entry].sort((a, b) => a.timestamp.localeCompare(b.timestamp));
            if (next.length > this.maxBufferSize()) {
              this.displayedEntries.set(next.slice(-this.maxBufferSize()));
            } else {
              this.displayedEntries.set(next);
            }
            if (this.autoScrollEnabled()) {
              setTimeout(() => this.scrollToBottom(), 0);
            }
          }
        },
        error: () => {
          this.isConnected.set(false);
        },
      });
  }

  private closeStream(): void {
    if (this.streamSub) {
      this.streamSub.unsubscribe();
      this.streamSub = null;
    }
    this.isConnected.set(false);
  }
}
