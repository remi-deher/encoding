import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatChipsModule } from '@angular/material/chips';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { FlowDocument } from '../../../shared/models/flow-validation';

export interface FlowAiAssistantDialogData {
  readonly mediaType: string;
  readonly currentFlow?: FlowDocument | null;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-flow-ai-assistant-dialog',
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle,
    MatFormFieldModule,
    MatInputModule,
    MatChipsModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  template: `
    <section class="app-dialog flow-ai-dialog">
      <header class="app-dialog-header">
        <div class="app-dialog-heading">
          <p class="app-dialog-eyebrow">Intelligence Artificielle & Génération</p>
          <h2 mat-dialog-title>Assistant de Flow</h2>
        </div>
      </header>

      <mat-dialog-content class="app-dialog-body flow-ai-body">
        <div class="mode-tabs">
          <button
            type="button"
            mat-button
            [class.active-tab]="mode() === 'generate'"
            (click)="setMode('generate')"
          >
            <mat-icon>auto_awesome</mat-icon> Nouveau flow
          </button>
          @if (data.currentFlow) {
            <button
              type="button"
              mat-button
              [class.active-tab]="mode() === 'refactor'"
              (click)="setMode('refactor')"
            >
              <mat-icon>edit_note</mat-icon> Modifier ce flow
            </button>
            <button
              type="button"
              mat-button
              [class.active-tab]="mode() === 'explain'"
              (click)="setMode('explain')"
            >
              <mat-icon>help_outline</mat-icon> Expliquer ce flow
            </button>
          }
        </div>

        @if (mode() !== 'explain') {
          <div class="suggestions-container">
            <span class="suggestions-title">Suggestions rapides :</span>
            <div class="chips-row">
              @for (suggestion of suggestions; track suggestion) {
                <button type="button" class="suggestion-chip" (click)="applySuggestion(suggestion)">
                  {{ suggestion }}
                </button>
              }
            </div>
          </div>

          <mat-form-field appearance="outline" class="prompt-field">
            <mat-label>Décrivez votre besoin en quelques mots</mat-label>
            <textarea
              matInput
              rows="4"
              [ngModel]="prompt()"
              (ngModelChange)="prompt.set($event)"
              placeholder="Ex: Convertir en HEVC 10-bit si nécessaire, normaliser l'audio en AAC stéréo et conserver les sous-titres complets et forcés..."
            ></textarea>
          </mat-form-field>
        } @else {
          <p class="explain-intro">
            L'IA va analyser l'ensemble des blocs, conditions et connexions de votre flow actuel
            pour en rédiger une synthèse pédagogique.
          </p>
        }

        @if (explanation()) {
          <div class="explanation-box">
            <h4>Explication du flow :</h4>
            <p>{{ explanation() }}</p>
          </div>
        }

        @if (errorMessage()) {
          <div class="error-box">
            <mat-icon>error_outline</mat-icon>
            <span>{{ errorMessage() }}</span>
          </div>
        }

        @if (errorMessage() && sessionId()) {
          <p>
            Vous pouvez réessayer la demande, ou ouvrir une nouvelle session depuis le dernier
            contexte sauvegardé. Une tentative sans résultat confirmé pourra consommer du quota à
            nouveau.
          </p>
          <button matButton="tonal" type="button" [disabled]="loading()" (click)="recoverSession()">
            Reprendre dans une nouvelle session
          </button>
        }
        @if (loading()) {
          <div class="loading-state">
            <mat-spinner diameter="24"></mat-spinner>
            <span>L'IA analyse le catalogue et compose le graphe...</span>
          </div>
        }
      </mat-dialog-content>

      <mat-dialog-actions align="end">
        <button matButton="text" type="button" [disabled]="loading()" (click)="close()">
          Annuler
        </button>
        <button
          matButton="filled"
          type="button"
          [disabled]="loading() || (mode() !== 'explain' && !prompt().trim())"
          (click)="submit()"
        >
          <mat-icon>{{ mode() === 'explain' ? 'visibility' : 'auto_fix_high' }}</mat-icon>
          {{
            mode() === 'explain'
              ? 'Expliquer'
              : mode() === 'refactor'
                ? 'Appliquer modifications'
                : 'Générer le flow'
          }}
        </button>
      </mat-dialog-actions>
    </section>
  `,
  styles: [
    `
      .flow-ai-dialog {
        min-width: 440px;
        max-width: 640px;
      }
      .flow-ai-body {
        display: flex;
        flex-direction: column;
        gap: 16px;
        padding: 16px 0;
      }
      .mode-tabs {
        display: flex;
        gap: 8px;
        border-bottom: 1px solid var(--mat-sys-outline-variant, #333);
        padding-bottom: 8px;
      }
      .mode-tabs button {
        border-radius: 6px;
      }
      .active-tab {
        background: var(--mat-sys-secondary-container, #2c3e50);
        color: var(--mat-sys-on-secondary-container, #fff);
      }
      .suggestions-container {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .suggestions-title {
        font-size: 0.85rem;
        color: var(--mat-sys-on-surface-variant, #aaa);
      }
      .chips-row {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
      }
      .suggestion-chip {
        background: var(--mat-sys-surface-container, #222);
        border: 1px solid var(--mat-sys-outline-variant, #444);
        color: var(--mat-sys-on-surface, #ddd);
        padding: 4px 10px;
        border-radius: 16px;
        font-size: 0.82rem;
        cursor: pointer;
        transition: background 0.2s;
      }
      .suggestion-chip:hover {
        background: var(--mat-sys-surface-container-high, #333);
      }
      .prompt-field {
        width: 100%;
      }
      .loading-state {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 12px;
        background: var(--mat-sys-surface-container, #1f1f1f);
        border-radius: 8px;
        font-size: 0.9rem;
      }
      .error-box {
        display: flex;
        align-items: center;
        gap: 8px;
        color: var(--mat-sys-error, #f44336);
        font-size: 0.9rem;
      }
      .explanation-box {
        padding: 12px 16px;
        background: var(--mat-sys-surface-container-high, #222);
        border-radius: 8px;
        line-height: 1.5;
      }
    `,
  ],
})
export class FlowAiAssistantDialogComponent {
  readonly data = inject<FlowAiAssistantDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<FlowAiAssistantDialogComponent>);
  private readonly aiService = inject(AiToolsService);

  readonly mode = signal<'generate' | 'refactor' | 'explain'>('generate');
  readonly prompt = signal('');
  readonly loading = signal(false);
  readonly explanation = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);

  readonly sessionId = signal<string | null>(null);
  private requestId: string = crypto.randomUUID();
  private lastPrompt = '';

  readonly suggestions = [
    'Anime japonais (JP/FR + sous-titres ASS)',
    '4K HDR vers 1080p SDR HEVC',
    'Normalisation audio AAC stéréo',
    'Garde de lecture Plex avant remplacement',
  ];

  setMode(newMode: 'generate' | 'refactor' | 'explain'): void {
    this.mode.set(newMode);
    this.sessionId.set(null);
    this.requestId = crypto.randomUUID();
    this.errorMessage.set(null);
    this.explanation.set(null);
  }

  recoverSession(): void {
    const id = this.sessionId();
    if (!id || this.loading()) return;
    this.loading.set(true);
    this.aiService.forkSession(id).subscribe({
      next: (session) => {
        this.sessionId.set(session.id);
        this.requestId = crypto.randomUUID();
        this.loading.set(false);
        this.errorMessage.set(null);
      },
      error: () => {
        this.loading.set(false);
        this.errorMessage.set(
          'Le checkpoint n’est pas encore disponible ou une tentative peut encore être active. Réessayez après expiration de son bail.',
        );
      },
    });
  }

  applySuggestion(text: string): void {
    this.prompt.set(text);
  }

  submit(): void {
    if (this.loading()) return;
    if (this.lastPrompt !== this.prompt()) {
      this.requestId = crypto.randomUUID();
      this.lastPrompt = this.prompt();
    }
    this.loading.set(true);
    this.errorMessage.set(null);
    this.explanation.set(null);

    const currentMode = this.mode();
    if (currentMode === 'generate') {
      this.aiService
        .generateFlow({
          sessionId: this.sessionId(),
          requestId: this.requestId,
          mediaType: this.data.mediaType,
          userPrompt: this.prompt(),
        })
        .subscribe({
          next: (res) => {
            this.sessionId.set(res.sessionId ?? this.sessionId());
            if (res.requestId) this.requestId = res.requestId;
            if (res.success) this.requestId = crypto.randomUUID();
            this.loading.set(false);
            if (res.success && res.flow) {
              this.dialogRef.close({ flow: res.flow });
            } else {
              this.errorMessage.set(res.explanation || 'Impossible de générer un flow valide.');
            }
          },
          error: (err) => {
            this.loading.set(false);
            this.errorMessage.set(err.message ?? 'Erreur lors de la génération.');
          },
        });
    } else if (currentMode === 'refactor' && this.data.currentFlow) {
      this.aiService
        .refactorFlow({
          sessionId: this.sessionId(),
          requestId: this.requestId,
          currentFlow: this.data.currentFlow,
          userPrompt: this.prompt(),
        })
        .subscribe({
          next: (res) => {
            this.sessionId.set(res.sessionId ?? this.sessionId());
            if (res.requestId) this.requestId = res.requestId;
            if (res.success) this.requestId = crypto.randomUUID();
            this.loading.set(false);
            if (res.success && res.flow) {
              this.dialogRef.close({ flow: res.flow });
            } else {
              this.errorMessage.set(
                res.explanation || 'Impossible d’appliquer les modifications au flow.',
              );
            }
          },
          error: (err) => {
            this.loading.set(false);
            this.errorMessage.set(err.message ?? 'Erreur lors de la modification.');
          },
        });
    } else if (currentMode === 'explain' && this.data.currentFlow) {
      this.aiService
        .explainFlow({
          sessionId: this.sessionId(),
          requestId: this.requestId,
          flow: this.data.currentFlow,
        })
        .subscribe({
          next: (res) => {
            this.sessionId.set(res.sessionId ?? this.sessionId());
            if (res.requestId) this.requestId = res.requestId;
            if (res.success) this.requestId = crypto.randomUUID();
            this.loading.set(false);
            if (res.success) this.explanation.set(res.explanation ?? 'Explication non disponible.');
            else this.errorMessage.set(res.explanation ?? 'Impossible de joindre le fournisseur.');
          },
          error: (err) => {
            this.loading.set(false);
            this.errorMessage.set(err.message ?? 'Erreur lors de l’analyse.');
          },
        });
    }
  }

  close(): void {
    this.dialogRef.close();
  }
}
