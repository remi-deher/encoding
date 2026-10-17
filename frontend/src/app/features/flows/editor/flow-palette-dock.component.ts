import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { FlowComponentDefinition } from '../../../shared/models/flow-component';
import { FlowComponentPackSummary } from '../../../shared/models/flow-component-packs';
import { flowComponentColors } from '../../../shared/models/flow-presentation';

@Component({
  selector: 'app-flow-palette-dock',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatTooltipModule],
  template: `
    <aside
      class="palette-dock"
      [class.palette-dock_collapsed]="!opened()"
      aria-label="Palette des composants du flow"
    >
      @if (opened()) {
        <div class="dock-header">
          <div class="header-title-row">
            <span class="dock-glyph" aria-hidden="true">▦</span>
            <h2 class="dock-title">Composants & Packs</h2>
            <button
              type="button"
              class="dock-close-btn"
              (click)="openedChange.emit(false)"
              matTooltip="Masquer la palette (Ctrl+B)"
              aria-label="Masquer la palette"
            >
              ‹
            </button>
          </div>

          @if (insertionPending()) {
            <div class="insertion-banner" role="status">
              <div class="banner-text">
                <span class="banner-indicator" aria-hidden="true">●</span>
                <span>
                  {{ destinationLabel() ?? 'Choisissez un composant à insérer sur la liaison' }}
                </span>
              </div>
              <button
                type="button"
                class="cancel-insertion-btn"
                (click)="cancelInsertion.emit()"
                aria-label="Annuler l’insertion"
              >
                Annuler
              </button>
            </div>
          }

          <div class="search-box">
            <span class="search-icon" aria-hidden="true">🔍</span>
            <input
              type="search"
              class="search-input"
              placeholder="Rechercher un composant… (/)"
              [value]="searchQuery()"
              (input)="onSearchInput($event)"
              aria-label="Rechercher un composant par nom ou description"
            />
            @if (searchQuery()) {
              <button
                type="button"
                class="clear-search-btn"
                (click)="clearSearch()"
                aria-label="Effacer la recherche"
              >
                ✕
              </button>
            }
          </div>

          <div class="category-chips" role="tablist" aria-label="Catégories de composants">
            <button
              type="button"
              class="chip-btn"
              [class.chip-btn_active]="activeCategory() === 'all'"
              (click)="activeCategory.set('all')"
              role="tab"
              [attr.aria-selected]="activeCategory() === 'all'"
            >
              Tous ({{ components().length }})
            </button>
            <button
              type="button"
              class="chip-btn"
              [class.chip-btn_active]="activeCategory() === 'video'"
              (click)="activeCategory.set('video')"
              role="tab"
              [attr.aria-selected]="activeCategory() === 'video'"
            >
              Vidéo
            </button>
            <button
              type="button"
              class="chip-btn"
              [class.chip-btn_active]="activeCategory() === 'audio'"
              (click)="activeCategory.set('audio')"
              role="tab"
              [attr.aria-selected]="activeCategory() === 'audio'"
            >
              Audio
            </button>
            <button
              type="button"
              class="chip-btn"
              [class.chip-btn_active]="activeCategory() === 'subtitles'"
              (click)="activeCategory.set('subtitles')"
              role="tab"
              [attr.aria-selected]="activeCategory() === 'subtitles'"
            >
              Sous-titres
            </button>
            <button
              type="button"
              class="chip-btn"
              [class.chip-btn_active]="activeCategory() === 'general'"
              (click)="activeCategory.set('general')"
              role="tab"
              [attr.aria-selected]="activeCategory() === 'general'"
            >
              Logique
            </button>
            <button
              type="button"
              class="chip-btn chip-btn_packs"
              [class.chip-btn_active]="activeCategory() === 'packs'"
              (click)="activeCategory.set('packs')"
              role="tab"
              [attr.aria-selected]="activeCategory() === 'packs'"
            >
              Packs ({{ packCatalog().length }})
            </button>
          </div>
        </div>

        <div class="dock-body">
          @if (activeCategory() === 'packs') {
            <div class="catalog-list">
              @for (pack of filteredPacks(); track pack.id) {
                <div
                  class="pack-card"
                  tabindex="0"
                  role="button"
                  (click)="packSelected.emit(pack)"
                  (keydown.enter)="packSelected.emit(pack)"
                  matTooltip="Cliquer pour insérer ce pack dans le flow"
                >
                  <div class="pack-card-header">
                    <span class="pack-name">{{ pack.name }}</span>
                    <span class="pack-badge">{{ pack.componentCount }} blocs</span>
                  </div>
                  <div class="pack-card-meta">
                    <span class="meta-tag">Média : {{ pack.mediaType }}</span>
                  </div>
                  <button type="button" class="insert-pack-btn">Insérer le pack →</button>
                </div>
              } @empty {
                <p class="empty-state">
                  Aucun pack disponible. Créez-en depuis un flow publié ou dans les Librairies.
                </p>
              }
            </div>
          } @else {
            <div class="catalog-list">
              @for (component of filteredComponents(); track component.id) {
                <div
                  class="component-card"
                  tabindex="0"
                  role="button"
                  [style.--component-accent]="colors(component).color"
                  (click)="componentSelected.emit(component)"
                  (keydown.enter)="componentSelected.emit(component)"
                  matTooltip="{{ component.description }}"
                >
                  <div class="component-card-top">
                    <span class="component-kind">{{ kindLabel(component.kind) }}</span>
                    <span class="component-plugin">{{ component.pluginName }}</span>
                  </div>
                  <span class="component-title">{{ component.name }}</span>
                  <p class="component-desc">{{ component.description }}</p>
                  <div class="component-card-footer">
                    <span class="outputs-tag">
                      {{ component.outputs.length }}
                      {{ component.outputs.length > 1 ? 'sorties' : 'sortie' }}
                    </span>
                    <span class="add-hint">+ Ajouter</span>
                  </div>
                </div>
              } @empty {
                <p class="empty-state">Aucun composant ne correspond à cette recherche.</p>
              }
            </div>
          }
        </div>
      }
    </aside>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
    .palette-dock {
      width: 320px;
      height: 100%;
      background: var(--surface-container-low);
      border-right: 1px solid var(--border-subtle);
      display: flex;
      flex-direction: column;
      transition: width 0.22s var(--motion-emphasized);
      overflow: hidden;
      &_collapsed {
        width: 0;
        border-right: none;
      }
    }
    .dock-header {
      padding: 12px 12px 8px;
      border-bottom: 1px solid var(--border-subtle);
      display: flex;
      flex-direction: column;
      gap: 8px;
      background: var(--surface-container);
    }
    .header-title-row {
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
    .dock-close-btn {
      width: 28px;
      height: 28px;
      border-radius: var(--radius-xs);
      border: none;
      background: transparent;
      color: var(--text-secondary);
      font-size: 20px;
      line-height: 1;
      display: grid;
      place-items: center;
      cursor: pointer;
      &:hover {
        background: var(--surface-container-highest);
        color: var(--text-primary);
      }
    }
    .insertion-banner {
      background: var(--accent-soft);
      border-radius: var(--radius-sm);
      padding: 6px 10px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      font-size: 11px;
      color: var(--accent);
      font-weight: 600;
    }
    .banner-text {
      display: flex;
      align-items: center;
      gap: 6px;
      min-width: 0;
      line-height: 1.3;
    }
    .banner-indicator {
      font-size: 8px;
      animation: blink 1.2s infinite ease-in-out;
    }
    @keyframes blink {
      0%,
      100% {
        opacity: 1;
      }
      50% {
        opacity: 0.3;
      }
    }
    .cancel-insertion-btn {
      border: none;
      background: transparent;
      color: var(--text-primary);
      font-size: 11px;
      font-weight: 700;
      cursor: pointer;
      text-decoration: underline;
      white-space: nowrap;
    }
    .search-box {
      position: relative;
      display: flex;
      align-items: center;
    }
    .search-icon {
      position: absolute;
      left: 10px;
      font-size: 12px;
      color: var(--text-muted);
      pointer-events: none;
    }
    .search-input {
      width: 100%;
      height: 32px;
      padding: 0 28px 0 30px;
      background: var(--surface);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-sm);
      color: var(--text-primary);
      font-size: 12px;
      outline: none;
      &:focus {
        border-color: var(--accent);
      }
    }
    .clear-search-btn {
      position: absolute;
      right: 8px;
      border: none;
      background: transparent;
      color: var(--text-muted);
      cursor: pointer;
      font-size: 12px;
    }
    .category-chips {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }
    .chip-btn {
      padding: 2px 7px;
      border-radius: var(--radius-pill);
      border: 1px solid var(--border-subtle);
      background: var(--surface);
      color: var(--text-secondary);
      font-size: 11px;
      font-weight: 600;
      cursor: pointer;
      &:hover {
        background: var(--surface-container-high);
      }
      &_active {
        background: var(--accent);
        color: var(--on-accent);
        border-color: var(--accent);
      }
      &_packs {
        border-style: dashed;
      }
    }
    .dock-body { flex: 1; overflow-y: auto; padding: 10px; }
    .catalog-list { display: flex; flex-direction: column; gap: 8px; }
    .component-card, .pack-card {
      padding: 10px 12px; background: var(--surface); border-radius: var(--radius-md);
      cursor: pointer; display: flex; flex-direction: column; gap: 4px;
      transition: transform 0.15s ease, box-shadow 0.15s ease;
      &:hover { transform: translateY(-1px); box-shadow: var(--elevation-1); }
      &:focus-visible { outline: 2px solid var(--accent); }
    }
    .component-card {
      border: 1px solid var(--border-subtle);
      border-left: 4px solid var(--component-accent, var(--accent));
      &:hover { border-color: var(--border-strong); }
    }
    .component-card-top, .pack-card-header, .component-card-footer {
      display: flex; align-items: center; justify-content: space-between; gap: 6px;
    }
    .component-card-top { font-size: 10px; }
    .component-kind { font-weight: 700; color: var(--component-accent, var(--accent)); text-transform: uppercase; letter-spacing: 0.04em; }
    .component-plugin, .outputs-tag, .pack-card-meta { color: var(--text-muted); font-size: 10px; }
    .component-title, .pack-name { font-size: 13px; font-weight: 700; color: var(--text-primary); }
    .component-desc {
      margin: 0; font-size: 11px; color: var(--text-secondary); line-height: 1.35;
      display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden;
    }
    .component-card-footer {
      margin-top: 4px; padding-top: 4px; border-top: 1px dashed var(--border-subtle); font-size: 10px;
    }
    .add-hint, .insert-pack-btn { color: var(--accent); font-weight: 600; }
    .pack-card { border: 1px dashed var(--accent); }
    .pack-badge {
      font-size: 10px; padding: 2px 6px; border-radius: var(--radius-pill); background: var(--accent-soft);
      color: var(--accent); font-weight: 600;
    }
    .insert-pack-btn {
      align-self: flex-end;
      border: none;
      background: transparent;
      font-size: 11px;
      cursor: pointer;
    }
    .empty-state {
      padding: 24px 12px;
      text-align: center;
      color: var(--text-muted);
      font-size: 12px;
      line-height: 1.5;
    }
  `,
})
export class FlowPaletteDockComponent {
  readonly opened = input(true);
  readonly components = input<readonly FlowComponentDefinition[]>([]);
  readonly packCatalog = input<readonly FlowComponentPackSummary[]>([]);
  readonly insertionPending = input(false);
  readonly destinationLabel = input<string | null>(null);

