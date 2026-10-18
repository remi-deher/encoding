import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { SystemLogViewerComponent } from './system-log-viewer.component';
import { SystemLogsService } from '../../services/system-logs.service';
import { ClipboardService } from '../../services/clipboard.service';
import { FileDownload } from '../../services/file-download';
import { NotificationService } from '../../services/notification.service';
import { SystemLogEntry } from '../../models/system-logs';

describe('SystemLogViewerComponent', () => {
  let mockLogsService: {
    getLogs: ReturnType<typeof vi.fn>;
    getCategories: ReturnType<typeof vi.fn>;
    createLogStream: ReturnType<typeof vi.fn>;
  };
  let mockClipboard: { copy: ReturnType<typeof vi.fn> };
  let mockFileDownload: { save: ReturnType<typeof vi.fn> };
  let mockNotifications: { show: ReturnType<typeof vi.fn> };
  let streamSubject: Subject<SystemLogEntry>;

  const sampleLog: SystemLogEntry = {
    id: 'log-1',
    timestamp: '2026-10-18T20:15:30.123Z',
    level: 'Information',
    category: 'Encoding.Api',
    message: 'Server started successfully',
  };

  const sampleErrorLog: SystemLogEntry = {
    id: 'log-2',
    timestamp: '2026-10-18T20:15:32.456Z',
    level: 'Error',
    category: 'Encoding.Core',
    message: 'Worker execution failed',
    exception: 'System.InvalidOperationException: Invalid state\n  at Encoding.Core...',
  };

  beforeEach(async () => {
    streamSubject = new Subject<SystemLogEntry>();

    mockLogsService = {
      getLogs: vi.fn().mockReturnValue(
        of({
          entries: [sampleLog],
          totalBuffered: 1,
        }),
      ),
      getCategories: vi.fn().mockReturnValue(of(['Encoding.Api', 'Encoding.Core'])),
      createLogStream: vi.fn().mockReturnValue(streamSubject.asObservable()),
    };

    mockClipboard = {
      copy: vi.fn().mockResolvedValue(true),
    };

    mockFileDownload = {
      save: vi.fn(),
    };

    mockNotifications = {
      show: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [SystemLogViewerComponent],
      providers: [
        { provide: SystemLogsService, useValue: mockLogsService },
        { provide: ClipboardService, useValue: mockClipboard },
        { provide: FileDownload, useValue: mockFileDownload },
        { provide: NotificationService, useValue: mockNotifications },
      ],
    }).compileComponents();
  });

  it('orders descending API history chronologically and ignores SSE replays', () => {
    mockLogsService.getLogs.mockReturnValue(
      of({ entries: [sampleErrorLog, sampleLog], totalBuffered: 2 }),
    );
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();
    expect(fixture.componentInstance['displayedEntries']().map((entry) => entry.id)).toEqual([
      'log-1',
      'log-2',
    ]);
    streamSubject.next(sampleLog);
    expect(fixture.componentInstance['displayedEntries']()).toHaveLength(2);
    fixture.destroy();
  });

  it('retains live entries received while the history request is pending', () => {
    const history = new Subject<{ entries: readonly SystemLogEntry[]; totalBuffered: number }>();
    mockLogsService.getLogs.mockReturnValue(history);
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();
    streamSubject.next(sampleErrorLog);
    history.next({ entries: [sampleLog], totalBuffered: 1 });
    expect(fixture.componentInstance['displayedEntries']().map((entry) => entry.id)).toEqual([
      'log-1',
      'log-2',
    ]);
    fixture.destroy();
  });

  it('renders toolbar, initial logs, and categories', () => {
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Tous');
    expect(compiled.textContent).toContain('Info+');
    expect(compiled.textContent).toContain('Server started successfully');
    expect(compiled.textContent).toContain('Encoding.Api');
  });

  it('appends streaming logs when stream emits and not paused', () => {
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();

    streamSubject.next(sampleErrorLog);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Worker execution failed');
    expect(compiled.textContent).toContain('ERR');
  });

  it('buffers logs when paused and flushes on resume', () => {
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    comp.togglePause();
    fixture.detectChanges();

    expect(comp['isPaused']()).toBe(true);

    streamSubject.next(sampleErrorLog);
    fixture.detectChanges();

    expect(comp['pendingCount']()).toBe(1);
    // Not yet displayed in console
    expect(comp['displayedEntries']().length).toBe(1);

    comp.togglePause();
    fixture.detectChanges();

    expect(comp['isPaused']()).toBe(false);
    expect(comp['pendingCount']()).toBe(0);
    expect(comp['displayedEntries']().length).toBe(2);
  });

  it('clears logs when clearLogs is called', () => {
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    expect(comp['displayedEntries']().length).toBe(1);

    comp.clearLogs();
    fixture.detectChanges();

    expect(comp['displayedEntries']().length).toBe(0);
  });

  it('copies logs to clipboard', async () => {
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    await comp.copyLogs();

    expect(mockClipboard.copy).toHaveBeenCalled();
    expect(mockNotifications.show).toHaveBeenCalledWith(
      expect.stringContaining('copié(s) dans le presse-papier'),
    );
  });

  it('exports logs to txt and json files', () => {
    const fixture = TestBed.createComponent(SystemLogViewerComponent);
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    comp.exportTxt();
    expect(mockFileDownload.save).toHaveBeenCalledWith(
      expect.any(Blob),
      expect.stringMatching(/^system-logs-.*\.txt$/),
    );

    comp.exportJson();
    expect(mockFileDownload.save).toHaveBeenCalledWith(
      expect.any(Blob),
      expect.stringMatching(/^system-logs-.*\.json$/),
    );
  });
});
