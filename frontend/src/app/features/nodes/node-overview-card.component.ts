import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import {
  ResourceSheetNotice,
  ResourceSheetSection,
} from '../../shared/ui/resource-sheet.component';
import { ResourceCardAction } from '../../shared/ui/resource-card.component';
import { StatusBadgeComponent, StatusTone } from '../../shared/ui/status-badge.component';

export interface NodeOverviewLibrary {
  readonly id: string;
  readonly name: string;
  readonly path: string;
  readonly stateLabel: string;
  readonly stateTone: StatusTone;
  readonly modeLabel: string;
  readonly accessLabel: string;
}

export interface NodeOverviewRoleSwitch {
  readonly role: 'server' | 'worker';
  readonly label: string;
  readonly checked: boolean;
  readonly pending?: boolean;
  readonly disabled?: boolean;
}

export interface NodeOverviewTaskCapacity {
  readonly accepted: number | null;
  readonly remaining: number | null;
  readonly maximum: number | null;
  readonly note: string;
}

@Component({
  selector: 'app-node-overview-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatCheckboxModule,
    MatMenuModule,
    MatSlideToggleModule,
    StatusBadgeComponent,
  ],
  template: `
    <article
      class="node-overview-card"
      [class.is-active]="active()"
      [class.is-selected]="selected()"
      [attr.aria-label]="ariaLabel()"
    >
      @for (notice of notices(); track notice.title) {
        <div class="node-overview-notice-banner" [attr.data-tone]="notice.tone" role="status">
          <strong>{{ notice.title }}</strong>
          <span>{{ notice.description }}</span>
        </div>
      }
      <header
        class="node-overview-head"
        [class.node-overview-head_local]="layout() === 'local'"
        [class.has-select]="selectable()"
      >
        @if (selectable()) {
          <mat-checkbox
            class="node-overview-select"
            [checked]="selected()"
            [attr.aria-label]="'Sélectionner ' + title()"
            (change)="selectedChange.emit($event.checked)"
          />
        }
        <span class="node-overview-symbol" aria-hidden="true">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
            <rect x="3" y="4" width="18" height="6" rx="2" />
            <rect x="3" y="14" width="18" height="6" rx="2" />
            <path d="M7 7h.01M7 17h.01M11 7h7M11 17h7" />
          </svg>
        </span>
        <div class="node-overview-identity">
          @if (layout() === 'local') {
            <span class="node-overview-eyebrow">CE NŒUD</span>
          }
          <div class="node-overview-title-line">
            @if (openable()) {
              <button class="node-overview-title" type="button" (click)="open.emit()">
                {{ title() }}
              </button>
            } @else {
              <h2 class="node-overview-title-static">{{ title() }}</h2>
            }
            @if (layout() === 'standard' && admissionEnabled() !== null) {
              <mat-slide-toggle
                class="node-overview-admission-toggle"
                [checked]="admissionEnabled() ?? false"
                [disabled]="admissionDisabled()"
                [aria-label]="
                  (admissionEnabled() ? 'Suspendre' : 'Réactiver') +
                  ' les nouvelles tâches sur ' + title()
                "
                (change)="admissionChange.emit($event.checked)"
              >
                Nouvelles tâches
              </mat-slide-toggle>
            }
          </div>
          @if (subtitle()) {
            <p class="node-overview-subtitle">{{ subtitle() }}</p>
          }
          <div class="node-overview-badges">
            <app-status-badge [label]="statusLabel()" [tone]="statusTone()" />
            @if (!roleSwitches().length) {
              @for (role of roles(); track role) {
                <span class="node-overview-role">{{ role }}</span>
              }
            }
          </div>
          @if (roleSwitches().length) {
            <div
              class="node-overview-role-switches"
              [class.node-overview-role-switches_local]="layout() === 'local'"
              role="group"
              aria-label="Fonctions du nœud"
            >
              @if (layout() === 'local') {
                <span class="node-overview-role-group-title">FONCTIONS</span>
              }
              @for (control of roleSwitches(); track control.role) {
                <div class="node-overview-role-control">
                  <mat-slide-toggle
                    [checked]="control.checked"
                    [disabled]="control.disabled ?? false"
                    [aria-label]="
                      (control.checked ? 'Désactiver' : 'Activer') +
                      ' la fonction ' + control.label + ' de ' + title()
                    "
                    (change)="roleToggle.emit({ role: control.role, enabled: $event.checked })"
                  >
                    {{ control.label }}
                  </mat-slide-toggle>
                  @if (layout() === 'local') {
                    <span
                      class="node-overview-role-state"
                      [class.is-active]="control.checked && !control.pending"
                    >
                      {{
                        control.pending
                          ? 'En attente'
                          : control.checked
                            ? 'Activée'
                            : 'Désactivée'
                      }}
                    </span>
                  } @else if (control.pending) {
                    <span class="node-overview-role-pending">En attente</span>
                  }
                </div>
              }
            </div>
          }
          @if (roleControlNote()) {
            <p class="node-overview-role-note">{{ roleControlNote() }}</p>
          }
        </div>
        <div class="node-overview-head-controls">
          @if (layout() === 'local' && admissionEnabled() !== null) {
            <div
              class="node-overview-admission-control"
              [class.is-disabled]="admissionDisabled()"
            >
              @if (layout() === 'local') {
                <span class="node-overview-control-label">ADMISSION</span>
              }
              <mat-slide-toggle
                class="node-overview-admission-toggle"
                [checked]="admissionEnabled() ?? false"
                [disabled]="admissionDisabled()"
                [aria-label]="
                  (admissionEnabled() ? 'Suspendre' : 'Réactiver') +
                  ' les nouvelles tâches sur ' + title()
                "
                (change)="admissionChange.emit($event.checked)"
              >
                Nouvelles tâches
              </mat-slide-toggle>
              @if (layout() === 'local') {
                <span class="node-overview-control-state">
                  {{ admissionEnabled() ? 'Acceptées' : 'Suspendues' }}
                </span>
              }
            </div>
          }
          @if (layout() === 'local') {
            @if (taskCapacity(); as capacity) {
              <section
                class="node-overview-task-capacity"
                aria-label="Tâches acceptées et places restantes"
              >
                <span class="node-overview-control-label">CAPACITÉ DE TRAITEMENT</span>
                <dl class="node-overview-task-capacity-values">
                  <div>
                    <dt>Tâches acceptées</dt>
                    <dd>{{ capacity.accepted ?? '—' }}</dd>
                  </div>
                  <div>
                    <dt>Places restantes</dt>
                    <dd>{{ capacity.remaining ?? '—' }}</dd>
                  </div>
                </dl>
                @if (capacity.maximum !== null) {
                  <span class="node-overview-task-capacity-total">
                    Sur {{ capacity.maximum }} place(s) simultanée(s)
                  </span>
                }
                @if (capacity.note) {
                  <span class="node-overview-task-capacity-note">{{ capacity.note }}</span>
                }
              </section>
            }
          }
          @if (meta()) {
            <div class="node-overview-meta-control">
              @if (layout() === 'local') {
                <span class="node-overview-control-label">DERNIER SIGNAL</span>
              }
              <span class="node-overview-meta">{{ meta() }}</span>
            </div>
          }
        </div>
        @if (actions().length) {
          <button
            mat-icon-button
            type="button"
            class="node-overview-menu-button"
            [matMenuTriggerFor]="menu"
            [attr.aria-label]="'Autres actions pour ' + title()"
          >
            <span aria-hidden="true">···</span>
          </button>
          <mat-menu #menu="matMenu">
            @for (action of actions(); track action.id) {
              <button
                mat-menu-item
                type="button"
                [class.danger]="action.tone === 'danger'"
                (click)="actionSelected.emit(action.id)"
              >
                {{ action.label }}
              </button>
            }
          </mat-menu>
        }
      </header>

      <div class="node-overview-section-grid">
        @for (section of sections(); track section.id) {
          <section
            class="node-overview-section"
            [attr.aria-labelledby]="nodeId() + '-' + section.id"
          >
            <div class="node-overview-section-heading">
              <h3 [id]="nodeId() + '-' + section.id">{{ section.title }}</h3>
              @if (section.caption) {
                <span>{{ section.caption }}</span>
              }
              @if (section.action; as action) {
                <button matButton="text" type="button" (click)="actionSelected.emit(action.id)">
                  {{ action.label }}
                </button>
              }
            </div>
            @if (section.notice) {
              <p class="node-overview-notice">{{ section.notice }}</p>
            }
            @if (section.items?.length) {
              <div class="node-overview-items">
                @for (item of section.items; track item.label) {
                  <div class="node-overview-item">
                    <div class="node-overview-item-main">
                      <strong>{{ item.label }}</strong>
                      @if (item.badges?.length) {
                        <div class="node-overview-chip-row">
                          @for (badge of item.badges ?? []; track badge.label) {
                            <app-status-badge
                              [label]="badge.label"
                              [tone]="badge.tone ?? 'neutral'"
                            />
                          }
                        </div>
                      }
                      @if (item.detail) {
                        <span class="node-overview-detail">{{ item.detail }}</span>
                      }
                    </div>
                    @if (item.action; as action) {
                      <button
                        matButton="outlined"
                        type="button"
                        [disabled]="action.disabled"
                        (click)="actionSelected.emit(action.id)"
                      >
                        {{ action.label }}
                      </button>
                    }
                  </div>
                }
              </div>
            }
            @if (section.metrics?.length || section.fields?.length) {
              <dl class="node-overview-values">
                @for (metric of section.metrics ?? []; track metric.label) {
                  <div class="node-overview-value">
                    <dt>{{ metric.label }}</dt>
                    <dd [class.is-unavailable]="metric.unavailable">{{ metric.value }}</dd>
                    @if (metric.detail) {
                      <span class="node-overview-detail">{{ metric.detail }}</span>
                    }
                  </div>
                }
                @for (field of section.fields ?? []; track field.label) {
                  <div class="node-overview-value">
                    <dt>{{ field.label }}</dt>
                    <dd [class.is-mono]="field.mono">{{ field.value }}</dd>
                    @if (field.detail) {
                      <span class="node-overview-detail">{{ field.detail }}</span>
                    }
                  </div>
                }
              </dl>
            }
          </section>
        }
      </div>

      <section class="node-overview-libraries" aria-label="Bibliothèques et chemins configurés">
        <header class="node-overview-libraries-heading">
          <div>
            <h3>Bibliothèques et chemins configurés</h3>
            <p>Chemins vus depuis ce nœud et mode d’accès associé.</p>
          </div>
          <span class="node-overview-library-count">
            {{ libraries().length }} autorisée{{ libraries().length > 1 ? 's' : '' }}
          </span>
        </header>
        @if (libraryAccessState() === 'loading') {
          <p class="node-overview-empty" role="status">Chargement des accès configurés…</p>
        } @else if (libraryAccessState() === 'error') {
          <p class="node-overview-empty" role="status">
            Les accès aux bibliothèques n’ont pas pu être chargés.
          </p>
        } @else if (libraries().length) {
          <div class="node-overview-library-grid">
            @for (library of libraries(); track library.id) {
              <article class="node-overview-library-card">
                <header class="node-overview-library-head">
                  <span class="node-overview-library-symbol" aria-hidden="true">▱</span>
                  <span class="node-overview-library-name">
                    <strong>{{ library.name }}</strong>
                    <small>Bibliothèque</small>
                  </span>
                  <app-status-badge [label]="library.stateLabel" [tone]="library.stateTone" />
                </header>
                <div class="node-overview-path-block">
                  <span>Chemin configuré sur ce nœud</span>
                  <code>{{ library.path }}</code>
                </div>
                <footer class="node-overview-library-foot">
                  <span class="node-overview-library-mode">{{ library.modeLabel }}</span>
                  <span>{{ library.accessLabel }}</span>
                </footer>
              </article>
            }
          </div>
        } @else {
          <p class="node-overview-empty">Aucune bibliothèque configurée pour ce nœud.</p>
        }
      </section>
    </article>
  `,
  styleUrl: './node-overview-card.component.scss',
})
export class NodeOverviewCardComponent {
  readonly ariaLabel = input('Informations du nœud');
  readonly title = input.required<string>();
  readonly subtitle = input('');
  readonly statusLabel = input.required<string>();
  readonly statusTone = input<StatusTone>('neutral');
  readonly layout = input<'standard' | 'local'>('standard');
  readonly nodeId = input('node-overview');
  readonly roles = input<readonly string[]>([]);
  readonly roleSwitches = input<readonly NodeOverviewRoleSwitch[]>([]);
  readonly roleControlNote = input('');
  readonly admissionEnabled = input<boolean | null>(null);
  readonly admissionDisabled = input(false);
  readonly taskCapacity = input<NodeOverviewTaskCapacity | null>(null);
  readonly meta = input('');
  readonly notices = input<readonly ResourceSheetNotice[]>([]);
  readonly sections = input<readonly ResourceSheetSection[]>([]);
  readonly libraries = input<readonly NodeOverviewLibrary[]>([]);
  readonly libraryAccessState = input<'loading' | 'ready' | 'error'>('ready');
  readonly actions = input<readonly ResourceCardAction[]>([]);
  readonly selectable = input(false);
  readonly selected = input(false);
  readonly active = input(false);
  readonly openable = input(false);

  readonly open = output<void>();
  readonly selectedChange = output<boolean>();
  readonly actionSelected = output<string>();
  readonly roleToggle = output<{ readonly role: 'server' | 'worker'; readonly enabled: boolean }>();
  readonly admissionChange = output<boolean>();
}
