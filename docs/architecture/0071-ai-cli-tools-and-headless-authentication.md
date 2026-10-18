# 0071 — Intégration des outils IA en ligne de commande (CLIs), authentification headless et traitement dans les flows

Statut : décision d'architecture. Complète [0005](0005-media-tools-in-flow-components.md), [0015](0015-flow-runtime-and-portable-archives.md), [0019](0019-plugin-configuration.md), [0033](0033-flow-waits-and-time.md), [0054](0054-script-tools-helper.md), [0064](0064-custom-and-managed-media-tools.md) et la politique d'isolation matérielle [todo-hardware-accelerated-tools-policy.md](../operations/todo-hardware-accelerated-tools-policy.md).

---

## 1. Contexte & Problématique

Pour enrichir les flux de traitement multimédia (traduction et adaptation contextuelle de sous-titres SRT/ASS, transcription de dialogues, génération de résumés, détection de chapitres), les utilisateurs souhaitent mobiliser des modèles d'intelligence artificielle sans être obligés de souscrire à des abonnements d'API facturés au token, en exploitant leurs abonnements existants (ex. ChatGPT Plus, Claude Pro/Team).

### Approche retenue : Outils CLI officiels
Plutôt que d'avoir recours au scraping web ou à des passerelles d'émulation non officielles (sujettes aux blocages, captchas et violations de conditions d'utilisation), Encoding intègre les **outils officiels en ligne de commande (CLIs)** fournis par les éditeurs :
- **Codex CLI / ChatGPT CLI** (authentification via compte ChatGPT).
- **Claude Code CLI** (`claude`) (authentification via compte Anthropic Claude).
- **Gemini CLI** (authentification Google / Google AI Studio).

### Défis à résoudre
1. **Environnement conteneurisé sans écran (Headless Docker)** : les serveurs d'encodage (NAS, serveurs dédiés, Proxmox, Unraid) ne disposent pas de navigateur web local pour valider une mire de connexion OAuth graphique.
2. **Exécution automatisée en tâche de fond (Non-interactive batching)** : les CLIs interactives affichent des spinners, couleurs ANSI et demandes de confirmation incompatibles avec l'exécution automatisée d'un flow.
3. **Persistance et sécurité des sessions sous Landlock** : stockage sécurisé des jetons de rafraîchissement sans compromettre le bac à sable du worker.

---

## 2. Décisions d'Architecture

### Règle 1 : Modélisation des CLIs comme outils média déclaratifs (`tool.json`)
Conformément à [0064](0064-custom-and-managed-media-tools.md), chaque CLI IA est déclarée dans le catalogue d'outils du worker via un descripteur déclaratif :

```json
{
  "$schema": "https://encoding.io/schemas/v1/tool.json",
  "id": "codex-cli",
  "name": "OpenAI Codex / ChatGPT CLI",
  "description": "Interface en ligne de commande officielle pour ChatGPT / Codex.",
  "category": "ai-text",
  "executable": "codex",
  "probe": {
    "versionArgs": ["--version"],
    "versionRegex": "codex(?:-cli)?\\s+v?(?<version>[0-9\\.]+)",
    "smokeTestArgs": ["--help"]
  },
  "capabilitiesExposed": [
    "ai-text-generation",
    "subtitles-translation"
  ],
  "auth": {
    "type": "device-code",
    "loginCommand": ["codex", "auth", "login", "--device-code"],
    "statusCommand": ["codex", "auth", "status"]
  }
}
```

> **Rappel de sécurité (Accélération matérielle)** :  
> Ces outils sont purement logiciels (CPU/Réseau). En vertu de [todo-hardware-accelerated-tools-policy.md](../operations/todo-hardware-accelerated-tools-policy.md), ils ne peuvent en aucun cas se substituer aux encodeurs matériels du conteneur (`ffmpeg` / NVENC / QSV / VA-API).

---

### Règle 2 : Authentification Headless par Code Unique (Méthode A1 - Device Code Flow)

Pour authentifier le worker Docker sans navigateur web local, le système implémente le flux **OAuth 2.0 Device Authorization Grant (RFC 8628)** :

```text
┌─────────────────────────┐          ┌─────────────────────────┐          ┌─────────────────────────┐
│     Interface Web       │          │      Worker Docker      │          │     Fournisseur IA      │
│    (PC Administrateur)  │          │    (Serveur Headless)   │          │   (ChatGPT / Anthropic) │
└────────────┬────────────┘          └────────────┬────────────┘          └────────────┬────────────┘
             │                                    │                                    │
             │ 1. Clic "Associer mon compte"      │                                    │
             ├───────────────────────────────────>│                                    │
             │                                    │ 2. Lance la CLI en mode device     │
             │                                    ├───────────────────────────────────>│
             │                                    │    (Reçoit URL + Code court)       │
             │                                    │<───────────────────────────────────┤
             │ 3. Affiche URL + Code court        │                                    │
             │    (ex: "Ouvrez chatgpt.com/auth   │                                    │
             │     et entrez le code ABCD-1234")  │                                    │
             │<───────────────────────────────────┤                                    │
             │                                    │                                    │
             │ 4. L'admin ouvre l'URL et valide   │                                    │
             │    le code sur son navigateur      │                                    │
             ├────────────────────────────────────────────────────────────────────────>│
             │                                    │                                    │
             │                                    │ 5. Polling CLI en arrière-plan     │
             │                                    ├───────────────────────────────────>│
             │                                    │ 6. Jeton de session validé         │
             │                                    │<───────────────────────────────────┤
             │ 7. Statut "Connecté" avec succès   │                                    │
             │<───────────────────────────────────┤                                    │
```

#### Modalités techniques :
1. **Déclenchement depuis l'IHM Angular** : dans la page de gestion du nœud (section **Outils IA**), un bouton `Associer un compte` est disponible pour chaque CLI compatible.
2. **Affichage du code** : l'API serveur démarre le processus d'authentification en boucle courte, intercepte l'URL d'autorisation et le code à usage unique (ex. `ABCD-1234`), et les affiche dans une modale Angular Material avec un bouton de copie rapide et lien cliquable.
3. **Stockage persistant des sessions** :
   - Les fichiers de configuration et jetons d'authentification générés par la CLI (ex. `~/.config/codex/` ou `~/.claude/`) sont redirigés vers le répertoire de données persistant du worker : `/data/credentials/ai-tools/<tool-id>/`.
   - Ce dossier est persisté hors de l'image Docker, monté en lecture/écriture pour le compte de service du worker, et exclu des réplications non chiffrées.

---

### Règle 3 : Mode d'Exécution Non-Interactif en Flow

Lorsqu'un bloc de flow sollicite la CLI (ex. pour traduire des sous-titres) :
1. **Drapeaux d'exécution stricts** :
   - L'appel via `tools.run()` applique obligatoirement les drapeaux désactivant le terminal interactif :
     - Codex CLI : `codex --non-interactive -p "<prompt>" --output-format text`
     - Claude CLI : `claude -p "<prompt>" --output-format text`
2. **Nettoyage de sortie** :
   - Le moteur filtre les séquences d'échappement ANSI (couleurs, retours chariot de barres de progression) pour ne retenir que le flux de données textuel pur (`stdout`).
3. **Codes d'erreur et quotas** :
   - Si la CLI renvoie un code d'erreur de quota temporaire (ex. rate limit d'abonnement horaire) :
     - Le composant ne marque pas l'exécution comme corrompue.
     - Il peut emprunter la sortie dédiée `rate-limit` ou déclencher une attente automatique via le mécanisme de reprise [0033](0033-flow-waits-and-time.md) jusqu'à la réouverture de la fenêtre de requêtes.

