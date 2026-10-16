import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatMenuModule } from '@angular/material/menu';
import { StatusBadgeComponent, StatusTone } from './status-badge.component';

export interface ResourceCardAction {
  readonly id: string;
  readonly label: string;
  readonly tone?: 'default' | 'danger';
}

export interface ResourceCardFact {
  readonly label: string;
  readonly value: string;
}

export interface ResourceCardTag {
  readonly label: string;
  /** Mise en avant (ex. accélération matérielle). */
  readonly highlighted?: boolean;
}

export type ResourceCardTone = 'neutral' | 'lime' | 'blue' | 'purple';

export interface ResourceCardChip {
  readonly label: string;
  readonly tone?: ResourceCardTone;
}

/**
 * Bloc d'une carte à sections : un accent principal, des pastilles, une jauge, des faits, un complément repliable.
 * Les couleurs sont des tons génériques ; la page décide ce qu'ils représentent.
 */
export interface ResourceCardSection {
  readonly label: string;
  readonly primary?: ResourceCardChip;
  readonly chips?: readonly ResourceCardChip[];
  readonly hint?: string;
  readonly facts?: readonly ResourceCardFact[];
  readonly meter?: ResourceCardMeter & { readonly valueLabel: string };
  /** Éléments secondaires masqués par défaut derrière « +N <label> ». */
  readonly folded?: {
    readonly summary: string;
    readonly items: readonly string[];
    readonly hint?: string;
  };
}

export interface ResourceCardMeter {
  readonly value: number;
  readonly max: number;
  readonly label: string;
  readonly caption?: string;
}

/**
 * Carte de ressource générique (architecture 0046) : identité, état, charge, étiquettes, faits et action contextuelle.
 * Même squelette pour toutes les ressources d'un catalogue ; la page fournit les données.
 */
