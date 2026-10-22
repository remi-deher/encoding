import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { AiAssistantModelOption } from '../../../shared/models/ai-tools';
import { AiModelSelectorComponent } from './ai-model-selector.component';

export const AI_ASSISTANT_FILE_ACCEPT =
  'Dockerfile,Makefile,.env,.txt,.md,.markdown,.rst,.csv,.tsv,.log,.out,.json,.jsonc,.json5,.yaml,.yml,.xml,.toml,.ini,.conf,.config,.properties,.srt,.ass,.ssa,.vtt,.sub,.smi,.sami,.ttml,.dfxp,.mpl,.lrc,.sbv,.ffmetadata,.ffpreset,.ffscript,.ffconcat,.filter,.m3u,.m3u8,.nfo,.prx,.avs,.avsi,.vpy,.qpfile,.chapters,.edl,.cue,.xspf,.pls,.mpd,.csproj,.sln,.props,.targets,.editorconfig,.gitignore,.cs,.fs,.vb,.ts,.tsx,.js,.jsx,.mjs,.cjs,.py,.pyw,.sh,.bash,.zsh,.ps1,.psm1,.bat,.cmd,.sql,.html,.htm,.css,.scss,.less,.proto,.graphql,.gql';

@Component({
  selector: 'app-ai-conversation-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatInputModule,
    MatFormFieldModule,
    AiModelSelectorComponent,
  ],
  template: `
    <form class="composer" (ngSubmit)="send.emit()">
      <mat-form-field class="prompt-field" appearance="outline" subscriptSizing="dynamic">
        <textarea
          matInput
          name="prompt"
          aria-label="Votre question"
          placeholder="Votre question"
          rows="2"
          maxlength="4000"
          [ngModel]="text()"
          (ngModelChange)="text.set($event)"
          [disabled]="locked()"
          (keydown)="onKeydown($event)"
        ></textarea>
      </mat-form-field>
      @if (attachmentName()) {
        <div class="file-attachment">
          <span aria-hidden="true">📎</span>
          <span class="file-name">{{ attachmentName() }}</span>
          <button
            mat-icon-button
            type="button"
            aria-label="Retirer le fichier joint"
            [disabled]="locked()"
            (click)="clearAttachment()"
          >
            ×
          </button>
        </div>
      }
      <div class="composer-tools">
        <button
          mat-icon-button
          type="button"
          aria-label="Ressources, fichiers et outils de l’assistant"
          title="Ressources, fichiers et outils"
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
        <button
          mat-icon-button
          type="button"
          class="attach-file"
          aria-label="Joindre un fichier texte"
          title="Joindre un fichier"
          [disabled]="locked()"
          (click)="fileInput.click()"
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
            <path
              d="m20 11-8.5 8.5a5 5 0 0 1-7-7L13 4a3.3 3.3 0 0 1 4.7 4.7l-8.6 8.6a1.7 1.7 0 0 1-2.4-2.4L14.5 7"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
        </button>
        <input
          #fileInput
          class="visually-hidden"
          type="file"
          multiple
          [accept]="fileAccept"
          aria-label="Choisir des fichiers texte à joindre"
          (change)="filesChosen.emit($event)"
        />
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
          (settingsOpenChange)="settingsOpenChange.emit($event)"
        />
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
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      padding: 0;
      margin: -1px;
      overflow: hidden;
      clip: rect(0, 0, 0, 0);
      white-space: nowrap;
      border: 0;
    }
    .file-attachment {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      width: fit-content;
      max-width: 100%;
      margin: 0 0 var(--space-2) var(--space-2);
      padding: 0 var(--space-2);
      border-radius: var(--radius-md);
      background: var(--surface-container-highest);
    }
    .file-name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: var(--font-label);
    }
    .file-attachment button {
      flex: 0 0 auto;
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
      order: 1;
    }
    .composer-tools > button:first-child {
      order: 2;
    }
    .composer-tools > button:not(:first-child):not(.send-button):not(.attach-file) {
      order: 3;
    }
    .attach-file {
      order: 2;
    }
    .send-button {
      flex: 0 0 40px;
      margin-left: auto;
    }
    @container (max-width: 420px) {
      .composer-tools {
        display: grid;
        grid-template-columns: 40px 40px minmax(0, 1fr) 40px;
      }
      app-ai-model-selector {
        grid-column: 1/-1;
        grid-row: 1;
      }
      .composer-tools > button:first-child {
        grid-column: 1;
        grid-row: 2;
      }
      .attach-file {
        grid-column: 2;
        grid-row: 2;
      }
      .composer-tools > button:not(:first-child):not(.send-button):not(.attach-file) {
        grid-column: 3;
        grid-row: 2;
        justify-self: end;
      }
      .send-button {
        grid-column: 4;
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
  readonly attachmentName = input<string | null>(null);
  readonly typeChange = output<string>();
  readonly modelChange = output<string>();
  readonly effortChange = output<string | null>();
  readonly settingsOpenChange = output<boolean>();
  readonly send = output<void>();
  readonly cancel = output<void>();
  readonly attach = output<void>();
  readonly fileRemoved = output<void>();
  readonly filesChosen = output<Event>();
  protected readonly fileAccept = AI_ASSISTANT_FILE_ACCEPT;
  protected clearAttachment(): void {
    this.fileRemoved.emit();
  }
  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      if (this.canSend()) this.send.emit();
    }
  }
}
