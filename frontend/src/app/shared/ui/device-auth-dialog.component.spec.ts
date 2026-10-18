import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { DeviceAuthDialogComponent } from './device-auth-dialog.component';
import { AiToolsService } from '../services/ai-tools.service';
import { ClipboardService } from '../services/clipboard.service';
import { NotificationService } from '../services/notification.service';

describe('DeviceAuthDialogComponent', () => {
  const close = vi.fn();
  const status = vi.fn();
  const cancel = vi.fn();
  beforeEach(() => {
    close.mockReset();
    status.mockReset();
    cancel.mockReset();
    cancel.mockReturnValue(of({}));
  });
  async function setup(state: string) {
    status.mockReturnValue(of({ state }));
    await TestBed.configureTestingModule({
      imports: [DeviceAuthDialogComponent],
      providers: [
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            toolId: 'codex-cli',
            toolName: 'Codex',
            prompt: {
              userCode: 'ABCD-12345',
              verificationUri: 'https://auth.openai.com/codex/device',
              expiresInSeconds: 60,
              intervalSeconds: 5,
            },
          },
        },
        { provide: MatDialogRef, useValue: { close } },
        { provide: AiToolsService, useValue: { getDeviceAuthStatus: status, cancelAuth: cancel } },
        { provide: ClipboardService, useValue: { copy: vi.fn() } },
        { provide: NotificationService, useValue: { show: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(DeviceAuthDialogComponent);
    fixture.detectChanges();
    return fixture;
  }
  it('ne déclare aucun succès tant que le serveur attend, et annule à la fermeture', async () => {
    const fixture = await setup('pending');
    expect(fixture.componentInstance.isAuthorized()).toBe(false);
    expect(fixture.nativeElement.textContent).not.toContain('Simuler');
    fixture.componentInstance.close();
    expect(close).toHaveBeenCalledWith({ success: false });
    fixture.destroy();
    expect(cancel).toHaveBeenCalledWith('codex-cli');
  });
  it('ne confirme que le statut autorisé renvoyé par le serveur', async () => {
    const fixture = await setup('authorized');
    expect(close).toHaveBeenCalledWith({ success: true });
    fixture.destroy();
    expect(cancel).not.toHaveBeenCalled();
  });
  it('arrête le parcours lorsque le serveur refuse la connexion', async () => {
    const fixture = await setup('failed');
    expect(fixture.componentInstance.expired()).toBe(true);
    expect(close).not.toHaveBeenCalled();
    fixture.destroy();
  });
});
