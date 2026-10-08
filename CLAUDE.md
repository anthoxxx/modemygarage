# modemygarage — Mods / plugins pour le jeu *My Garage*

Ce dépôt sert à créer des mods (plugins) pour **My Garage** (jeu Unity, Viking).
Référence officielle (non officielle du jeu) : https://fedearre.github.io/my-garage-modding-docs/#/
(source : https://github.com/FedeArre/my-garage-modding-docs). Résumé ci-dessous — c'est **la formule à suivre** pour tout plugin.

## Environnement
- C# / Unity. Le jeu a son propre ModLoader intégré (pas d'installation côté joueur).
- IDE : Visual Studio 2022 Community, workloads « .NET desktop » + « Game development with Unity »,
  composants « .NET Framework 3.5 development tools » et « .NET Framework 4.6 targeting pack ».
- Projet = **Class Library** (template « My Garage Template », `ModdingTemplate.vsix`).
- Références obligatoires : `<Jeu>/MyGarage_Data/Managed/Assembly-CSharp.dll` (+ `UnityEngine*.dll` du même dossier si besoin).
  Mettre **Copy Local = False** sur toutes les références.
- Sortie du build (Build → Output path) = dossier `Mods` du jeu. Fermer le jeu avant de compiler (dll verrouillée).
- Log : `%appdata%/../LocalLow/Viking/My Garage/Player.log` (le jeu ne signale pas les crashs de mods).
- Assets : Unity **2020.3.0**, AssetBundles en `BuildTarget.StandaloneWindows`.
- Reverse : dnSpy (Assembly-CSharp.dll, namespace vide = code du jeu), AssetStudio
  (`level0` = menu, `level1` = jeu, `level2` = test multi — ne pas modder le multi).

## Squelette d'un mod (formule de base)
```cs
using System;
using UnityEngine;

namespace MonMod
{
    public class ModMain : Mod
    {
        public override string ID => "MonMod_Unique";   // requis, unique
        public override string Name => "Mon Mod";       // requis
        public override string Author => "Auteur";      // requis
        public override string Version => "1.0";        // requis
        // public override byte[] Icon => ...;          // optionnel

        public ModMain()              // appelé 1 seule fois par session, avant OnMenuLoad → charger les assets ici
        {
        }

        public override void OnLoad() { }    // nouvelle partie / continue, AVANT le chargement de la sauvegarde
        public override void Continue() { }  // APRÈS chargement de la sauvegarde (même frame → attendre WaitForEndOfFrame)
        public override void OnSave() { }
        public override void Update() { }    // seulement en jeu
    }
}
```

### API `Mod` (toutes les méthodes sont optionnelles)
- Constructeur — une fois par session, avant `OnMenuLoad`, pour charger les assets.
- Menu : `OnMenuLoad`, `SecondPassOnMenuLoad`, `MenuUpdate`, `MenuLateUpdate`, `MenuFixedUpdate`, `MenuOnGUI`.
- Jeu : `OnLoad`, `SecondPassOnLoad`, `Update`, `LateUpdate`, `FixedUpdate`, `OnGUI`,
  `Continue`, `SecondPassContinue`, `OnSave`, `OnBarnSave` (grange sauvée avant le reste), `OnBarnLoad`.
- Champ `bool enabled` (ne pas toucher).
- `ModLoader` (ne jamais instancier) : `ModLoader.mods`, `GetModInstance(string id)`, `IsModInstalled(string id)`.

### Pièges connus
- `OnLoad` : ce qu'on modifie (ex. l'argent) est écrasé ensuite par la sauvegarde → utiliser `Continue`.
- Pas de coroutines dans la classe `Mod` → créer un `MonoBehaviour`, l'ajouter à un `GameObject`
  (`DontDestroyOnLoad` si besoin entre menu et jeu).
- Performance : ne jamais faire `GameObject.Find` / `GetComponent` dans `Update` → mettre en cache (dans `Continue`/`Start`).

## Internals du jeu
- **tools** (sur le GameObject `Player`) : champs statiques (`tools.money = 1234;`) ;
  méthodes d'instance via `GameObject.Find("Player").GetComponent<tools>()` (ex. `GoGarage()`), à mettre en cache.
- **Objets ramassables** : `AddComponent<MooveItem>()` (sans parent, avec Rigidbody).
  Propriétés : `price`, `CanPutInBox`, `Collectible` (false = sauvé seulement dans le garage), `IsFurniture`.
  Les pièces de voiture utilisent `Pickup`, pas `MooveItem`.
