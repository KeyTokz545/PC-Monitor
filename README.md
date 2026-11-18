# Keystone

*(anciennement PC Monitor)*

Un seul logiciel sur le PC, une appli sur le téléphone : températures, charge, réseau, modes
Silencieux / Équilibré / Turbo, ventilateurs, son, applis et alimentation, depuis un iPhone ou
un téléphone Android posé à côté du PC.

- **Keystone.exe** (Windows 10/11) : tourne en arrière-plan, icône près de l'horloge. Il remplace la fenêtre de LibreHardwareMonitor et G-Helper.
- **Keystone.apk** (Android) : le tableau de bord en plein écran, écran toujours allumé, recherche
  automatique du PC sur le Wi-Fi, réveil du PC à distance.
- **iPhone** : le même tableau de bord dans Safari, ajouté à l'écran d'accueil.

Les deux fichiers sont dans **Releases → latest**, recompilés automatiquement à chaque modification.

## Installation sur le PC

1. **PawnIO** doit être installé (pilote utilisé pour lire les capteurs du processeur).
2. Ferme **G-Helper** et **LibreHardwareMonitor**, et retire-les du démarrage de Windows : Keystone fait leur travail.
3. Mise à jour depuis `PCMonitor.exe` : mets `Keystone.exe` dans le même dossier et lance-le. Il ferme
   l'ancienne version, garde tous les réglages (`%AppData%\PCMonitor`) et le démarrage avec Windows
   lance désormais `Keystone.exe`. L'ancien `PCMonitor.exe` peut ensuite être supprimé.
4. Double-clic sur `Keystone.exe`, puis **Oui** à la demande d'autorisation de Windows.
   Si Windows affiche « Windows a protégé votre ordinateur », clique sur **Informations complémentaires
   → Exécuter quand même** (normal pour un logiciel perso non signé).
5. La fenêtre « Connecter un téléphone » s'ouvre. Keystone démarrera ensuite tout seul avec Windows.

Clic droit sur l'icône près de l'horloge :

- **Connecter un téléphone** : QR code pour l'iPhone, code à 6 chiffres pour l'appli Android.
- **Silencieux / Équilibré / Turbo**, et **Ventilos à fond en Turbo**.
- **Couleurs de l'écran** et **Bloquer la touche Windows**.
- **Lancer avec Keystone** : ThrottleStop et MSI Afterburner démarrent en même temps.
- **Démarrer avec Windows**, **Ouvrir le dossier des réglages** (réglages et journal), **Quitter**.

## Installation sur Android 

1. Sur le téléphone, ouvre la page **Releases** du dépôt et télécharge `Keystone.apk`.
2. Ouvre le fichier. Android demande d'autoriser l'installation depuis cette source : accepte.
   Si Play Protect affiche un avertissement, choisis **Installer quand même**.
3. Ouvre **Keystone** : ton PC apparaît dans la liste. Touche-le et tape le code affiché sur le PC.
4. Pour changer de PC plus tard, touche le nom du PC en haut du tableau de bord.

Les mises à jour s'installent par-dessus l'ancienne version (même signature).

Conseil si le téléphone reste branché en permanence : Paramètres → Batterie → **Protection de la
batterie** (charge limitée à 85 %).

## iPhone

Clic droit sur l'icône → **Connecter un téléphone**, vise le QR code avec l'appareil photo, puis dans
Safari : Partager → **Sur l'écran d'accueil**.

## Onglet Jeu (2.2)

- **FPS** en direct (1 % et 0,1 % bas, temps par image) pour les jeux DirectX 9 à 12, sans rien
  injecter dans le jeu : Keystone lit les événements « Present » de Windows, comme PresentMon.
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

- **Léger** : sans téléphone connecté, Keystone ne relève que le CPU et le GPU toutes les 5 s
  (ni Wi-Fi ni ping), puis rend la mémoire inutilisée à Windows au bout d'une minute.
- **Max / min des températures** gardés par le PC (communs à tous les téléphones) ; toucher une jauge
  pour les remettre à zéro.
