import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { NodeMode, NodeModeDialogData, NodeModeStatus } from '../models/node-mode';
import { ConfirmationDialogComponent } from '../ui/confirmation-dialog.component';
import { NodeModeDialogComponent } from '../ui/node-mode-dialog.component';
import { NotificationService } from './notification.service';

/**
 * Parcours commun de changement du mode du nœud local (serveur et console du worker) : lecture, choix, confirmation
 * quand le serveur local va s'arrêter, application puis retour à l'utilisateur. L'adresse seule distingue les deux interfaces.
 */
@Injectable({ providedIn: 'root' })
export class NodeModeController {
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);

  /** Retourne le mode appliqué, ou null si l'utilisateur annule ou si l'opération échoue (déjà notifiée). */
  async change(endpoint: string): Promise<NodeModeStatus | null> {
    let status: NodeModeStatus;
    try {
      status = await firstValueFrom(this.http.get<NodeModeStatus>(endpoint));
    } catch {
      this.notifications.show('Impossible de lire le mode de ce nœud.');
      return null;
    }
    const mode = await firstValueFrom(
      this.dialog
        .open<NodeModeDialogComponent, NodeModeStatus, NodeMode>(NodeModeDialogComponent, {
          data: status,
        })
        .afterClosed(),
    );
    if (!mode) return null;
    if (status.server && !mode.server && !(await this.confirmServerStop())) return null;
    try {
      const applied = await firstValueFrom(this.http.put<NodeModeStatus>(endpoint, mode));
      this.notifications.show(
        'Mode appliqué. Les fonctions retirées s’arrêtent dans quelques secondes.',
      );
      return applied;
    } catch (error) {
      this.notifications.show(this.errorMessage(error));
      return null;
    }
  }

  /**
   * Demande le mode d'un autre nœud du cluster. Le serveur l'enregistre et le nœud l'applique à son prochain signal :
   * retourne true si la demande est acceptée, false si l'utilisateur annule ou si elle est refusée (déjà notifié).
   */
  async request(
    nodeId: string,
    nodeName: string,
    current: NodeMode,
    online: boolean,
  ): Promise<boolean> {
    const data: NodeModeDialogData = { ...current, managed: true, remoteNodeName: nodeName };
    const mode = await firstValueFrom(
      this.dialog
        .open<NodeModeDialogComponent, NodeModeDialogData, NodeMode>(NodeModeDialogComponent, {
          data,
        })
        .afterClosed(),
    );
    if (!mode) return false;
    if (current.server && !mode.server && !(await this.confirmServerStop(nodeName))) return false;
    try {
      await firstValueFrom(this.http.put(`/api/v1/nodes/${nodeId}/mode`, mode));
      this.notifications.show(
        online
          ? 'Changement demandé : le nœud l’applique à son prochain signal.'
          : 'Changement enregistré : le nœud l’appliquera à son retour en ligne.',
      );
      return true;
    } catch (error) {
      this.notifications.show(this.errorMessage(error));
      return false;
    }
  }

  private confirmServerStop(nodeName?: string): Promise<boolean> {
    return firstValueFrom(
      this.dialog
        .open<ConfirmationDialogComponent, unknown, boolean>(ConfirmationDialogComponent, {
          data: {
            title: nodeName
              ? `Arrêter le serveur de « ${nodeName} » ?`
              : 'Arrêter le serveur de ce nœud ?',
            message:
              'L’interface et l’API de ce nœud s’arrêtent. Les workers rattachés à ce serveur le retrouvent s’il revient ou passent par un autre serveur du cluster.',
            confirmLabel: 'Arrêter le serveur',
          },
        })
        .afterClosed(),
    ).then((confirmed) => confirmed === true);
  }

  private errorMessage(error: unknown): string {
    const title = (error as { error?: { title?: string } } | null)?.error?.title;
    return title ?? 'Le changement de mode a échoué.';
  }
}
