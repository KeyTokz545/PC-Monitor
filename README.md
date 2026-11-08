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