  readonly openedChange = output<boolean>();
  readonly componentSelected = output<FlowComponentDefinition>();
  readonly packSelected = output<FlowComponentPackSummary>();
  readonly cancelInsertion = output<void>();

  protected readonly searchQuery = signal('');
  protected readonly activeCategory = signal<
    'all' | 'video' | 'audio' | 'subtitles' | 'general' | 'packs'
  >('all');

  protected readonly filteredComponents = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    const category = this.activeCategory();
    return this.components().filter((comp) => {
      const matchCat = category === 'all' || comp.mediaType === category;
      if (!matchCat) return false;
      if (!query) return true;
      return (
        comp.name.toLowerCase().includes(query) ||
        comp.description.toLowerCase().includes(query) ||
        comp.pluginName.toLowerCase().includes(query)
      );
    });
  });

  protected readonly filteredPacks = computed(() => {
    const query = this.searchQuery().trim().toLowerCase();
    return this.packCatalog().filter((pack) => {
      if (!query) return true;
      return pack.name.toLowerCase().includes(query);
    });
  });

  protected colors(component: FlowComponentDefinition) {
    return flowComponentColors(component);
  }

  protected kindLabel(kind: string): string {
    switch (kind) {
      case 'action':
        return 'Action';
      case 'condition':
        return 'Condition';
      case 'tool':
        return 'Outil';
      case 'variable':
        return 'Variable';
      case 'input':
        return 'Entrée';
      default:
        return kind;
    }
  }

  protected onSearchInput(event: Event): void {
    const target = event.target as HTMLInputElement;
    this.searchQuery.set(target.value);
  }

  protected clearSearch(): void {
    this.searchQuery.set('');
  }
}
