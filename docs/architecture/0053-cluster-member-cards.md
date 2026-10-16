# 0053 — Cartes des membres du cluster et noms de GPU

Statut : accepté (variante A de la maquette validée par le propriétaire du projet). Complète 0049 et 0052.

## Décision

La liste « Membres du cluster actuel » n'est plus un tableau (colonne Matériel trop étroite, actions coupées, défilement horizontal) mais une suite de cartes pleine largeur :

- En-tête : nom, état, fonctions, dernier signal, sélection et menu ⋮ (Nom et étiquettes, Changer le mode, Supprimer).
- Trois blocs : **Accélérateur graphique**, **Matériel déclaré** (OS, CPU, RAM, GPU), **Traitements**.
- La sélection multiple et les actions groupées (Vidanger, Reprendre le travail, Supprimer) sont conservées dans une barre au-dessus des cartes.

Le composant commun `ResourceCardComponent` reçoit une entrée générique `sections` (accent principal, pastilles, faits, jauge, complément repliable) et `meta`. Les tons de pastille sont génériques (`--tone-*` dans `styles.scss`) ; la page décide ce qu'ils représentent. Le tableau reste utilisé pour les demandes de rattachement.

## Accélérateurs

Règles de 0052 inchangées. Les accélérateurs seulement détectés sont repliés derrière « +N non vérifiés » : un FFmpeg compilé avec NVENC sans GPU NVIDIA ne doit pas se lire comme une capacité.

## Noms de GPU

L'agent résout le nom commercial d'un GPU d'après la base d'identifiants PCI (`pci.ids`, paquet `hwdata` ajouté à l'image) : « CoffeeLake-S GT2 [UHD Graphics 630] » devient « Intel UHD Graphics 630 ». Sans base ou sans entrée, le nom reste « fabricant 0xid ». L'inventaire est lu une seule fois par processus.

## Limites

Aucune charge CPU/GPU n'est affichée (pas de source). Les noms ne sont corrects que si le GPU est exposé au conteneur (`/dev/dri`, `/sys/class/drm`).
