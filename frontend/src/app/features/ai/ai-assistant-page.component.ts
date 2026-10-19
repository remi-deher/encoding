import { BreakpointObserver } from '@angular/cdk/layout';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { map, Observable } from 'rxjs';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatChipsModule } from '@angular/material/chips';
import { MatMenuModule } from '@angular/material/menu';
import { AiConversationComposerComponent } from './components/ai-conversation-composer.component';
import { aiEffortLabel, aiModelKey } from './components/ai-model-selection';
import { HttpErrorResponse } from '@angular/common/http';
import {
  afterNextRender,
  afterRenderEffect,
  DestroyRef,
  ElementRef,
  viewChild,
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
  AiAssistantActionProposal,
  AiAssistantSource,
  AiAssistantToolCall,
  AiAssistantOptions,
  AiTurnModelSelection,
} from '../../shared/models/ai-tools';
import { ExecutionResponse } from '../../shared/models/execution';
import { LibraryResponse, NodeResponse } from '../../shared/models/cluster';
import {
  FlowDocument,
  FlowSummary,
  FlowVersionResponse,
} from '../../shared/models/flow-validation';
import { FlowComponentDefinition } from '../../shared/models/flow-component';
import { FlowPreviewComponent } from '../../shared/ui/flow-preview/flow-preview';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { ClusterApi } from '../../shared/services/cluster-api';
import { ExecutionTracking } from '../../shared/services/execution-tracking';
import { FlowValidation } from '../../shared/services/flow-validation';
import { AuthService } from '../../core/auth/auth.service';
import { createUuid } from '../../shared/utils/uuid';

