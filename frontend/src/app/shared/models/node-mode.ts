/** Mode de fonctionnement du nœud local : fonctions serveur et/ou worker (architecture 0036). */
export interface NodeMode {
  readonly server: boolean;
  readonly worker: boolean;
}

/** `managed` est faux hors image unique : le mode se règle alors par la configuration du déploiement. */
export interface NodeModeStatus extends NodeMode {
  readonly managed: boolean;
}
