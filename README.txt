=====================================================================
 MONITEUR GPU / PCIe  -  vitesse de transfert et historique des liaisons
 Fichier unique, aucune installation, aucun droit administrateur.
=====================================================================

1. CE QUE FAIT LE PROGRAMME
---------------------------------------------------------------------
Trois onglets :

  * Cartes graphiques
      Liste chaque carte video detectee (nom, fabricant, memoire
      dediee, definition, identifiant PnP) et, surtout, la liaison
      PCIe reelle de chaque carte : generation (Gen1 a Gen6),
      largeur (x1 / x4 / x8 / x16), debit actuel, debit maximal,
      plus la chaine d'ancetres (carte -> port racine PCIe -> PCI).
      Le bouton "Mesurer les transferts" lance des copies reelles
      (CPU -> GPU, GPU -> GPU, GPU -> CPU, copie systeme) et affiche
      le debit en Go/s pour chaque sens de transfert.

  * Liaisons PCIe
      Inventaire de TOUS les peripheriques branches sur le bus PCI
      Express de la machine (cartes video, SSD NVMe, reseau WiFi,
      etc.) avec generation, largeur, debit actuel et debit maximal.

  * Historique
      Trace de chaque changement de liaison (ex : la carte est
      passee de x8 a x16) et de chaque mesure de transfert.
      L'historique est en memoire uniquement : il est vide a chaque
      demarrage du programme.


2. CONFIGURATION REQUISE
---------------------------------------------------------------------
  * Windows 10 ou Windows 11 (x64 ou ARM64)
  * .NET Framework 4.x  : deja present dans Windows 10 et 11, rien a
    installer
  * Droits administrateur : NON requis
  * Aucune dependance, aucun fichier supplementaire, pas de connexion
    Internet


