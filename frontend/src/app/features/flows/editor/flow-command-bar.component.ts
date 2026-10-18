import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MediaType } from '../../../shared/models/media-type';

@Component({
  selector: 'app-flow-command-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatButtonModule, MatTooltipModule],
  template: `
    <header class="command-bar" role="toolbar" aria-label="Commandes du flow">
      <div class="command-group navigation-group">
        <a
          matButton="text"
          routerLink="/flows"
          class="back-link"
          aria-label="Retourner au catalogue des flows"
          matTooltip="Retourner au catalogue des flows"
        >
          <span class="back-icon" aria-hidden="true">←</span>
          <span class="back-label">Flows</span>
        </a>

        <div class="title-container">
          @if (isEditingTitle()) {
            <input
              #titleInput
              type="text"
              class="title-input"
              [value]="title()"
              (blur)="commitTitle(titleInput.value)"
              (keydown.enter)="commitTitle(titleInput.value)"
              (keydown.escape)="cancelEditingTitle()"
              aria-label="Nom du flow"
            />
          } @else {
            <h1
              class="flow-title"
              (click)="startEditingTitle()"
              tabindex="0"
              (keydown.enter)="startEditingTitle()"
              matTooltip="Cliquer pour renommer le flow"
              role="button"
              aria-label="Renommer le flow"
            >
              {{ title() }}
              <span class="edit-hint" aria-hidden="true">✎</span>
            </h1>
          }

          <div class="status-pills">
            <span class="media-type-pill" [attr.data-media]="mediaType()">
              {{ mediaTypeLabel() }}
            </span>

            @if (hasUnsavedChanges()) {
              <span class="state-pill state-pill_unsaved" role="status">
                <span class="status-dot status-dot_amber" aria-hidden="true"></span>
                Non enregistré
              </span>
            } @else if (storedVersion() > 0) {
              <span class="state-pill state-pill_published" role="status">
                <span class="status-dot status-dot_green" aria-hidden="true"></span>
                v{{ storedVersion() }} publiée
              </span>
            } @else if (hasDraft()) {
              <span class="state-pill state-pill_draft" role="status">
                <span class="status-dot status-dot_blue" aria-hidden="true"></span>
                Brouillon
              </span>
            }

            @if (storedVersion() > 0 && backupStatus()) {
              <span
                class="backup-pill"
                [class.backup-pill_ready]="backupStatus() === 'ready'"
                [class.backup-pill_failed]="backupStatus() === 'failed'"
                matTooltip="État de la sauvegarde JSON persistante"
              >
                Sauvegarde :
                {{
                  backupStatus() === 'ready'
                    ? 'disponible'
                    : backupStatus() === 'failed'
                      ? 'en échec'
                      : 'en attente'
                }}
              </span>
            }
          </div>
        </div>
      </div>

      <div class="command-group tool-actions">
        <button
          matButton="tonal"
          type="button"
          class="tool-btn"
          [disabled]="disabled() || simulatorBusy()"
          (click)="simulateRequested.emit()"
          matTooltip="Simuler le parcours des branches avec un profil de test (Ctrl+Entrée)"
        >
          <span class="btn-icon" aria-hidden="true">▷</span>
          <span>Tester</span>
        </button>

        <button
          matButton="outlined"
          type="button"
          class="tool-btn"
          [disabled]="disabled() || validating()"
          (click)="validateRequested.emit()"
          matTooltip="Vérifier la validité du graphe et des composants auprès du backend"
        >
          <span class="btn-icon" aria-hidden="true">✔</span>
          <span>{{ validating() ? 'Vérification…' : 'Vérifier' }}</span>
        </button>

        @if (canCreatePack()) {
          <button
            matButton="outlined"
            type="button"
            class="tool-btn"
            [disabled]="disabled() || saving()"
            (click)="packCreationRequested.emit()"
            matTooltip="Créer un pack réutilisable depuis cette version publiée"
          >
            <span class="btn-icon" aria-hidden="true">⊞</span>
            <span>Pack</span>
          </button>
        }

        <button
          matButton="outlined"
          type="button"
          class="tool-btn"
          [disabled]="disabled() || saving()"
          (click)="aiAssistantRequested.emit()"
          matTooltip="Assistant IA : générer, modifier ou expliquer le flow"
          aria-label="Ouvrir l'assistant IA"
        >
          <span class="btn-icon" aria-hidden="true">✨</span>
          <span>Assistant IA</span>
        </button>

        <button
          matButton="text"
          type="button"
          class="toggle-btn"
          [class.toggle-btn_active]="showConnections()"
          (click)="toggleConnections.emit()"
          matTooltip="Afficher ou masquer les lignes de raccordement SVG"
        >
          <span class="btn-icon" aria-hidden="true">☍</span>
          <span>Liaisons</span>
        </button>
      </div>

      <div class="command-group workbench-actions">
        <div class="dock-toggles" role="group" aria-label="Affichage des volets du workbench">
          <button
            type="button"
            class="dock-toggle-btn"
            [class.dock-toggle-btn_active]="paletteOpen()"
            (click)="togglePalette.emit()"
            matTooltip="Afficher/masquer la palette de blocs (Ctrl+B)"
            aria-label="Basculer la palette de blocs"
          >
            <span class="btn-glyph" aria-hidden="true">▦</span>
            <span class="toggle-label">Palette</span>
          </button>

          <button
            type="button"
            class="dock-toggle-btn"
            [class.dock-toggle-btn_active]="inspectorOpen()"
            (click)="toggleInspector.emit()"
            matTooltip="Afficher/masquer l'inspecteur contextuel"
            aria-label="Basculer l'inspecteur contextuel"
          >
            <span class="btn-glyph" aria-hidden="true">▤</span>
            <span class="toggle-label">Inspecteur</span>
          </button>

          <button
            type="button"
            class="dock-toggle-btn"
            [class.dock-toggle-btn_active]="trayOpen()"
            (click)="toggleTray.emit()"
            matTooltip="Afficher/masquer le volet de simulation et diagnostics"
            aria-label="Basculer le volet de simulation"
          >
            <span class="btn-glyph" aria-hidden="true">≡</span>
            <span class="toggle-label">Simulation</span>
          </button>
        </div>

        <button
          matButton="filled"
          type="button"
          class="save-btn"
          [disabled]="saving() || disabled() || !canSave()"
          (click)="saveRequested.emit()"
          matTooltip="Enregistrer ou publier le flow (Ctrl+S)"
        >
          @if (saving()) {
            <span>Enregistrement…</span>
          } @else {
            <span class="save-icon" aria-hidden="true">💾</span>
            <span>Enregistrer</span>
          }
        </button>
      </div>
    </header>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
    }

    .command-bar {
      min-height: 58px;
      padding: 6px 16px;
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      background: var(--surface-container);
      border-bottom: 1px solid var(--border-subtle);
      box-shadow: var(--elevation-1);
    }

    .command-group {
      display: flex;
      align-items: center;
      gap: 10px;
    }

    .navigation-group {
      flex: 1 1 auto;
      min-width: 0;
    }

    .back-link {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: var(--text-secondary);
      font-weight: 600;
      text-decoration: none;
      padding: 0 10px;
      height: 36px;
      border-radius: var(--radius-pill);
      transition: background 0.15s ease;

      &:hover {
        background: var(--surface-container-high);
        color: var(--text-primary);
      }
    }

    .back-icon {
      font-size: 16px;
    }

    .title-container {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 10px;
      min-width: 0;
    }

    .flow-title {
      margin: 0;
      font-size: var(--font-title-lg);
      font-weight: 700;
      letter-spacing: -0.02em;
      color: var(--text-primary);
      display: inline-flex;
      align-items: center;
      gap: 8px;
      cursor: pointer;
      border-radius: var(--radius-sm);
      padding: 2px 6px;
      transition: background 0.15s ease;

      &:hover {
        background: var(--surface-container-high);
      }

      &:focus-visible {
        outline: 2px solid var(--accent);
      }
    }

    .edit-hint {
      font-size: 13px;
      opacity: 0.5;
    }

    .title-input {
      font-size: var(--font-title-lg);
      font-weight: 700;
      font-family: inherit;
      color: var(--text-primary);
      background: var(--surface-container-highest);
      border: 1px solid var(--accent);
      border-radius: var(--radius-sm);
      padding: 4px 8px;
      outline: none;
      min-width: 220px;
    }

    .status-pills {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }

    .media-type-pill {
      display: inline-flex;
      align-items: center;
      padding: 3px 10px;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 600;
      background: var(--surface-container-highest);
      color: var(--text-secondary);
      text-transform: capitalize;
    }

    .state-pill {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 3px 10px;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 600;

      &_unsaved {
        background: var(--warning-soft);
        color: var(--warning);
      }

      &_published {
        background: var(--success-soft);
        color: var(--success);
      }

      &_draft {
        background: var(--accent-soft);
        color: var(--accent);
      }
    }

    .status-dot {
      width: 7px;
      height: 7px;
      border-radius: 999px;

      &_amber {
        background: var(--warning);
        box-shadow: 0 0 6px var(--warning);
        animation: pulse 1.8s infinite ease-in-out;
      }

      &_green {
        background: var(--success);
      }

      &_blue {
        background: var(--accent);
      }
    }

    @keyframes pulse {
      0%,
      100% {
        opacity: 1;
        transform: scale(1);
      }
      50% {
        opacity: 0.45;
        transform: scale(0.85);
      }
    }

    .backup-pill {
      font-size: 11px;
      color: var(--text-muted);
      padding: 2px 6px;
      border-radius: var(--radius-xs);
      background: var(--surface-container-low);

      &_ready {
        color: var(--success);
      }

      &_failed {
        color: var(--danger);
      }
    }

    .tool-actions {
      display: flex;
      align-items: center;
      gap: 6px;
    }

    .btn-icon {
      margin-right: 4px;
      font-size: 13px;
    }

    .toggle-btn {
      color: var(--text-secondary);
      border-radius: var(--radius-pill);

      &_active {
        background: var(--surface-container-highest);
        color: var(--accent);
        font-weight: 600;
      }
    }

    .workbench-actions {
      display: flex;
      align-items: center;
      gap: 12px;
    }

    .dock-toggles {
      display: inline-flex;
      align-items: center;
      background: var(--surface-container-high);
      border-radius: var(--radius-pill);
      padding: 2px;
      border: 1px solid var(--border-subtle);
    }

    .dock-toggle-btn {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      padding: 5px 10px;
      border: none;
      background: transparent;
      color: var(--text-muted);
      font-size: var(--font-label);
      font-weight: 600;
      border-radius: var(--radius-pill);
      cursor: pointer;
      transition: all 0.16s ease;

      &:hover {
        color: var(--text-primary);
        background: var(--surface-container-highest);
      }

      &_active {
        background: var(--surface);
        color: var(--accent);
        box-shadow: 0 1px 4px rgba(0, 0, 0, 0.12);
      }
    }

    .btn-glyph {
      font-size: 14px;
    }

    .save-btn {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-weight: 600;
    }

    .save-icon {
      font-size: 14px;
    }

    @media (max-width: 960px) {
      .toggle-label {
        display: none;
      }
      .dock-toggle-btn {
        padding: 6px 8px;
      }
    }
  `,
})
export class FlowCommandBarComponent {
  readonly title = input.required<string>();
  readonly mediaType = input.required<MediaType>();
  readonly storedVersion = input(0);
  readonly hasDraft = input(false);
  readonly hasUnsavedChanges = input(false);
  readonly backupStatus = input<string | null>(null);
  readonly editorStatus = input('');
  readonly nodeCount = input(0);
  readonly saving = input(false);
  readonly validating = input(false);
  readonly canSave = input(false);
  readonly disabled = input(false);
  readonly simulatorBusy = input(false);
  readonly canCreatePack = input(false);

