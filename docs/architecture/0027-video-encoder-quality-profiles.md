# 0027 — Encodeurs vidéo et profils de qualité

## Décision

Le codec de sortie, l’encodeur FFmpeg et sa cible de qualité sont des paramètres distincts. Le catalogue partagé `VideoEncoderCatalog` décrit les encodeurs connus, leur codec, leur accélération, leur mode et leur plage de qualité ainsi que leurs presets. La sélection des arguments FFmpeg reste dans l’adaptateur d’exécution média.

Un profil d’encodage contient une cible par encodeur. Il possède un brouillon à révision croissante et des versions publiées immuables stockés dans PostgreSQL. Le profil intégré « Équilibré » constitue le réglage initial et reste en lecture seule ; l’utilisateur peut créer un profil personnalisé.

Lorsqu’un profil est choisi dans un bloc d’encodage vidéo, sa version publiée est copiée dans la définition du flow. Les archives restent autonomes et la publication ultérieure d’un profil ne change pas les flows existants. Le backend vérifie les encodeurs, codecs, plages de qualité, presets et priorités de chaque instantané.

Le mode automatique filtre les cibles selon le codec choisi, puis prend dans l’ordre de priorité le premier encodeur vérifié utilisable par le worker. Le mode précis vérifie ce même état pour l’encodeur sélectionné. L’éligibilité backend et l’adaptateur du worker appliquent la même règle. Pour un ancien agent qui ne fournit pas encore les vérifications par encodeur, le cluster conserve temporairement l’éligibilité fondée sur les capacités historiques. Aucun remplacement non déclaré par le profil n’est effectué.

Dans le bloc vidéo, le mode précis présente les options de qualité et le preset du seul encodeur sélectionné. Le mode automatique présente les cibles correspondant au codec, dans l’ordre du profil, pour rendre visible le choix possible avant l’exécution. Les réglages se modifient dans la page des profils ; le flow conserve l’instantané utilisé.

Les profils expriment une cible spécifique à chaque encodeur ; leurs nombres ne prétendent pas représenter une mesure de qualité visuelle identique entre encodeurs. L’agent distingue la liste déclarée par FFmpeg des encodeurs catalogués qui réussissent un encodage synthétique d’une image de 64×64 vers une sortie nulle. Le contrôle est limité dans le temps, mis en cache et ne lit aucun média de bibliothèque. Une accélération n’est annoncée comme utilisable que si au moins un encodeur correspondant réussit ce contrôle. La mesure de débit et le classement par capacité restent à traiter dans une étape ultérieure.

Quand un encodage vidéo du profil réussit, le worker ajoute au contexte de reprise le nœud du flow, l’identité du worker, le codec, l’identifiant exact de l’encodeur, la version de FFmpeg, l’option et la valeur de qualité, l’option et la valeur de preset FFmpeg ainsi que la version du profil et sa priorité. Cette résolution voyage avec chaque checkpoint durable. L’API expose la dernière résolution dans l’historique de l’exécution ; les anciens checkpoints sans cette donnée restent lisibles. Les plans durables plus anciens qui ne conservent pas encore l’identifiant du bloc continuent de s’exécuter, sans produire une attribution historique erronée.

## API

- `GET /api/v1/encoding-profiles/encoders` publie le catalogue de l’éditeur.
- `GET /api/v1/encoding-profiles` liste le profil intégré et les profils gérés.
- Les routes de profil suivent le cycle brouillon, publication et lecture d’une version immuable.
- `0021_encoding_profiles.sql` ajoute les tables de brouillon et de versions ; aucune définition de flow existante n’est réécrite.

## Compatibilité

Les anciens flows sans instantané de profil conservent leurs champs historiques de codec, qualité, preset et accélération. Les nouveaux flows embarquent l’instantané choisi. Les paramètres métier sont validés contre le même catalogue au chargement et à l’exécution.
