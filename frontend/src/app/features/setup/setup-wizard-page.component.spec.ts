import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { signal } from '@angular/core';
import { SetupWizardPageComponent } from './setup-wizard-page.component';
import { AuthService } from '../../core/auth/auth.service';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { NotificationService } from '../../shared/services/notification.service';
import { LocalNodeStatus } from '../../shared/models/local-node';

describe('SetupWizardPageComponent', () => {
  let setupFn: ReturnType<typeof vi.fn>;
  let navigateFn: ReturnType<typeof vi.fn>;
  let statusSignal: ReturnType<typeof signal<LocalNodeStatus | null>>;

  beforeEach(() => {
    setupFn = vi.fn().mockResolvedValue(undefined);
    navigateFn = vi.fn().mockResolvedValue(true);
    statusSignal = signal<LocalNodeStatus | null>({
      nodeId: 'node-1',
      name: 'srv-transcode',
      displayName: 'Serveur Salon',
      roles: ['server', 'worker'],
      maxConcurrentTasks: 2,
      automaticEnrollmentEnabled: true,
      capabilities: [
        {
          toolId: 'ffmpeg',
          version: '7.1',
          accelerationTypes: ['nvenc'],
          mediaTypes: ['video', 'audio'],
        },
      ],
      inventory: {
        processorCount: 16,
        memoryMb: 32768,
        operatingSystem: 'Linux',
        architecture: 'x64',
        processorModel: 'AMD Ryzen 7 5800X',
        gpus: [{ name: 'NVIDIA GeForce RTX 3080', device: 'gpu0' }],
      },
      isEnrolled: true,
      pairingKey: null,
      pairingExpiresAt: null,
      clusterId: null,
      clusterName: null,
      verificationCode: null,
      switching: null,
      switchFailure: null,
    });

    TestBed.configureTestingModule({
      imports: [SetupWizardPageComponent],
      providers: [
        {
          provide: AuthService,
          useValue: { setup: setupFn },
        },
        {
          provide: Router,
          useValue: { navigate: navigateFn },
        },
        {
          provide: NotificationService,
          useValue: { show: vi.fn() },
        },
      ],
    });

    TestBed.overrideComponent(SetupWizardPageComponent, {
      set: {
        providers: [
          {
            provide: LocalNodeController,
            useValue: {
              status: statusSignal,
              clusters: signal([]),
              joinableClusters: signal([]),
              target: signal(null),
              pairing: signal(null),
              busy: signal(false),
              error: signal(''),
              verifyAddress: vi.fn(),
              sendRequest: vi.fn(),
            },
          },
        ],
      },
    });
  });

  it('renders step 1 with detected node info and hardware', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Bienvenue sur Encoding');
    expect(el.textContent).toContain('AMD Ryzen 7 5800X');
    expect(el.textContent).toContain('32 Go');
    expect(el.textContent).toContain('NVIDIA GeForce RTX 3080');
    expect(el.textContent).toContain('Accélération GPU');
    expect(el.textContent).toContain('NVENC');
  });

  it('advances across steps when clicking Continuer and Suivant', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    const continueBtn = Array.from(el.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Continuer'),
    );
    expect(continueBtn).toBeTruthy();
    continueBtn!.click();
    fixture.detectChanges();

    expect(fixture.componentInstance['currentStep']()).toBe(1);
    expect(el.textContent).toContain('Comment souhaitez-vous utiliser cette machine ?');

    const nextBtn = Array.from(el.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Suivant'),
    );
    expect(nextBtn).toBeTruthy();
    nextBtn!.click();
    fixture.detectChanges();

    expect(fixture.componentInstance['currentStep']()).toBe(2);
    expect(el.textContent).toContain('Prêt à démarrer en mode autonome');
  });

  it('allows selecting operation modes', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const component = fixture.componentInstance;

    expect(component['selectedMode']()).toBe('standalone');
    component['selectedMode'].set('createCluster');
    expect(component['selectedMode']()).toBe('createCluster');
    component['selectedMode'].set('joinCluster');
    expect(component['selectedMode']()).toBe('joinCluster');
  });

  it('submits standalone mode and redirects to root', async () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const component = fixture.componentInstance;

    await component['submitStandalone']();

    expect(setupFn).toHaveBeenCalledWith({
      nodeDisplayName: 'Serveur Salon',
      mode: 'standalone',
    });
    expect(navigateFn).toHaveBeenCalledWith(['/']);
  });

  it('submits createCluster mode with cluster name', async () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const component = fixture.componentInstance;

    component['selectedMode'].set('createCluster');
    component['clusterName'].set('Mon Cluster Maison');
    await component['submitCreateCluster']();

    expect(setupFn).toHaveBeenCalledWith({
      nodeDisplayName: 'Serveur Salon',
      mode: 'createCluster',
      clusterName: 'Mon Cluster Maison',
    });
    expect(navigateFn).toHaveBeenCalledWith(['/']);
  });
});
