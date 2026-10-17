import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { AuthService } from '../../core/auth/auth.service';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { NotificationService } from '../../shared/services/notification.service';
import { formatMemory } from '../../shared/models/node-hardware';

export type SetupOperationMode = 'standalone' | 'createCluster' | 'joinCluster';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-setup-wizard-page',
  providers: [LocalNodeController],
  imports: [CommonModule, FormsModule, MatButtonModule, MatProgressBarModule],
  template: `
    <section class="setup-studio" aria-label="Assistant de premier démarrage">
      @if (busy()) {
        <mat-progress-bar
          class="setup-progress"
          mode="indeterminate"
          aria-label="Action en cours"
        />
      }

      @if (errorMessage()) {
        <div role="alert" class="setup-error">
          <svg
            viewBox="0 0 24 24"
            width="20"
            height="20"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            aria-hidden="true"
          >
            <circle cx="12" cy="12" r="10" />
            <path d="M12 8v4m0 4h.01" stroke-linecap="round" />
          </svg>
          <span>{{ errorMessage() }}</span>
        </div>
      }

      <div class="setup-grid">
        <!-- Colonne gauche : Fiche d'identité live & télémétrie matérielle -->
        <aside class="setup-telemetry" aria-label="Identité et matériel détecté">
          <header class="setup-brand">
            <div class="brand-badge">
              <span class="brand-mark" aria-hidden="true">
                <svg
                  viewBox="0 0 24 24"
                  width="18"
                  height="18"
                  fill="none"
                  stroke="currentColor"
                  stroke-width="2.2"
                >
                  <polygon points="6 4 20 12 6 20 6 4" />
                </svg>
              </span>
              <span class="brand-name">Encoding</span>
              <span class="brand-tag">Premier démarrage</span>
            </div>
            <h1 class="setup-title">Bienvenue sur Encoding</h1>
            <p class="setup-subtitle">
              Configurez cette machine en un coup d’œil et vérifiez les ressources matérielles
              détectées localement.
            </p>
          </header>

          <div class="hardware-bento" role="region" aria-labelledby="hardware-card-title">
            <div class="hardware-bento-header">
              <span class="live-dot" aria-hidden="true"></span>
              <div class="hardware-bento-title-group">
                <span class="hardware-eyebrow">Nœud local détecté</span>
                <h2 id="hardware-card-title" class="hardware-node-name">
                  {{ effectiveNodeName() }}
                </h2>
                <span class="hardware-host-meta">{{ hostPlatformLabel() }}</span>
              </div>
            </div>

            <div class="hardware-tiles">
              <div class="hardware-tile">
                <span class="tile-label">Processeur</span>
                <span class="tile-value">{{ cpuInfo() }}</span>
              </div>
              <div class="hardware-tile">
                <span class="tile-label">Mémoire</span>
                <span class="tile-value">{{ memoryInfo() }}</span>
              </div>
              <div class="hardware-tile hardware-tile--wide">
                <span class="tile-label">Graphique / GPU</span>
                <span class="tile-value">{{ gpuInfo() }}</span>
              </div>
              <div class="hardware-tile hardware-tile--wide hardware-tile--accent">
                <span class="tile-label">Accélération GPU</span>
                <span class="tile-value">{{ gpuAccelerationInfo() }}</span>
              </div>
            </div>
          </div>

          <p class="telemetry-footnote">
            <svg
              viewBox="0 0 24 24"
              width="16"
              height="16"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              aria-hidden="true"
            >
              <circle cx="12" cy="12" r="10" />
              <line x1="12" y1="16" x2="12" y2="12" />
              <line x1="12" y1="8" x2="12.01" y2="8" />
            </svg>
            <span>
              Vous pourrez renommer ce nœud ou basculer entre autonome et cluster à chaud depuis la
              page <strong>Nœuds</strong>.
            </span>
          </p>
        </aside>

        <!-- Colonne droite : Atelier de configuration directe -->
        <div class="setup-workbench">
          <!-- Section 1 : Identité -->
          <section class="workbench-card" aria-labelledby="step-identity-heading">
            <div class="section-heading">
              <span class="step-pill" aria-hidden="true">1</span>
              <div>
                <h2 id="step-identity-heading">Identifier cette machine</h2>
                <p>Donnez un nom convivial pour repérer facilement ce nœud sur votre réseau.</p>
              </div>
            </div>

            <div class="form-group">
              <label for="node-name-input" class="field-label">Nom de ce nœud</label>
              <input
                id="node-name-input"
                type="text"
                class="text-input"
                [placeholder]="defaultNodeName()"
                [ngModel]="nodeName()"
                (ngModelChange)="nodeName.set($event)"
                maxlength="128"
                autocomplete="off"
              />
              <div class="Quick-suggestions" aria-label="Suggestions de noms">
                <span class="suggestions-label">Suggestions :</span>
                @for (suggestion of nodeNameSuggestions; track suggestion) {
                  <button
                    type="button"
                    class="suggestion-chip"
                    [class.active]="nodeName() === suggestion"
                    (click)="nodeName.set(suggestion)"
                  >
                    {{ suggestion }}
                  </button>
                }
              </div>
            </div>
          </section>

          <!-- Section 2 : Choix de l'architecture -->
          <section class="workbench-card" aria-labelledby="step-mode-heading">
            <div class="section-heading">
              <span class="step-pill" aria-hidden="true">2</span>
              <div>
                <h2 id="step-mode-heading">Comment souhaitez-vous utiliser cette machine ?</h2>
                <p>Sélectionnez l’organisation initiale de votre installation.</p>
              </div>
            </div>

            <div class="mode-grid" role="radiogroup" aria-labelledby="step-mode-heading">
              <!-- Mode 1 : Autonome -->
              <button
                type="button"
                class="mode-card"
                [class.selected]="selectedMode() === 'standalone'"
                (click)="selectMode('standalone')"
                role="radio"
                [attr.aria-checked]="selectedMode() === 'standalone'"
              >
                <div class="mode-card-top">
                  <span class="mode-icon" aria-hidden="true">
                    <svg
                      viewBox="0 0 24 24"
                      width="20"
                      height="20"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="2"
                    >
                      <rect x="2" y="3" width="20" height="14" rx="2" ry="2" />
                      <line x1="8" y1="21" x2="16" y2="21" />
                      <line x1="12" y1="17" x2="12" y2="21" />
                    </svg>
                  </span>
                  <span class="mode-badge primary">Recommandé</span>
                </div>
                <strong class="mode-title">Machine autonome</strong>
                <p class="mode-description">
                  Serveur et worker réunis sur cette machine. Idéal pour démarrer immédiatement sans
                  cluster.
                </p>
              </button>

              <!-- Mode 2 : Créer un cluster -->
              <button
                type="button"
                class="mode-card"
                [class.selected]="selectedMode() === 'createCluster'"
                (click)="selectMode('createCluster')"
                role="radio"
                [attr.aria-checked]="selectedMode() === 'createCluster'"
              >
                <div class="mode-card-top">
                  <span class="mode-icon" aria-hidden="true">
                    <svg
                      viewBox="0 0 24 24"
                      width="20"
                      height="20"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="2"
                    >
                      <circle cx="18" cy="5" r="3" />
                      <circle cx="6" cy="12" r="3" />
                      <circle cx="18" cy="19" r="3" />
                      <line x1="8.59" y1="13.51" x2="15.42" y2="17.49" />
                      <line x1="15.41" y1="6.51" x2="8.59" y2="10.49" />
                    </svg>
                  </span>
                  <span class="mode-badge neutral">Multi-machines</span>
                </div>
                <strong class="mode-title">Créer un cluster</strong>
                <p class="mode-description">
                  Initialise un nouveau maillage prêt à accueillir d’autres serveurs ou workers.
                </p>
              </button>

              <!-- Mode 3 : Rejoindre un cluster -->
              <button
                type="button"
                class="mode-card"
                [class.selected]="selectedMode() === 'joinCluster'"
                (click)="selectMode('joinCluster')"
                role="radio"
                [attr.aria-checked]="selectedMode() === 'joinCluster'"
              >
                <div class="mode-card-top">
                  <span class="mode-icon" aria-hidden="true">
                    <svg
                      viewBox="0 0 24 24"
                      width="20"
                      height="20"
                      fill="none"
                      stroke="currentColor"
                      stroke-width="2"
                    >
                      <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
                      <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
                    </svg>
                  </span>
                  <span class="mode-badge accent">Worker / Pair</span>
                </div>
                <strong class="mode-title">Rejoindre un cluster</strong>
                <p class="mode-description">
                  Rattache cette machine à un cluster Encoding déjà actif sur votre réseau.
                </p>
              </button>
            </div>
          </section>

          <!-- Section 3 : Finalisation contextuelle selon le mode choisi -->
          <section class="workbench-card workbench-card--Contextual" aria-live="polite">
            @if (selectedMode() === 'standalone') {
              <div class="contextual-summary">
                <div class="contextual-summary-head">
                  <span class="status-check" aria-hidden="true">✓</span>
                  <div>
                    <h3>Prêt à démarrer en mode autonome</h3>
                    <p>
                      <strong>{{ effectiveNodeName() }}</strong> assurera l’interface serveur et
                      l’encodage local. L’accès initial est ouvert sans mot de passe (activable dans
                      Paramètres).
                    </p>
                  </div>
                </div>
                <div class="workbench-actions">
                  <button
                    matButton="filled"
                    type="button"
                    [disabled]="busy()"
                    (click)="submitStandalone()"
                  >
                    Accéder au tableau de bord →
                  </button>
                </div>
              </div>
            }

            @if (selectedMode() === 'createCluster') {
              <div class="section-heading">
                <span class="step-pill" aria-hidden="true">3</span>
                <div>
                  <h3>Nommer votre nouveau cluster</h3>
                  <p>Ce nom sera affiché lors de l’ajout d’autres serveurs ou workers.</p>
                </div>
              </div>

              <div class="form-group">
                <label for="cluster-name-input" class="field-label">Nom du cluster</label>
                <input
                  id="cluster-name-input"
                  type="text"
                  class="text-input"
                  [placeholder]="defaultClusterName()"
                  [ngModel]="clusterName()"
                  (ngModelChange)="clusterName.set($event)"
                  maxlength="128"
                  autocomplete="off"
                />
                <span class="field-hint">
                  Premier coordinateur et worker : <strong>{{ effectiveNodeName() }}</strong>
                </span>
              </div>

              <div class="workbench-actions">
                <button
                  matButton="filled"
                  type="button"
                  [disabled]="busy()"
                  (click)="submitCreateCluster()"
                >
                  Créer le cluster et continuer →
                </button>
              </div>
            }

            @if (selectedMode() === 'joinCluster') {
              <div class="section-heading">
                <span class="step-pill" aria-hidden="true">3</span>
                <div>
                  <h3>Connexion au cluster d’accueil</h3>
                  <p>
                    Sélectionnez un cluster découvert sur le réseau local ou indiquez son adresse
                    URL.
                  </p>
                </div>
              </div>

              @if (availableClusters().length > 0) {
                <div class="discovered-clusters">
                  <span class="list-label">Clusters détectés automatiquement (mDNS) :</span>
                  <div class="cluster-cards-list">
                    @for (cluster of availableClusters(); track cluster.id) {
                      <div class="cluster-card">
                        <div class="cluster-info">
                          <strong>{{ cluster.name }}</strong>
                          <span class="cluster-address">{{ cluster.apiBaseUrl }}</span>
                        </div>
                        <button
                          matButton="outlined"
                          type="button"
                          (click)="selectClusterAddress(cluster.apiBaseUrl)"
                        >
                          Sélectionner
                        </button>
                      </div>
                    }
                  </div>
                </div>
              }

              <div class="form-group">
                <label for="cluster-address-input" class="field-label">
                  Adresse du cluster d’accueil
                </label>
                <div class="input-with-button">
                  <input
                    id="cluster-address-input"
                    type="url"
                    class="text-input"
                    placeholder="http://192.168.1.50:8090"
                    [ngModel]="clusterAddress()"
                    (ngModelChange)="clusterAddress.set($event)"
                  />
                  <button
                    matButton="outlined"
                    type="button"
                    [disabled]="!clusterAddress() || busy()"
                    (click)="verifyClusterAddress()"
                  >
                    Vérifier
                  </button>
                </div>
                <span class="field-hint">
                  Adresse complète avec port (ex. http://192.168.1.50:8090).
                </span>
              </div>

              @if (localController.target(); as target) {
                <div class="target-confirmed-box">
                  <div class="target-badge">Cluster joignable</div>
                  <h4>{{ target.name }}</h4>
                  <p class="target-url">{{ target.apiBaseUrl }} ({{ target.id }})</p>

                  @if (pairingCode(); as code) {
                    <div class="hero-code-box">
                      <span class="code-label">Code de confirmation à comparer</span>
                      <div class="hero-digits">{{ code }}</div>
                      <p class="code-helper">
                        Un administrateur du cluster <strong>{{ target.name }}</strong> doit valider
                        ce code depuis sa page Nœuds.
                      </p>
                    </div>
                  } @else {
                    <button
                      matButton="outlined"
                      type="button"
                      [disabled]="busy()"
                      (click)="requestClusterJoin()"
                    >
                      Envoyer la demande de rattachement
                    </button>
                  }
                </div>
              }

              <div class="workbench-actions">
                <button
                  matButton="filled"
                  type="button"
                  [disabled]="busy()"
                  (click)="submitJoinFinish()"
                >
                  Terminer et ouvrir l’application →
                </button>
              </div>
            }
          </section>
        </div>
      </div>
    </section>
  `,
  styles: `
    .setup-studio {
      width: min(100%, 1060px);
      margin: 2vh auto 4vh;
      display: grid;
      gap: var(--space-4);
    }
    .setup-progress {
      border-radius: var(--radius-pill);
      overflow: hidden;
    }
    .setup-error {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 12px 16px;
      border-radius: var(--radius-md);
      background: var(--danger-soft);
      color: var(--danger);
      border: 1px solid color-mix(in srgb, var(--danger) 35%, transparent);
      font-size: var(--font-body);
    }
    .setup-grid {
      display: grid;
      grid-template-columns: minmax(290px, 340px) minmax(0, 1fr);
      gap: var(--space-5);
      align-items: start;
    }
    .setup-telemetry,
    .workbench-card {
      display: grid;
      gap: var(--space-4);
      padding: var(--space-5);
      border-radius: var(--radius-2xl, 24px);
      background: var(--surface-container-low);
      border: 1px solid var(--border-subtle);
      box-shadow: var(--shadow-soft);
    }
    .setup-telemetry {
      position: sticky;
      top: 76px;
    }
    .setup-brand,
    .setup-workbench,
    .hardware-bento {
      display: grid;
      gap: var(--space-3);
    }
    .brand-badge {
      display: inline-flex;
      align-items: center;
      gap: 8px;
    }
    .brand-mark {
      width: 30px;
      height: 30px;
      border-radius: 9px;
      display: grid;
      place-items: center;
      background: linear-gradient(135deg, var(--accent) 0%, var(--accent-strong) 100%);
      color: var(--on-accent);
    }
    .brand-name {
      font-weight: 800;
      font-size: var(--font-body);
    }
    .brand-tag,
    .mode-badge {
      padding: 2px 8px;
      border-radius: var(--radius-pill);
      font-size: 12px;
      font-weight: 700;
      background: var(--accent-soft);
      color: var(--accent);
    }
    .setup-title {
      margin: 0;
      font-size: var(--font-title-lg);
      font-weight: 800;
      letter-spacing: -0.02em;
    }
    .setup-subtitle,
    .section-heading p,
    .contextual-summary-head p {
      margin: 0;
      font-size: var(--font-body);
      color: var(--text-secondary);
      line-height: 1.5;
    }
    .hardware-bento {
      padding: var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container);
      border: 1px solid var(--border-subtle);
    }
    .hardware-bento-header,
    .section-heading,
    .contextual-summary-head {
      display: flex;
      align-items: flex-start;
      gap: var(--space-3);
    }
    .live-dot {
      width: 10px;
      height: 10px;
      margin-top: 5px;
      border-radius: 50%;
      background: var(--success);
      box-shadow: 0 0 0 4px color-mix(in srgb, var(--success) 20%, transparent);
      flex-shrink: 0;
    }
    .hardware-bento-title-group {
      display: grid;
      gap: 2px;
      min-width: 0;
    }
    .hardware-eyebrow {
      font-size: 12px;
      font-weight: 700;
      text-transform: uppercase;
      color: var(--text-muted);
    }
    .hardware-node-name {
      margin: 0;
      font-size: var(--font-title-md);
      font-weight: 800;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .hardware-host-meta,
    .field-hint,
    .cluster-address,
    .target-url,
    .code-helper {
      font-size: 12px;
      color: var(--text-secondary);
      margin: 0;
    }
    .hardware-tiles {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: var(--space-2);
    }
    .hardware-tile {
      display: grid;
      gap: 3px;
      padding: 10px 12px;
      border-radius: var(--radius-md);
      background: var(--surface-container-high);
      border: 1px solid var(--border-subtle);
    }
    .hardware-tile--wide {
      grid-column: 1 / -1;
    }
    .hardware-tile--accent {
      background: color-mix(in srgb, var(--accent-soft) 55%, var(--surface-container-high));
    }
    .tile-label,
    .suggestions-label {
      font-size: 12px;
      font-weight: 600;
      color: var(--text-muted);
    }
    .tile-value {
      font-size: 13px;
      font-weight: 700;
    }
    .telemetry-footnote {
      margin: 0;
      display: flex;
      gap: 8px;
      font-size: 12px;
      line-height: 1.45;
      color: var(--text-secondary);
    }
    .workbench-card--Contextual {
      background: color-mix(in srgb, var(--surface-container) 85%, var(--accent-soft));
      border-color: color-mix(in srgb, var(--accent) 28%, var(--border-subtle));
    }
    .step-pill,
    .status-check {
      width: 28px;
      height: 28px;
      border-radius: 50%;
      display: grid;
      place-items: center;
      background: var(--accent-soft);
      color: var(--accent);
      font-size: 13px;
      font-weight: 800;
      flex-shrink: 0;
    }
    .status-check {
      background: var(--success-soft);
      color: var(--success);
    }
    .section-heading h2,
    .section-heading h3,
    .contextual-summary-head h3,
    .target-confirmed-box h4 {
      margin: 0 0 4px;
      font-size: var(--font-title-md);
      font-weight: 750;
    }
    .form-group,
    .discovered-clusters,
    .cluster-cards-list {
      display: grid;
      gap: 8px;
    }
    .field-label,
    .list-label {
      font-size: 13px;
      font-weight: 700;
    }
    .text-input {
      min-height: 44px;
      padding: 10px 14px;
      border-radius: var(--radius-md);
      border: 1px solid var(--border-strong);
      background: var(--surface-container-high);
      color: var(--text-primary);
      font-size: var(--font-body);
      outline: none;
    }
    .text-input:focus {
      border-color: var(--accent);
      box-shadow: 0 0 0 3px var(--accent-soft);
    }
    .Quick-suggestions {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px;
    }
    .suggestion-chip {
      padding: 4px 10px;
      border-radius: var(--radius-pill);
      border: 1px solid var(--border-subtle);
      background: var(--surface-container);
      color: var(--text-secondary);
      font-size: 12px;
      font-weight: 600;
      cursor: pointer;
    }
    .suggestion-chip:hover,
    .suggestion-chip.active {
      background: var(--accent-soft);
      color: var(--accent);
    }
    .mode-grid {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: var(--space-3);
    }
    .mode-card {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 10px;
      text-align: left;
      padding: var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container);
      border: 2px solid var(--border-subtle);
      color: inherit;
      cursor: pointer;
    }
    .mode-card.selected {
      background: color-mix(in srgb, var(--accent-soft) 55%, var(--surface-container));
      border-color: var(--accent);
    }
    .mode-card-top,
    .contextual-summary,
    .cluster-card {
      width: 100%;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
    }
    .mode-icon {
      width: 36px;
      height: 36px;
      border-radius: 10px;
      display: grid;
      place-items: center;
      background: var(--surface-container-high);
      color: var(--accent);
    }
    .mode-badge.primary {
      background: var(--success-soft);
      color: var(--success);
    }
    .mode-badge.neutral {
      background: var(--surface-container-high);
      color: var(--text-secondary);
    }
    .mode-title {
      font-size: var(--font-body);
      font-weight: 750;
    }
    .mode-description {
      margin: 0;
      font-size: 12px;
      color: var(--text-secondary);
      line-height: 1.45;
    }
    .workbench-actions,
    .input-with-button {
      display: flex;
      justify-content: flex-end;
      gap: var(--space-2);
    }
    .input-with-button .text-input {
      flex: 1;
    }
    .cluster-card,
    .target-confirmed-box {
      padding: var(--space-3) var(--space-4);
      background: var(--surface-container);
      border-radius: var(--radius-md);
      border: 1px solid var(--border-subtle);
    }
    .target-confirmed-box {
      display: grid;
      gap: 8px;
      border-color: var(--accent);
    }
    .target-badge,
    .code-label {
      font-size: 12px;
      text-transform: uppercase;
      font-weight: 700;
      color: var(--success);
    }
    .hero-code-box {
      text-align: center;
      padding: var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--accent-soft);
    }
    .hero-digits {
      font-size: 32px;
      font-weight: 800;
      letter-spacing: 0.14em;
      margin: 8px 0;
      font-family: ui-monospace, monospace;
    }
    @media (max-width: 900px) {
      .setup-grid,
      .mode-grid {
        grid-template-columns: 1fr;
      }
      .setup-telemetry {
        position: static;
      }
    }
  `,
})
export class SetupWizardPageComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly notifications = inject(NotificationService);
  protected readonly localController = inject(LocalNodeController);

  protected readonly currentStep = signal(0);
  protected readonly busy = signal(false);
  protected readonly errorMessage = signal('');

  protected readonly nodeName = signal('');
  protected readonly selectedMode = signal<SetupOperationMode>('standalone');
  protected readonly clusterName = signal('');
  protected readonly clusterAddress = signal('');

  protected readonly nodeNameSuggestions = ['Serveur Salon', 'Worker GPU', 'NAS Médias'] as const;

  protected readonly defaultNodeName = computed(() => {
    const status = this.localController.status();
    return status?.displayName || status?.name || 'Mon Serveur';
  });

  protected readonly effectiveNodeName = computed(() => {
    return this.nodeName().trim() || this.defaultNodeName();
  });

  protected readonly hostPlatformLabel = computed(() => {
    const status = this.localController.status();
    const inv = status?.inventory;
    const host = status?.name || 'localhost';
    if (!inv) return `Hôte : ${host}`;
    return `${host} · ${inv.operatingSystem} (${inv.architecture})`;
  });

  protected readonly defaultClusterName = computed(() => {
    return 'Cluster ' + this.effectiveNodeName();
  });

  protected readonly cpuInfo = computed(() => {
    const inv = this.localController.status()?.inventory;
    if (!inv) return 'Détection en cours…';
    return inv.processorModel?.trim() || `${inv.processorCount} cœurs`;
  });

  protected readonly memoryInfo = computed(() => {
    const inv = this.localController.status()?.inventory;
    if (!inv) return 'Détection en cours…';
    return formatMemory(inv.memoryMb);
  });

  protected readonly gpuInfo = computed(() => {
    const inv = this.localController.status()?.inventory;
    if (!inv?.gpus || inv.gpus.length === 0) return 'Aucun GPU dédié détecté';
    return inv.gpus.map((g) => g.name).join(' · ');
  });

  protected readonly gpuAccelerationInfo = computed(() => {
    const status = this.localController.status();
    if (!status) return 'Détection en cours…';
    const caps = status.capabilities ?? [];
    if (caps.length === 0) return 'Détection en cours…';

    const ACCEL_DISPLAY: Readonly<Record<string, string>> = {
      nvenc: 'NVENC',
      qsv: 'QSV',
      vaapi: 'VAAPI',
      amf: 'AMF',
      videotoolbox: 'VideoToolbox',
      cuda: 'CUDA',
      d3d11va: 'D3D11VA',
    };

    const supported = new Set<string>();
    for (const tool of caps) {
      for (const check of tool.encoderChecks ?? []) {
        if (check.status === 'usable') {
          const suffix = check.encoderId.split('_').pop() ?? '';
          if (suffix && suffix !== 'cpu' && suffix !== 'process' && suffix !== 'general') {
            supported.add(suffix.toLowerCase());
          }
        }
      }
      for (const accel of tool.accelerationTypes ?? []) {
        if (accel && accel !== 'cpu' && accel !== 'process' && accel !== 'general') {
          supported.add(accel.toLowerCase());
        }
      }
    }

    if (supported.size === 0) return 'Aucune (CPU)';

    const preferredOrder = ['nvenc', 'qsv', 'vaapi', 'amf', 'videotoolbox'];
    const ordered = [
      ...preferredOrder.filter((id) => supported.has(id)),
      ...Array.from(supported).filter((id) => !preferredOrder.includes(id)),
    ];

    return ordered.map((id) => ACCEL_DISPLAY[id] ?? id.toUpperCase()).join(' · ');
  });

  protected readonly availableClusters = computed(() => this.localController.joinableClusters());

  protected readonly pairingCode = computed(
    () =>
      this.localController.pairing()?.verificationCode ||
      this.localController.status()?.verificationCode ||
      null,
  );

  protected selectMode(mode: SetupOperationMode): void {
    this.errorMessage.set('');
    this.selectedMode.set(mode);
  }

  protected goToStep(index: number): void {
    this.errorMessage.set('');
    this.currentStep.set(index);
  }

  protected selectClusterAddress(url: string): void {
    this.clusterAddress.set(url);
    const known = this.availableClusters().find((c) => c.apiBaseUrl === url);
    if (known) {
      this.localController.target.set(known);
    }
    void this.verifyClusterAddress();
  }

  protected async verifyClusterAddress(): Promise<void> {
    const addr = this.clusterAddress().trim();
    if (!addr) return;
    this.errorMessage.set('');
    this.busy.set(true);
    try {
      await this.localController.verifyAddress({ address: addr });
      if (this.localController.error()) {
        this.errorMessage.set(this.localController.error());
      }
    } catch {
      this.errorMessage.set(
        'La vérification de l’adresse a échoué. Vérifiez que le serveur est allumé.',
      );
    } finally {
      this.busy.set(false);
    }
  }

  protected async requestClusterJoin(): Promise<void> {
    this.errorMessage.set('');
    this.busy.set(true);
    try {
      await this.localController.sendRequest(this.effectiveNodeName());
      if (this.localController.error()) {
        this.errorMessage.set(this.localController.error());
      }
    } catch {
      this.errorMessage.set('L’envoi de la demande a échoué. Réessayez.');
    } finally {
      this.busy.set(false);
    }
  }

  protected async submitStandalone(): Promise<void> {
    await this.completeSetup({
      nodeDisplayName: this.effectiveNodeName(),
      mode: 'standalone',
    });
  }

  protected async submitCreateCluster(): Promise<void> {
    const name = this.clusterName().trim() || this.defaultClusterName();
    await this.completeSetup({
      nodeDisplayName: this.effectiveNodeName(),
      mode: 'createCluster',
      clusterName: name,
    });
  }

  protected async submitJoinFinish(): Promise<void> {
    await this.completeSetup({
      nodeDisplayName: this.effectiveNodeName(),
      mode: 'joinCluster',
    });
  }

  private async completeSetup(payload: {
    nodeDisplayName: string;
    mode: SetupOperationMode;
    clusterName?: string;
  }): Promise<void> {
    this.errorMessage.set('');
    this.busy.set(true);
    try {
      await this.auth.setup(payload);
      this.notifications.show('Initialisation terminée avec succès !');
      await this.router.navigate(['/']);
    } catch (error) {
      this.errorMessage.set(
        AuthService.errorMessage(error, 'L’initialisation a échoué. Veuillez réessayer.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
