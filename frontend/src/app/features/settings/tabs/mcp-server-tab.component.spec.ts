import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { McpServerTabComponent } from './mcp-server-tab.component';
import { McpService } from '../../../shared/services/mcp.service';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { SettingsEditor } from '../settings-editor';

describe('McpServerTabComponent', () => {
  const copy = vi.fn().mockResolvedValue(true);
  async function setup() {
    await TestBed.configureTestingModule({
      imports: [McpServerTabComponent],
      providers: [
        {
          provide: McpService,
          useValue: {
            listTools: () =>
              of([
                {
                  name: 'get_library_by_id',
                  description: 'Récupère une bibliothèque.',
                  inputSchema: {
                    type: 'object',
                    properties: { library_id: { type: 'string' } },
                    required: ['library_id'],
                  },
                },
              ]),
          },
        },
        { provide: ClipboardService, useValue: { copy } },
        { provide: NotificationService, useValue: { show: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(McpServerTabComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return {
      fixture,
      component: fixture.componentInstance,
      element: fixture.nativeElement as HTMLElement,
    };
  }
  it('shows the endpoint and the real catalog without a second navigation shell', async () => {
    const { element } = await setup();
    expect(element.textContent).toContain('Serveur MCP');
    expect(element.textContent).toContain('/api/v1/mcp/sse');
    expect(element.textContent).toContain('get_library_by_id');
    expect(element.querySelector('[role="tablist"]')).toBeNull();
  });
  it('never substitutes a public token prefix for a full secret', async () => {
    const { component } = await setup();
    expect(component['configSnippet']()).toContain('<JETON_API_COMPLET>');
    component['clientToken'].set('enc_pat_full_private_value');
    await component['copyConfigSnippet']();
    expect(copy).toHaveBeenCalledWith(expect.stringContaining('Bearer enc_pat_full_private_value'));
  });
  it('clears a pasted client secret when its panel closes', async () => {
    const { component } = await setup();
    component['openClient']();
    await Promise.resolve();
    component['clientToken'].set('temporary_secret');
    await TestBed.inject(SettingsEditor).close();
    expect(component['clientToken']()).toBe('');
  });
});
