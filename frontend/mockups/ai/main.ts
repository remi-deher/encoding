import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
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
    MatExpansionModule,
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
  readonly model = signal('Codex CLI');
  readonly tabs = [
    { value: 'assistant', label: 'Assistant' },
    { value: 'providers', label: 'Fournisseurs' },
    { value: 'activity', label: 'Activité' },
  ];
  readonly providers = ['Codex CLI', 'Claude Code', 'Ollama'];
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