- **Seuil thermique à 100 °C** : ThrottleStop lancé caché quelques secondes au démarrage et après une
  veille, puis fermé (le module PawnIO ne permet pas d'écrire ce réglage).
- **Thèmes** Nuit, OLED noir, ROG ; taille Confort ou Compacte ; anti-marquage pour un écran toujours allumé.

## 2.13.4

- Cycle de couleurs et arc-en-ciel (anciens ROG) : couper le bandeau ou le logo n'éteint plus tout. Le logo suit
  ces deux effets (pas de bouton pour le couper seul sur ces claviers) ; en arc-en-ciel, couper le clavier éteint tout.

## 2.13.3

- Éclairage des ROG d'avant 2021 (Scar II GL504/GL704) : le logo et le bandeau s'allument aussi en fixe,
  respiration et stroboscope, et leurs interrupteurs (clavier, bandeau, logo) marchent séparément, dans tous les
  effets (format d'allumage et zones repris d'asusctl).

## 2.13.2

- Wi-Fi lu sans la localisation de Windows (plus de « netsh » toutes les 5 s) : signal, dBm, bande, canal,
  vitesse du lien et nom du réseau s'affichent même avec la localisation désactivée.

## 2.13.1

- Options : le haut de la page (titre, croix) ne sort plus de l'écran après « Créer un thème » ou quand le
  clavier s'ouvre.

## 2.13

- **Options** refaites : une page par catégorie (Apparence, Compteurs, Menus et gestes, Écran du PC, Touches,
  Alertes, Mise à jour).
- **Compteurs** au choix pour le CPU et le GPU : cadran, anneau, aiguille, LED, chiffres ; et les 3 infos
  affichées sous le compteur (charge, conso, fréquence, ventilateur, mémoire, max/min, VRAM).
- **Créateur de thème** : fond, fenêtres, texte, accent, coins (très ronds, arrondis, droits, coupés) et police
  des chiffres ; thèmes enregistrés sur le téléphone.
- **Touches dédiées** : Pause/Attn et Impr. écran, seules, changent la ventilation / le profil CPU / le micro
  ou lancent « Avant de jouer » (réglable). Avec Win, Alt ou Ctrl elles gardent leur rôle normal.
- **Avant de jouer** (onglet Jeu) : ferme les programmes choisis, vide la mémoire en cache de Windows, met la
  ventilation et le profil CPU voulus, en un appui.
- **Bridage** : la raison (chauffe, puissance, PROCHOT…) s'affiche sur la carte du CPU / GPU, bandeau en cas de
  chauffe.
- **Profils de jeu** : décalages GPU (undervolt) appliqués pendant le jeu.
- **Bilan de partie** : courbes FPS, températures et watts sur toute la partie.
- **Stockage** (onglet Contrôles) : santé des SSD, température, données écrites, place libre.
- **Mise à jour en un bouton** (Options > Mise à jour) : télécharge, vérifie, remplace Keystone.exe et redémarre.
  Dépôt privé : il faut un jeton GitHub en lecture seule (fine-grained, Contents : Read-only).

## 2.12

- **Annonce sur l'écran du PC** : quand on change la ventilation, le profil CPU, le micro, le son, le pavé
  tactile ou la touche Windows (depuis l'appli ou l'icône), le logo Keystone s'anime dans un coin de l'écran
  avec le choix. Réglages dans Personnaliser → Sur l'écran du PC (coin, test). Invisible aux clics, ne prend
  jamais le focus ; un jeu en plein écran exclusif peut le masquer (le mode fenêtré sans bordure l'affiche).

## 2.10 / 2.11

- **Menus sans trou** : les fenêtres se rangent en colonnes. **Appui long** sur une fenêtre puis glisser pour la
  déplacer ; **toucher son titre** pour la replier. Disposition gardée sur le téléphone (Personnaliser → Remettre).
- **Glisser à gauche / à droite** pour changer d'onglet. Les ventilateurs sont dans l'onglet **GPU**.
- **HUD en jeu** : plein écran (FPS, températures, watts, ping) qui s'ouvre seul au lancement d'un jeu à profil
  automatique, ou via le bouton « HUD plein écran » de l'onglet Jeu. Retour Android pour le fermer.
- Thèmes Braise, Sakura, Ardoise, Cyber, couleur libre, niveaux d'animation, fond animé.

## Bon à savoir

- Le téléphone doit être sur le même Wi-Fi que le PC, réglé en réseau **privé** dans Windows.
- **Gros téléchargement en Wi-Fi** (Steam…) : le Wi-Fi saturé retarde les réponses du PC. Keystone l'indique
  (point orange, « Le PC répond lentement », bandeau « Téléchargement en cours ») au lieu d'afficher « PC
  injoignable », réservé à 12 s sans aucune réponse. Pour garder tout fluide, limite la bande passante dans
  Steam (Paramètres > Téléchargements).
- **Allumer le PC à distance** (appli Android) fonctionne surtout avec un câble Ethernet et si le BIOS
  autorise le réveil par le réseau.
- Keystone règle le Turbo Boost Windows par mode (Silencieux : activé, Équilibré et Turbo : agressif).
  Les limites de puissance du processeur restent gérées par ThrottleStop.
- Tout est réglable dans `%AppData%\PCMonitor\config.json` ; le journal est dans `journal.txt`.

## Licence

Les commandes ASUS (modes et ventilateurs) reprennent le fonctionnement de
[G-Helper](https://github.com/seerge/g-helper), sous licence GNU GPL v3. Ce projet est distribué sous
la même licence (voir `LICENSE`). Les capteurs utilisent
[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0).
