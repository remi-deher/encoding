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

  it('renders 3 main domains for Settings hub', async () => {
    const { element } = await setup();
    expect(element.textContent).toContain('Accès & Sécurité');
    expect(element.textContent).toContain('Intégrations & MCP');
    expect(element.textContent).toContain('Système & Apparence');
  });

  it('renders API tokens subtab by default under Accès & Sécurité', async () => {
    const { element } = await setup();
    expect(element.querySelector('app-api-token-tab')).not.toBeNull();
    expect(element.textContent).toContain('Jetons d’API');
  });

  it('switches to Users subtab when clicked', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    component['onSubTabChange']('users');
    fixture.detectChanges();

    expect(element.querySelector('app-user-management-tab')).not.toBeNull();
    expect(element.textContent).toContain('Comptes utilisateurs');
  });

  it('switches to Integrations tab when selected', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    component['onMainTabChange']('integrations');
    fixture.detectChanges();

    expect(element.querySelector('app-mcp-server-tab')).not.toBeNull();
    expect(element.textContent).toContain('Serveur MCP');
  });

  it('switches to System tab when selected', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    component['onMainTabChange']('system');
    fixture.detectChanges();

    expect(element.querySelector('app-system-tab')).not.toBeNull();
    expect(element.textContent).toContain('Thème visuel');
  });

  it('switches to System Logs subtab under System', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    component['onMainTabChange']('system');
    component['onSubTabChange']('logs');
    fixture.detectChanges();

    expect(element.querySelector('app-system-log-viewer')).not.toBeNull();
    expect(element.textContent).toContain('Journaux système');
  });
});
