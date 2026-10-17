import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatStepperIntl, MatStepperModule } from '@angular/material/stepper';
import { AuthService } from '../../core/auth/auth.service';
import { LocalNodeController } from '../../shared/services/local-node-controller';
import { NotificationService } from '../../shared/services/notification.service';
import { formatMemory } from '../../shared/models/node-hardware';

export type SetupOperationMode = 'standalone' | 'createCluster' | 'joinCluster';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-setup-wizard-page',
  providers: [
    LocalNodeController,
    {
      provide: MatStepperIntl,
      useFactory: () =>
        Object.assign(new MatStepperIntl(), {
          completedLabel: 'Terminée',
          editableLabel: 'Modifiable',
          optionalLabel: 'Facultatif',
        }),
    },
  ],
  imports: [CommonModule, FormsModule, MatStepperModule, MatButtonModule, MatProgressBarModule],
  template: `
    <section class="wizard-container" aria-label="Assistant de premier démarrage">
      <header class="wizard-header">
        <div class="brand-badge">
          <svg
            class="brand-icon"
            viewBox="0 0 24 24"
            width="28"
            height="28"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
          >
            <path stroke-linecap="round" stroke-linejoin="round" d="M5 12h14M12 5l7 7-7 7" />
          </svg>
          <span class="brand-name">Encoding</span>
        </div>
        <h1 class="wizard-title">Assistant d'initialisation</h1>
        <p class="wizard-subtitle">
          Configurez cette machine en quelques étapes simples pour commencer à traiter vos médias.
        </p>
      </header>

      @if (busy()) {
        <mat-progress-bar mode="indeterminate" aria-label="Action en cours" />
      }

      @if (errorMessage()) {
        <div role="alert" class="wizard-error">
          <svg
            viewBox="0 0 24 24"
            width="20"
            height="20"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
          >
            <circle cx="12" cy="12" r="10" />
            <path d="M12 8v4m0 4h.01" stroke-linecap="round" />
          </svg>
          <span>{{ errorMessage() }}</span>
        </div>
      }

      <div class="wizard-card">
        <mat-stepper
          linear
          #stepper
          [selectedIndex]="currentStep()"
          (selectionChange)="goToStep($event.selectedIndex)"
        >
          <ng-template matStepperIcon="done">
            <svg
              viewBox="0 0 24 24"
              width="16"
              height="16"
              fill="none"
              stroke="currentColor"
              stroke-width="2.5"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="m5 12 4 4 10-10" />
            </svg>
          </ng-template>
          <ng-template matStepperIcon="edit">
            <svg
              viewBox="0 0 24 24"
              width="16"
              height="16"
              fill="none"
              stroke="currentColor"
              stroke-width="2.5"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="m5 12 4 4 10-10" />
            </svg>
          </ng-template>

          <!-- Étape 1 : Identification du nœud -->
          <mat-step [completed]="currentStep() > 0 && isStep1Valid()">
            <ng-template matStepLabel>Identification</ng-template>
            <div class="step-content">
              <h2>Bienvenue sur Encoding</h2>
              <p class="step-desc">
                Commençons par nommer cette machine pour la repérer facilement sur votre réseau
                local.
              </p>

              <div class="form-group">
                <label for="node-name-input" class="field-label">Nom de ce nœud</label>
                <input
                  id="node-name-input"
                  type="text"
                  class="text-input"
                  [placeholder]="defaultNodeName()"
                  [(ngModel)]="nodeName"
                  maxlength="128"
                  autocomplete="off"
                />
                <span class="field-hint"
                  >Exemple : Serveur Salon, NAS Synology, Worker RTX. Modifiable à tout moment
                  depuis la page Nœuds.</span
                >
              </div>

              <div class="info-card">
                <div class="info-card-header">
                  <svg
                    viewBox="0 0 24 24"
                    width="20"
                    height="20"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                  >
                    <rect x="2" y="2" width="20" height="8" rx="2" ry="2" />
                    <rect x="2" y="14" width="20" height="8" rx="2" ry="2" />
                    <line x1="6" y1="6" x2="6.01" y2="6" />
                    <line x1="6" y1="18" x2="6.01" y2="18" />
                  </svg>
                  <h3>Matériel détecté localement</h3>
                </div>
                <div class="info-grid">
                  <div class="info-item">
                    <span class="info-label">Processeur</span>
                    <span class="info-value">{{ cpuInfo() }}</span>
                  </div>
                  <div class="info-item">
                    <span class="info-label">Mémoire</span>
                    <span class="info-value">{{ memoryInfo() }}</span>
                  </div>
                  <div class="info-item">
                    <span class="info-label">Graphique / GPU</span>
                    <span class="info-value">{{ gpuInfo() }}</span>
                  </div>
                  <div class="info-item">
                    <span class="info-label">Accélération GPU</span>
                    <span class="info-value">{{ gpuAccelerationInfo() }}</span>
                  </div>
                </div>
              </div>

              <div class="step-actions">
                <button
                  mat-flat-button
                  color="primary"
                  type="button"
                  [disabled]="!isStep1Valid()"
                  (click)="goToStep(1)"
                >
                  Continuer →
                </button>
              </div>
            </div>
          </mat-step>

          <!-- Étape 2 : Choix du mode de fonctionnement -->
          <mat-step [completed]="currentStep() > 1 && isStep2Valid()">
            <ng-template matStepLabel>Mode de fonctionnement</ng-template>
            <div class="step-content">
              <h2>Comment souhaitez-vous utiliser cette machine ?</h2>
              <p class="step-desc">Sélectionnez le rôle de cette installation sur votre réseau.</p>

              <div class="mode-options" role="radiogroup" aria-label="Choix du mode">
                <!-- Option 1 : Autonome -->
                <button
                  type="button"
                  class="mode-card"
                  [class.selected]="selectedMode() === 'standalone'"
                  (click)="selectedMode.set('standalone')"
                  role="radio"
                  [attr.aria-checked]="selectedMode() === 'standalone'"
                >
                  <div class="mode-header">
                    <div class="mode-icon-box">
                      <svg
                        viewBox="0 0 24 24"
                        width="24"
                        height="24"
                        fill="none"
                        stroke="currentColor"
                        stroke-width="2"
                      >
                        <rect x="2" y="3" width="20" height="14" rx="2" ry="2" />
                        <line x1="8" y1="21" x2="16" y2="21" />
                        <line x1="12" y1="17" x2="12" y2="21" />
                      </svg>
                    </div>
                    <div class="mode-title-group">
                      <span class="mode-title">Démarrer comme machine autonome</span>
                      <span class="mode-badge primary">Recommandé pour débuter</span>
                    </div>
                  </div>
                  <p class="mode-description">
                    Installation unique et indépendante. Elle gère directement ses bibliothèques,
                    ses flows et ses encodages locaux sans cluster.
                  </p>
                </button>

                <!-- Option 2 : Créer un cluster -->
                <button
                  type="button"
                  class="mode-card"
                  [class.selected]="selectedMode() === 'createCluster'"
                  (click)="selectedMode.set('createCluster')"
                  role="radio"
                  [attr.aria-checked]="selectedMode() === 'createCluster'"
                >
                  <div class="mode-header">
                    <div class="mode-icon-box">
                      <svg
                        viewBox="0 0 24 24"
                        width="24"
                        height="24"
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
                    </div>
                    <div class="mode-title-group">
                      <span class="mode-title">Créer un cluster</span>
                      <span class="mode-badge neutral">Multi-machines</span>
                    </div>
                  </div>
                  <p class="mode-description">
                    Cette machine devient le premier serveur d'un nouveau cluster, prête à fédérer
                    d'autres serveurs ou workers pour distribuer la charge.
                  </p>
                </button>

                <!-- Option 3 : Rejoindre un cluster -->
                <button
                  type="button"
                  class="mode-card"
                  [class.selected]="selectedMode() === 'joinCluster'"
                  (click)="selectedMode.set('joinCluster')"
                  role="radio"
                  [attr.aria-checked]="selectedMode() === 'joinCluster'"
                >
                  <div class="mode-header">
                    <div class="mode-icon-box">
                      <svg
                        viewBox="0 0 24 24"
                        width="24"
                        height="24"
                        fill="none"
                        stroke="currentColor"
                        stroke-width="2"
                      >
                        <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71" />
                        <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71" />
                      </svg>
                    </div>
                    <div class="mode-title-group">
                      <span class="mode-title">Rejoindre un cluster</span>
                      <span class="mode-badge accent">Worker / Renfort</span>
                    </div>
                  </div>
                  <p class="mode-description">
                    Vous avez déjà un serveur Encoding actif sur votre réseau. Cette machine le
                    rejoint pour exécuter des tâches et apporter sa puissance.
                  </p>
                </button>
              </div>

              <div class="step-actions">
                <button mat-button type="button" (click)="goToStep(0)">← Retour</button>
                <button mat-flat-button color="primary" type="button" (click)="goToStep(2)">
                  Suivant →
                </button>
              </div>
            </div>
          </mat-step>

          <!-- Étape 3 : Finalisation selon le mode -->
          <mat-step [completed]="false">
            <ng-template matStepLabel>Finalisation</ng-template>
            <div class="step-content">
              <!-- Cas 3A : Autonome -->
              @if (selectedMode() === 'standalone') {
                <h2>Prêt à démarrer en mode autonome</h2>
                <p class="step-desc">
                  Votre serveur local est prêt. Vous allez accéder directement à votre interface de
                  gestion.
                </p>

                <div class="summary-box">
                  <div class="summary-row">
                    <span class="summary-label">Nom de cette machine</span>
                    <strong class="summary-value">{{ effectiveNodeName() }}</strong>
                  </div>
                  <div class="summary-row">
                    <span class="summary-label">Mode d'organisation</span>
                    <span class="summary-value">Machine autonome (aucun cluster requis)</span>
                  </div>
                  <div class="summary-row">
                    <span class="summary-label">Sécurité & Accès</span>
                    <span class="summary-value"
                      >Accès libre sans mot de passe (activable dans Paramètres)</span
                    >
                  </div>
                </div>

                <div class="tip-box">
                  <svg
                    viewBox="0 0 24 24"
                    width="18"
                    height="18"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="2"
                  >
                    <circle cx="12" cy="12" r="10" />
                    <line x1="12" y1="16" x2="12" y2="12" />
                    <line x1="12" y1="8" x2="12.01" y2="8" />
                  </svg>
                  <span
                    >Vous pourrez à tout moment créer ou rejoindre un cluster depuis l'onglet
                    <strong>Nœuds</strong> de l'application.</span
                  >
                </div>

                <div class="step-actions">
                  <button mat-button type="button" (click)="goToStep(1)">← Retour</button>
                  <button
                    mat-flat-button
                    color="primary"
                    type="button"
                    [disabled]="busy()"
                    (click)="submitStandalone()"
                  >
                    Accéder au tableau de bord ✓
                  </button>
                </div>
              }

              <!-- Cas 3B : Créer un cluster -->
              @if (selectedMode() === 'createCluster') {
                <h2>Nommer votre nouveau cluster</h2>
                <p class="step-desc">
                  Donnez un nom clair pour ce cluster afin de le reconnaître lors de l'ajout
                  d'autres machines.
                </p>

                <div class="form-group">
                  <label for="cluster-name-input" class="field-label">Nom du cluster</label>
                  <input
                    id="cluster-name-input"
                    type="text"
                    class="text-input"
                    [placeholder]="defaultClusterName()"
                    [(ngModel)]="clusterName"
                    maxlength="128"
                    autocomplete="off"
                  />
                  <span class="field-hint">Exemple : Cluster Maison, Encodage-Prod, Homelab.</span>
                </div>

                <div class="summary-box">
                  <div class="summary-row">
                    <span class="summary-label">Premier membre</span>
                    <strong class="summary-value">{{ effectiveNodeName() }}</strong>
                  </div>
                  <div class="summary-row">
                    <span class="summary-label">Rôle initial</span>
                    <span class="summary-value">Serveur coordinateur et worker</span>
                  </div>
                </div>

                <div class="step-actions">
                  <button mat-button type="button" (click)="goToStep(1)">← Retour</button>
                  <button
                    mat-flat-button
                    color="primary"
                    type="button"
                    [disabled]="busy()"
                    (click)="submitCreateCluster()"
                  >
                    Créer le cluster et continuer ✓
                  </button>
                </div>
              }

              <!-- Cas 3C : Rejoindre un cluster -->
              @if (selectedMode() === 'joinCluster') {
                <h2>Rejoindre un cluster existant</h2>
                <p class="step-desc">
                  Sélectionnez un cluster détecté sur votre réseau ou saisissez l'adresse de son
                  serveur principal.
                </p>

                @if (availableClusters().length > 0) {
                  <div class="discovered-clusters">
                    <span class="list-label">Clusters détectés automatiquement :</span>
                    <div class="cluster-cards-list">
                      @for (cluster of availableClusters(); track cluster.id) {
                        <div class="cluster-card">
                          <div class="cluster-info">
                            <strong>{{ cluster.name }}</strong>
                            <span class="cluster-address">{{ cluster.apiBaseUrl }}</span>
                          </div>
                          <button
                            mat-stroked-button
                            color="primary"
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
                  <label for="cluster-address-input" class="field-label"
                    >Adresse du cluster d'accueil</label
                  >
                  <div class="input-with-button">
                    <input
                      id="cluster-address-input"
                      type="url"
                      class="text-input"
                      placeholder="http://192.168.1.50:8090"
                      [(ngModel)]="clusterAddress"
                    />
                    <button
                      mat-flat-button
                      type="button"
                      [disabled]="!clusterAddress() || busy()"
                      (click)="verifyClusterAddress()"
                    >
                      Vérifier
                    </button>
                  </div>
                  <span class="field-hint"
                    >Adresse URL complète avec port (ex: http://192.168.1.50:8090).</span
                  >
                </div>

                @if (localController.target(); as target) {
                  <div class="target-confirmed-box">
                    <div class="target-badge">Cluster joignable</div>
                    <h3>{{ target.name }}</h3>
                    <p class="target-url">{{ target.apiBaseUrl }} ({{ target.id }})</p>

                    @if (pairingCode(); as code) {
                      <div class="hero-code-box">
                        <span class="code-label">Code de confirmation à comparer</span>
                        <div class="hero-digits">{{ code }}</div>
                        <p class="code-helper">
                          Un administrateur du cluster <strong>{{ target.name }}</strong> doit
                          valider ce code depuis sa page Nœuds.
                        </p>
                      </div>
                    } @else {
                      <button
                        mat-flat-button
                        color="primary"
                        type="button"
                        [disabled]="busy()"
                        (click)="requestClusterJoin()"
                      >
                        Envoyer la demande de rattachement
                      </button>
                    }
                  </div>
                }

                <div class="step-actions">
                  <button mat-button type="button" (click)="goToStep(1)">← Retour</button>
                  <button
                    mat-flat-button
                    color="primary"
                    type="button"
                    [disabled]="busy()"
                    (click)="submitJoinFinish()"
                  >
                    Terminer et ouvrir l'application ✓
                  </button>
                </div>
              }
            </div>
          </mat-step>
        </mat-stepper>
      </div>
    </section>
  `,
  styles: `
    .wizard-container {
      width: min(100%, 720px);
      margin: 4vh auto;
      padding: 0 16px;
      font-family: inherit;
    }
    .wizard-header {
      text-align: center;
      margin-bottom: 24px;
    }
    .brand-badge {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 6px 14px;
      border-radius: var(--radius-pill);
      background: var(--surface-container-high, #2b2b2b);
      color: var(--accent, #64b5f6);
      margin-bottom: 12px;
      font-weight: 600;
    }
    .wizard-title {
      font-size: var(--font-title-lg, 24px);
      font-weight: 700;
      color: var(--text-primary, #ffffff);
      margin: 0 0 8px;
      letter-spacing: -0.02em;
    }
    .wizard-subtitle {
      color: var(--text-secondary, #b0bec5);
      font-size: var(--font-body, 14px);
      margin: 0;
      line-height: 1.5;
    }
    .wizard-card {
      background: var(--surface-container, #1e1e1e);
      border: 1px solid var(--border-subtle, #333333);
      border-radius: var(--radius-xl, 20px);
      padding: 24px;
      box-shadow: 0 8px 32px rgba(0, 0, 0, 0.24);
    }
    .wizard-error {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 12px 16px;
      border-radius: var(--radius-md, 12px);
      background: rgba(239, 83, 80, 0.15);
      color: #ef5350;
      border: 1px solid rgba(239, 83, 80, 0.3);
      margin-bottom: 16px;
      font-size: 14px;
    }
    .step-content {
      padding: 20px 8px 8px;
    }
    .step-content h2 {
      font-size: 18px;
      font-weight: 600;
      color: var(--text-primary, #ffffff);
      margin: 0 0 6px;
    }
    .step-desc {
      color: var(--text-secondary, #9e9e9e);
      font-size: 14px;
      margin: 0 0 20px;
      line-height: 1.5;
    }
    .form-group {
      margin-bottom: 20px;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .field-label {
      font-size: 13px;
      font-weight: 600;
      color: var(--text-primary, #ffffff);
    }
    .text-input {
      padding: 12px 14px;
      border-radius: var(--radius-md, 10px);
      border: 1px solid var(--border-subtle, #424242);
      background: var(--surface-container-high, #2a2a2a);
      color: var(--text-primary, #ffffff);
      font-size: 15px;
      outline: none;
      transition: border-color 0.2s ease;
    }
    .text-input:focus {
      border-color: var(--accent, #64b5f6);
    }
    .field-hint {
      font-size: 12px;
      color: var(--text-secondary, #757575);
    }
    .info-card {
      background: var(--surface-container-low, #181818);
      border: 1px solid var(--border-subtle, #333333);
      border-radius: var(--radius-lg, 14px);
      padding: 16px;
      margin-bottom: 24px;
    }
    .info-card-header {
      display: flex;
      align-items: center;
      gap: 8px;
      color: var(--accent, #64b5f6);
      margin-bottom: 14px;
    }
    .info-card-header h3 {
      font-size: 14px;
      font-weight: 600;
      margin: 0;
      color: var(--text-primary, #ffffff);
    }
    .info-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 12px;
    }
    .info-item {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .info-label {
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--text-secondary, #757575);
    }
    .info-value {
      font-size: 13px;
      font-weight: 500;
      color: var(--text-primary, #e0e0e0);
    }
    .mode-options {
      display: flex;
      flex-direction: column;
      gap: 12px;
      margin-bottom: 24px;
    }
    .mode-card {
      display: flex;
      flex-direction: column;
      text-align: left;
      padding: 16px;
      background: var(--surface-container-low, #181818);
      border: 2px solid var(--border-subtle, #333333);
      border-radius: var(--radius-lg, 14px);
      cursor: pointer;
      transition: all 0.2s cubic-bezier(0.2, 0, 0, 1);
      color: inherit;
    }
    .mode-card:hover {
      background: var(--surface-container-high, #252525);
      border-color: #555555;
    }
    .mode-card.selected {
      background: rgba(100, 181, 246, 0.08);
      border-color: var(--accent, #64b5f6);
    }
    .mode-header {
      display: flex;
      align-items: center;
      gap: 12px;
      margin-bottom: 6px;
    }
    .mode-icon-box {
      width: 40px;
      height: 40px;
      border-radius: var(--radius-md, 10px);
      background: var(--surface-container-high, #2a2a2a);
      color: var(--accent, #64b5f6);
      display: flex;
      align-items: center;
      justify-content: center;
      flex-shrink: 0;
    }
    .mode-title-group {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
    }
    .mode-title {
      font-size: 15px;
      font-weight: 600;
      color: var(--text-primary, #ffffff);
    }
    .mode-badge {
      font-size: 12px;
      padding: 2px 8px;
      border-radius: var(--radius-pill);
      font-weight: 500;
    }
    .mode-badge.primary {
      background: rgba(76, 175, 80, 0.15);
      color: #81c784;
    }
    .mode-badge.neutral {
      background: rgba(158, 158, 158, 0.15);
      color: #bdbdbd;
    }
    .mode-badge.accent {
      background: rgba(100, 181, 246, 0.15);
      color: #90caf9;
    }
    .mode-description {
      margin: 0;
      font-size: 13px;
      color: var(--text-secondary, #9e9e9e);
      line-height: 1.5;
    }
    .summary-box {
      background: var(--surface-container-low, #181818);
      border: 1px solid var(--border-subtle, #333333);
      border-radius: var(--radius-lg, 14px);
      padding: 16px;
      margin-bottom: 16px;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .summary-row {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
      font-size: 14px;
    }
    .summary-label {
      color: var(--text-secondary, #9e9e9e);
    }
    .summary-value {
      color: var(--text-primary, #ffffff);
      text-align: right;
    }
    .tip-box {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 12px;
      border-radius: var(--radius-md, 10px);
      background: rgba(100, 181, 246, 0.08);
      border: 1px solid rgba(100, 181, 246, 0.2);
      color: #90caf9;
      font-size: 13px;
      margin-bottom: 24px;
    }
    .step-actions {
      display: flex;
      justify-content: flex-end;
      align-items: center;
      gap: 12px;
      margin-top: 24px;
    }
    .input-with-button {
      display: flex;
      gap: 8px;
    }
    .input-with-button .text-input {
      flex: 1;
    }
    .discovered-clusters {
      margin-bottom: 16px;
    }
    .list-label {
      font-size: 13px;
      font-weight: 500;
      color: var(--text-secondary, #9e9e9e);
      margin-bottom: 8px;
      display: block;
    }
    .cluster-cards-list {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .cluster-card {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 10px 14px;
      background: var(--surface-container-high, #252525);
      border-radius: var(--radius-md, 10px);
      border: 1px solid var(--border-subtle, #333333);
    }
    .cluster-info {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .cluster-address {
      font-size: 12px;
      color: var(--text-secondary, #9e9e9e);
    }
    .target-confirmed-box {
      background: var(--surface-container-low, #181818);
      border: 1px solid var(--accent, #64b5f6);
      border-radius: var(--radius-lg, 14px);
      padding: 18px;
      margin-top: 16px;
    }
    .target-badge {
      display: inline-block;
      font-size: 12px;
      text-transform: uppercase;
      font-weight: 700;
      color: #81c784;
      margin-bottom: 6px;
    }
    .target-confirmed-box h3 {
      margin: 0 0 4px;
      font-size: 16px;
      color: var(--text-primary, #ffffff);
    }
    .target-url {
      margin: 0 0 16px;
      font-size: 13px;
      color: var(--text-secondary, #9e9e9e);
    }
    .hero-code-box {
      text-align: center;
      padding: 20px;
      border-radius: var(--radius-lg, 14px);
      background: rgba(100, 181, 246, 0.1);
      border: 1px solid rgba(100, 181, 246, 0.3);
      margin-top: 12px;
    }
    .code-label {
      font-size: 12px;
      color: var(--text-secondary, #90caf9);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .hero-digits {
      font-size: 36px;
      font-weight: 800;
      letter-spacing: 0.15em;
      color: var(--text-primary, #ffffff);
      margin: 8px 0;
      font-family: ui-monospace, monospace;
    }
    .code-helper {
      font-size: 13px;
      color: var(--text-secondary, #e0e0e0);
      margin: 0;
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

  protected readonly defaultNodeName = computed(() => {
    const status = this.localController.status();
    return status?.displayName || status?.name || 'Mon Serveur';
  });

  protected readonly effectiveNodeName = computed(() => {
    return this.nodeName().trim() || this.defaultNodeName();
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

  protected isStep1Valid(): boolean {
    return true; // Nom facultatif avec valeur par défaut automatique
  }

  protected isStep2Valid(): boolean {
    return !!this.selectedMode();
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
      await this.localController.sendRequest();
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
