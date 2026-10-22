import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatSlideToggleChange, MatSlideToggleModule } from '@angular/material/slide-toggle';
import { take } from 'rxjs';
import {
  ConfirmationDialogComponent,
  ConfirmationDialogData,
} from './confirmation-dialog.component';
import type {
  DataTableActionEvent,
  DataTableRow,
  DataTableRowAction,
} from './data-table.component';
import { IconComponent, IconName } from './icon.component';

export interface ResourceListTag {
  readonly label: string;
  /** `ok` pour un état conforme, `warn` pour une attention à porter, `neutral` sinon. */
  readonly tone?: 'neutral' | 'ok' | 'warn';
}

export interface ResourceListItem {
  readonly id: string;
  readonly name: string;
  readonly subtitle?: string;
  readonly tags: readonly ResourceListTag[];
  readonly toggle?: {
    readonly checked: boolean;
    readonly disabled?: boolean;
    readonly ariaLabel: string;
    /** Confirmation demandée à la désactivation seulement. */
    readonly confirmOff?: ConfirmationDialogData;
  };
}

export interface ResourceListToggleEvent {
  readonly id: string;
  readonly checked: boolean;
}

/** Liste de ressources en lignes empilées (maquette Bibliothèques): icône, titre, pastilles, interrupteur, actions. */
@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-resource-list',
  imports: [IconComponent, MatButtonModule, MatMenuModule, MatSlideToggleModule],
  template: `
    @if (items().length) {
      <ul class="resource-list">
        @for (item of items(); track item.id) {
          <li class="resource-item">
            <span class="resource-icon"><app-icon [name]="icon()" /></span>
            <div class="resource-title">
              <h3>{{ item.name }}</h3>
              @if (item.subtitle) {
                <p>{{ item.subtitle }}</p>
              }
            </div>
            <div class="resource-tags">
              @for (tag of item.tags; track tag.label) {
                <span class="resource-tag" [class]="'resource-tag_' + (tag.tone ?? 'neutral')">{{
                  tag.label
                }}</span>
              }
            </div>
            @if (item.toggle; as toggle) {
              <mat-slide-toggle
                class="resource-toggle"
                [checked]="toggle.checked"
                [disabled]="toggle.disabled ?? false"
                [aria-label]="toggle.ariaLabel"
                (change)="requestToggle(item, $event)"
              />
            }
            <div class="resource-actions">
              <button matButton="outlined" type="button" (click)="open.emit(item.id)">
                {{ openLabel() }}
              </button>
              @if (actions().length) {
                <button
                  matIconButton
                  type="button"
                  [matMenuTriggerFor]="menu"
                  [attr.aria-label]="'Plus d’actions pour ' + item.name"
                >
                  <app-icon name="more" />
                </button>
                <mat-menu #menu="matMenu">
                  @for (action of actions(); track action.id) {
                    <button
                      mat-menu-item
                      type="button"
                      [class.resource-menu-item_danger]="action.tone === 'danger'"
                      (click)="requestAction(action, item)"
                    >
                      {{ action.label }}
                    </button>
                  }
                </mat-menu>
              }
            </div>
          </li>
        }
      </ul>
    } @else {
      <p class="resource-empty">{{ emptyMessage() }}</p>
    }
  `,
  styleUrl: './resource-list.component.scss',
})
export class ResourceListComponent {
  private readonly dialog = inject(MatDialog);

  readonly items = input.required<readonly ResourceListItem[]>();
  readonly actions = input<readonly DataTableRowAction[]>([]);
  readonly icon = input<IconName>('pack');
  readonly openLabel = input('Ouvrir');
  readonly emptyMessage = input('Aucun résultat.');

  readonly open = output<string>();
  readonly action = output<DataTableActionEvent>();
  readonly toggleChange = output<ResourceListToggleEvent>();

  protected requestToggle(item: ResourceListItem, event: MatSlideToggleChange): void {
    const checked = event.checked;
    const confirmation = checked ? undefined : item.toggle?.confirmOff;
    if (!confirmation) {
      this.toggleChange.emit({ id: item.id, checked });
      return;
    }
    // L'interrupteur reste sur son état réel tant que la confirmation n'est pas donnée.
    event.source.checked = !checked;
    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .pipe(take(1))
      .subscribe((confirmed) => {
        if (confirmed) this.toggleChange.emit({ id: item.id, checked });
      });
  }

  protected requestAction(action: DataTableRowAction, item: ResourceListItem): void {
    const row: DataTableRow = { id: item.id, name: item.name };
    const confirmation = action.confirmation?.(row);
    if (!confirmation) {
      this.action.emit({ actionId: action.id, row });
      return;
    }
    this.dialog
      .open(ConfirmationDialogComponent, { data: confirmation })
      .afterClosed()
      .pipe(take(1))
      .subscribe((confirmed) => {
        if (confirmed) this.action.emit({ actionId: action.id, row });
      });
  }
}
