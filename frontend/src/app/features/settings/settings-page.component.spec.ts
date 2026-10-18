import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { SettingsPageComponent } from './settings-page.component';

describe('SettingsPageComponent', () => {
  async function setup() {
    await TestBed.configureTestingModule({
      imports: [SettingsPageComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsPageComponent);
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('renders navigation tabs for Settings hub', async () => {
    const { element } = await setup();
    expect(element.textContent).toContain('Utilisateurs');
    expect(element.textContent).toContain('Jetons d’API (IA)');
    expect(element.textContent).toContain('Serveur MCP');
    expect(element.textContent).toContain('Système & Santé');
    expect(element.textContent).toContain('Webhooks & Intégrations');
  });

  it('renders default users tab first', async () => {
    const { element } = await setup();
    expect(element.querySelector('app-user-management-tab')).not.toBeNull();
  });

  it('switches to MCP server tab when selected', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    component['onTabChange']('mcp');
    fixture.detectChanges();

    expect(element.querySelector('app-mcp-server-tab')).not.toBeNull();
    expect(element.textContent).toContain('Serveur MCP');
  });

  it('switches to API tokens tab when selected', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    component['onTabChange']('tokens');
    fixture.detectChanges();

    expect(element.querySelector('app-api-token-tab')).not.toBeNull();
    expect(element.textContent).toContain('Jetons d’API Cluster');
  });
});
