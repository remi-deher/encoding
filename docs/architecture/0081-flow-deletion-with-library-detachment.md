# 0081 — Suppression d’un flow affecté à des bibliothèques

La suppression d’un flow n’est plus refusée lorsqu’il est affecté à des bibliothèques. La fenêtre de confirmation du catalogue lit `GET /api/v1/flows/{id}/usage`, affiche les bibliothèques concernées dans un tableau (nom, version, état) et exige de cocher une case avant d’activer « Supprimer ». Sans bibliothèque, la même fenêtre s’affiche avec un message vide et la même case.

`DELETE /api/v1/flows/{id}?detachLibraries=true` supprime le flow (suppression logique, brouillon retiré) et, dans la même transaction, retire ses lignes de `library_flow_assignments` en incrémentant `libraries.flow_assignment_revision` de chaque bibliothèque touchée. Une bibliothèque détachée se retrouve sans flow : elle n’est plus surveillée ni traitée automatiquement jusqu’à l’affectation d’un autre flow, comme après une désaffectation manuelle.

Sans `detachLibraries`, le refus 409 historique est conservé. L’assistant IA et le serveur MCP appellent le dépôt sans ce paramètre et restent donc protégés. Le refus pour exécution en file d’attente ou en cours est inchangé et évalué avant tout détachement ; les exécutions déjà créées embarquent leur définition de flow et ne dépendent pas de l’affectation. Les versions publiées et l’historique sont conservés.

Limite : le chemin base de données uniquement est traité ; le mode consensus pair-à-pair sans transactions de base ne réplique pas ce détachement (la suppression de flow elle-même n’y est pas répliquée non plus).