- **Météo / temps** (Enviro) : `EnviroSkyMgr.instance.ChangeSeason(EnviroSeasons.Seasons.Autumn)`,
  `EnviroSkyMgr.instance.ChangeWeather(8)`, `EnviroSky.instance.ChangeWeather(1)`.
- **Pause du joueur** : `FirstPersonAIO` (composant d'instance sur `Player`, pas statique) :
  `controllerPauseState`, `ControllerPause()`, `ControllerUnPause()`.
- **Liste des pièces** : `PartsParent` → `JunkPartsList.Parts` (+ `SHOPITEMS` → `SaleItem.Item`) ;
  achat = `tools.SpawnSpot = go; partinfo.SpawnThis();`. Son caisse : `hand` → `AudioManager.Cash` + `AudioSource`.
- **Adhérence** : tag du GameObject sous la roue — `Road`, `DirtRoad` (salit la voiture), `TRACK` (glissant).

## Compiler ici (Linux, sans Visual Studio)
`mcs` (paquet `mono-mcs`) avec `-nostdlib` et les dll du dossier `Managed` du jeu fournies par l'utilisateur
(Assembly-CSharp, mscorlib, netstandard, UnityEngine*.dll) ; `System.Core` depuis `/usr/lib/mono/4.7.1-api`.
Ne jamais commiter les dll du jeu (propriétaires) — seulement la dll du mod.

## AssetBundles (modèles custom)
1. Unity 2020.3 : dossiers `Editor` + `Assets`, script éditeur :
```cs
using UnityEditor; using System.IO;
public class CreateAssetBundles {
    [MenuItem("Assets/Build AssetBundles")]
    static void BuildAllAssetBundles() {
        string dir = "Assets/AssetBundles";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        BuildPipeline.BuildAssetBundles(dir, BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows);
    }
}
```
2. Prefab → Inspector → AssetBundle → nom ; clic droit → *Build AssetBundles* ; garder le fichier **sans extension**.
3. VS : Propriétés → Resources → ajouter le fichier existant. Puis :
```cs
AssetBundle bundle = AssetBundle.LoadFromMemory(Properties.Resources.mon_bundle);
GameObject go = GameObject.Instantiate(bundle.LoadAsset<GameObject>("NomDuPrefab"));
```

## SimplePartLoader (SPL) — ajouter des pièces de voiture
- Télécharger sur NexusMods (mods/7) versions *users* (dll → référence VS, `using SimplePartLoader;`)
  et *developers* (projet Unity « My Garage Modding » avec les composants du jeu ; ne pas attacher les scripts *dummy*).
- Ne gère pas : nouvelles voitures, transparents complexes, BoltNut complexes, données custom sauvegardées.
- **Unity** : modèle sans parent, scale **(1,1,1)**, rotation (0,0,0) (attention export Blender). Composants :
  - `CarProperties` : `PrefabName` **unique** (ex. NomDuMod+Pièce), Junk Spawn Chance (1–3), **Single part = true**, Part name (+ extension).
  - `Partinfo` : Renamed prefab (identifiant des transparents), Weight (kg), Price, catégories, Fits to car (`LAD`, `LADCoupe`, `Chad`), Thumbnail ≥ 512×512.
  - `MeshCollider` **Convex** (pas de trigger ; seul MeshCollider fonctionne pour ramasser).
  - Écrous : `HexNut` / `FlatNut` sur des enfants.
  - Peinture : matériau Albedo, rendu Opaque, bon index de matériau.
- **Code** (uniquement dans le constructeur du mod) :
```cs
public ModMain()
{
    AssetBundle bundle = AssetBundle.LoadFromMemory(Properties.Resources.mon_bundle);
    Part part = SPL.LoadPart(bundle, "NomDuPrefab");
    // attachesTo, localPos, localRot, [scale = Vector3.one], [testingModeEnable = false]
    part.SetupTransparent("TrunkDoor06", Vector3.zero, Quaternion.identity);
    bundle.Unload(false);
}
```
- Éditeur de transparents (`testingModeEnable = true`, à désactiver avant release) : nouvelle partie,
  Z = afficher/masquer, Num0 = mode pos/rot, Num1/3 X, Num4/6 Y, Num7/9 Z, Entrée pavé = multiplicateur,
  Num2/5/8 = rotation 90° sur X/Y/Z. Recopier les valeurs affichées en haut à gauche.
- Pièce absente du catalogue → vérifier « Dont show on catalog » puis le log (SPL désactive le mod fautif et explique pourquoi).
