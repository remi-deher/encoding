import { IconComponent } from './icon.component';
import { MatExpansionModule } from '@angular/material/expansion';
import { ChangeDetectionStrategy, Component, input, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { ExecutionPipelineStep, RuntimeCommandLog, executionPhaseLabel } from '../models/execution';
import { FileDownload } from '../services/file-download';
import { NotificationService } from '../services/notification.service';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-execution-pipeline-logs',
  imports: [IconComponent, MatButtonModule, MatExpansionModule],
  template: `
    <section class="pipeline-logs" aria-label="Pipeline et journaux de commandes">
      <header class="pipeline-logs__header">
        <div>
          <h3>Pipeline d’exécution</h3>
          <p>{{ stepCount() }} étape(s) · {{ commandCount() }} commande(s)</p>
        </div>
        <div class="pipeline-logs__actions" aria-label="Mode d’affichage et export des logs">
          <div class="pipeline-logs__view-toggle" role="group" aria-label="Affichage du pipeline">
            <button
              matButton="outlined"
              type="button"
              [attr.aria-pressed]="viewMode() === 'simple'"
              (click)="viewMode.set('simple')"
            >
              Vue simplifiée
            </button>
            <button
              matButton="outlined"
              type="button"
              [attr.aria-pressed]="viewMode() === 'detailed'"
              (click)="viewMode.set('detailed')"
            >
              Vue détaillée
            </button>
          </div>
          <button
            matButton="outlined"
            type="button"
            [disabled]="commandCount() === 0"
            (click)="copyLogs()"
          >
            Copier les logs
          </button>
          <button
            matButton="outlined"
            type="button"
            [disabled]="commandCount() === 0"
            (click)="exportLogs()"
          >
            Exporter
          </button>
        </div>
      </header>

      @if (phaseLabel(phase()); as label) {
        <p role="status">{{ label }}</p>
      }
      @if (feedback()) {
        <p class="pipeline-logs__feedback" role="status">{{ feedback() }}</p>
      } @else if (steps().length === 0) {
        <p class="pipeline-logs__empty">Aucune étape enregistrée pour cette exécution.</p>
      } @else {
        <ol class="pipeline-steps">
          @for (step of steps(); track step.invocationId) {
            <li class="pipeline-step">
              <article class="pipeline-step__card" [attr.data-kind]="step.componentKind">
                <header class="pipeline-step__header">
                  <div>
                    <h4>{{ step.nodeName }}</h4>
                    <p>{{ statusLabel(step.status) }} · {{ duration(step) }}</p>
                  </div>
                  <span class="pipeline-step__kind">{{ kindLabel(step.componentKind) }}</span>
                </header>
                @if (step.diagnostic; as diagnostic) {
                  <p role="status">{{ diagnostic.message }}</p>
                }
                @if (step.outputLabel) {
                  <p class="pipeline-step__decision">
                    <strong
                      >{{ step.componentKind === 'condition' ? 'Décision' : 'Sortie' }} :</strong
                    >
                    {{ step.outputLabel }}
                  </p>
                }
                @if (viewMode() === 'detailed') {
                  @if (step.commands.length === 0) {
                    <p class="pipeline-step__empty">
                      Cette étape n’a lancé aucune commande système.
                    </p>
                  } @else {
                    <section class="pipeline-step__commands" aria-label="Commandes de cette étape">
                      <ol class="pipeline-commands">
                        @for (command of step.commands; track command.id) {
                          <li class="pipeline-command">
                            <code class="pipeline-command__line">{{ command.command }}</code>
                            <p class="pipeline-command__status">
                              @if (command.exitCode === null) {
                                {{
                                  command.finishedAt === null &&
                                  (step.status === 'running' || step.status === 'cancelling')
                                    ? 'Commande en cours'
                                    : 'Processus interrompu ou code de sortie indisponible'
                                }}
                              } @else {
                                Code de sortie : {{ command.exitCode }}
                              }
                              @if (commandDuration(command); as dur) {
                                · {{ dur }}
                              }
                              @if (
                                command.progressPercent !== null &&
                                command.progressPercent !== undefined
                              ) {
                                · Contrôle : {{ command.progressPercent.toFixed(1) }} %
                              }
                              @if (command.truncated) {
                                · Sortie tronquée
                              }
                            </p>
                            @if (command.standardError) {
                              <mat-expansion-panel class="command-output">
                                <mat-expansion-panel-header
                                  >Erreur standard</mat-expansion-panel-header
                                >
                                <pre>{{ command.standardError }}</pre>
                              </mat-expansion-panel>
                            }
                            @if (command.standardOutput) {
                              <mat-expansion-panel class="command-output">
                                <mat-expansion-panel-header
                                  >Sortie standard</mat-expansion-panel-header
                                >
                                <pre>{{ command.standardOutput }}</pre>
                              </mat-expansion-panel>
                            }
                          </li>
                        }
                      </ol>
                    </section>
                  }
                } @else if (step.commands.length) {
                  <p class="pipeline-step__command-count">{{ step.commands.length }} commande(s)</p>
                }
              </article>
              @if (nextExecutedStep(step, $index); as next) {
                <div class="pipeline-link" [attr.aria-label]="'Liaison vers ' + next.nodeName">
                  <span>{{ step.outputLabel ?? 'Suite' }}</span>
                  <span class="pipeline-link__arrow" aria-hidden="true"
                    ><app-icon name="down"
                  /></span>
                </div>
              }
            </li>
          }
        </ol>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
      margin-block: 1rem;
    }
    .pipeline-logs {
      display: grid;
      gap: var(--space-3);
    }
    .pipeline-logs__header,
    .pipeline-step__header {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: var(--space-3);
    }
    .pipeline-logs__header h3,
    .pipeline-step__header h4,
    .pipeline-logs__header p,
    .pipeline-step__header p,
    .pipeline-logs__empty,
    .pipeline-step__empty,
    .pipeline-command__status {
      margin: 0;
    }
    .pipeline-logs__header h3 {
      color: var(--text-primary);
      font-size: var(--font-body);
    }
    .pipeline-logs__header p,
    .pipeline-step__header p,
    .pipeline-step__header > span,
    .pipeline-logs__empty,
    .pipeline-step__empty,
    .pipeline-command__status {
      color: var(--text-secondary);
      font-size: var(--font-body);
    }
    .pipeline-logs__actions {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-2);
    }
    .pipeline-logs__view-toggle {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .pipeline-logs__view-toggle [aria-pressed='true'] {
      border-color: var(--accent);
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .pipeline-steps,
    .pipeline-commands {
      display: grid;
      gap: 10px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .pipeline-step {
      display: grid;
      justify-items: stretch;
      gap: 0;
      margin-inline: auto;
      width: min(100%, 680px);
    }
    .pipeline-step__card {
      display: grid;
      gap: 10px;
      padding: 14px;
      border: 1px solid var(--border-subtle);
      border-inline-start: 4px solid var(--accent);
      border-radius: var(--radius-md);
      background: var(--surface);
    }
    .pipeline-step__card[data-kind='condition'] {
      border-inline-start-color: var(--warning);
    }
    .pipeline-step__card[data-kind='input'] {
      border-inline-start-color: var(--tone-blue);
    }
    .pipeline-step__card[data-kind='terminal'] {
      border-inline-start-color: var(--accent);
    }
    .pipeline-step__header h4 {
      color: var(--text-primary);
      font-size: var(--font-body);
    }
    .pipeline-step__kind {
      flex: 0 0 auto;
      padding: var(--space-1) var(--space-2);
      border-radius: var(--radius-pill);
      background: var(--surface-muted);
      color: var(--text-secondary);
      font-size: var(--font-body);
    }
    .pipeline-step__decision {
      margin: 0;
      padding: 9px 11px;
      border-left: 3px solid var(--accent);
      border-radius: 0 6px 6px 0;
      background: var(--surface-muted);
      color: var(--text-primary);
      font-size: var(--font-body);
    }
    .pipeline-step__command-count {
      margin: 0;
      color: var(--text-secondary);
      font-size: var(--font-body);
    }
    .pipeline-link {
      display: grid;
      justify-items: center;
      gap: 2px;
      min-height: 42px;
      color: var(--text-secondary);
      font-size: var(--font-body);
    }
    .pipeline-link__arrow {
      color: var(--accent-strong);
      font-size: var(--font-title-lg);
      line-height: 1;
    }
    .pipeline-command {
      display: grid;
      gap: var(--space-2);
      padding: 10px;
      border-radius: var(--radius-md);
      background: var(--surface-muted);
    }
    .pipeline-command__line {
      display: block;
      overflow-wrap: anywhere;
      color: var(--text-primary);
      font:
        14px/1.5 ui-monospace,
        Consolas,
        monospace;
    }
    .pipeline-command .command-output {
      min-width: 0;
      box-shadow: none;
      border: 1px solid var(--border-subtle);
    }
    .pipeline-command pre {
      margin: var(--space-2) 0 0;
      overflow-wrap: anywhere;
      white-space: pre-wrap;
      color: var(--text-secondary);
      font:
        14px/1.5 ui-monospace,
        Consolas,
        monospace;
    }
    @media (max-width: 760px) {
      .pipeline-logs__header,
      .pipeline-step__header {
        flex-direction: column;
      }
    }
  `,
})
export class ExecutionPipelineLogsComponent {
  readonly executionId = input('execution');
  readonly steps = input<readonly ExecutionPipelineStep[]>([]);
  readonly feedback = input('');
  readonly phase = input<string | null>(null);
  protected readonly phaseLabel = executionPhaseLabel;
  protected readonly viewMode = signal<'simple' | 'detailed'>('simple');
  private readonly downloads = inject(FileDownload);
  private readonly notifications = inject(NotificationService);
  protected readonly stepCount = computed(() => this.steps().length);
  protected readonly commandCount = computed(() =>
    this.steps().reduce((count, step) => count + step.commands.length, 0),
  );

