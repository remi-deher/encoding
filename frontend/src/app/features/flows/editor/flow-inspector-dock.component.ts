import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  FlowComponentConfiguration,
  FlowComponentConfigurationSchema,
  FlowGraphNode,
} from '../../../shared/models/flow-component';
import { flowComponentColors } from '../../../shared/models/flow-presentation';
import { ComponentConfigurationFormComponent } from '../../../shared/ui/component-configuration-form.component';
import { MediaType } from '../../../shared/models/media-type';

@Component({
  selector: 'app-flow-inspector-dock',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatTooltipModule, ComponentConfigurationFormComponent],
  template: `
    <aside
      class="inspector-dock"
      [class.inspector-dock_collapsed]="!opened()"
      aria-label="Inspecteur contextuel des blocs et propriétés"
    >
      @if (opened()) {
        @if (node(); as current) {
          <div class="dock-header" [style.--node-color]="colors(current).color">
            <div class="header-kind-row">
              <span class="node-kind-badge">{{ current.component.kind }}</span>
              <span class="node-plugin-badge">{{ current.component.pluginName }}</span>
              <button
                type="button"
                class="dock-close-btn"
                (click)="closeRequested.emit()"
                matTooltip="Fermer l'inspecteur"
                aria-label="Fermer l'inspecteur"
              >
                ✕
              </button>
            </div>
            <h2 class="node-title">{{ current.name || current.component.name }}</h2>
            <p class="node-description">{{ current.component.description }}</p>
          </div>

          <div class="dock-body">
            <div class="instance-name-section">
              <label for="inspector-node-name" class="field-label">Nom de l’étape</label>
              <input
                id="inspector-node-name"
                type="text"
                class="name-input"
                [value]="current.name ?? ''"
                placeholder="Ex. 1. Transcodage principal"
                (input)="onNameInput($event)"
                aria-label="Nom personnalisé de l’étape dans le flow"
              />
              <span class="field-hint"
                >Ce nom apparaît directement sur la carte dans le canevas.</span
              >
            </div>

            <div class="configuration-section">
              <h3 class="section-title">Paramètres du composant</h3>
              <app-component-configuration-form
                [schema]="schema()"
                [values]="values()"
                [instanceName]="current.name ?? ''"
                submitLabel="Appliquer au bloc"
                (instanceNameChange)="instanceNameChange.emit($event)"
                (saved)="configurationSaved.emit($event)"
                (cancelled)="closeRequested.emit()"
              />
            </div>

            @if (current.component.kind !== 'input') {
              <div class="danger-zone">
                <button
                  matButton="outlined"
                  type="button"
                  class="delete-node-btn"
                  (click)="deleteNodeRequested.emit(current.id)"
                >
                  <span aria-hidden="true">🗑</span>
                  <span>Supprimer cette étape</span>
                </button>
              </div>
            }
          </div>
        } @else {
          <div class="dock-header">
            <div class="header-kind-row">
              <span class="dock-glyph" aria-hidden="true">▤</span>
              <h2 class="dock-title">Propriétés du flow</h2>
              <button
                type="button"
                class="dock-close-btn"
                (click)="openedChange.emit(false)"
                matTooltip="Masquer le volet"
                aria-label="Masquer le volet"
              >
                ✕
              </button>
            </div>
            <p class="node-description">
              Sélectionnez ou double-cliquez sur un bloc du flow pour afficher et modifier ses
              réglages.
            </p>
          </div>

          <div class="dock-body">
            <div class="flow-summary-card">
              <h3 class="summary-title">{{ flowTitle() }}</h3>
              <div class="summary-metrics">
                <div class="metric-item">
                  <span class="metric-label">Média</span>
                  <span class="metric-value">{{ flowMediaType() }}</span>
                </div>
                <div class="metric-item">
                  <span class="metric-label">Blocs</span>
                  <span class="metric-value">{{ nodeCount() }}</span>
                </div>
                <div class="metric-item">
                  <span class="metric-label">Liaisons</span>
                  <span class="metric-value">{{ connectionCount() }}</span>
                </div>
              </div>
            </div>

            <div class="shortcuts-card">
              <h4 class="shortcuts-title">Raccourcis clavier</h4>
              <dl class="shortcuts-list">
                <div class="shortcut-row">
                  <dt><kbd>Ctrl</kbd> + <kbd>S</kbd></dt>
                  <dd>Enregistrer ou publier le flow</dd>
                </div>
                <div class="shortcut-row">
                  <dt><kbd>Ctrl</kbd> + <kbd>Entrée</kbd></dt>
                  <dd>Lancer la simulation</dd>
                </div>
                <div class="shortcut-row">
                  <dt><kbd>/</kbd></dt>
                  <dd>Rechercher un composant</dd>
                </div>
                <div class="shortcut-row">
                  <dt><kbd>Tab</kbd> + <kbd>Entrée</kbd></dt>
                  <dd>Ouvrir le bloc focalisé</dd>
                </div>
                <div class="shortcut-row">
                  <dt><kbd>Échap</kbd></dt>
                  <dd>Fermer l'inspecteur</dd>
                </div>
              </dl>
            </div>
          </div>
        }
      }
    </aside>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }

    .inspector-dock {
      width: 380px;
      height: 100%;
      background: var(--surface-container);
      border-left: 1px solid var(--border-subtle);
      display: flex;
      flex-direction: column;
      transition: width 0.22s var(--motion-emphasized);
      overflow: hidden;

      &_collapsed {
        width: 0;
        border-left: none;
      }
    }

    .dock-header {
      padding: 14px 16px;
      border-bottom: 1px solid var(--border-subtle);
      background: var(--surface);
      display: flex;
      flex-direction: column;
      gap: 6px;
    }

    .header-kind-row {
      display: flex;
      align-items: center;
      gap: 8px;
    }

    .dock-glyph {
      font-size: 16px;
      color: var(--accent);
    }

    .dock-title {
      margin: 0;
      font-size: var(--font-title);
      font-weight: 700;
      color: var(--text-primary);
      flex: 1;
    }

    .node-kind-badge {
      font-size: 10px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      padding: 2px 7px;
      border-radius: var(--radius-pill);
      background: var(--surface-container-high);
      color: var(--node-color, var(--accent));
    }

    .node-plugin-badge {
      font-size: 11px;
      color: var(--text-muted);
      flex: 1;
    }

    .dock-close-btn {
      width: 26px;
      height: 26px;
      border-radius: var(--radius-xs);
      border: none;
      background: transparent;
      color: var(--text-secondary);
      font-size: 14px;
      display: grid;
      place-items: center;
      cursor: pointer;

      &:hover {
        background: var(--surface-container-highest);
        color: var(--text-primary);
      }
    }

    .node-title {
      margin: 2px 0 0;
      font-size: var(--font-title-md);
      font-weight: 700;
      color: var(--text-primary);
      overflow-wrap: anywhere;
    }

    .node-description {
      margin: 0;
      font-size: 12px;
      color: var(--text-secondary);
      line-height: 1.4;
    }

    .dock-body {
      flex: 1;
      overflow-y: auto;
      padding: 16px;
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    .instance-name-section {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }

    .field-label {
      font-size: 12px;
      font-weight: 600;
      color: var(--text-secondary);
    }

    .name-input {
      height: 38px;
      padding: 0 10px;
      background: var(--surface);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-sm);
      color: var(--text-primary);
      font-size: 13px;
      font-family: inherit;
      outline: none;

      &:focus {
        border-color: var(--accent);
      }
    }

    .field-hint {
      font-size: 11px;
      color: var(--text-muted);
    }

    .configuration-section {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }

    .section-title {
      margin: 0;
      font-size: 13px;
      font-weight: 700;
      color: var(--text-primary);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .danger-zone {
      margin-top: 12px;
      padding-top: 14px;
      border-top: 1px dashed var(--border-subtle);
    }

    .delete-node-btn {
      width: 100%;
      color: var(--danger);
      border-color: var(--danger-soft);
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 8px;

      &:hover {
        background: var(--danger-soft);
      }
    }

    .flow-summary-card {
      background: var(--surface);
      border-radius: var(--radius-md);
      padding: 14px;
      border: 1px solid var(--border-subtle);
    }

    .summary-title {
      margin: 0 0 10px;
      font-size: 14px;
      font-weight: 700;
      color: var(--text-primary);
    }

    .summary-metrics {
      display: grid;
      grid-template-columns: repeat(3, 1fr);
      gap: 8px;
    }

    .metric-item {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .metric-label {
      font-size: 10px;
      color: var(--text-muted);
      text-transform: uppercase;
    }

    .metric-value {
      font-size: 16px;
      font-weight: 700;
      color: var(--accent);
    }

    .shortcuts-card {
      background: var(--surface-container-low);
      border-radius: var(--radius-md);
      padding: 14px;
      border: 1px solid var(--border-subtle);
    }

    .shortcuts-title {
      margin: 0 0 10px;
      font-size: 12px;
      font-weight: 700;
      color: var(--text-secondary);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .shortcuts-list {
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }

    .shortcut-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      font-size: 11px;

      dt {
        color: var(--text-primary);
        white-space: nowrap;
      }

      dd {
        margin: 0;
        color: var(--text-muted);
        text-align: right;
      }

      kbd {
        display: inline-block;
        padding: 2px 5px;
        background: var(--surface);
        border: 1px solid var(--border-strong);
        border-radius: 4px;
        font-family: inherit;
        font-size: 10px;
        font-weight: 600;
        box-shadow: 0 1px 1px rgba(0, 0, 0, 0.1);
      }
    }
  `,
})
export class FlowInspectorDockComponent {
  readonly opened = input(true);
  readonly node = input<FlowGraphNode | undefined>(undefined);
  readonly schema = input<FlowComponentConfigurationSchema | undefined>(undefined);
  readonly values = input<FlowComponentConfiguration>({});
  readonly flowTitle = input('');
  readonly flowMediaType = input<MediaType>('video');
  readonly nodeCount = input(0);
  readonly connectionCount = input(0);

  readonly openedChange = output<boolean>();
  readonly instanceNameChange = output<string>();
  readonly configurationSaved = output<FlowComponentConfiguration>();
  readonly deleteNodeRequested = output<string>();
  readonly closeRequested = output<void>();

  protected colors(node: FlowGraphNode) {
    return flowComponentColors(node.component);
  }

  protected onNameInput(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.instanceNameChange.emit(target.value);
  }
}