---

### Règle 4 : Blocs de Flow Dédiés

#### 1. `ai-translate-subtitles` (Traduction de sous-titres)
- **Découpage intelligent (Chunking)** :
  - Découpe le fichier source SRT ou ASS en paquets de 80 à 150 répliques pour respecter la fenêtre de contexte optimale de la CLI.
  - Préserve strictement la numérotation des blocs, les horodatages (`00:01:23,456 --> 00:01:25,789`) et les balises d'effets ASS (`{\an8}`, `{\i1}`).
- **Prompt système spécialisé** :
  - Injecte une consigne stricte de fidélité au minutage et d'adaptation culturelle / tutoiement-vouvoiement cohérent.
- **Validation syntaxique** :
  - Re-parse le résultat traduit. Si une ligne temporelle est altérée, le bloc réessaie avec une température plus basse ou bascule sur la sortie `error`.

#### 2. `ai-metadata-enricher` (Métadonnées & Chapitres)
- Analyse les résumés ou pistes audio pour générer des tags thématiques ou nommer des chapitres automatiquement, utilisables par le plugin Plex ([0059](0059-plex-playback-guard-and-analysis.md)).

#### 3. Scripts personnalisés (`base-script`)
- Accessible directement aux scripts JavaScript/Python des utilisateurs via `tools.run()` :
  ```javascript
  const res = tools.run("codex-cli", [
    "--non-interactive", 
    "-p", 
    "Résume en 3 phrases le contexte de cet épisode : " + dialoguesText
  ]);
  ```

---

## 3. Sécurité et Confinement Landlock

1. **Accès réseau contrôlé** :
   - Contrairement aux outils de transcodage média locaux qui sont coupés du réseau par le profil Landlock (0015 & 0054), les outils de la catégorie `ai-text` / `ai-cli` sont autorisés à communiquer en sortie HTTPS (port 443) vers les domaines officiels déclarés dans leur manifeste (ex. `api.openai.com`, `chatgpt.com`, `anthropic.com`).
2. **Isolation du système de fichiers** :
   - La CLI n'a accès qu'à son répertoire de session `/data/credentials/ai-tools/<tool-id>/` et au répertoire temporaire de travail du job en cours.
   - Aucun accès en lecture ou écriture aux bibliothèques multimédias sources ou aux clés de cluster.

---

## 4. Conséquences et Bénéfices

- **Autonomie financière** : L'utilisateur exploite pleinement son compte payant ChatGPT ou Claude sans surcoût d'API.
- **Zéro maintenance de reverse engineering** : Aucune fragilité liée au scraping de code HTML de pages web.
- **Expérience utilisateur fluide** : L'appairage par code unique (Device Code) prend moins de 30 secondes depuis n'importe quel smartphone ou navigateur.
