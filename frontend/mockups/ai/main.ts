import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatChipsModule } from '@angular/material/chips';
import { ThemeToggleComponent } from '../../src/app/core/theme/theme-toggle.component';
import { StatusBadgeComponent } from '../../src/app/shared/ui/status-badge.component';
import { CategoryTabsComponent } from '../../src/app/shared/ui/category-tabs.component';
import { NotificationService } from '../../src/app/shared/services/notification.service';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatChipsModule,
    MatSlideToggleModule,
    ThemeToggleComponent,
    StatusBadgeComponent,
    CategoryTabsComponent,
  ],
  templateUrl: './mockup.html',
  styleUrl: './mockup.scss',
})
class AiMockup {
  readonly notice = inject(NotificationService);
  readonly tab = signal('assistant');
  readonly started = signal(false);
  readonly context = signal(true);
  readonly prompt = signal('');
  readonly messages = signal<string[]>([]);
  readonly model = signal('gpt-6.1');
  readonly modelGroups = [
    { provider: 'OpenAI · Codex', models: [{ id: 'gpt-6.1', label: 'GPT 6.1' }] },
    {
      provider: 'Anthropic · Claude Code',
      models: [
        { id: 'claude-opus', label: 'Claude Opus' },
        { id: 'claude-sonnet', label: 'Claude Sonnet' },
        { id: 'claude-haiku', label: 'Claude Haiku' },
      ],
    },
  ];
  readonly tabs = [
    { value: 'assistant', label: 'Assistant' },
    { value: 'providers', label: 'Fournisseurs' },
    { value: 'activity', label: 'Activité' },
  ];
  readonly providers = ['Codex CLI', 'Claude Code'];
  readonly disabledModels = signal<readonly string[]>([]);
  toggleModel(id: string, enabled: boolean) {
    this.disabledModels.update((ids) =>
      enabled ? ids.filter((item) => item !== id) : [...ids, id],
    );
    if (!enabled && this.model() === id) {
      const fallback = this.modelGroups
        .flatMap((group) => group.models)
        .find((item) => !this.disabledModels().includes(item.id));
      this.model.set(fallback?.id ?? '');
    }
  }
  readonly suggestions = [
    'Expliquer mon flow HEVC',
    'Comprendre une exécution échouée',
    'Créer un flow à partir de mon besoin',
  ];
  send() {
    if (!this.prompt().trim()) return;
    this.messages.update((items) => [...items, this.prompt().trim()]);
    this.prompt.set('');
    this.started.set(true);
  }
  reset() {
    this.started.set(false);
    this.messages.set([]);
    this.prompt.set('');
  }
}
bootstrapApplication(AiMockup).catch(console.error);
