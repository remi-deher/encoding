# Flows associés aux bibliothèques

## Décisions

- Une bibliothèque ne porte pas de type de média. Son rôle est de déclarer une source logique et les accès des nœuds.
- Le type de média appartient au flow et guide la sélection des composants compatibles.
- Une bibliothèque peut être associée à zéro ou un flow à la fois.
- Un même flow peut être associé à plusieurs bibliothèques.
- Une bibliothèque sans flow associé ne déclenche aucun traitement automatique.
- Une exécution référence une version immuable du flow utilisé, afin qu'une modification ultérieure ne change pas une tâche déjà démarrée.

## Mise en œuvre

Le contrat et le formulaire des bibliothèques ne transportent plus de type de média. La migration `0007_remove_library_media_type.sql` retire cette métadonnée de la table `libraries`. Le modèle d'association des flows sera ajouté avec la persistance des flows et l'enregistrement des versions d'exécution.
