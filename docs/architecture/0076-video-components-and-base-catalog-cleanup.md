# 0076 — Classement des blocs vidéo et retrait de l'ancienne condition Base

Statut : implémenté.

## Décision

- Le changement de conteneur FFmpeg `video-remux-media` appartient au plugin Vidéo. Les nouveaux flows utilisent cette référence.
- Le bloc générique `base-condition` disparaît du catalogue Base. Les flows déjà enregistrés restent exécutables grâce à son descripteur historique conservé hors du catalogue ; l'éditeur ne le propose plus.
- Les références historiques à `base-remux-media` sont résolues vers `video-remux-media`, avec prise en charge maintenue dans le runtime.
- Base passe en 1.7.0 et Vidéo en 1.1.0. Les références existantes vers les versions précédentes des composants conservés restent compatibles.

## VMAF, PSNR et SSIM

Ces métriques sont utiles pour comparer une sortie avec sa source après encodage. VMAF est le score perceptuel le plus parlant pour juger une perte de qualité. PSNR et SSIM sont des mesures techniques complémentaires, mais ne prédisent pas toujours bien la qualité perçue.

Si ces blocs sont ajoutés, privilégier un bloc Vidéo « Comparer la qualité » avec un choix VMAF / PSNR / SSIM et des sorties selon un seuil configurable, plutôt que trois blocs presque identiques. Il devra comparer la source conservée au résultat temporaire, produire le score dans une variable et signaler explicitement si le filtre VMAF n'est pas disponible sur le worker. La comparaison doit aligner les trames et les dimensions ; un redimensionnement ou un rognage peut légitimement changer le score. PSNR et SSIM utilisent les filtres FFmpeg courants ; VMAF dépend d'une compilation FFmpeg avec libvmaf et coûte davantage en calcul. Aucun seuil universel ne s'applique à toutes les vidéos. Cette évolution est une proposition, pas encore implémentée.