type AssistantResourceType = 'flow' | 'execution' | 'node' | 'library';

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
    AiConversationComposerComponent,
    MatSidenavModule,
    MatChipsModule,
    MatMenuModule,
    RouterLink,
    FlowPreviewComponent,
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
  private readonly destroyRef = inject(DestroyRef);
  protected readonly compact = toSignal(
    inject(BreakpointObserver)
      .observe('(max-width: 760px)')
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
  protected readonly historyOpen = signal(false);
  protected readonly toolsOpen = signal(false);
  private readonly followTail = signal(true);
  private readonly messageThread = viewChild<ElementRef<HTMLElement>>('messageThread');
  protected readonly isAdmin = inject(AuthService).isAdmin;

  protected readonly flows = signal<readonly FlowSummary[]>([]);
  protected readonly executions = signal<readonly ExecutionResponse[]>([]);
  protected readonly nodes = signal<readonly NodeResponse[]>([]);
  protected readonly libraries = signal<readonly LibraryResponse[]>([]);
  protected readonly conversations = signal<readonly AiAssistantConversation[]>([]);
  protected readonly detail = signal<AiAssistantConversationDetail | null>(null);
  readonly conversationId = computed(() => this.detail()?.conversation.id ?? null);
  protected readonly assistantOptions = signal<AiAssistantOptions | null>(null);
  protected readonly selectedModelKey = signal('');
  protected readonly selectedReasoningEffort = signal<string | null>(null);
  protected readonly loadingAssistantOptions = signal(true);
  protected readonly selectedResourceType = signal<AssistantResourceType>('flow');
  protected readonly selectedResourceId = signal('');
  protected readonly contextPickerOpen = signal(false);
  protected readonly contextSearch = signal('');
  protected readonly prompt = signal('');
  protected readonly loadingFlows = signal(true);
  protected readonly loadingExecutions = signal(true);
  protected readonly loadingNodes = signal(true);
  protected readonly loadingLibraries = signal(true);
  protected readonly loadingConversations = signal(true);
  protected readonly loadingDetail = signal(false);
  protected readonly sending = signal(false);
  protected readonly cancelling = signal(false);
  protected readonly selectedReadTools = signal<string[]>([]);
  protected readonly readTools = [
    { name: 'list_flows', label: 'Lister les flows' },
    { name: 'get_flow', label: 'Relire le flow sélectionné' },
    { name: 'list_nodes', label: 'Lister les nœuds' },
    { name: 'list_executions', label: 'Consulter les exécutions récentes' },
    { name: 'get_libraries_overview', label: 'Consulter les bibliothèques' },
  ] as const;
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly pendingPrompt = signal<string | null>(null);
  protected readonly hasPendingRequest = signal(false);
  protected readonly resumingRequest = signal(false);
  protected readonly updatingSources = signal(false);
  protected readonly actionBusy = signal(false);
  protected readonly actionMessage = signal<string | null>(null);
  protected readonly pendingActionProposal = signal<AiAssistantActionProposal | null>(null);
  protected readonly confirmingAiAction = signal(false);
  protected readonly requestProgress = signal<string | null>(null);
  protected readonly requestProgressDetail = signal<string | null>(null);
  protected readonly previewOpen = signal(false);
  protected readonly previewTitle = signal('Aperçu');
  protected readonly previewFlow = signal<FlowDocument | null>(null);
  protected readonly previewComponents = signal<readonly FlowComponentDefinition[]>([]);
  protected readonly previewLoading = signal(false);
  protected readonly previewError = signal<string | null>(null);
  private progressPollTimer: ReturnType<typeof setTimeout> | null = null;
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
      case 'library':
        return this.libraries()
          .filter((library) => library.isActive)
          .map((library) => ({
            id: library.id,
            label: `${library.name}${library.flowName ? ` · ${library.flowName}` : ' · aucun flow attribué'}`,
          }));
    }
  });
  protected readonly selectedResource = computed(
    () =>
      this.resourceOptions().find((resource) => resource.id === this.selectedResourceId()) ?? null,
  );
  protected readonly filteredResourceOptions = computed(() => {
    const search = this.contextSearch().trim().toLocaleLowerCase();
    return search
      ? this.resourceOptions().filter((resource) =>
          resource.label.toLocaleLowerCase().includes(search),
        )
      : this.resourceOptions();
  });
  protected readonly selectedModelOption = computed(
    () =>
      this.assistantOptions()?.models.find(
        (option) => this.modelOptionKey(option) === this.selectedModelKey(),
      ) ?? null,
  );
  protected readonly reasoningEfforts = computed(
    () => this.selectedModelOption()?.reasoningEfforts ?? [],
  );
  protected readonly loadingSelectedResource = computed(() => {
    switch (this.selectedResourceType()) {
      case 'flow':
        return this.loadingFlows();
      case 'execution':
        return this.loadingExecutions();
      case 'node':
        return this.loadingNodes();
      case 'library':
        return this.loadingLibraries();
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
      case 'library':
        return 'L’état de la bibliothèque, son affectation de flow et jusqu’à 10 000 fichiers détectés seront transmis.';
    }
  });
  protected readonly canSend = computed(
    () =>
      !this.sending() &&
      !this.loadingDetail() &&
      Boolean(this.prompt().trim()) &&
      (this.hasPendingRequest() ||
        (!this.loadingAssistantOptions() && Boolean(this.selectedModelOption()))) &&
      (!this.hasPendingRequest() || this.prompt() === this.requestPrompt),
  );

  private requestPrompt = '';
  private requestId = '';
  private requestSelection: AiTurnModelSelection | null = null;
  private pendingPollTimer: ReturnType<typeof setTimeout> | null = null;
  ngOnDestroy(): void {
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
    this.stopProgressPolling();
  }

  constructor() {
    this.loadFlows();
    this.loadExecutions();
    this.loadNodes();
    this.loadLibraries();
    this.loadConversations();
    this.loadAssistantOptions();
    afterNextRender(() => {
      if (this.panelMode()) return;
      this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
        const id = params.get('conversation');
        if (!id) {
          if (this.detail()) this.detail.set(null);
          return;
        }
        if (this.detail()?.conversation.id !== id) this.openConversation(id, false);
      });
    });
    afterRenderEffect(() => {
      this.detail();
      this.pendingPrompt();
      this.sending();
      const thread = this.messageThread()?.nativeElement;
      if (thread && this.followTail()) thread.scrollTop = thread.scrollHeight;
    });
  }

  protected onThreadScroll(): void {
    const thread = this.messageThread()?.nativeElement;
    if (thread)
      this.followTail.set(thread.scrollHeight - thread.clientHeight - thread.scrollTop < 80);
  }

  protected selectType(provider: string): void {
    if (this.sending() || this.hasPendingRequest()) return;
    const options =
      this.assistantOptions()?.models.filter((option) => option.provider === provider) ?? [];
    const selected = options.find((option) => option.isDefault) ?? options[0];
    if (selected) this.selectModel(this.modelOptionKey(selected));
  }

  protected startNewConversation(): void {
    if (this.sending()) return;
    this.closePreview();
    this.selectedResourceId.set('');
    this.historyOpen.set(false);
    this.followTail.set(true);
    this.detail.set(null);
    this.errorMessage.set(null);
    this.pendingActionProposal.set(null);
    this.prompt.set('');
    this.pendingPrompt.set(null);
    this.hasPendingRequest.set(false);
    this.resumingRequest.set(false);
    this.requestPrompt = '';
    this.requestId = '';
    this.requestSelection = null;
    this.selectedReasoningEffort.set(null);
    this.contextPickerOpen.set(false);
    this.contextSearch.set('');
    this.selectDefaultModel();
    if (this.panelMode()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { conversation: null },
      queryParamsHandling: 'merge',
    });
  }

  protected readonly modelOptionKey = aiModelKey;
  protected readonly effortLabel = aiEffortLabel;

  protected selectModel(key: string): void {
    this.selectedModelKey.set(key);
    this.selectedReasoningEffort.set(this.selectedModelOption()?.defaultReasoningEffort ?? null);
  }

  protected selectEffort(value: string | null): void {
    this.selectedReasoningEffort.set(value || null);
  }

  protected toggleContextPicker(): void {
    this.contextPickerOpen.update((open) => !open);
    this.contextSearch.set('');
  }

  protected isSourceAttached(resource: AssistantResourceOption): boolean {
    const conversation = this.detail()?.conversation;
    return (
      conversation !== null &&
      conversation !== undefined &&
      this.conversationSources(conversation).some(
        (source) =>
          source.kind === this.selectedResourceType() && source.resourceId === resource.id,
      )
    );
  }

  protected addResourceToContext(resource: AssistantResourceOption): void {
    if (
      this.isSourceAttached(resource) ||
      this.updatingSources() ||
      this.hasPendingRequest() ||
      this.sending()
    )
      return;
    const current = this.detail()?.conversation;
    if (!current) return;
    const sources = this.conversationSources(current);
    if (sources.length >= 5) {
      this.errorMessage.set('Une conversation peut consulter au maximum cinq ressources.');
      return;
    }
    const href = {
      flow: '/flows',
      execution: '/tasks',
      node: '/nodes',
      library: '/libraries',
    }[this.selectedResourceType()];
    this.updateConversationSources([
      ...sources,
      {
        kind: this.selectedResourceType(),
        resourceId: resource.id,
        version: resource.version,
        label: resource.label,
        href,
      },
    ]);
  }

  protected deleteFlowFromAssistant(source: AiAssistantSource): void {
    if (
      !this.isAdmin() ||
      source.kind !== 'flow' ||
      source.version === null ||
      source.version === undefined ||
      this.actionBusy()
    )
      return;
    this.actionBusy.set(true);
    this.actionMessage.set(null);
    this.aiTools
      .prepareAssistantFlowDeletion({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            this.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantFlowDeletion({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: () => {
                this.actionBusy.set(false);
                this.actionMessage.set(`Flow supprimé : ${source.label}.`);
                this.loadFlows();
              },
              error: (error: HttpErrorResponse) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  this.errorText(
                    error,
                    'La suppression a échoué ou la version a changé. Actualisez le flow.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          this.actionBusy.set(false);
          this.actionMessage.set(
            this.errorText(
              error,
              'La suppression du flow a échoué. Actualisez son état puis réessayez.',
            ),
          );
        },
      });
  }

  protected hasDraftFor(source: AiAssistantSource): boolean {
    return (
      source.kind === 'flow' &&
      this.flows().some((flow) => flow.id === source.resourceId && flow.isDraft)
    );
  }

  protected canStartLibrary(source: AiAssistantSource): boolean {
    const library =
      source.kind === 'library'
        ? this.libraries().find((item) => item.id === source.resourceId)
        : null;
    return library?.isActive === true && library.flowName != null && source.version != null;
  }

  protected publishFlowFromAssistant(source: AiAssistantSource): void {
    if (!this.isAdmin() || source.kind !== 'flow' || source.version == null || this.actionBusy())
      return;
    this.actionBusy.set(true);
    this.actionMessage.set(null);
    this.aiTools
      .prepareAssistantFlowPublish({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            this.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantFlowPublish({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: () => {
                this.actionBusy.set(false);
                this.actionMessage.set(`Brouillon publié pour ${source.label}.`);
                this.loadFlows();
              },
              error: (error: HttpErrorResponse) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  this.errorText(
                    error,
                    'La publication a échoué ou le brouillon a changé. Actualisez le flow.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          this.actionBusy.set(false);
          this.actionMessage.set(this.errorText(error, 'Aucun brouillon publiable pour ce flow.'));
        },
      });
  }

  protected startLibraryFromAssistant(source: AiAssistantSource): void {
    if (!this.isAdmin() || source.kind !== 'library' || source.version == null || this.actionBusy())
      return;
    this.actionBusy.set(true);
    this.actionMessage.set(null);
    this.aiTools
      .prepareAssistantLibraryStart({
        resourceId: source.resourceId,
        expectedVersion: source.version,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            this.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantLibraryStart({ confirmationToken: prepared.confirmationToken })
            .subscribe({
              next: (result) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  `${result.queued} exécution(s) mise(s) en file, ${result.skipped} ignorée(s).`,
                );
                this.loadExecutions();
              },
              error: (error: HttpErrorResponse) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  this.errorText(
                    error,
                    'La bibliothèque ou sa sélection a changé. Actualisez le contexte.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          this.actionBusy.set(false);
          this.actionMessage.set(
            this.errorText(error, 'Aucun traitement ne peut être lancé pour cette bibliothèque.'),
          );
        },
      });
  }

  protected cancelExecutionFromAssistant(source: AiAssistantSource): void {
    if (!this.isAdmin() || source.kind !== 'execution' || this.actionBusy()) return;
    const execution = this.executions().find((item) => item.id === source.resourceId);
    if (!execution || !['queued', 'running'].includes(execution.state)) return;
    this.actionBusy.set(true);
    this.actionMessage.set(null);
    this.aiTools
      .prepareAssistantExecutionCancellation({
        resourceId: execution.id,
        expectedVersion: execution.revision,
      })
      .subscribe({
        next: (prepared) => {
          if (!window.confirm(prepared.summary)) {
            this.actionBusy.set(false);
            return;
          }
          this.aiTools
            .confirmAssistantExecutionCancellation({
              confirmationToken: prepared.confirmationToken,
            })
            .subscribe({
              next: () => {
                this.actionBusy.set(false);
                this.actionMessage.set(`Annulation demandée pour ${execution.media}.`);
                this.loadExecutions();
              },
              error: (error: HttpErrorResponse) => {
                this.actionBusy.set(false);
                this.actionMessage.set(
                  this.errorText(
                    error,
                    'L’exécution a changé ou ne peut plus être annulée. Actualisez son état.',
                  ),
                );
              },
            });
        },
        error: (error: HttpErrorResponse) => {
          this.actionBusy.set(false);
          this.actionMessage.set(
            this.errorText(
              error,
              'L’exécution a changé ou ne peut plus être annulée. Actualisez son état.',
            ),
          );
        },
      });
  }

  protected isCancelableExecution(source: AiAssistantSource): boolean {
    const execution =
      source.kind === 'execution'
        ? this.executions().find((item) => item.id === source.resourceId)
        : null;
    return execution != null && ['queued', 'running'].includes(execution.state);
  }

  protected openConversation(id: string, updateUrl = true): void {
    this.closePreview();
    this.pendingActionProposal.set(null);
    this.historyOpen.set(false);
    this.followTail.set(true);
    if (this.loadingDetail()) return;
    if (this.pendingPollTimer !== null) clearTimeout(this.pendingPollTimer);
    this.loadingDetail.set(true);
    this.errorMessage.set(null);
    this.aiTools.getAssistantConversation(id).subscribe({
      next: (detail) => {
        this.detail.set(detail);
        this.contextPickerOpen.set(false);
        this.contextSearch.set('');
        this.selectedModelKey.set(this.modelOptionKey(detail.conversation));
        this.selectedReasoningEffort.set(detail.conversation.reasoningEffort ?? null);
        if (detail.conversation.source.kind !== 'tool')
          this.selectedResourceType.set(detail.conversation.source.kind);
        this.selectedResourceId.set('');
        this.requestSelection = null;
        if (detail.pendingTurn) {
          this.requestSelection = detail.conversation;
          this.prompt.set(detail.pendingTurn.userPrompt);
          this.requestPrompt = detail.pendingTurn.userPrompt;
          this.requestId = detail.pendingTurn.requestId;
          this.hasPendingRequest.set(true);
          this.resumingRequest.set(!detail.pendingTurn.isRunning);
          if (detail.pendingTurn.isRunning) {
            this.pendingPrompt.set(detail.pendingTurn.userPrompt);
            this.sending.set(true);
            this.pollPendingConversation(id, detail.pendingTurn.requestId);
            this.pollRequestProgress(detail.pendingTurn.requestId);
          } else {
            this.pendingPrompt.set(detail.pendingTurn.userPrompt);
            this.sending.set(false);
            this.errorMessage.set(
              'Une demande interrompue peut être reprise avec le même identifiant.',
            );
          }
        } else {
          this.requestPrompt = '';
          this.requestId = '';
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
      this.requestId = createUuid();
    }

    const activeConversation = this.detail()?.conversation ?? null;
    const selectedResource = this.selectedResource();
    const selectedModel = this.selectedModelOption();
    this.requestSelection ??= selectedModel
      ? {
          provider: selectedModel.provider,
          model: selectedModel.model,
          reasoningEffort: this.selectedReasoningEffort(),
          nodeId: selectedModel.nodeId,
        }
      : activeConversation;
    this.followTail.set(true);
    this.contextPickerOpen.set(false);
    this.toolsOpen.set(false);
    this.sending.set(true);
    this.cancelling.set(false);
    this.pendingPrompt.set(message);
    this.resumingRequest.set(false);
    this.errorMessage.set(null);
    this.requestProgress.set('preparing');
    this.requestProgressDetail.set('Préparation de votre demande');
    this.pollRequestProgress(this.requestId);
    this.aiTools
      .sendAssistantMessage({
        requestId: this.requestId,
        userPrompt: message,
        conversationId: activeConversation?.id,
        resourceType: activeConversation
          ? undefined
          : selectedResource
            ? this.selectedResourceType()
            : undefined,
        resourceId: activeConversation ? undefined : (selectedResource?.id ?? undefined),
        resourceVersion: activeConversation ? undefined : (selectedResource?.version ?? undefined),
        toolCalls: this.selectedReadTools().map(
          (name) =>
            ({
              name,
              ...(name === 'get_flow' ? { flowId: selectedResource?.id ?? null } : {}),
            }) as AiAssistantToolCall,
        ),
        provider: this.requestSelection?.provider,
        model: this.requestSelection?.model,
        reasoningEffort: this.requestSelection?.reasoningEffort,
        nodeId: this.requestSelection?.nodeId,
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
                      sources: response.sources,
                      selection: response.selection,
                      actualModel: response.actualModel,
                      executorNodeId: response.executorNodeId,
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
          this.cancelling.set(false);
          this.stopProgressPolling();
          this.loadConversations();
          this.setConversationUrl(response.conversation.id);
          this.pendingActionProposal.set(
            response.success ? (response.actionProposal ?? null) : null,
          );
          if (response.success) {
            this.prompt.set('');
            this.requestPrompt = '';
            this.requestId = '';
            this.requestSelection = null;
            this.hasPendingRequest.set(false);
            this.resumingRequest.set(false);
            this.selectedReadTools.set([]);
          } else {
            this.pendingPrompt.set(message);
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
            this.errorMessage.set(response.errorMessage ?? 'Le fournisseur IA n’a pas répondu.');
          }
        },
        error: (error: HttpErrorResponse) => {
          this.sending.set(false);
          this.cancelling.set(false);
          this.stopProgressPolling();
          this.pendingPrompt.set(null);
          this.errorMessage.set(
            this.errorText(error, 'Impossible d’envoyer le message. Réessayez.'),
          );
          if (error.status === 400 || error.status === 404) {
            this.hasPendingRequest.set(false);
            this.resumingRequest.set(false);
            this.requestId = '';
            this.requestPrompt = '';
            this.requestSelection = null;
            this.loadAssistantOptions(true);
          } else {
            this.pendingPrompt.set(message);
            this.hasPendingRequest.set(true);
            this.resumingRequest.set(true);
          }
        },
      });
  }

  protected setSelectedReadTools(tools: readonly string[]): void {
    this.selectedReadTools.set([...new Set(tools)].slice(0, 3));
  }

  protected cancelRequest(): void {
    if (!this.requestId || this.cancelling()) return;
    this.cancelling.set(true);
    this.aiTools.cancelAssistantRequest(this.requestId).subscribe({
      next: () => this.cancelling.set(false),
      error: (error: HttpErrorResponse) => {
        this.cancelling.set(false);
        this.errorMessage.set(
          this.errorText(error, 'La demande est terminée ou ne peut plus être interrompue.'),
        );
      },
    });
  }

  protected dismissAiAction(): void {
    this.pendingActionProposal.set(null);
  }

  protected confirmAiAction(): void {
    const proposal = this.pendingActionProposal();
    if (!proposal || this.confirmingAiAction() || !this.isAdmin()) return;
    this.confirmingAiAction.set(true);
    this.actionMessage.set(null);
    const request = { confirmationToken: proposal.confirmationToken };
    const confirmation: Observable<unknown> = (() => {
      switch (proposal.action) {
        case 'publish-flow':
          return this.aiTools.confirmAssistantFlowPublish(request);
        case 'delete-flow':
          return this.aiTools.confirmAssistantFlowDeletion(request);
        case 'start-library':
          return this.aiTools.confirmAssistantLibraryStart(request);
        case 'cancel-execution':
          return this.aiTools.confirmAssistantExecutionCancellation(request);
        case 'assign-library-flow':
          return this.aiTools.confirmAssistantLibraryFlowAssignment(request);
      }
    })();
    confirmation.subscribe({
      next: (result) => {
        this.confirmingAiAction.set(false);
        this.pendingActionProposal.set(null);
        const detail =
          proposal.action === 'start-library'
            ? `${(result as { queued: number; skipped: number }).queued} tâche(s) mise(s) en file.`
            : proposal.action === 'cancel-execution'
              ? 'Annulation demandée.'
              : proposal.action === 'delete-flow'
                ? 'Flow supprimé.'
                : proposal.action === 'assign-library-flow'
                  ? 'Flow affecté à la bibliothèque.'
                  : 'Flow publié.';
        this.actionMessage.set(detail);
        if (proposal.action === 'start-library') this.loadExecutions();
        if (proposal.action === 'delete-flow' || proposal.action === 'publish-flow')
          this.loadFlows();
        if (proposal.action === 'cancel-execution') this.loadExecutions();
        if (proposal.action === 'assign-library-flow') this.loadLibraries();
      },
      error: (error: HttpErrorResponse) => {
        this.confirmingAiAction.set(false);
        this.errorMessage.set(
          this.errorText(
            error,
            'L’action a expiré ou la ressource a changé. Demandez une nouvelle proposition.',
          ),
        );
      },
    });
  }

  protected previewSource(source: AiAssistantSource): void {
    this.previewTitle.set(source.label);
    this.previewOpen.set(true);
    this.previewError.set(null);
    this.previewFlow.set(null);
    if (source.kind !== 'flow' || source.version == null) {
      this.previewLoading.set(false);
      this.previewError.set('L’aperçu visuel est disponible pour les flows publiés.');
      return;
    }
    this.previewLoading.set(true);
    this.flowValidation.versionStatus(source.resourceId, source.version).subscribe({
      next: (saved: FlowVersionResponse) => {
        this.previewFlow.set(saved.definition);
        this.previewLoading.set(false);
        void this.flowValidation
          .catalog()
          .then((catalog) =>
            this.previewComponents.set(
              catalog.flatMap((plugin) =>
                plugin.components.map((component) => ({
                  ...component,
                  pluginId: plugin.id,
                  pluginName: plugin.name,
                })),
              ),
            ),
          )
          .catch(() =>
            this.previewError.set('Les descriptions des blocs n’ont pas pu être chargées.'),
          );
      },
      error: () => {
        this.previewLoading.set(false);
        this.previewError.set(
          'Impossible de charger cet aperçu. Vérifiez que le flow est toujours disponible.',
        );
      },
    });
  }

  protected closePreview(): void {
    this.previewOpen.set(false);
    this.previewFlow.set(null);
    this.previewError.set(null);
  }

  private pollRequestProgress(requestId: string): void {
    this.stopProgressPolling();
    const poll = () =>
      this.aiTools.getAssistantActivity(requestId).subscribe({
        next: (activity) => {
          this.requestProgress.set(activity.progress ?? activity.status);
          this.requestProgressDetail.set(
            activity.progressDetail ?? this.progressLabel(activity.progress ?? activity.status),
          );
          if (this.sending() && this.requestId === requestId)
            this.progressPollTimer = window.setTimeout(poll, 900);
        },
        error: () => {
          if (this.sending() && this.requestId === requestId)
            this.progressPollTimer = window.setTimeout(poll, 1800);
        },
      });
    this.progressPollTimer = window.setTimeout(poll, 350);
  }

  private stopProgressPolling(): void {
    if (this.progressPollTimer !== null) clearTimeout(this.progressPollTimer);
    this.progressPollTimer = null;
  }

  private progressLabel(progress: string): string {
    switch (progress) {
      case 'context':
        return 'Recherche des informations utiles dans Encoding';
      case 'reading':
        return 'Consultation des ressources nécessaires';
      case 'generating':
        return 'Rédaction de la réponse';
      case 'completed':
        return 'Réponse prête';
      case 'cancelled':
        return 'Demande interrompue';
      case 'failed':
        return 'La demande a échoué';
      default:
        return 'Préparation de votre demande';
    }
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
    this.contextSearch.set('');
    if (type !== 'flow')
      this.selectedReadTools.update((tools) => tools.filter((tool) => tool !== 'get_flow'));
  }

  protected conversationSources(
    conversation: AiAssistantConversation,
  ): readonly AiAssistantSource[] {
    return [conversation.source, ...(conversation.additionalSources ?? [])];
  }

  protected removeConversationSource(source: AiAssistantSource): void {
    const current = this.detail()?.conversation;
    if (!current || this.updatingSources() || this.hasPendingRequest() || this.sending()) return;
    const sources = this.conversationSources(current);
    if (sources.length <= 1) return;
    this.updateConversationSources(sources.filter((candidate) => candidate !== source));
  }

  private updateConversationSources(sources: readonly AiAssistantSource[]): void {
    const conversation = this.detail()?.conversation;
    if (!conversation) return;
    this.updatingSources.set(true);
    this.errorMessage.set(null);
    this.aiTools
      .updateAssistantSources(conversation.id, {
        sources: sources.map((source) => ({
          type: source.kind,
          id: source.resourceId,
          version: source.version,
        })),
      })
      .subscribe({
        next: (updated) => {
          this.detail.update((detail) => (detail ? { ...detail, conversation: updated } : detail));
          this.selectedResourceId.set('');
          this.updatingSources.set(false);
        },
        error: (error: HttpErrorResponse) => {
          this.updatingSources.set(false);
          this.errorMessage.set(
            this.errorText(error, 'Impossible de modifier les ressources du contexte.'),
          );
        },
      });
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

  private loadLibraries(): void {
    this.clusterApi.listLibraries().subscribe({
      next: (libraries) => {
        this.libraries.set(libraries);
        this.loadingLibraries.set(false);
      },
      error: () => this.loadingLibraries.set(false),
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

  protected loadAssistantOptions(refresh = false): void {
    this.loadingAssistantOptions.set(true);
    this.aiTools.getAssistantOptions(refresh).subscribe({
      next: (options) => {
        this.assistantOptions.set(options);
        this.loadingAssistantOptions.set(false);
        if (!this.detail()) this.selectDefaultModel();
      },
      error: () => this.loadingAssistantOptions.set(false),
    });
  }

  private selectDefaultModel(): void {
    const options = this.assistantOptions();
    if (!options) return;
    const selected =
      options.models.find(
        (option) =>
          option.provider === options.defaultProvider && option.model === options.defaultModel,
      ) ??
      options.models.find((option) => option.provider === options.defaultProvider) ??
      options.models[0];
    this.selectedModelKey.set(selected ? this.modelOptionKey(selected) : '');
    this.selectedReasoningEffort.set(selected?.defaultReasoningEffort ?? null);
  }

  private setConversationUrl(id: string): void {
    if (this.panelMode()) return;
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
            this.requestSelection = null;
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
    if (typeof title === 'string' && title.trim()) return title;
    if (error.status === 0)
      return 'Connexion au serveur impossible. Vérifiez votre réseau puis réessayez.';
    if (error.status === 401) return 'Votre session a expiré. Reconnectez-vous pour continuer.';
    if (error.status === 403) return 'Vous n’avez pas les droits nécessaires pour cette action.';
    if (error.status === 408 || error.status === 504)
      return 'La demande a dépassé le délai prévu. Vous pouvez la reprendre après vérification de son état.';
    if (error.status === 429)
      return 'Trop de demandes sont en cours. Patientez un instant avant de réessayer.';
    if (error.status === 502 || error.status === 503)
      return 'Le service IA est momentanément indisponible. Vérifiez le fournisseur sélectionné ou réessayez plus tard.';
    if (error.status >= 500)
      return 'Le serveur n’a pas pu terminer la demande. Elle peut être reprise avec le même message.';
    return fallback;
  }
}
