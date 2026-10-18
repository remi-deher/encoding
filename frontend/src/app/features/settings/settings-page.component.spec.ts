import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Subject } from 'rxjs';
import { SettingsPageComponent } from './settings-page.component';

describe('SettingsPageComponent', () => {
  async function setup() {
    await TestBed.configureTestingModule({
      imports: [SettingsPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    await TestBed.inject(Router).navigateByUrl('/');
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
    await component['onSubTabChange']('users');
    fixture.detectChanges();

    expect(element.querySelector('app-user-management-tab')).not.toBeNull();
    expect(element.textContent).toContain('Comptes utilisateurs');
  });

  it('switches to Integrations tab when selected', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    await component['onMainTabChange']('integrations');
    fixture.detectChanges();

    expect(element.querySelector('app-mcp-server-tab')).not.toBeNull();
    expect(element.textContent).toContain('Serveur MCP');
  });

  it('switches to System tab when selected', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    await component['onMainTabChange']('system');
    fixture.detectChanges();

    expect(element.querySelector('app-system-tab')).not.toBeNull();
    expect(element.textContent).toContain('Thème visuel');
  });

  it('switches to System Logs subtab under System', async () => {
    const { fixture, element } = await setup();
    const component = fixture.componentInstance;
    await component['onMainTabChange']('system');
    await component['onSubTabChange']('logs');
    fixture.detectChanges();

    expect(element.querySelector('app-system-log-viewer')).not.toBeNull();
    expect(element.textContent).toContain('Journaux système');
  });

  it.each([false, true])(
    'attend la confirmation avant de quitter les réglages modifiés (confirmation : %s)',
    async (confirmed) => {
      const { fixture, element } = await setup();
      const component = fixture.componentInstance;
      const decision = new Subject<boolean>();
      vi.spyOn(component, 'hasUnsavedChanges').mockReturnValue(true);
      const open = vi.spyOn(fixture.debugElement.injector.get(MatDialog), 'open').mockReturnValue({
        afterClosed: () => decision,
      } as unknown as MatDialogRef<unknown>);

      const navigation = component['onMainTabChange']('system');
      fixture.detectChanges();
      expect(open).toHaveBeenCalledOnce();
      expect(element.querySelector('app-api-token-tab')).not.toBeNull();
      expect(element.querySelector('app-system-tab')).toBeNull();

      decision.next(confirmed);
      decision.complete();
      await navigation;
      fixture.detectChanges();

      expect(element.querySelector('app-system-tab') !== null).toBe(confirmed);
      expect(element.querySelector('app-api-token-tab') !== null).toBe(!confirmed);
    },
  );
});
