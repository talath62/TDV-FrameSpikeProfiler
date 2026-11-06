# v1.2.0 — diagnostic exhaustif (instance de test uniquement)

- Remplace le filtrage `Broad` par une découverte de **toutes les méthodes IL éligibles** de Valheim + DLL des plugins BepInEx et assemblies situées dans `BepInEx/plugins`.
- Inclut méthodes générées (lambdas/coroutines), accesseurs et constructeurs d'instance, avec filtres de compatibilité.
- Plafond configurable **18 000** méthodes par défaut (maximum absolu 30 000), rapports de couverture et de patches échoués.
- Profilage sur le thread principal avec **temps exclusif** et inclusif, par méthode, sans doubles comptes descendants dans le temps exclusif.
- Top 10 classé par `ExclusiveMax` par défaut, avec 4 modes de classement et nom du parent instrumenté (`calledBy`).
- Watchdog indépendant échantillonnant l'identité de la méthode en cours pendant une attente longue.
- Temps agrégé des méthodes exécutées sur les threads de fond, affiché séparément sans attribution fictive à la frame.
- Stockage des slots actifs seulement dans les records de frame : pas de remise à zéro de 30 000 compteurs à chaque frame.
- Conservation du GUID `tdv.frame-spike-profiler`, aucune modification volontaire des mécanismes de gameplay ou de réseau.

**Limites :** surcharge potentiellement massive ; exceptions/patches Harmony tiers peuvent perturber l'instrumentation ; pas de couverture native Unity / GC / noyau OS. Livré en source ; compilation réelle non effectuée faute d'assemblies/compilateur dans cet environnement.
