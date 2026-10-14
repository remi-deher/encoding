# 0028 — Journal des commandes du pipeline

## Décision

Une exécution expose les étapes du flow dans leur ordre d’exécution et les commandes lancées par chaque étape. Le worker capture la commande construite par l’adaptateur, ses horodatages, son code de sortie ainsi que les sorties standard et erreur disponibles.

Les journaux sont associés à l’invocation de bloc dans `execution_steps.command_logs`. Une étape terminée enregistre ses journaux dans la même transaction que son checkpoint. Si elle échoue ou est annulée avant le checkpoint, le worker joint les journaux déjà capturés à sa clôture. Les limites par commande sont de 32 K caractères pour chaque sortie et 16 K caractères pour la ligne de commande ; une sortie tronquée est signalée.

Les commandes et sorties sont filtrées pour masquer les valeurs de secrets usuelles. Les flux stdout/stderr des scripts et plugins sont exclus, car ils transportent aussi le protocole de retour et peuvent contenir des valeurs de configuration. Leur commande, leur durée et leur code de sortie restent visibles.

`GET /api/v1/executions/{id}/pipeline` fournit les étapes et leurs commandes. Pour chaque étape, le checkpoint fournit la sortie réellement choisie ; le catalogue résout son libellé, notamment Oui / Non / Erreur pour une condition. Les horodatages début/fin servent à afficher le temps passé, et un processus encore actif utilise l’heure courante.

Les pages Tâches et Librairies réutilisent le même composant de consultation. Il affiche les blocs exécutés dans l’ordre, leur état, leur durée et leur sortie, reliés par le successeur enregistré dans le checkpoint. Le mode simplifié montre le chemin exécuté ; le mode détaillé ajoute les commandes et leurs sorties consultables. L’ensemble peut être copié ou exporté en fichier texte. Le détail d’une tâche sépare Synthèse, Pipeline, Événements et Résultats en onglets, afin que le contenu suive l’onglet au lieu d’afficher constamment la table de chronologie.

## Limites

La capture est persistée à la fin du processus enfant puis au checkpoint du bloc. Elle n’est donc pas un flux stdout en direct ; une interruption brutale du worker avant l’envoi peut laisser sans journal le processus en cours. Les sorties des scripts et plugins sont volontairement masquées.
