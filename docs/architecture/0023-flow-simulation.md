# 0023 — Testeur de flow sans effets externes

## Besoin

Explorer le chemin suivi par le document actuellement ouvert dans l’éditeur, avec un fichier fictif ou les métadonnées d’un fichier importé. Le test doit expliquer les décisions sans créer d’exécution, lire une bibliothèque, encoder, exécuter du code utilisateur, appeler un plugin ou transférer du travail.

## Contrats et moteur

`POST /api/v1/flows/simulate` reçoit `FlowSimulationRequest` : définition portable, profil de fichier et résultats fictifs indexés par identifiant de bloc. Le backend applique la validation de publication commune, puis parcourt une seule branche séquentielle du DAG. Une reconvergence est visitée une fois. Une définition invalide retourne 422 ; un profil ou résultat fictif invalide retourne 400. Le scénario est limité à 1 Mio.

Le service `FlowSimulator` ne possède aucune dépendance au runtime, aux outils, aux dépôts d’exécutions ou aux processus plugins. Il réutilise `ConditionTree` pour les comparaisons et leur court-circuit ET/OU. L’observateur facultatif de cet évaluateur produit les valeurs réellement comparées, résultats et erreurs ; les règles court-circuitées ne sont pas présentées comme évaluées. Le worker conserve le même évaluateur ; `FlowPath` centralise aussi le passage à la prochaine étape depuis une sortie. `FlowVariableValue` et `FlowMediaValues` centralisent respectivement l’interprétation JSON/texte des variables, les sélections de pistes des probes et l’identification des opérations d’un plan FFmpeg.

L’entrée utilise le profil fourni. Les variables, probes, préparation du plan, ajout d’opérations au plan, exécution ignorée si le plan est vide et fins Base ont un comportement déterministe. Les conditions natives utilisent les instantanés origine/courant, variables, extension, chemin d’origine et identifiants simulés. Les données absentes empruntent `error`, comme sur le worker. Un plan restant en fin de chemin est un échec.

Toute conversion autonome, exécution de plan, script, condition JavaScript, intégration ou contrainte/relais de nœud nécessite un résultat explicite : sortie déclarée, métadonnées courantes éventuelles, fusion de variables, extension et indicateur de transformation. Le simulateur s’arrête avec `waiting` au premier résultat absent. Une transformation fictive ne remplace jamais les métadonnées d’origine. La trace distingue systématiquement une hypothèse d’une décision native. Ces résultats n’attestent ni la validité du fichier produit ni la disponibilité des outils/services.

Le relais nécessite un nouveau worker fictif et utilise `RuntimeHandoffPolicy`, avec les paramètres globaux et dérogations enregistrées du flow. L’endpoint lit seulement les paramètres du plugin Nœuds via le dépôt commun, à chaque requête de simulation. Il vérifie la cible explicite et le changement de worker ; il décrit délégation ou transfert selon l’indicateur de transformation. L’accès aux bibliothèques, les capacités, les limites et la disponibilité réelles du candidat restent à vérifier par le répartiteur. Aucun bail, réservation ou transfert n’est créé.

## Import MediaInfo

`POST /api/v1/flows/inspect-metadata` reçoit les octets du fichier, sans chemin serveur fourni par l’utilisateur. `FlowMetadataImporter` copie temporairement le flux dans un dossier aléatoire privé au serveur, rend la copie en lecture seule et lance uniquement `mediainfo --Output=JSON`. Les arguments passent par `ArgumentList`. La copie est supprimée dans `finally`, y compris après erreur/annulation. Le média original et les bibliothèques ne sont jamais ouverts en écriture.

Bornes : 512 Mio par fichier, deux lectures simultanées par serveur, deux minutes incluant réception/analyse et 1 Mio par flux de sortie du processus. Le processus est interrompu après annulation, dépassement ou rapport trop volumineux. Un fichier vide/non reconnu reçoit 400, une saturation 429 et l’absence de MediaInfo 503. L’image API installe MediaInfo ; aucune dépendance à un worker permanent n’est ajoutée.

`MediaInfoMetadata` normalise les dimensions, taille, durée et pistes vers `format`/`streams`, utilisés par les conditions communes. Les codecs courants et langues courantes sont harmonisés, les champs inconnus restent absents. Ce profil couvre les critères natifs disponibles et ne prétend pas reproduire un rapport ffprobe ou MediaInfo exhaustif.

## Interface

Le bouton **Tester le flow** ouvre le drawer commun. Il capture le document courant, sans sauvegarde ni publication. Le profil utilise le formulaire de configuration partagé ; nom, taille, durée, codec/résolution, pistes audio/sous-titres, contexte et variables sont modifiables. L’import alimente ce profil. Les métadonnées après une étape et variables produites se renseignent en JSON, avec le profil courant prérempli.

Le parcours est une liste repliable ordonnée avec sortie, décision/hypothèse, comparaisons et contexte avant/après. La poursuite rejoue le même document depuis son entrée avec les résultats fictifs déjà renseignés. Une nouvelle simulation de profil réinitialise ces résultats ; rouvrir le testeur capture les dernières modifications du flow. Profils/résultats restent dans la session de l’éditeur, sans stockage PostgreSQL ni sauvegarde de médias importés.

Le composant partagé `ComponentConfigurationForm` possède désormais un état `busy` optionnel ; les contrôles et soumissions sont suspendus pendant une requête. Changer uniquement cet état ou le résultat d’un test de connexion conserve les saisies du formulaire. Les autres usages gardent leur état par défaut.

## Vérifications

- Tests déterministes des sorties Oui/Non/Erreur, reconvergence, variables/probes, plans, instantanés et relais hérité.
- Contrats HTTP 200/400/422 et profils/résultats invalides.
- Import réel d’un média synthétique FFmpeg/MediaInfo, empreinte source inchangée, fichier vide/non reconnu, saturation et annulation avec libération des slots.
- Tests frontend de construction/édition du profil, valeurs absentes, types JSON et limites de pistes.
- Parcours navigateur sur ordinateur/mobile : conversion supposée, comparaisons origine/courant, import MediaInfo et modification du profil importé.
