# PC Monitor

Un seul logiciel sur le PC, une appli sur le téléphone : températures, charge, réseau, modes
Silencieux / Équilibré / Turbo, ventilateurs, son, applis et alimentation, depuis un iPhone ou
un téléphone Android posé à côté du PC.

- **PCMonitor.exe** (Windows 10/11) : tourne en arrière-plan, icône près de l'horloge. Il remplace la
  fenêtre noire Python, la fenêtre de LibreHardwareMonitor et G-Helper.
- **PCMonitor.apk** (Android) : le tableau de bord en plein écran, écran toujours allumé, recherche
  automatique du PC sur le Wi-Fi, réveil du PC à distance.
- **iPhone** : le même tableau de bord dans Safari, ajouté à l'écran d'accueil.

Les deux fichiers sont dans **Releases → latest**, recompilés automatiquement à chaque modification.

## Installation sur le PC

1. **PawnIO** doit être installé (pilote utilisé pour lire les capteurs du processeur).
2. Ferme l'ancienne version : la fenêtre noire `pc_monitor.py`. Ferme aussi **G-Helper** et
   **LibreHardwareMonitor**, et retire-les du démarrage de Windows : PC Monitor fait leur travail.
3. Pour garder ta clé et tes applis de l'ancienne version, mets `PCMonitor.exe` **dans l'ancien dossier
   PC-Monitor** (à côté de `config.json`) avant de le lancer la première fois.
4. Double-clic sur `PCMonitor.exe`, puis **Oui** à la demande d'autorisation de Windows.
   Si Windows affiche « Windows a protégé votre ordinateur », clique sur **Informations complémentaires
   → Exécuter quand même** (normal pour un logiciel perso non signé).
5. La fenêtre « Connecter un téléphone » s'ouvre. PC Monitor démarrera ensuite tout seul avec Windows.

Clic droit sur l'icône près de l'horloge :

- **Connecter un téléphone** : QR code pour l'iPhone, code à 6 chiffres pour l'appli Android.
- **Silencieux / Équilibré / Turbo**, et **Ventilos à fond en Turbo**.
- **Couleurs de l'écran** et **Bloquer la touche Windows**.
- **Lancer avec PC Monitor** : ThrottleStop et MSI Afterburner démarrent en même temps.
- **Démarrer avec Windows**, **Ouvrir le dossier des réglages** (réglages et journal), **Quitter**.

## Installation sur Android (Galaxy S20 FE)

1. Sur le téléphone, ouvre la page **Releases** du dépôt et télécharge `PCMonitor.apk`.
2. Ouvre le fichier. Android demande d'autoriser l'installation depuis cette source : accepte.
   Si Play Protect affiche un avertissement, choisis **Installer quand même**.
3. Ouvre **PC Monitor** : ton PC apparaît dans la liste. Touche-le et tape le code affiché sur le PC.
4. Pour changer de PC plus tard, touche le nom du PC en haut du tableau de bord.

Les mises à jour s'installent par-dessus l'ancienne version (même signature).

Conseil si le téléphone reste branché en permanence : Paramètres → Batterie → **Protection de la
batterie** (charge limitée à 85 %).

## iPhone

Clic droit sur l'icône → **Connecter un téléphone**, vise le QR code avec l'appareil photo, puis dans
Safari : Partager → **Sur l'écran d'accueil**.

## Onglet Jeu (2.2)

- **FPS** en direct (1 % et 0,1 % bas, temps par image) pour les jeux DirectX 9 à 12, sans rien
  injecter dans le jeu : PC Monitor lit les événements « Present » de Windows, comme PresentMon.
- **Couleurs de l'écran** : profils (gamma, contraste, luminosité, température, saturation).
  La saturation passe par le filtre couleur de Windows ; le reste par la table gamma de la carte graphique.
- **Profils de jeu automatiques** : au lancement du jeu (ex. `cod.exe`), mode, bridage GPU, couleurs et
  blocage de la touche Windows s'appliquent seuls, puis tout revient comme avant à sa fermeture.
- **Captures** : capture d'écran, clip et enregistrement (Xbox Game Bar ou NVIDIA).
- **Minuteur** d'arrêt ou de mise en veille (onglet Contrôles).

## Onglet CPU (2.3) : remplace ThrottleStop

- **Undervolt** cœurs + cache, GPU Intel et System Agent, avec un mode test : une valeur en test n'est
  jamais réappliquée après un redémarrage, seule la dernière valeur « gardée » l'est.
- **Limites de puissance PL1 / PL2** et durée du boost, **Turbo Boost** et **préférence d'énergie
  (Speed Shift)**, par mode (Silencieux, Équilibré, Turbo). Remises toutes les 10 s si le système les change.
- **Reprendre les réglages de ThrottleStop** : lit l'undervolt et les limites qu'il a appliqués,
  les garde, ferme ThrottleStop et coupe son lancement automatique.
- Accès au processeur par le pilote signé **PawnIO** (déjà installé pour les capteurs) et son module
  officiel IntelMSR 0.2.11 (LGPL-2.1, voir `pc/Resources`). Pas de BD PROCHOT, d'états C ni de
  multiplicateurs : ce module n'autorise pas ces registres.

## 2.4

- **Léger** : sans téléphone connecté, PC Monitor ne relève que le CPU et le GPU toutes les 5 s
  (ni Wi-Fi ni ping), puis rend la mémoire inutilisée à Windows au bout d'une minute.
- **Max / min des températures** gardés par le PC (communs à tous les téléphones) ; toucher une jauge
  pour les remettre à zéro.
- **Seuil thermique à 100 °C** : ThrottleStop lancé caché quelques secondes au démarrage et après une
  veille, puis fermé (le module PawnIO ne permet pas d'écrire ce réglage).
- **Thèmes** Nuit, OLED noir, ROG ; taille Confort ou Compacte ; anti-marquage pour un écran toujours allumé.

## Bon à savoir

- Le téléphone doit être sur le même Wi-Fi que le PC, réglé en réseau **privé** dans Windows.
- **Allumer le PC à distance** (appli Android) fonctionne surtout avec un câble Ethernet et si le BIOS
  autorise le réveil par le réseau.
- PC Monitor règle le Turbo Boost Windows par mode (Silencieux : activé, Équilibré et Turbo : agressif).
  Les limites de puissance du processeur restent gérées par ThrottleStop.
- Tout est réglable dans `%AppData%\PCMonitor\config.json` ; le journal est dans `journal.txt`.

## Licence

Les commandes ASUS (modes et ventilateurs) reprennent le fonctionnement de
[G-Helper](https://github.com/seerge/g-helper), sous licence GNU GPL v3. Ce projet est distribué sous
la même licence (voir `LICENSE`). Les capteurs utilisent
[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0).
