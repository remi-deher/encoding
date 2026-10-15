# 0050 — Fiche « Ce nœud » et inventaire matériel

## Contexte

L'onglet **Ce nœud** affichait trois cadres indépendants (rôle, installation, capacités). La référence [interface-resource-layout](../design/interface-resource-layout.md) demande une fiche fluide, avec les mêmes codes visuels que l'onglet Cluster.

## Décision

- Un composant commun `app-resource-sheet` (`frontend/src/app/shared/ui`) rend une ressource unique : avis, puis sections ouvertes séparées par des filets (éléments, métriques, champs, action). Il ne charge aucune API et ne contient rien de propre au nœud.
- La définition métier `localNodeSheet` (`shared/models/node-management.ts`) fournit les sections : **État et activité**, **Matériel déclaré**, **Capacités de traitement**, **Identité et appartenance**.
- Règles d'état : une fonction inconnue n'est jamais présentée comme inactive ; agent injoignable = « dernier état connu » et barre hachurée ; worker désactivé = « Non applicable » ; une demande de fonctions (`desiredRoles`) est affichée séparément des fonctions appliquées.
- Le bandeau de contexte de l'onglet est supprimé (le nom et l'appartenance sont dans la fiche). Créer, Rejoindre et la gestion des membres restent dans **Cluster**.
- `NodeInventory` gagne deux champs optionnels : `processorModel` et `gpus` (`NodeGpu` : nom, fabricant, pilote, périphérique). Ils sont lus par l'agent (`HardwareInventory` : `/proc/cpuinfo`, `/sys/class/drm`) et validés par l'API (longueurs bornées). Absents chez les agents plus anciens : l'interface affiche « Non déclaré », sans changer la version de protocole.

## Limites

- Aucune charge CPU/GPU ni débit : aucune source réelle.
- Le nom du GPU est dérivé des identifiants PCI (fabricant + identifiant) ; sans base `pci.ids`, il n'est pas plus précis. Seul un GPU exposé au conteneur (`/dev/dri`, `/sys/class/drm`) est vu.
- Les tâches actives du nœud local reposent sur l'endpoint d'activité, donc sur la fonction serveur.
