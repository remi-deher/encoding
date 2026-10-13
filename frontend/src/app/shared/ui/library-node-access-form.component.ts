import { Component, OnInit, input, output, signal } from '@angular/core';
import { FormControl, FormGroup, FormRecord, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { LibraryNodeAccessMode, LibraryNodeAccessResponse } from './library-node-access-types';

export interface LibraryNodeAccessOption {
  readonly id: string;
  readonly name: string;
  readonly roles: readonly string[];
}

export interface LibraryNodeAccessValue {
  readonly nodeId: string;
  readonly mode: LibraryNodeAccessMode;
  readonly localPath: string | null;
  readonly canRead: boolean;
  readonly canWrite: boolean;
  readonly transferSourceNodeId: string | null;
}

interface NodeAccessControls {
  mode: FormControl<LibraryNodeAccessMode>;
  localPath: FormControl<string>;
  canRead: FormControl<boolean>;
  canWrite: FormControl<boolean>;
  transferSourceNodeId: FormControl<string>;
}

@Component({
  selector: 'app-library-node-access-form',
  imports: [MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, ReactiveFormsModule],
  template: `
    @if (!nodes().length) {
      <div class="access-empty" role="status">
        <strong>Aucun nœud n’est encore enregistré</strong>
        <p>Ajoutez un nœud au cluster avant de configurer l’accès aux médias de « {{ libraryName() }} ».</p>
        <button matButton="filled" type="button" (click)="cancelled.emit()">Fermer</button>
      </div>
    } @else {
      <section class="access-form" [attr.aria-label]="'Accès aux médias pour ' + libraryName()">
        <div class="access-context">
          <span class="step-label">{{ setupMode() ? 'Configuration initiale' : 'Modification des accès' }}</span>
          <p>Choisissez comment chaque nœud atteint les fichiers de « {{ libraryName() }} ». Le chemin peut être différent sur chaque machine.</p>
          <p class="access-safety-note">Un nœud sans accès ne recevra aucune tâche pour cette bibliothèque.</p>
        </div>

        @if (!reviewing()) {
          <form [formGroup]="form" (ngSubmit)="review()">
            <div class="node-access-list">
              @for (node of nodes(); track node.id) {
                @let controls = form.controls[node.id];
                <section class="node-access-card" [formGroup]="controls" [attr.aria-label]="'Accès de ' + node.name">
                  <header class="node-heading">
                    <div><strong>{{ node.name }}</strong><span>{{ rolesLabel(node.roles) }}</span></div>
                    <span class="current-access">{{ currentSummary(node.id) }}</span>
                  </header>

                  <mat-form-field appearance="outline">
                    <mat-label>Comment ce nœud accède-t-il aux fichiers ?</mat-label>
                    <mat-select formControlName="mode">
                      <mat-option value="direct">Il lit les fichiers sur son stockage</mat-option>
                      <mat-option value="transfer" [disabled]="!node.roles.includes('worker')">Un autre nœud lui transfère une copie</mat-option>
                      <mat-option value="unsupported">Ne pas utiliser ce nœud</mat-option>
                    </mat-select>
                  </mat-form-field>

                  @if (controls.controls.mode.value === 'direct') {
                    <mat-form-field appearance="outline">
                      <mat-label>Chemin des fichiers sur {{ node.name }}</mat-label>
                      <input matInput formControlName="localPath" autocomplete="off" placeholder="Ex. /media/films ou D:\\Films" />
                      <mat-hint>Indiquez le chemin local ou le point de montage visible depuis ce nœud.</mat-hint>
                    </mat-form-field>
                    <div class="permissions" aria-label="Droits de l’accès direct">
                      <mat-checkbox formControlName="canRead">Lire les fichiers source</mat-checkbox>
                      <mat-checkbox formControlName="canWrite">Écrire les résultats</mat-checkbox>
                    </div>
                  } @else if (controls.controls.mode.value === 'transfer') {
                    <p class="mode-help">Le worker encode une copie locale temporaire. Le nœud source conserve le fichier original et reçoit le résultat.</p>
                    <mat-form-field appearance="outline">
                      <mat-label>Nœud qui a accès aux fichiers</mat-label>
                      <mat-select formControlName="transferSourceNodeId">
                        @for (source of transferSources(node.id); track source.id) {
                          <mat-option [value]="source.id">{{ source.name }}</mat-option>
                        }
                      </mat-select>
                      @if (!transferSources(node.id).length) {
                        <mat-hint>Choisissez d’abord un autre nœud avec accès direct en lecture et en écriture.</mat-hint>
                      }
                    </mat-form-field>
                  } @else {
                    <p class="mode-help">Ce nœud sera exclu de la distribution des tâches liées à cette bibliothèque.</p>
                  }
                </section>
              }
            </div>
            <div class="form-actions">
              <button matButton="text" type="button" [disabled]="busy()" (click)="cancelled.emit()">{{ setupMode() ? 'Plus tard' : 'Annuler' }}</button>
              <button matButton="filled" type="submit" [disabled]="!canSave() || busy()">Vérifier les changements</button>
            </div>
          </form>
        } @else {
          <section class="access-review" aria-labelledby="access-review-title">
            <div class="review-heading">
              <h3 id="access-review-title">Vérifier les accès avant l’enregistrement</h3>
              <p>Comparez la configuration actuelle avec les choix proposés. Les modifications s’appliqueront à tous les nœuds listés.</p>
            </div>
            <div class="review-list">
              @for (node of nodes(); track node.id) {
                <article class="review-row">
                  <strong>{{ node.name }}</strong>
                  <div><span>Actuellement</span><p>{{ currentSummary(node.id) }}</p></div>
                  <div><span>Après enregistrement</span><p>{{ proposedSummary(node.id) }}</p></div>
                </article>
              }
            </div>
            <div class="form-actions">
              <button matButton="text" type="button" [disabled]="busy()" (click)="reviewing.set(false)">Revenir aux choix</button>
              <button matButton="filled" type="button" [disabled]="busy()" (click)="save()">{{ busy() ? 'Enregistrement…' : 'Enregistrer les accès' }}</button>
            </div>
          </section>
        }
      </section>
    }
  `,
  styles: `
    .access-form { display: grid; gap: 18px; }
    .access-context { display: grid; gap: 7px; }
    .access-context p { margin: 0; color: var(--text-secondary); line-height: 1.5; }
    .step-label { color: var(--accent); font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: .04em; }
    .access-safety-note { padding: 10px 12px; border-left: 3px solid var(--accent); background: var(--surface-muted); }
    form, .access-review { display: grid; gap: 16px; }
    .node-access-list { display: grid; gap: 12px; }
    .node-access-card { padding: 16px; display: grid; gap: 10px; border: 1px solid var(--border-subtle); border-radius: 10px; background: var(--surface); }
    .node-heading { display: grid; gap: 7px; }
    .node-heading div { display: flex; flex-wrap: wrap; align-items: baseline; gap: 8px; }
    .node-heading strong { color: var(--text-primary); font-size: 13px; }
    .node-heading div span { color: var(--text-muted); font-size: 11px; text-transform: capitalize; }
    .current-access { color: var(--text-secondary); font-size: 11px; }
    mat-form-field { width: 100%; }
    .permissions { display: flex; flex-wrap: wrap; gap: 16px; }
    .mode-help { margin: 0; color: var(--text-secondary); line-height: 1.5; }
    .form-actions { display: flex; justify-content: flex-end; gap: 8px; padding-top: 4px; }
    .access-empty { display: grid; justify-items: start; gap: 12px; }
    .access-empty strong { color: var(--text-primary); }
    .access-empty p { margin: 0; color: var(--text-secondary); line-height: 1.5; }
    .review-heading { display: grid; gap: 6px; }
    .review-heading h3 { margin: 0; color: var(--text-primary); font-size: 14px; }
    .review-heading p { margin: 0; color: var(--text-secondary); line-height: 1.5; }
    .review-list { display: grid; gap: 8px; }
    .review-row { padding: 12px; display: grid; gap: 10px; border: 1px solid var(--border-subtle); border-radius: 9px; background: var(--surface); }
    .review-row > strong { color: var(--text-primary); font-size: 13px; }
    .review-row div { display: grid; gap: 3px; }
    .review-row div span { color: var(--text-muted); font-size: 10px; font-weight: 700; text-transform: uppercase; }
    .review-row p { margin: 0; color: var(--text-secondary); line-height: 1.45; overflow-wrap: anywhere; }
  `,
})
export class LibraryNodeAccessFormComponent implements OnInit {
  readonly libraryName = input.required<string>();
  readonly setupMode = input(false);
  readonly nodes = input.required<readonly LibraryNodeAccessOption[]>();
  readonly initialAccesses = input<readonly LibraryNodeAccessResponse[]>([]);
  readonly busy = input(false);
  readonly saved = output<readonly LibraryNodeAccessValue[]>();
  readonly cancelled = output<void>();
  protected readonly form = new FormRecord<FormGroup<NodeAccessControls>>({});
  protected readonly reviewing = signal(false);
  private readonly currentAccesses = new Map<string, LibraryNodeAccessResponse>();
  protected canSave(): boolean {
    return this.nodes().length > 0 && this.nodes().every((node) => {
    const controls = this.form.controls[node.id]?.controls;
    if (!controls) return false;
    const mode = controls.mode.value;
    if (mode === 'direct') return !!controls.localPath.value.trim() && controls.canRead.value;
    if (mode === 'unsupported') return true;
    if (mode !== 'transfer' || !controls.transferSourceNodeId.value) return false;
    const source = this.form.controls[controls.transferSourceNodeId.value]?.controls;
    return !!source && source.mode.value === 'direct' && !!source.localPath.value.trim() && source.canRead.value && source.canWrite.value;
    });
  }

  ngOnInit(): void {
    for (const access of this.initialAccesses()) this.currentAccesses.set(access.nodeId, access);
    for (const node of this.nodes()) {
      const access = this.currentAccesses.get(node.id);
      this.form.addControl(node.id, new FormGroup<NodeAccessControls>({
        mode: new FormControl(access?.mode ?? 'unsupported', { nonNullable: true }),
        localPath: new FormControl(access?.localPath ?? '', { nonNullable: true }),
        canRead: new FormControl(access?.mode === 'direct' ? access.canRead : false, { nonNullable: true }),
        canWrite: new FormControl(access?.mode === 'direct' ? access.canWrite : false, { nonNullable: true }),
        transferSourceNodeId: new FormControl(access?.transferSourceNodeId ?? '', { nonNullable: true }),
      }));
    }
  }

  protected transferSources(targetNodeId: string): readonly LibraryNodeAccessOption[] {
    return this.nodes().filter((node) => {
      if (node.id === targetNodeId) return false;
      const source = this.form.controls[node.id]?.controls;
      return !!source && source.mode.value === 'direct' && !!source.localPath.value.trim() && source.canRead.value && source.canWrite.value;
    });
  }

  protected review(): void {
    if (this.canSave()) this.reviewing.set(true);
  }

  protected rolesLabel(roles: readonly string[]): string {
    return roles.map((role) => role === 'worker' ? 'Worker' : role === 'server' ? 'Serveur' : role).join(' · ');
  }

  protected currentSummary(nodeId: string): string {
    const access = this.currentAccesses.get(nodeId);
    if (!access) return 'Non configuré';
    return this.describe(access.mode, access.localPath, access.transferSourceNodeName, access.canRead, access.canWrite);
  }

  protected proposedSummary(nodeId: string): string {
    const controls = this.form.controls[nodeId].controls;
    const mode = controls.mode.value;
    const source = this.nodes().find((node) => node.id === controls.transferSourceNodeId.value)?.name ?? null;
    return this.describe(mode, controls.localPath.value.trim(), source, controls.canRead.value, controls.canWrite.value);
  }

  private describe(mode: LibraryNodeAccessMode, path: string | null, source: string | null, canRead: boolean, canWrite: boolean): string {
    if (mode === 'direct') {
      const permissions = [canRead ? 'lecture' : '', canWrite ? 'écriture' : ''].filter(Boolean).join(' et ');
      return `Accès direct · ${path || 'chemin à préciser'} · ${permissions || 'aucun droit'}`;
    }
    if (mode === 'transfer') return `Copie temporaire depuis ${source ?? 'source à choisir'}`;
    return 'Ne recevra pas de tâches pour cette bibliothèque';
  }

  protected save(): void {
    if (!this.canSave()) return;
    const values = this.nodes().map((node): LibraryNodeAccessValue => {
      const controls = this.form.controls[node.id].controls;
      const mode = controls.mode.value;
      return {
        nodeId: node.id,
        mode,
        localPath: mode === 'direct' ? controls.localPath.value.trim() : null,
        canRead: mode === 'direct' ? controls.canRead.value : mode === 'transfer',
        canWrite: mode === 'direct' ? controls.canWrite.value : mode === 'transfer',
        transferSourceNodeId: mode === 'transfer' ? controls.transferSourceNodeId.value : null,
      };
    });
    this.saved.emit(values);
  }
}