@Component({
  selector: 'app-resource-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatCheckboxModule, MatMenuModule, StatusBadgeComponent],
  template: `
    <article class="resource-card" [class.is-selected]="active()" [attr.aria-label]="title()">
      <header class="resource-card-head">
        @if (selectable()) {
          <mat-checkbox
            class="resource-card-check"
            [checked]="selected()"
            [attr.aria-label]="'Sélectionner ' + title()"
            (change)="selectedChange.emit($event.checked)"
          />
        }
        <div class="resource-card-identity">
          <button type="button" class="resource-card-title" (click)="open.emit()">
            {{ title() }}
          </button>
          <div class="resource-card-badges">
            <app-status-badge [label]="statusLabel()" [tone]="statusTone()" />
            @for (badge of badges(); track badge) {
              <span class="resource-card-badge">{{ badge }}</span>
            }
          </div>
        </div>
        @if (meta()) {
          <span class="resource-card-meta">{{ meta() }}</span>
        }
        @if (actions().length) {
          <button
            matIconButton
            type="button"
            class="resource-card-menu"
            [matMenuTriggerFor]="menu"
            [attr.aria-label]="'Autres actions pour ' + title()"
          >
            <span aria-hidden="true">⋮</span>
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

      @if (sections().length) {
        <div class="resource-card-sections">
          @for (section of sections(); track section.label) {
            <section class="resource-card-section">
              <h3 class="resource-card-section-label">{{ section.label }}</h3>
              @if (section.primary || section.chips?.length) {
                <div class="resource-card-chips">
                  @if (section.primary; as primary) {
                    <span
                      class="resource-card-primary"
                      [attr.data-tone]="primary.tone ?? 'neutral'"
                      >{{ primary.label }}</span
                    >
                  }
                  @for (chip of section.chips ?? []; track chip.label) {
                    <span class="resource-card-chip" [attr.data-tone]="chip.tone ?? 'neutral'">{{
                      chip.label
                    }}</span>
                  }
                </div>
              }
              @if (section.hint) {
                <p class="resource-card-hint">{{ section.hint }}</p>
              }
              @if (section.folded; as folded) {
                @if (isUnfolded(section.label)) {
                  <div class="resource-card-chips">
                    @for (item of folded.items; track item) {
                      <span class="resource-card-chip resource-card-chip_pending">{{ item }}</span>
                    }
                    <button
                      type="button"
                      class="resource-card-fold"
                      (click)="toggleFold(section.label)"
                    >
                      Masquer
                    </button>
                  </div>
                  @if (folded.hint) {
                    <p class="resource-card-hint">{{ folded.hint }}</p>
                  }
                } @else {
                  <div class="resource-card-chips">
                    <button
                      type="button"
                      class="resource-card-fold"
                      [attr.aria-expanded]="false"
                      (click)="toggleFold(section.label)"
                    >
                      {{ folded.summary }}
                    </button>
                  </div>
                }
              }
              @if (section.facts?.length) {
                <dl class="resource-card-section-facts">
                  @for (fact of section.facts ?? []; track fact.label) {
                    <div>
                      <dt>{{ fact.label }}</dt>
                      <dd>{{ fact.value }}</dd>
                    </div>
                  }
                </dl>
              }
              @if (section.meter; as sectionMeter) {
                <div class="resource-card-section-value">{{ sectionMeter.valueLabel }}</div>
                <div
                  class="resource-card-bar"
                  role="progressbar"
                  [attr.aria-valuemin]="0"
                  [attr.aria-valuemax]="sectionMeter.max"
                  [attr.aria-valuenow]="sectionMeter.value"
                  [attr.aria-label]="sectionMeter.label"
                >
                  <span [style.width.%]="percentOf(sectionMeter)"></span>
                </div>
              }
            </section>
          }
        </div>
      } @else {
        @if (meter(); as meter) {
          <div class="resource-card-meter">
            <div
              class="resource-card-bar"
              role="progressbar"
              [attr.aria-valuemin]="0"
              [attr.aria-valuemax]="meter.max"
              [attr.aria-valuenow]="meter.value"
              [attr.aria-label]="meter.label"
            >
              <span [style.width.%]="meterPercent()"></span>
            </div>
            <div class="resource-card-caption">
              <span>{{ meter.label }}</span>
              @if (meter.caption) {
                <span>{{ meter.caption }}</span>
              }
            </div>
          </div>
        }

        @if (tags().length) {
          <ul class="resource-card-tags" aria-label="Capacités">
            @for (tag of visibleTags(); track tag.label) {
              <li [class.is-highlighted]="tag.highlighted">{{ tag.label }}</li>
            }
            @if (hiddenTagCount() > 0) {
              <li class="resource-card-more">+{{ hiddenTagCount() }}</li>
            }
          </ul>
        }

        @if (facts().length) {
          <dl class="resource-card-facts">
            @for (fact of facts(); track fact.label) {
              <div>
                <dt>{{ fact.label }}</dt>
                <dd>{{ fact.value }}</dd>
              </div>
            }
          </dl>
        }

        <footer class="resource-card-foot">
          <span class="resource-card-note">{{ note() }}</span>
          <button matButton="text" type="button" (click)="open.emit()">{{ openLabel() }} ›</button>
        </footer>
      }
    </article>
  `,
  styleUrl: './resource-card.component.scss',
})
export class ResourceCardComponent {
  readonly title = input.required<string>();
  readonly statusLabel = input.required<string>();
  readonly statusTone = input<StatusTone>('neutral');
  readonly badges = input<readonly string[]>([]);
  readonly meter = input<ResourceCardMeter | null>(null);
  readonly tags = input<readonly ResourceCardTag[]>([]);
  readonly maxTags = input(3);
  readonly facts = input<readonly ResourceCardFact[]>([]);
  readonly note = input('');
  readonly openLabel = input('Ouvrir');
  readonly actions = input<readonly ResourceCardAction[]>([]);
  /** Information secondaire à droite de l'en-tête (ex. fraîcheur de l'état). */
  readonly meta = input('');
  /** Quand elle est fournie, la carte affiche ces blocs à la place de la jauge, des étiquettes, des faits et du pied. */
  readonly sections = input<readonly ResourceCardSection[]>([]);
  readonly selectable = input(false);
  readonly selected = input(false);
  /** Carte dont le détail est ouvert. */
  readonly active = input(false);

  readonly open = output<void>();
  readonly selectedChange = output<boolean>();
  /** Identifiant de l'action choisie dans le menu. */
  readonly actionSelected = output<string>();

  private readonly unfolded = signal<ReadonlySet<string>>(new Set());
  protected isUnfolded(label: string): boolean {
    return this.unfolded().has(label);
  }
  protected toggleFold(label: string): void {
    this.unfolded.update((current) => {
      const next = new Set(current);
      if (!next.delete(label)) next.add(label);
      return next;
    });
  }
  protected percentOf(meter: ResourceCardMeter): number {
    return meter.max > 0 ? Math.min(100, Math.round((meter.value / meter.max) * 100)) : 0;
  }
  protected readonly meterPercent = computed(() => {
    const meter = this.meter();
    return meter && meter.max > 0 ? Math.min(100, Math.round((meter.value / meter.max) * 100)) : 0;
  });
  protected readonly visibleTags = computed(() => {
    const tags = [...this.tags()].sort((a, b) => Number(!!b.highlighted) - Number(!!a.highlighted));
    return tags.slice(0, this.maxTags());
  });
  protected readonly hiddenTagCount = computed(() =>
    Math.max(0, this.tags().length - this.maxTags()),
  );
}
