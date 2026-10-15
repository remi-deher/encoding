import { Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
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
  imports: [MatButtonModule, MatMenuModule, StatusBadgeComponent],
  template: `
    <article class="resource-card" [class.is-selected]="active()" [attr.aria-label]="title()">
      <header class="resource-card-head">
        @if (selectable()) {
          <input
            class="resource-card-check"
            type="checkbox"
            [checked]="selected()"
            [attr.aria-label]="'Sélectionner ' + title()"
            (change)="selectedChange.emit($any($event.target).checked)"
          />
        }
        <div class="resource-card-identity">
          <button type="button" class="resource-card-title" (click)="open.emit()">{{ title() }}</button>
          <div class="resource-card-badges">
            <app-status-badge [label]="statusLabel()" [tone]="statusTone()" />
            @for (badge of badges(); track badge) {
              <span class="resource-card-badge">{{ badge }}</span>
            }
          </div>
        </div>
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
              <button mat-menu-item type="button" [class.danger]="action.tone === 'danger'" (click)="actionSelected.emit(action.id)">
                {{ action.label }}
              </button>
            }
          </mat-menu>
        }
      </header>

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
  readonly selectable = input(false);
  readonly selected = input(false);
  /** Carte dont le détail est ouvert. */
  readonly active = input(false);

  readonly open = output<void>();
  readonly selectedChange = output<boolean>();
  /** Identifiant de l'action choisie dans le menu. */
  readonly actionSelected = output<string>();

  protected readonly meterPercent = computed(() => {
    const meter = this.meter();
    return meter && meter.max > 0 ? Math.min(100, Math.round((meter.value / meter.max) * 100)) : 0;
  });
  protected readonly visibleTags = computed(() => {
    const tags = [...this.tags()].sort((a, b) => Number(!!b.highlighted) - Number(!!a.highlighted));
    return tags.slice(0, this.maxTags());
  });
  protected readonly hiddenTagCount = computed(() => Math.max(0, this.tags().length - this.maxTags()));
}
