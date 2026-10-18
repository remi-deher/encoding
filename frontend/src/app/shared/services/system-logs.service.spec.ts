import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SystemLogsService } from './system-logs.service';
import { SystemLogEntry, SystemLogOverviewResponse } from '../models/system-logs';

describe('SystemLogsService', () => {
  let service: SystemLogsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [SystemLogsService, provideHttpClient(), provideHttpClientTesting()],
    });

    service = TestBed.inject(SystemLogsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('queries logs with filter parameters', () => {
    const mockResponse: SystemLogOverviewResponse = {
      entries: [
        {
          id: 'log-1',
          timestamp: '2026-10-18T20:00:00Z',
          level: 'Warning',
          category: 'Encoding.Core',
          message: 'Test warning',
        },
      ],
      totalBuffered: 1,
    };

    service
      .getLogs({
        minLevel: 'Warning',
        category: 'Encoding.Core',
        search: 'warning',
        limit: 50,
      })
      .subscribe((res) => {
        expect(res.entries.length).toBe(1);
        expect(res.entries[0].message).toBe('Test warning');
        expect(res.totalBuffered).toBe(1);
      });

    const req = httpMock.expectOne((request) => {
      return (
        request.url === '/api/v1/system/logs' &&
        request.params.get('minLevel') === 'Warning' &&
        request.params.get('category') === 'Encoding.Core' &&
        request.params.get('search') === 'warning' &&
        request.params.get('limit') === '50'
      );
    });

    expect(req.request.method).toBe('GET');
    req.flush(mockResponse);
  });

  it('queries categories and unwraps the list', () => {
    service.getCategories().subscribe((categories) => {
      expect(categories).toEqual(['Encoding.Api', 'Encoding.Core']);
    });

    const req = httpMock.expectOne('/api/v1/system/logs/categories');
    expect(req.request.method).toBe('GET');
    req.flush({ categories: ['Encoding.Api', 'Encoding.Core'] });
  });

  it('queries categories with nodeId if specified', () => {
    service.getCategories('node-123').subscribe((categories) => {
      expect(categories).toEqual(['WorkerNode']);
    });

    const req = httpMock.expectOne(
      (request) =>
        request.url === '/api/v1/system/logs/categories' &&
        request.params.get('nodeId') === 'node-123',
    );
    expect(req.request.method).toBe('GET');
    req.flush({ categories: ['WorkerNode'] });
  });

  it('creates an EventSource stream for SSE', () => {
    const originalEventSource = (globalThis as unknown as { EventSource?: unknown }).EventSource;

    let createdUrl = '';
    class MockEventSource {
      onmessage: ((event: MessageEvent) => void) | null = null;
      onerror: ((error: unknown) => void) | null = null;
      closed = false;

      constructor(url: string) {
        createdUrl = url;
      }

      close() {
        this.closed = true;
      }
    }

    (globalThis as unknown as { EventSource: unknown }).EventSource = MockEventSource;

    try {
      const received: SystemLogEntry[] = [];
      const sub = service
        .createLogStream({ minLevel: 'Error', search: 'critical issue' })
        .subscribe((entry) => {
          received.push(entry);
        });

      expect(createdUrl).toContain('/api/v1/system/logs/stream');
      expect(createdUrl).toContain('minLevel=Error');
      expect(createdUrl).toContain('search=critical+issue');

      sub.unsubscribe();
    } finally {
      (globalThis as unknown as { EventSource?: unknown }).EventSource = originalEventSource;
    }
  });
});
