# Moniteur GPU / PCIe

Affiche en temps réel la **liaison PCIe réelle** de chaque carte graphique (génération et
largeur : x1 / x4 / x8 / x16), le **débit de transfert mesuré** de chaque carte, et
l'**historique** des changements de liaison.

Un seul fichier C#, aucune dépendance, aucun installateur, aucun droit administrateur.

---

## Les trois onglets

| Onglet | Contenu |
|---|---|
| **Cartes graphiques** | Chaque carte vidéo détectée (nom, fabricant, mémoire dédiée, définition, identifiant PnP) avec sa liaison PCIe réelle : génération (Gen1 → Gen6), largeur (x1/x4/x8/x16), débit actuel, débit maximal, et la chaîne d'ancêtres (carte → port racine PCIe → bus PCI). Le bouton *Mesurer les transferts* lance des copies réelles CPU → GPU, GPU → GPU, GPU → CPU et copie système, et affiche le débit en Go/s pour chaque sens. |
| **Liaisons PCIe** | Inventaire de **tous** les périphériques branchés sur le bus PCI Express de la machine (cartes vidéo, SSD NVMe, carte WiFi, etc.) : génération, largeur, débit actuel, débit maximal. |
| **Historique** | Trace de chaque changement de liaison (ex. la carte est passée de x8 à x16) et de chaque mesure de transfert. En mémoire uniquement : vidé à chaque démarrage. |

## Lancement rapide

```
double-cliquer sur GpuMonitor.cmd
```

Le lanceur compile `GpuMonitor.exe` à la volée avec le compilateur .NET déjà présent dans
Windows s'il n'est pas encore présent, puis ouvre la fenêtre.

## Configuration requise

- Windows 10 ou Windows 11 (**x64 ou ARM64**)
- .NET Framework 4.x — déjà présent dans Windows 10 et 11, rien à installer
- Droits administrateur : **non requis**
- Aucune dépendance, aucune connexion Internet

## Fichiers

| Fichier | Rôle |
|---|---|
| `GpuMonitor.cmd` | Lanceur par double-clic (recompile au besoin, puis démarre la fenêtre). |
| `installer.cmd` | Recompile `GpuMonitor.exe` à partir de `GpuMonitor.cs` avec le `csc.exe` de Windows. |
| `lancer-sans-exe.ps1` | Compile et lance **sans écrire de .exe sur le disque** (si AppLocker / WDAC / SmartScreen bloque les exécutables non signés). |
| `lancer-gpumonitor.ps1` / `.cmd` | Même méthode « sans .exe », côté développeur. |
| `GpuMonitor.cs` | Le code source complet (un seul fichier). |
| `README.txt` | Le mode d'emploi livré dans le package (même contenu que ce fichier). |
| `probes/` | Sondes de diagnostic décrites plus bas. |

Test sans interface graphique (pratique à distance, sur un serveur) :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -STA -File .\lancer-sans-exe.ps1 -SelfTest
```

Compilation manuelle :

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /platform:anycpu /target:winexe /optimize+ /out:GpuMonitor.exe GpuMonitor.cs /r:System.dll,System.Core.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Management.dll
```

## Comment lire les résultats

`Gen4 (16 GT/s) x4` = liaison PCIe 4.0 sur 4 lignes. Débit théorique affiché en Go/s (GiB/s) :

| Génération | Débit par ligne |
|---|---|
| Gen1 | 0,23 Go/s |
| Gen2 | 0,46 Go/s |
| Gen3 | 0,92 Go/s |
| Gen4 | 1,84 Go/s |
| Gen5 | 3,68 Go/s |
| Gen6 | 7,36 Go/s |

Exemples : Gen4 x4 = 7,34 Go/s ; Gen3 x2 = 1,83 Go/s.

- **`AUCUN (non relié par PCIe)`** — la carte n'est pas sur un bus PCI Express. Normal pour un
  GPU intégré à un SoC (Qualcomm Adreno, Intel grand public récent) ou un adaptateur virtuel.
  La mesure de transfert reste disponible.
- **`Microsoft Remote Display Adapter`** — vous êtes en session RDP : Windows remplace la carte
  réelle par un adaptateur virtuel. Lancer le programme sur la console physique du serveur, ou
  en session administrateur (`mstsc /admin`).
- **`copie élidée par le pilote : non mesurable`** — pour GPU → GPU, le pilote détecte une copie
  redondante et ne déplace aucune donnée ; le chiffre affiché n'est pas un débit réel. Les trois
  autres mesures (CPU → GPU, GPU → CPU, copie système) sont fiables.
- **Mémoire dédiée `non renseignée`** — WMI ne remonte pas la mémoire de certaines cartes
  (GPU intégré à mémoire partagée, adaptateur virtuel) ; la carte utilise la RAM commune.
- Les mesures dépendent de la charge de la machine, de la taille du tampon (16 Mo par défaut) et
  du pilote. Utiliser 64 ou 256 Mo pour s'approcher du débit de saturation.

## Notes techniques

- Liaisons PCIe : API Configuration Manager (`cfgmgr32`) + propriétés de périphérique
  `DEVPKEY_PciDevice_CurrentLinkSpeed` / `CurrentLinkWidth` / `MaxLinkSpeed` / `MaxLinkWidth`.
- Liste des cartes : WMI (`Win32_VideoController`).
- Mesures de transfert : Direct3D 11 (textures staging), sans dépendance externe.
- Le programme est **en lecture seule** : il ne modifie aucun périphérique, pilote ou registre.
- Le code vise **C# 5** (compilateur .NET Framework 4.8 fourni avec Windows) : pas d'interpolation
  de chaînes, pas d'`?.`. Les interfaces D3D11 sont appelées via la table de méthodes virtuelles
  (`IntPtr`), `QueryInterface` / DXGI n'étant pas disponibles sur toutes les machines.

## `probes/` — sondes de diagnostic

Petits programmes utilisés pour valider le matériel et l'API avant d'écrire l'application.
Ils documentent les contraintes rencontrées et servent encore à diagnostiquer une machine
inconnue.

| Sonde | Utilité |
|---|---|
| `pdiag.cs` | Inventaire du bus PCI via `cfgmgr32` et lecture des propriétés de liaison (génération / largeur). Référence de l'implémentation PCIe. |
| `wmiprobe.cs` | Vide les propriétés de `Win32_VideoController` (montre notamment que `PNPClass` et `AdapterRAM` sont absents ou inexploitables sur certaines machines). |
| `nameprobe5.cs` | Tente d'obtenir les noms d'affichage des périphériques PCIe par `cfgmgr32` et par DEVPKEY — conclut que seule WMI (`Win32_PnPEntity`) fournit ces noms. |
| `diag4cls.cs` | Moteur de mesure D3D11 validé (copies H2D / D2D / D2H / memcpy) avec statistiques min / médiane / max. |

Compilation d'une sonde :

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe /out:probe.exe probes\pdiag.cs /r:System.dll,System.Core.dll,System.Management.dll
```

## Licence

Code fourni tel, pour un usage personnel ou interne.