  protected statusLabel(status: ExecutionPipelineStep['status']): string {
    return {
      completed: 'Terminé',
      failed: 'Échec',
      cancelled: 'Annulé',
      cancelling: 'Annulation demandée',
      running: 'En cours',
    }[status];
  }

  protected kindLabel(kind: string): string {
    return (
      {
        condition: 'Condition',
        input: 'Entrée',
        terminal: 'Sortie',
        execution: 'Traitement',
        preparation: 'Préparation',
        action: 'Action',
        variable: 'Variable',
        script: 'Script',
        unknown: 'Bloc',
      }[kind] ?? kind
    );
  }

  protected commandDuration(command: RuntimeCommandLog): string | null {
    if (!command.finishedAt) return null;
    const elapsed = Math.max(0, Date.parse(command.finishedAt) - Date.parse(command.startedAt));
    if (elapsed < 1000) return `${elapsed} ms`;
    const seconds = Math.floor(elapsed / 1000);
    return seconds >= 60
      ? `${Math.floor(seconds / 60)} min ${seconds % 60} s`
      : `${(elapsed / 1000).toFixed(elapsed < 10_000 ? 1 : 0)} s`;
  }

  protected duration(step: ExecutionPipelineStep): string {
    const end = step.completedAt ? Date.parse(step.completedAt) : Date.now();
    const elapsed = Math.max(0, end - Date.parse(step.startedAt));
    if (elapsed < 1000) return `${elapsed} ms`;
    const seconds = Math.floor(elapsed / 1000);
    return seconds >= 60
      ? `${Math.floor(seconds / 60)} min ${seconds % 60} s`
      : `${(elapsed / 1000).toFixed(elapsed < 10_000 ? 1 : 0)} s`;
  }

