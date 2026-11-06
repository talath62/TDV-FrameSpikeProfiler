# TDV Frame Spike Profiler v1.2.0 — MODE EXHAUSTIF DE TEST

**But :** détecter automatiquement les méthodes C# longues dans Valheim et les mods sur une **instance de test isolée**, sans devoir saisir les noms des méthodes. Cette version remplace le scan Broad de v1.1.0 par le parcours de **toutes les méthodes IL éligibles** dans les assemblies sélectionnées. Elle mesure le temps propre (exclusif), le temps inclusif, le maximum par appel et le nombre d'appels. Le GUID et le fichier de config sont conservés.

**Attention : jusqu'à 18 000 méthodes sont patchées par défaut**, chacune avec un Prefix/Finalizer Harmony. La phase d'installation peut être longue et la surcharge CPU importante. Cela peut produire des spikes supplémentaires et changer le comportement temporel. Ne pas installer sur la production.

## Installation / build

- Valheim dédié + BepInEx 5 ; projet C# .NET Framework 4.7.2.
- Windows avec .NET SDK : `BUILD.bat "C:\chemin\vers\Valheim"` ou `dotnet build TDVFrameSpikeProfiler.csproj -c Release -p:ValheimDir="C:\chemin\vers\Valheim"`.
- Mettre **une seule** DLL `TDVFrameSpikeProfiler.dll` dans le dossier `BepInEx/plugins` du serveur. Retirer l'ancienne version, redémarrer.
- En cas de validation de mods par **ValheimEnforcer**, exclure explicitement `tdv.frame-spike-profiler` des dépendances imposées aux clients. La DLL est **server-side only**.
- Rapports dans `BepInEx/LogOutput.log`, filtrer avec `grep '\[TDVFrame\]' BepInEx/LogOutput.log`.

## Configuration recommandée (instance de test)

Dans `BepInEx/config/tdv.frame-spike-profiler.cfg` :

```ini
[Profiler]
Enabled = true
DedicatedBatchModeOnly = true
SpikeThresholdMs = 100
MinimumLogSpacingSeconds = 1
TopMethods = 10
RankBy = ExclusiveMax
ExtraMethods =
IncludeBackgroundThreads = true

[AggressiveInstrumentation]
MaxMethods = 18000
IncludeValheim = true
IncludeBepInExPlugins = true
IncludePluginFolderAssemblies = true
IncludeAccessorsAndGeneratedMethods = true
IncludeInstanceConstructors = true
AdditionalAssemblyNames =

[Watchdog]
Enabled = true
IntervalMs = 20
```

**Important en mise à jour :** le système BepInEx conserve les valeurs déjà présentes. Les anciennes clés `AutoInstrumentation.Mode`, `MaxAutoMethods`, etc. ne sont plus utilisées par v1.2.0. Les nouvelles clés `AggressiveInstrumentation` apparaissent au premier lancement. Corriger `RankBy` si l'ancien fichier contient `Maximum` : cette valeur n'est plus reconnue et revient à `ExclusiveMax`.

**Si le démarrage est très lent :** ramener `MaxMethods` à 5000, puis augmenter à 10000 et 18000 en tests successifs. Pour cibler une DLL non découverte automatiquement, mettre le nom d'assembly (ou préfixe suivi de `*`) dans `AdditionalAssemblyNames`. Les assemblies des plugins et `assembly_valheim` sont incluses automatiquement.

## Les rapports

```text
[TDVFrame] SPIKE 711.1 ms | interUpdate=711.9 ms | GC(0/1/2)=1/1/1
 | patches=13652 | activeMethods=512 | measuredSelf~=680.0 ms | rank=ExclusiveMax
  1. ModX.CostlyMethod(2 args) | selfMax=516.000 ms | selfTotal=516.000 ms | inclMax=517.500 ms | inclTotal=517.500 ms | calls=1
  2. ModY.Work(0 args) | selfMax=60.000 ms | selfTotal=60.000 ms | inclMax=66.000 ms | inclTotal=66.000 ms | calls=1
  ... top 10 ...
  Watchdog: longest-gap-observed=680.0 ms | active-leaf=ModX.CostlyMethod(2 args) | depth=5
  BACKGROUND since last report (NOT attributable to this frame):
    ...
```

*Exemple fictif, pas une mesure du serveur.*

- `selfMax` : maximum **exclusif** d'un appel, après soustraction des sous-appels **eux aussi instrumentés**. Bon premier indicateur de la fonction qui consomme le temps.
- `selfTotal` : somme exclusive sur la frame (attention à la surcharge de profiling).
- `inclMax` / `inclTotal` : temps comprenant les appels descendants ; permet de retrouver le point d'entrée.
- `calls` : nombre de retours de cette méthode au cours de la frame.
- `calledBy` : méthode instrumentée parente observée pendant le plus long appel exclusif, si disponible.
- `measuredSelf~` : couverture temporelle approximative des appels du thread principal terminés durant la frame ; ce **n'est pas** une mesure CPU ni une garantie de couverture intégrale.
- `Watchdog.active-leaf` : méthode instrumentée la plus profonde observable au moment où le watchdog a détecté la plus longue attente ; **ce n'est PAS une stack trace native ni une preuve causale**. La méthode peut elle-même être en attente de GC/OS.
- `BACKGROUND` : temps agrégé depuis le précédent rapport des tâches de fond, **à ne pas attribuer directement à la frame anormale**.
- Rapport limité à un spike par seconde par défaut ; un seul log peut être répliqué dans la console AMP/Unity.

`RankBy` : `ExclusiveMax` (recommandé), `InclusiveMax`, `ExclusiveTotal`, `InclusiveTotal`.

## Couverture / limites importantes

Au démarrage, une ligne par assembly indique la quantité de patches réussis et un résumé affiche : `patched`, `candidates`, `failed`, `ineligible`, `cap`, temps d'installation. Si `CAP REACHED`, la couverture est incomplète. Les méthodes dont le patch Harmony échoue sont listées (12 premières erreurs) puis comptées.

La v1.2.0 instrumente méthodes ordinaires, lambdas, coroutines `MoveNext`, getters/setters et constructeurs d'instance, sauf methods abstraites, génériques ouverts, retours `ref`, méthodes sans IL et constructeurs statiques. Elle n'instrumente pas elle-même, ni les assemblies système, natives Unity ou BepInEx/Harmony core. Le code inliné/JIT, les callbacks natifs et méthodes chargées après le démarrage peuvent échapper au suivi.

**Une frame de 700 ms peut rester non attribuée** si le temps est passé dans le GC, le moteur Unity natif, une attente OS, la régulation FPS, une méthode externe au scan, ou entre callbacks managés. Même en mode exhaustif Harmony, on ne peut pas promettre de nommer une méthode C# dans ces cas. Il faut alors un profiler système/Mono natif ou un échantillonneur de la pile native.

Ces hooks préservent la valeur d'exception originale et ne cherchent pas à changer les données du jeu, mais **l'instrumentation Harmony elle-même n'est pas sans risque**. Comparer une session sans plugin, avec 5000 patches, puis avec 18000 patches ; noter le taux de spikes et la progression de chargement. Arrêter le test si l'instance devient instable.

## État de validation

- Vérification structurelle du code C#, cohérence des nouvelles clés et ZIP : oui.
- Compilation avec les DLL réelles Valheim / Harmony / BepInEx : **non disponible dans l'environnement de création**.
- Test de fonctionnement multi-joueurs et mesure de surcharge : **à réaliser sur l'instance de test**.
