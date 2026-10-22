import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { IconComponent, IconName } from './icon.component';

/**
 * Gabarit commun d’une section de Paramètres : pastille, titre h2, description, puce d’état et
 * actions d’en-tête (`section-actions`), puis le contenu (lignes `app-setting-row`, notices, etc.).
 */
@Component({
  selector: 'app-configuration-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    <section class="configuration-section" [attr.aria-label]="title()">
      <header class="section-heading">
        @if (icon(); as name) {
          <span class="section-icon"><app-icon [name]="name" /></span>
        }
        <div class="section-text">
          <h2>
            {{ title() }}
            @if (badge()) {
              <span class="section-badge" [attr.data-tone]="badgeTone()">{{ badge() }}</span>
            }
          </h2>
          @if (description()) {
            <p>{{ description() }}</p>
          }
        </div>
        <div class="section-actions"><ng-content select="[section-actions]" /></div>
      </header>
      <ng-content />
    </section>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .section-heading {
      display: flex;
      align-items: flex-start;
      gap: var(--space-3);
      margin: 0 var(--space-1) var(--space-3);
      flex-wrap: wrap;
    }
    .section-icon {
      display: grid;
      place-items: center;
      flex: none;
      width: 40px;
      height: 40px;
      border-radius: var(--radius-md);
      background: var(--accent-soft);
      color: var(--accent);
    }
    .section-text {
      flex: 1;
      min-width: 220px;
    }
    h2 {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin: 0;
      font-size: var(--font-title-lg);
      font-weight: 650;
    }
    p {
      margin: var(--space-1) 0 0;
      color: var(--text-secondary);
      line-height: 1.5;
    }
    .section-badge {
      padding: 2px var(--space-3);
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      color: var(--text-primary);
      font-size: var(--font-label);
      font-weight: 500;
    }
    .section-badge[data-tone='success'] {
      background: var(--success-soft);
      color: var(--success);
    }
    .section-badge[data-tone='warning'] {
      background: var(--warning-soft);
      color: var(--warning);
    }
    .section-actions {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      flex-wrap: wrap;
    }
    .section-actions:empty {
      display: none;
    }
  `,
})
export class ConfigurationSectionComponent {
  readonly title = input.required<string>();
  readonly description = input('');
  readonly icon = input<IconName | null>(null);
  /** Puce d’état à côté du titre (« 3 nœuds sur 3 connectés »). */
  readonly badge = input('');
  readonly badgeTone = input<'neutral' | 'success' | 'warning'>('neutral');
}
