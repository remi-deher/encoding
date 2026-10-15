import { firstValueFrom } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
  DataTableRowAction,
} from '../../shared/ui/data-table.component';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import { NotificationService } from '../../shared/services/notification.service';
import { NodeGroupCriteria, NodeGroupMode, NodeGroupResponse } from '../../shared/models/cluster';
import { AuthService } from '../../core/auth/auth.service';

interface GroupRow extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly mode: string;
  readonly members: number;
  readonly labels: string;
  readonly description: string;
}

@Component({
  selector: 'app-node-groups-page',
  imports: [
    ManagementCatalogTemplateComponent,
    DataTableComponent,
    RecordFormComponent,
    MatButtonModule,
  ],
  template: `
    <app-management-catalog-template
      title="Groupes de nœuds"
      filterLabel="Groupes"
      [catalogState]="state()"
      [catalogMessage]="message()"
      [actionOpen]="formOpen()"
      [actionTitle]="form().title"
      [actionDescription]="form().description"
      (actionClosed)="closeForm()"
      note="Les critères des groupes automatiques sont réévalués à la lecture et à l’attribution. Les étiquettes des nœuds remplacent les étiquettes de groupe portant la même clé."
    >
      <ng-template #pageActions>
        @if (isAdmin()) {
          <button matButton="filled" type="button" (click)="create()">Créer un groupe</button>
        }
      </ng-template>
      <app-data-table layout="cards"
        catalogue-table
        [columns]="columns"
        [rows]="rows()"
        [rowActions]="actions"
        (rowAction)="onAction($event)"
        emptyMessage="Aucun groupe de nœuds."
      ></app-data-table>
      <ng-template #actionContent
        ><app-record-form
          [data]="form()"
          [busy]="busy()"
          (submitted)="save($event)"
          (cancelled)="closeForm()"
      /></ng-template>
    </app-management-catalog-template>
  `,
})
export class NodeGroupsPageComponent {
  private readonly http = inject(HttpClient);
  private readonly notifications = inject(NotificationService);
  protected readonly isAdmin = inject(AuthService).isAdmin;
  private readonly groups = signal<readonly NodeGroupResponse[]>([]);
  protected readonly state = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly message = signal('Chargement des groupes…');
  protected readonly formOpen = signal(false);
  protected readonly busy = signal(false);
  private readonly editing = signal<NodeGroupResponse | null>(null);
  protected readonly form = signal<RecordFormData>(this.formData(null));
  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom' },
    { key: 'id', label: 'Identifiant' },
    { key: 'mode', label: 'Type' },
    { key: 'members', label: 'Nœuds' },
    { key: 'labels', label: 'Étiquettes' },
    { key: 'description', label: 'Description' },
  ];
  protected readonly actions: readonly DataTableRowAction[] = [
    { id: 'edit', label: 'Modifier', visibleWhen: () => this.isAdmin() },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      visibleWhen: () => this.isAdmin(),
      confirmation: (row) => ({
        title: 'Supprimer ce groupe ?',
        message: `Le groupe « ${row['name']} » et ses étiquettes partagées seront supprimés.`,
        confirmLabel: 'Supprimer',
      }),
    },
  ];
  protected readonly rows = () =>
    this.groups().map(
      (group) =>
        ({
          id: group.id,
          name: group.name,
          mode: group.mode === 'manual' ? 'Manuel' : 'Automatique',
          members: group.nodeIds.length,
          labels:
            Object.entries(group.labels)
              .map(([k, v]) => (v == null ? k : `${k}=${v}`))
              .join(', ') || '—',
          description: group.description || '—',
        }) satisfies GroupRow,
    );

  constructor() {
    void this.load();
  }

  protected create(): void {
    this.editing.set(null);
    this.form.set(this.formData(null));
    this.formOpen.set(true);
  }
  protected closeForm(): void {
    this.formOpen.set(false);
    this.busy.set(false);
  }
  protected onAction(event: DataTableActionEvent): void {
    const group = this.groups().find((item) => item.id === event.row['id']);
    if (!group) return;
    if (event.actionId === 'edit') {
      this.editing.set(group);
      this.form.set(this.formData(group));
      this.formOpen.set(true);
    }
    if (event.actionId === 'delete') void this.remove(group);
  }
  protected async save(values: Record<string, string>): Promise<void> {
    if (this.busy()) return;
    let labels: Record<string, string | null> = {};
    try {
      labels = this.parseLabels(values['labels'] ?? '');
    } catch {
      this.notifications.show(
        'Étiquettes invalides. Utilisez une clé par ligne, avec une valeur facultative après =.',
      );
      return;
    }
    const mode = values['mode'] as NodeGroupMode;
    const role = values['role'];
    const criteria: NodeGroupCriteria =
      mode === 'automatic'
        ? {
            roles: role === 'all' ? [] : [role as 'server' | 'worker'],
            operatingSystem: values['os'] || null,
            architecture: values['architecture'] || null,
            minimumMemoryMb: values['memoryMb'] ? Number(values['memoryMb']) : null,
            requiredToolIds: (values['tools'] || '')
              .split(',')
              .map((x) => x.trim())
              .filter(Boolean),
            minimumConcurrentTasks: values['concurrency'] ? Number(values['concurrency']) : null,
            minimumProcessorCount: values['processors'] ? Number(values['processors']) : null,
          }
        : {};
    const nodeIds =
      mode === 'manual'
        ? (values['nodeIds'] || '')
            .split(/[\s,]+/)
            .map((x) => x.trim())
            .filter(Boolean)
        : [];
    if (nodeIds.some((id) => !/^[0-9a-f-]{36}$/i.test(id))) {
      this.notifications.show('Chaque nœud doit être identifié par un UUID valide.');
      return;
    }
    const body = {
      name: values['name'],
      description: values['description'],
      mode,
      criteria,
      labels,
      nodeIds,
    };
    const current = this.editing();
    this.busy.set(true);
    try {
      if (current) await firstValueFrom(this.http.put(`/api/v1/node-groups/${current.id}`, body));
      else await firstValueFrom(this.http.post('/api/v1/node-groups', body));
      await this.load();
      this.closeForm();
      this.notifications.show('Groupe enregistré.');
    } catch {
      this.notifications.show(
        'Le groupe n’a pas pu être enregistré. Vérifiez son nom, ses étiquettes et les nœuds sélectionnés.',
      );
    } finally {
      this.busy.set(false);
    }
  }
  private async remove(group: NodeGroupResponse): Promise<void> {
    try {
      await firstValueFrom(this.http.delete(`/api/v1/node-groups/${group.id}`));
      await this.load();
      this.notifications.show('Groupe supprimé.');
    } catch (error) {
      const title = (error as { error?: { title?: unknown } }).error?.title;
      this.notifications.show(
        typeof title === 'string' ? title : 'Le groupe n’a pas pu être supprimé.',
      );
    }
  }
  private async load(): Promise<void> {
    this.state.set('loading');
    try {
      this.groups.set(
        await firstValueFrom(this.http.get<readonly NodeGroupResponse[]>('/api/v1/node-groups')),
      );
      this.state.set('ready');
    } catch {
      this.state.set('error');
      this.message.set('Impossible de charger les groupes de nœuds.');
    }
  }
  private parseLabels(source: string): Record<string, string | null> {
    const labels: Record<string, string | null> = {};
    for (const line of source
      .split(/[\n,]+/)
      .map((x) => x.trim())
      .filter(Boolean)) {
      const i = line.indexOf('=');
      const key = (i < 0 ? line : line.slice(0, i)).trim();
      if (!/^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$/.test(key)) throw new Error('invalid key');
      labels[key] = i < 0 ? null : line.slice(i + 1).trim();
    }
    return labels;
  }
  private formData(group: NodeGroupResponse | null): RecordFormData {
    const c = group?.criteria;
    return {
      title: group ? 'Modifier un groupe' : 'Créer un groupe',
      description:
        'Les étiquettes s’écrivent une par ligne : gpu=nvidia ou production. Pour un groupe manuel, indiquez les UUID des nœuds séparés par des virgules.',
      submitLabel: 'Enregistrer',
      fields: [
        { id: 'name', label: 'Nom unique', type: 'text', required: true, maxLength: 128 },
        { id: 'description', label: 'Description', type: 'textarea', maxLength: 2000 },
        {
          id: 'mode',
          label: 'Mode',
          type: 'select',
          required: true,
          options: [
            { value: 'manual', label: 'Manuel' },
            { value: 'automatic', label: 'Automatique' },
          ],
        },
        {
          id: 'nodeIds',
          label: 'UUID des nœuds (manuel)',
          type: 'textarea',
          hint: 'Une liste séparée par des espaces ou des virgules.',
        },
        {
          id: 'role',
          label: 'Rôle requis (automatique)',
          type: 'select',
          options: [
            { value: 'all', label: 'Tous les rôles' },
            { value: 'worker', label: 'Worker' },
            { value: 'server', label: 'Serveur' },
          ],
        },
        { id: 'os', label: 'Système (automatique)', type: 'text', maxLength: 128 },
        { id: 'architecture', label: 'Architecture (automatique)', type: 'text', maxLength: 64 },
        {
          id: 'memoryMb',
          label: 'Mémoire minimale en Mo',
          type: 'number',
          min: 0,
          max: 4194304,
          step: 1,
        },
        {
          id: 'concurrency',
          label: 'Traitements simultanés minimum',
          type: 'number',
          min: 0,
          max: 1024,
          step: 1,
        },
        {
          id: 'processors',
          label: 'Processeurs disponibles minimum',
          type: 'number',
          min: 0,
          max: 4096,
          step: 1,
        },
        {
          id: 'tools',
          label: 'Identifiants d’outils requis',
          type: 'text',
          hint: 'Séparés par des virgules, par exemple ffmpeg, ffprobe.',
        },
        {
          id: 'labels',
          label: 'Étiquettes partagées',
          type: 'textarea',
          hint: 'Les étiquettes locales du nœud remplacent celles du groupe.',
        },
      ],
      initialValues: {
        name: group?.name ?? '',
        description: group?.description ?? '',
        mode: group?.mode ?? 'manual',
        nodeIds: group?.nodeIds.join(', ') ?? '',
        role: c?.roles?.[0] ?? 'all',
        os: c?.operatingSystem ?? '',
        architecture: c?.architecture ?? '',
        memoryMb: c?.minimumMemoryMb?.toString() ?? '',
        concurrency: c?.minimumConcurrentTasks?.toString() ?? '',
        tools: c?.requiredToolIds?.join(', ') ?? '',
        processors: c?.minimumProcessorCount?.toString() ?? '',
        labels: Object.entries(group?.labels ?? {})
          .map(([k, v]) => (v == null ? k : `${k}=${v}`))
          .join('\n'),
      },
    };
  }
}