  protected nextExecutedStep(
    step: ExecutionPipelineStep,
    index: number,
  ): ExecutionPipelineStep | null {
    const next = this.steps()[index + 1];
    return next?.nodeId === step.nextNodeId ? next : null;
  }

  protected async copyLogs(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.serializeLogs());
      this.notifications.show('Logs copiés dans le presse-papiers.');
    } catch {
      this.notifications.show(
        'Copie impossible : autorisez l’accès au presse-papiers du navigateur.',
      );
    }
  }

  protected exportLogs(): void {
    const name = 'execution-' + this.executionId().replace(/[^a-zA-Z0-9-]/g, '_') + '.log';
    this.downloads.save(
      new Blob([this.serializeLogs()], { type: 'text/plain;charset=utf-8' }),
      name,
    );
  }

  private serializeLogs(): string {
    return this.steps()
      .map((step) => {
        const header = [
          `===== ${step.nodeName} (${step.nodeId}) =====`,
          `État : ${this.statusLabel(step.status)}`,
          `Durée : ${this.duration(step)}`,
          step.outputLabel
            ? `${step.componentKind === 'condition' ? 'Décision' : 'Sortie'} : ${step.outputLabel}`
            : '',
          step.nextNodeId ? `Prochain bloc : ${step.nextNodeId}` : '',
        ]
          .filter(Boolean)
          .join('\n');
        const commands = step.commands.map((command) => this.formatCommand(command)).join('\n\n');
        return commands ? `${header}\n${commands}` : `${header}\nAucune commande système.`;
      })
      .join('\n\n');
  }

  private formatCommand(command: RuntimeCommandLog): string {
    const status = command.exitCode === null ? 'code indisponible' : `code ${command.exitCode}`;
    return [
      `$ ${command.command}`,
      `Début : ${new Date(command.startedAt).toLocaleString('fr-FR')}`,
      `Fin : ${command.finishedAt ? new Date(command.finishedAt).toLocaleString('fr-FR') : 'inconnue'}`,
      `Durée : ${this.commandDuration(command) ?? 'en cours'}`,
      `Résultat : ${status}${command.truncated ? ' (sortie tronquée)' : ''}`,
      command.standardOutput ? `\n--- Sortie standard ---\n${command.standardOutput}` : '',
      command.standardError ? `\n--- Erreur standard ---\n${command.standardError}` : '',
    ]
      .filter(Boolean)
      .join('\n');
  }
}
