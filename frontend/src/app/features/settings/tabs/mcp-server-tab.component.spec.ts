import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { McpServerTabComponent } from './mcp-server-tab.component';
import { McpService } from '../../../shared/services/mcp.service';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { ApiTokenResponse } from '../../../shared/models/api-token';

describe('McpServerTabComponent', () => {
  let mcpServiceMock: { listTools: ReturnType<typeof vi.fn> };
  let clipboardServiceMock: { copy: ReturnType<typeof vi.fn> };
  let notificationServiceMock: { show: ReturnType<typeof vi.fn> };

  const sampleTokens: ApiTokenResponse[] = [
    {
      id: '11111111-1111-1111-1111-111111111111',
      name: 'Agent Claude',
      tokenPrefix: 'enc_pat_clau',
      role: 'admin',
      isActive: true,
      createdAt: '2026-10-18T10:00:00Z',
    },
  ];

  async function setup(tokens: ApiTokenResponse[] = sampleTokens) {
    mcpServiceMock = {
      listTools: vi.fn().mockReturnValue(
        of([
          {
            name: 'get_library_by_id',
            description: 'Récupère une bibliothèque par son identifiant.',
            inputSchema: {
              type: 'object',
              properties: {
                library_id: { type: 'string', description: 'ID de la bibliothèque' },
              },
              required: ['library_id'],
            },
          },
        ]),
      ),
    };

    clipboardServiceMock = {
      copy: vi.fn().mockResolvedValue(true),
    };

    notificationServiceMock = {
      show: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [McpServerTabComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: McpService, useValue: mcpServiceMock },
        { provide: ClipboardService, useValue: clipboardServiceMock },
        { provide: NotificationService, useValue: notificationServiceMock },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(McpServerTabComponent);
    fixture.componentRef.setInput('tokens', tokens);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return {
      fixture,
      component: fixture.componentInstance,
      element: fixture.nativeElement as HTMLElement,
    };
  }

  it('renders tab title and does NOT display noisy status pill or tester button', async () => {
    const { element } = await setup();

    expect(element.textContent).toContain('Serveur MCP');
    expect(element.textContent).toContain('Configuration client');
    // Vérifier l'absence de badge de statut et de bouton Tester pour une interface épurée
    expect(element.querySelector('.mcp-status-pill')).toBeNull();
    expect(element.textContent).not.toContain('Tester');
    expect(element.textContent).not.toContain('Hors ligne');
  });

  it('renders available tools from MCP catalog', async () => {
    const { component, fixture, element } = await setup();
    await component.loadTools();
    fixture.detectChanges();

    expect(element.textContent).toContain('Outils disponibles (1)');
    expect(element.textContent).toContain('get_library_by_id');
    expect(element.textContent).toContain('library_id');
  });

  it('copies configuration using ClipboardService when button is clicked', async () => {
    const { element } = await setup();
    const copyButton = element.querySelector<HTMLButtonElement>('.token-selector-row button');
    expect(copyButton).not.toBeNull();
    expect(copyButton?.textContent).toContain('Copier la configuration');

    copyButton?.click();
    // Allow async promise to resolve
    await Promise.resolve();

    expect(clipboardServiceMock.copy).toHaveBeenCalled();
    expect(notificationServiceMock.show).toHaveBeenCalledWith(
      'Configuration copiée dans le presse-papier.',
    );
  });
});