  readonly paletteOpen = input(true);
  readonly inspectorOpen = input(true);
  readonly trayOpen = input(false);
  readonly showConnections = input(true);

  readonly titleChange = output<string>();
  readonly saveRequested = output<void>();
  readonly validateRequested = output<void>();
  readonly simulateRequested = output<void>();
  readonly packCreationRequested = output<void>();
  readonly aiAssistantRequested = output<void>();
  readonly togglePalette = output<void>();
  readonly toggleInspector = output<void>();
  readonly toggleTray = output<void>();
  readonly toggleConnections = output<void>();

  protected readonly isEditingTitle = signal(false);

  protected readonly mediaTypeLabel = computed(() => {
    switch (this.mediaType()) {
      case 'video':
        return 'Vidéo';
      case 'music':
        return 'Musique';
      case 'image':
        return 'Image';
      case 'subtitles':
        return 'Sous-titres';
      default:
        return 'Générique';
    }
  });

  protected startEditingTitle(): void {
    this.isEditingTitle.set(true);
  }

  protected commitTitle(value: string): void {
    const trimmed = value.trim();
    if (trimmed && trimmed !== this.title()) {
      this.titleChange.emit(trimmed);
    }
    this.isEditingTitle.set(false);
  }

  protected cancelEditingTitle(): void {
    this.isEditingTitle.set(false);
  }
}
