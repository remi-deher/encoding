# 0026 — Packs de composants

## Définition et création

Un pack de composants est un fragment réutilisable d’un flow publié. Sa création copie une version publiée précise depuis `flow_versions`, conserve les versions exactes de ses composants et enregistre son entrée ainsi que ses sorties déclarées. La définition publiée du flow source n’est jamais modifiée.

L’entrée du pack est le premier composant relié à la sortie `success` de l’entrée du flow source. Les sorties exposées sont les sorties de composants internes qui ne sont pas utilisées par une liaison interne. L’API enregistre cette interface avec la définition afin qu’elle reste stable même si le catalogue des plugins évolue.

Les packs sont conservés dans `flow_component_packs` avec un nom unique sans tenir compte de la casse. Leur définition est immuable ; pour créer une variante, l’utilisateur crée un autre pack depuis un flow publié.

## Insertion et édition dans un flow

Insérer un pack clone ses composants internes et ses connexions dans le graphe du flow. Les identifiants des composants et des connexions sont renouvelés. L’entrée source du flow n’est pas copiée : les connexions choisies dans le flow cible se raccordent directement à l’entrée déclarée du pack. Les sorties exposées correspondent à des sorties réelles des composants clonés.

Le document du flow conserve les métadonnées de chaque instance : identité et version du pack, composants membres, entrée et correspondance des sorties. Le graphe stocké reste aplati. L’exécution, la validation du DAG, les baux et les checkpoints continuent donc d’utiliser les composants et connexions ordinaires ; le runtime ne charge pas le pack depuis la bibliothèque.

L’éditeur projette chaque instance sous la forme d’un bloc composite replié, avec son nom, le nombre de composants et ses ports. Le dépliage affiche le sous-graphe interne, sans réécrire les raccordements externes. Les composants internes restent configurables et modifiables depuis l’éditeur. Les connexions entrantes doivent viser l’entrée déclarée ; les connexions sortantes doivent passer par une sortie exposée. Le backend refuse les métadonnées de groupe incohérentes.

Les modifications d’une instance sont locales au flow et ne changent pas le pack enregistré. Les flows existants restent donc indépendants des évolutions ultérieures de la bibliothèque.

## API

- `GET /api/v1/component-packs` liste les packs.
- `GET /api/v1/component-packs/{id}` retourne la définition, son entrée et ses sorties.
- `POST /api/v1/component-packs/from-flow` crée un pack depuis une version publiée précise.

La migration `0020_component_packs.sql` ajoute le catalogue sans modifier les flows ou les médias existants.