3. INSTALLATION (30 secondes)
---------------------------------------------------------------------
  Telechargement : le package est dans le depot, dossier "dist" :
      dist\GpuMonitor-Windows11.zip
  Lien direct :
      https://github.com/Superloulou/monitor-pci/raw/main/dist/GpuMonitor-Windows11.zip

  1. Decompresser le zip ou vous le souhaitez, puis copier le dossier
     "GpuMonitor" ou il vous plait, par exemple :
         C:\Tools\GpuMonitor
     (sur le serveur IA : meme chose ; le programme n'a pas besoin
      d'etre installe, il n'ecrit rien dans le registre ni ailleurs)

  2. Double-cliquer sur :
         GpuMonitor.cmd
     La fenetre s'ouvre. C'est tout.

  Premier lancement : Windows peut afficher "Windows a protege votre PC"
  (le programme n'est pas signe numeriquement). Cliquer sur "Plus
  d'infos" puis "Executer quand meme". Si c'est genant, signer le
  fichier avec votre propre certificat d'entreprise, ou utiliser la
  methode sans .exe decrite au point 6.


4. DESINSTALLATION
---------------------------------------------------------------------
Supprimer le dossier. Le programme n'installe rien et ne laisse aucun
fichier ailleurs.


5. FICHIERS DU PACKAGE
---------------------------------------------------------------------
  GpuMonitor.exe          Le programme, pret a lancer.
  GpuMonitor.cmd          Lanceur par double-clic.
  GpuMonitor.cs           Code source complet (C#, 1 fichier).
  installer.cmd           Recompile GpuMonitor.exe a partir du source
                          avec le compilateur .NET deja present dans
                          Windows. Utile si vous voulez rebuild sur la
                          machine cible.
  lancer-sans-exe.ps1     Lance le programme sans jamais ecrire de .exe
                          sur le disque (compile en memoire). A
                          utiliser si la securite de la machine bloque
                          les executables non signes.
  README.txt              Ce fichier.
  (Le package complet est distribue sous forme d'archive :
   GpuMonitor-Windows11.zip, dans le dossier "dist" du depot GitHub.)


6. SI LE .EXE EST BLOQUE PAR LA SECURITE DE LA MACHINE
---------------------------------------------------------------------
Certaines machines (AppLocker, WDAC, SmartScreen, antivirus
d'entreprise) empechent l'execution d'un executable non signe. Dans ce
cas, lancer le programme sans fichier .exe :

    powershell -NoProfile -ExecutionPolicy Bypass -STA -File "C:\Tools\GpuMonitor\lancer-sans-exe.ps1"

Le code est compile en memoire par PowerShell : rien n'est ecrit sur le
disque. Compilation : 3 a 6 secondes.


7. TEST SANS INTERFACE (verifier que tout fonctionne)
---------------------------------------------------------------------
    powershell -NoProfile -ExecutionPolicy Bypass -STA -File "C:\Tools\GpuMonitor\lancer-sans-exe.ps1" -SelfTest

Affiche le resultat des trois moteurs (PCIe, cartes graphiques, mesures
de transfert) dans la console. Pratique pour verifier a distance.


8. COMMENT LIRE LES RESULTATS
---------------------------------------------------------------------
  * "Gen4 (16 GT/s) x4" = la liaison est en PCIe 4.0 sur 4 lignes.
    Debit theorique affiche en Go/s (GiB/s) :
        Gen1 : 0,23 Go/s par ligne     Gen4 : 1,84 Go/s par ligne
        Gen2 : 0,46 Go/s par ligne     Gen5 : 3,68 Go/s par ligne
        Gen3 : 0,92 Go/s par ligne     Gen6 : 7,36 Go/s par ligne
    Exemple : Gen4 x4 = 7,34 Go/s ; Gen3 x2 = 1,83 Go/s.

  * "AUCUN (non relie par PCIe)" : la carte n'est pas sur un bus PCI
    Express. C'est normal pour un GPU integre a un SoC (Qualcomm
    Adreno, Intel grand public recent) ou pour un adaptateur virtuel.
    La mesure de transfert reste disponible.

  * "Microsoft Remote Display Adapter" : vous etes en session bureau a
    distance (RDP). Windows remplace la carte video reelle par un
    adaptateur virtuel. Pour voir la carte reelle, il faut lancer le
    programme sur la console physique du serveur (ou en session
    administrateur : mstsc /admin), et selon les configurations, la
    carte n'apparait que lorsque une session locale l'utilise.

  * "copie elidee par le pilote : non mesurable" : pour GPU -> GPU, le
    pilote detecte une copie redondante et ne deplace aucune donnee.
    Le chiffre affiche n'est donc pas un debit reel. Les trois autres
    mesures (CPU -> GPU, GPU -> CPU, copie systeme) sont fiables.

  * Memoire dediee "non renseignee" : WMI ne remonte pas la memoire de
    certaines cartes (GPU integre a memoire partagee, adaptateur
    virtuel). La carte utilise de la RAM commune.

  * Les mesures de transfert dependent de la charge de la machine, de
    la taille du tampon (par defaut 16 Mo) et du pilote. Utiliser des
    tampons plus grands (64 ou 256 Mo) pour s'approcher du debit de
    saturation.


9. NOTES TECHNIQUES
---------------------------------------------------------------------
  * Lecture des liaisons PCIe : API Configuration Manager (cfgmgr32) +
    proprietes du peripherique (DEVPKEY_PciDevice_CurrentLinkSpeed /
    CurrentLinkWidth / MaxLinkSpeed / MaxLinkWidth).
  * Liste des cartes : WMI (Win32_VideoController).
  * Mesures de transfert : Direct3D 11 (DirectCompute / textures
    intermediaires staging), sans dependance externe.
  * Le programme est en lecture seule : il ne modifie aucun
    peripherique, aucun pilote, aucun registre.
  * Code source : un seul fichier C# (GpuMonitor.cs), compile avec le
    compilateur .NET Framework 4.8 fourni avec Windows.
