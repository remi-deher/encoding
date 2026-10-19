import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  OnDestroy,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import {
  AiAssistantConversation,
  AiAssistantConversationDetail,
} from '../../shared/models/ai-tools';
import { ExecutionResponse } from '../../shared/models/execution';
import { NodeResponse } from '../../shared/models/cluster';
import { FlowSummary } from '../../shared/models/flow-validation';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { ClusterApi } from '../../shared/services/cluster-api';
import { ExecutionTracking } from '../../shared/services/execution-tracking';
import { FlowValidation } from '../../shared/services/flow-validation';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

type AssistantResourceType = 'flow' | 'execution' | 'node';

interface AssistantResourceOption {
  readonly id: string;
  readonly label: string;
  readonly version?: number;
}

@Component({
  selector: 'app-ai-assistant-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    PageHeaderComponent,
    RouterLink,
  ],
  templateUrl: './ai-assistant-page.component.html',
  styleUrl: './ai-assistant-page.component.scss',
  host: { '[class.assistant-panel-mode]': 'panelMode()' },
})
export class AiAssistantPageComponent implements OnDestroy {
  readonly panelMode = input(false);
  private readonly aiTools = inject(AiToolsService);
  private readonly flowValidation = inject(FlowValidation);
  private readonly clusterApi = inject(ClusterApi);
  private readonly executionTracking = inject(ExecutionTracking);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly flows = signal<readonly FlowSummary[]>([]);
  protected readonly executions = signal<readonly ExecutionResponse[]>([]);
  protected readonly nodes = signal<readonly NodeResponse[]>([]);
  protected readonly conversations = signal<readonly AiAssistantConversation[]>([]);
  protected readonly detail = signal<AiAssistantConversationDetail | null>(null);
  protected readonly selectedResourceType = signal<AssistantResourceType>('flow');
  protected readonly selectedResourceId = signal('');
  protected readonly prompt = signal('');
  protected readonly loadingFlows = signal(true);
  protected readonly loadingExecutions = signal(true);
  protected readonly loadingNodes = signal(true);
  protected readonly loadingConversations = signal(true);
  protected readonly loadingDetail = signal(false);
  protected readonly sending = signal(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly pendingPrompt = signal<string | null>(null);
  protected readonly hasPendingRequest = signal(false);
  protected readonly resumingRequest = signal(false);
  protected readonly resourceOptions = computed<readonly AssistantResourceOption[]>(() => {
    switch (this.selectedResourceType()) {
      case 'flow':
        return this.flows().map((flow) => ({
          id: flow.id,
          label: `${flow.name} · version ${flow.version}`,
          version: flow.version,
        }));
      case 'execution':
        return this.executions().map((execution) => ({
          id: execution.id,
          label: `${execution.media} · ${execution.state} · ${execution.id.slice(0, 8)}`,
        }));
      case 'node':
        return this.nodes().map((node) => ({
          id: node.id,
          label: `${node.displayName || node.name} · ${node.lifecycle ?? node.status}`,
        }));
    }
  });
  protected readonly selectedResource = computed(
    () =>
      this.resourceOptions().find((resource) => resource.id === this.selectedResourceId()) ?? null,
  );
  protected readonly loadingSelectedResource = computed(() => {
    switch (this.selectedResourceType()) {
      case 'flow':
        return this.loadingFlows();
      case 'execution':
        return this.loadingExecutions();
      case 'node':
        return this.loadingNodes();
    }
  });
  protected readonly resourceDisclosure = computed(() => {
    switch (this.selectedResourceType()) {
      case 'flow':
        return 'La définition de flow publiée et votre question seront transmises.';
      case 'execution':
        return 'L’état, les événements, le pipeline et les journaux de commande disponibles seront transmis.';
      case 'node':
        return 'La présence, les rôles, les capacités déclarées et les informations matérielles du nœud seront transmises.';
    }
  });
  protected readonly canSend = computed(
    () =>
      !this.sending() &&
      !this.loadingDetail() &&
      Boolean(this.prompt().trim()) &&
      (Boolean(this.detail()) || Boolean(this.selectedResource())) &&
      (!this.hasPendingRequest() || this.prompt() === this.requestPrompt),
  );

  private requestPrompt = '';
  private requestId = '';
  private pendingPollTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnDestroy(): void {
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
  }

  constructor() {
    this.loadFlows();
    this.loadExecutions();
    this.loadNodes();
    this.loadConversations();
    this.route.queryParamMap.subscribe((params) => {
      const id = params.get('conversation');
      if (!id) {
        if (this.detail()) this.detail.set(null);
        return;
      }
      if (this.detail()?.conversation.id !== id) this.openConversation(id, false);
    });
  }

  protected startNewConversation(): void {
    this.detail.set(null);
    this.errorMessage.set(null);
    this.prompt.set('');
    this.pendingPrompt.set(null);
    this.hasPendingRequest.set(false);
    this.resumingRequest.set(false);
    this.requestPrompt = '';
    this.requestId = '';
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { conversation: null },
      queryParamsHandling: 'merge',
    });
  }

  protected openConversation(id: string, updateUrl = true): void {
    if (this.loadingDetail()) return;
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
    this.loadingDetail.set(true);
    this.errorMessage.set(null);
    this.aiTools.getAssistantConversation(id).subscribe({
      next: (detail) => {
        this.detail.set(detail);
        this.selectedResourceType.set(detail.conversation.source.kind);
        this.selectedResourceId.set(detail.conversation.source.resourceId);
        if (detail.pendingTurn) {
          this.prompt.set(detail.pendingTurn.userPrompt);
          this.requestPrompt = detail.pendingTurn.userPrompt;
          this.requestId = detail.pendingTurn.requestId;
          this.hasPendingRequest.set(true);
          this.resumingRequest.set(!detail.pendingTurn.isRunning);
          if (detail.pendingTurn.isRunning) {
            this.pendingPrompt.set(detail.pendingTurn.userPrompt);
            this.sending.set(true);
            this.pollPendingConversation(id, detail.pendingTurn.requestId);
          } else {
            this.pendingPrompt.set(detail.pendingTurn.userPrompt);
            this.sending.set(false);
            this.errorMessage.set(
              'Une demande interrompue peut être reprise avec le même identifiant.',
            );
          }
        } else {
          this.pendingPrompt.set(null);
          this.hasPendingRequest.set(false);
          this.resumingRequest.set(false);
          this.sending.set(false);
        }
        this.loadingDetail.set(false);
        if (updateUrl) this.setConversationUrl(id);
      },
      error: (error: HttpErrorResponse) => {
        this.loadingDetail.set(false);
        this.errorMessage.set(this.errorText(error, 'Cette conversation est indisponible.'));
      },
    });
  }

  protected send(): void {
    const message = this.prompt().trim();
    if (!message || !this.canSend()) return;
    if (this.requestPrompt !== message || !this.requestId) {
      this.requestPrompt = message;
      this.requestId = crypto.randomUUID();
    }

    const activeConversation = this.detail()?.conversation ?? null;
    const selectedResource = this.selectedResource();
    this.sending.set(true);
    this.pendingPrompt.set(message);
    this.resumingRequest.set(false);
    this.errorMessage.set(null);
    this.aiTools
      .sendAssistantMessage({
        requestId: this.requestId,
        userPrompt: message,
        conversationId: activeConversation?.id,
        resourceType: activeConversation ? undefined : this.selectedResourceType(),
        resourceId: activeConversation ? undefined : (selectedResource?.id ?? undefined),
        resourceVersion: activeConversation ? undefined : (selectedResource?.version ?? undefined),
      })
      .subscribe({
        next: (response) => {
          const oldTurns = this.detail()?.turns ?? [];
          const turns =
            response.success && response.content
              ? oldTurns.some((turn) => turn.requestId === response.requestId)
                ? oldTurns
                : [
                    ...oldTurns,
                    {
                      requestId: response.requestId,
                      userPrompt: message,
                      content: response.content,
                    },
                  ]
              : oldTurns;
          this.detail.set({
            conversation: response.conversation,
            turns,
            pendingTurn: response.success
              ? null
              : { requestId: response.requestId, userPrompt: message, isRunning: false },
          });
          this.pendingPrompt.set(null);
          this.sending.set(false);
          this.loadConversations();
          this.setConversationUrl(response.conversation.id);
          if (response.success) {
            this.prompt.set('');
            this.requestPrompt = '';
            this.requestId = '';
            this.hasPendingRequest.set(false);
            this.resumingRequest.set(false);
          } else {
            this.pendingPrompt.set(message);
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
            this.errorMessage.set(response.errorMessage ?? 'Le fournisseur IA n’a pas répondu.');
          }
        },
        error: (error: HttpErrorResponse) => {
          this.sending.set(false);
          this.pendingPrompt.set(null);
          this.errorMessage.set(
            this.errorText(error, 'Impossible d’envoyer le message. Réessayez.'),
          );
          if (activeConversation) {
            this.pendingPrompt.set(message);
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
          }
        },
      });
  }

  private loadFlows(): void {
    this.flowValidation.list().subscribe({
      next: (flows) => {
        this.flows.set(flows.filter((flow) => flow.version > 0));
        this.loadingFlows.set(false);
      },
      error: () => this.loadingFlows.set(false),
    });
  }

  protected selectResourceType(type: AssistantResourceType): void {
    this.selectedResourceType.set(type);
    this.selectedResourceId.set('');
  }

  private loadExecutions(): void {
    this.executionTracking.overview({ limit: 100 }).subscribe({
      next: (overview) => {
        this.executions.set(overview.executions);
        this.loadingExecutions.set(false);
      },
      error: () => this.loadingExecutions.set(false),
    });
  }

  private loadNodes(): void {
    this.clusterApi.listNodes().subscribe({
      next: (nodes) => {
        this.nodes.set(nodes);
        this.loadingNodes.set(false);
      },
      error: () => this.loadingNodes.set(false),
    });
  }

  private loadConversations(): void {
    this.loadingConversations.set(true);
    this.aiTools.listAssistantConversations().subscribe({
      next: (conversations) => {
        this.conversations.set(conversations);
        this.loadingConversations.set(false);
      },
      error: () => this.loadingConversations.set(false),
    });
  }

  private setConversationUrl(id: string): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { conversation: id },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  private pollPendingConversation(conversationId: string, requestId: string): void {
    this.pendingPollTimer = window.setTimeout(() => {
      this.pendingPollTimer = null;
      if (
        !this.detail() ||
        this.detail()?.conversation.id !== conversationId ||
        this.detail()?.pendingTurn?.requestId !== requestId
      )
        return;
      this.aiTools.getAssistantConversation(conversationId).subscribe({
        next: (detail) => {
          if (this.detail()?.conversation.id !== conversationId) return;
          this.detail.set(detail);
          if (detail.turns.some((turn) => turn.requestId === requestId)) {
            this.sending.set(false);
            this.pendingPrompt.set(null);
            this.hasPendingRequest.set(false);
            this.resumingRequest.set(false);
            this.prompt.set('');
            this.requestPrompt = '';
            this.requestId = '';
            this.loadConversations();
            return;
          }
          if (detail.pendingTurn?.requestId === requestId && detail.pendingTurn.isRunning) {
            this.pollPendingConversation(conversationId, requestId);
          } else {
            this.sending.set(false);
            this.pendingPrompt.set(detail.pendingTurn?.userPrompt ?? this.prompt());
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
            this.errorMessage.set(
              'La demande est interrompue ou son résultat reste incertain. Vous pouvez la reprendre.',
            );
          }
        },
        error: () => this.pollPendingConversation(conversationId, requestId),
      });
    }, 2000);
  }

  private errorText(error: HttpErrorResponse, fallback: string): string {
    const title: unknown = error.error?.title;
    return typeof title === 'string' && title.trim() ? title : fallback;
  }
}
