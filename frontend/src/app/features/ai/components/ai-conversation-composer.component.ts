import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatMenuModule } from '@angular/material/menu';
import { AiAssistantModelOption } from '../../../shared/models/ai-tools';
import { AiModelSelectorComponent } from './ai-model-selector.component';

@Component({
  selector: 'app-ai-conversation-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatInputModule,
    MatFormFieldModule,
    MatMenuModule,
    AiModelSelectorComponent,
  ],
  template: `
    <form class="composer" (ngSubmit)="send.emit()">
      <mat-form-field class="prompt-field" appearance="outline" subscriptSizing="dynamic">
        <mat-label>Votre question</mat-label>
        <textarea
          matInput
          name="prompt"
          rows="2"
          maxlength="4000"
          [ngModel]="text()"
          (ngModelChange)="text.set($event)"
          [disabled]="locked()"
          (keydown)="onKeydown($event)"
        ></textarea>
      </mat-form-field>
      <div class="composer-tools">
        <button
          mat-icon-button
          type="button"
          aria-label="Joindre une ressource"
          [attr.aria-expanded]="attachmentOpen()"
          [disabled]="locked()"
          (click)="attach.emit()"
        >
          <svg
            viewBox="0 0 24 24"
            width="20"
            height="20"
            fill="none"
            stroke="currentColor"
            stroke-width="1.8"
            aria-hidden="true"
          >
            <path d="M12 5v14M5 12h14" stroke-linecap="round" />
          </svg>
        </button>
        <app-ai-model-selector
          [options]="options()"
          [selectedKey]="selectedKey()"
          [fallbackProvider]="provider()"
          [unavailableModel]="unavailableModel()"
          [effort]="effort()"
          [disabled]="locked() || loading()"
          (typeChange)="typeChange.emit($event)"
          (modelChange)="modelChange.emit($event)"
          (effortChange)="effortChange.emit($event)"
        />
        <button
          mat-icon-button
          type="button"
          aria-label="Options de la conversation"
          [matMenuTriggerFor]="menu"
        >
          <svg viewBox="0 0 24 24" width="20" height="20" fill="currentColor" aria-hidden="true">
            <circle cx="5" cy="12" r="1.6" />
            <circle cx="12" cy="12" r="1.6" />
            <circle cx="19" cy="12" r="1.6" />
          </svg>
        </button>
        @if (busy()) {
          <button
            mat-mini-fab
            class="send-button"
            type="button"
            aria-label="Interrompre"
            [disabled]="cancelling()"
            (click)="cancel.emit()"
          >
            <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor" aria-hidden="true">
              <rect x="6" y="6" width="12" height="12" rx="2" />
            </svg>
          </button>
        } @else {
          <button
            mat-mini-fab
            class="send-button"
            type="submit"
            [attr.aria-label]="resuming() ? 'Reprendre la demande' : 'Envoyer'"
            [disabled]="!canSend()"
          >
            <svg
              viewBox="0 0 24 24"
              width="20"
              height="20"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              aria-hidden="true"
            >
              <path d="M12 19V5m-6 6 6-6 6 6" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
        }
      </div>
      <mat-menu #menu="matMenu">
        <button
          mat-menu-item
          type="button"
          [disabled]="locked() || loading()"
          (click)="refresh.emit()"
        >
          Actualiser les modèles
        </button>
        <button mat-menu-item type="button" [disabled]="locked()" (click)="tools.emit()">
          Outils de lecture
        </button>
        <button mat-menu-item type="button" [disabled]="busy()" (click)="newConversation.emit()">
          Nouvelle conversation
        </button>
      </mat-menu>
    </form>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
      container-type: inline-size;
    }
    .composer {
      padding: var(--space-2);
      background: var(--surface-container-low);
      border-radius: var(--radius-xl);
    }
    .prompt-field {
      width: 100%;
    }
    textarea {
      resize: none;
      min-height: 3rem;
      max-height: 8rem;
    }
    .composer-tools {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-1);
    }
    app-ai-model-selector {
      flex: 1 1 16rem;
    }
    .send-button {
      flex: 0 0 40px;
      margin-left: auto;
    }
    @container (max-width: 420px) {
      .composer-tools {
        display: grid;
        grid-template-columns: 40px minmax(0, 1fr) 40px;
      }
      app-ai-model-selector {
        grid-column: 1/-1;
        grid-row: 1;
      }
      .composer-tools > button:first-child {
        grid-column: 1;
        grid-row: 2;
      }
      .composer-tools > button:not(:first-child):not(.send-button) {
        grid-column: 2;
        grid-row: 2;
        justify-self: end;
      }
      .send-button {
        grid-column: 3;
        grid-row: 2;
      }
    }
  `,
})
export class AiConversationComposerComponent {
  readonly text = model('');
  readonly options = input<readonly AiAssistantModelOption[]>([]);
  readonly selectedKey = input('');
  readonly provider = input('');
  readonly unavailableModel = input<string | null>(null);
  readonly effort = input<string | null>(null);
  readonly locked = input(false);
  readonly loading = input(false);
  readonly busy = input(false);
  readonly cancelling = input(false);
  readonly resuming = input(false);
  readonly canSend = input(false);
  readonly attachmentOpen = input(false);
  readonly typeChange = output<string>();
  readonly modelChange = output<string>();
  readonly effortChange = output<string | null>();
  readonly send = output<void>();
  readonly cancel = output<void>();
  readonly attach = output<void>();
  readonly refresh = output<void>();
  readonly tools = output<void>();
  readonly newConversation = output<void>();
  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      if (this.canSend()) this.send.emit();
    }
  }
}
