# Périmètres CI

Les workflows utilisent les mêmes filtres sur les PR et les push vers dev/main :

| Workflow | Fichiers concernés |
| --- | --- |
| Backend | backend, hors tests Prometheus, et son workflow |
| Frontend | frontend et son workflow |
| Image | backend hors tests Prometheus, frontend, .dockerignore et son workflow |
| Monitoring | règles peer-cluster-alerts, tests Prometheus et son workflow |
| Release scripts | scripts GitHub et workflows release*.yml |

Une documentation ordinaire ne déclenche aucune compilation. Les changements des
workflows restent testés par le workflow qu'ils modifient. La construction de
l'image reste nécessaire pour un changement frontend : le Dockerfile embarque
l'interface. Le contrôle de politique de PR reste indépendant des fichiers.

La release exige toujours les contrôles de chaque domaine. Si un contrôle est absent
du commit publié parce que son domaine n'a pas changé, elle consulte le dernier
commit de son périmètre (y compris son workflow), et uniquement celui-ci. Les
contrôles présents sur le commit publié restent prioritaires. Les filtres et le
résolveur de .github/scripts/ci-scopes.mjs sont synchronisés par des tests.

Validation locale et CI : `node --test .github/scripts/*.test.mjs`.

Ne pas rendre obligatoires dans les protections de branche les workflows qui peuvent
être absents à cause des filtres : GitHub les laisserait en attente. Si une protection
globale est ajoutée, utiliser un contrôle agrégateur qui distingue les suites non
concernées des suites en échec.
