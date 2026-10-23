import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { signal } from '@angular/core';
import { SetupWizardPageComponent } from './setup-wizard-page.component';
import { AuthService } from '../../core/auth/auth.service';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { NotificationService } from '../../shared/services/notification.service';
import { LocalNodeStatus } from '../../shared/models/local-node';

const VALID_PASSWORD = 'un-mot-de-passe-long';

function fillAdmin(component: SetupWizardPageComponent): void {
  component['adminPassword'].set(VALID_PASSWORD);
  component['adminConfirm'].set(VALID_PASSWORD);
}

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

  it('displays anonymous setup node sheet with IP address, announced URL and hardware when session is not yet created', () => {
    statusSignal.set(null);
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.componentInstance['setupSheet'].set({
      name: 'encoding-host',
      displayName: 'Nœud Salon',
      agentAvailable: true,
      inventory: {
        operatingSystem: 'Ubuntu 24.04 LTS',
        architecture: 'X64',
        processorCount: 8,
        memoryMb: 16384,
        processorModel: 'Intel Core i5-12400',
        gpus: [{ name: 'Intel UHD Graphics 730', vendor: 'Intel', driver: 'i915', device: 'card0' }],
      },
      tools: [
        {
          toolId: 'ffmpeg',
          accelerationTypes: ['qsv'],
          encoderChecks: [{ encoderId: 'hevc_qsv', status: 'usable' }],
        },
      ],
      addresses: [{ interface: 'eth0', address: '192.168.1.42' }],
      urls: ['https://transcodage.deher.net'],
    });
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Nœud Salon');
    expect(el.textContent).toContain('encoding-host · Ubuntu 24.04 LTS (X64)');
    expect(el.textContent).toContain('192.168.1.42 (eth0)');
    expect(el.textContent).toContain('URL annoncée : https://transcodage.deher.net');
    expect(el.textContent).toContain('Intel Core i5-12400 (8 cœurs)');
    expect(el.textContent).toContain('16 Go');
    expect(el.textContent).toContain('Intel UHD Graphics 730');
    expect(el.textContent).toContain('QSV');
  });

  it('renders the contextual panel dynamically when switching operation modes', () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent).toContain('Prêt à démarrer en mode autonome');

    const modeRadios = Array.from(el.querySelectorAll('[role="radio"]')) as HTMLButtonElement[];
    expect(modeRadios.length).toBe(3);

    modeRadios[1].click();
    fixture.detectChanges();
    expect(fixture.componentInstance['selectedMode']()).toBe('createCluster');
    expect(el.textContent).toContain('Nommer votre nouveau cluster');

    modeRadios[2].click();
    fixture.detectChanges();
    expect(fixture.componentInstance['selectedMode']()).toBe('joinCluster');
    expect(el.textContent).toContain('Connexion au cluster d’accueil');
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
    fillAdmin(component);

    await component['submitStandalone']();

    expect(setupFn).toHaveBeenCalledWith({
      nodeDisplayName: 'Serveur Salon',
      mode: 'standalone',
      username: 'admin',
      password: VALID_PASSWORD,
    });
    expect(navigateFn).toHaveBeenCalledWith(['/']);
  });

  it('submits createCluster mode with cluster name', async () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const component = fixture.componentInstance;

    fillAdmin(component);
    component['selectedMode'].set('createCluster');
    component['clusterName'].set('Mon Cluster Maison');
    await component['submitCreateCluster']();

    expect(setupFn).toHaveBeenCalledWith({
      nodeDisplayName: 'Serveur Salon',
      mode: 'createCluster',
      clusterName: 'Mon Cluster Maison',
      username: 'admin',
      password: VALID_PASSWORD,
    });
    expect(navigateFn).toHaveBeenCalledWith(['/']);
  });

  it('passes custom nodeName when requesting cluster join and finishing setup', async () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const localController = fixture.debugElement.injector.get(LocalNodeController);

    component['nodeName'].set('Worker Garage');
    component['selectedMode'].set('joinCluster');
    await component['requestClusterJoin']();

    expect(localController.sendRequest).toHaveBeenCalledWith('Worker Garage');

    fillAdmin(component);
    await component['submitJoinFinish']();
    expect(setupFn).toHaveBeenCalledWith({
      nodeDisplayName: 'Worker Garage',
      mode: 'joinCluster',
      username: 'admin',
      password: VALID_PASSWORD,
    });
  });

  it('refuses to finish without a valid administrator account', async () => {
    const fixture = TestBed.createComponent(SetupWizardPageComponent);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const el = fixture.nativeElement as HTMLElement;

    await component['submitStandalone']();
    fixture.detectChanges();
    expect(setupFn).not.toHaveBeenCalled();
    expect(el.textContent).toContain('au moins 12 caractères');

    component['adminPassword'].set('court');
    component['adminConfirm'].set('court');
    await component['submitStandalone']();
    expect(setupFn).not.toHaveBeenCalled();

    component['adminPassword'].set(VALID_PASSWORD);
    component['adminConfirm'].set('autre-mot-de-passe');
    await component['submitStandalone']();
    fixture.detectChanges();
    expect(setupFn).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Les deux mots de passe sont différents.');

    component['adminConfirm'].set(VALID_PASSWORD);
    component['adminUsername'].set('nom invalide!');
    await component['submitStandalone']();
    expect(setupFn).not.toHaveBeenCalled();
  });
});
