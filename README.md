# Code Obfuscator for Unity

A drop-in, project-agnostic **pre-build obfuscator** for Unity. It renames the names that make a shipped build
readable — classes, methods, fields, namespaces, assemblies, folders, assets, shaders, scene objects, PlayerPrefs
keys and your own studio / product words — **directly in the project, before the build**, and keeps every by-name
reference in sync so the build still runs. SDK folders are detected and never touched. Every change is journalled,
so one click restores the readable source. A pre-build gate refuses a player build until the project is obfuscated.

> **Why.** An IL2CPP build's `global-metadata.dat` holds every type, method and field name, every namespace and
> assembly name, and (Unity 6) every script's project path. Off-the-shelf dumpers turn that back into a readable
> class tree in minutes; a Mono build ships the IL itself. `PlayerWallet.AddCoins` explains itself — `ArcCell.ComposeCore`
> does not. This raises the cost of reading your build. It is **not** encryption and does not protect secrets
> (see [Honest limits](#honest-limits)).

- Editor-only, zero runtime footprint, no third-party dependencies.
- Unity **2021.3+** (developed on 6000.3).

---

## Install

**A — Package Manager, git URL (recommended)**
Window ▸ Package Manager ▸ **+** ▸ *Add package from git URL…* →
```
https://github.com/ayan29gojo/unity-code-obfuscator.git
```

**B — `manifest.json`**
```json
"com.eastwhiz.code-obfuscator": "https://github.com/ayan29gojo/unity-code-obfuscator.git"
```

**C — Manual**
Copy the `Editor/` folder into your project under `Assets/Editor/CodeObfuscator/`.

Then set **Edit ▸ Project Settings ▸ Editor ▸ Asset Serialization → Force Text** (scenes / prefabs must be YAML),
and open **Tools ▸ Code Obfuscator ▸ Open Window**. A profile is created at
`ProjectSettings/CodeObfuscator.profile.json` on first open.

---

## The workflow — obfuscate → build → restore

The tool rewrites the project **in place**. You keep readable source to work on and ship an obfuscated build, so
treat every run as a build step:

1. **Commit** (or use a throw-away branch / copy). Exit Play mode and Prefab mode.
2. **Scan Project** → review the list → **Apply Selected** (or **Apply & Build…**).
3. Wait for **Compiled OK**, build the player, and **play-test the build**.
4. **Restore last backup** (or `git reset --hard` + `git clean`) to get the readable source back.

Keep each shipped build's `change_map.json` **private** — it is the only key that maps a crash log from that build
back to your real names, and it undoes the obfuscation. Never Apply twice without a Restore in between (the tool
refuses).

---

## What gets renamed — and what stays in sync

| Category | Default | Kept in sync automatically |
|---|---|---|
| Namespaces | on | `namespace` / `using` / qualified refs, YAML `ns:` + `m_TargetAssemblyTypeName`, asmdef `rootNamespace`, `Type.GetType("Ns.Type, Asm")` strings |
| Classes | on | all usages, `.cs` filename (GUID kept → prefab `m_Script` links survive), `GetComponent("…")`, YAML `class:` / `EventTypeName:` values, class names inside log-tag literals |
| Resources | on | asset file **and** `Resources.Load("…")` string together; path-like consts too |
| Shaders | on | inner `Shader "…"`, `Shader.Find`, `Fallback` / `UsePass` in other shaders, file name |
| Scene objects | on | `m_Name` in scenes / prefabs, `GameObject.Find` / `transform.Find` paths (per segment), `.anim` curve `path:` |
| PlayerPrefs | on | key strings (resets existing saves) |
| Asset files | on | prefabs, ScriptableObjects, materials, textures, audio, models, controllers, scenes — GUID-referenced so links survive; `LoadScene("…")` + Build Settings synced; compound extensions (`.skel.bytes`) preserved |
| Brand pass | on | every casing variant of each `Old => New` pair over text, file **and** folder names, Player Settings, cloud project name |
| Assemblies + folders | on | `.asmdef` names (+ `references`, `link.xml`, `InternalsVisibleTo`, `"Type, Assembly"` strings) and folders named after namespaces / assemblies / brand terms |
| Spine paths | on | `imagesPath` / `audioPath` (`C:/Users/<artist>/…`) blanked in `.skel` / `.skel.bytes` / JSON skeletons |
| Strip comments | on | removes `//` and `/* */` |
| Inject unused vars | on | one `private static int` per class |
| Enum members | off | guarded: skipped if another type owns a same-named member, or it is used as a string |
| Fields | off | `[FormerlySerializedAs]` injected on every renamed serialized field; JSON-bound / reflected / event / static / const skipped |
| Methods | off | textual and risky — coroutine / Invoke / SendMessage strings, UnityEvent `m_MethodName`, AnimationEvent `functionName` synced; interface members, overrides, Unity messages skipped |

**Never renamed:** C# keywords, anything in detected SDK folders, every public / protected member of every non-project
assembly (engine, .NET, packages, SDKs), names read back by reflection (`GetMethod("…")`), every field of a JSON-bound
type and every key of a `.json` / `.txt` data file, and whatever you list in the profile's `protectedNames` /
`keepLiterals`.

---

## Your studio / product names (brand pass)

Two inputs:
- **Brand pairs** `Old => New` replace a word everywhere, in every casing (`SpaceBlaster`, `space_blaster`,
  `SPACE_BLASTER`, `"Space Blaster"`), in text, file / folder names and Player Settings.
- **Terms** (detect-only, plus the ones auto-derived from product / company / cloud project name, bundle id and your
  `.asmdef` names) are not replaced — they drive the identifier sweep, folder / assembly renames, verification and the
  build gate.

To keep your public product name but strip it from code: leave the pairs empty and keep *Auto-detect terms* on.

---

## SDK auto-detection

Every folder is scored on independent signals (any strong one is enough): known vendor folder name, native binaries,
vendor `.asmdef`, EDM4U / `link.xml` / `package.json` / `LICENSE`, vendor root namespaces, license headers, known
global-namespace SDK classes. The highest qualifying folder is excluded whole; loose SDK files are excluded
individually; assets an SDK loads by name are protected wherever they are. All lists are extensible from the profile.
If your *own* scripts carry SDK-like signals, list a regex under **Own-code markers** so their folder is never mistaken
for an SDK.

---

## Headless / CI

```
Unity.exe -batchmode -nographics -projectPath <project> -executeMethod CodeObfuscator.ObfBatch.Run -quit
          -obfAction scan|apply|verify|restore     (default apply)
          -obfProfile <file.json> [-obfProfileAll]  import a profile first (rules only, or everything)
          -obfBackup <backup dir>                   for restore (default: last run)
          -obfDisableHighRisk                       apply: untick every High-risk row first
```
Exit codes: `0` ok · `1` run failed / cancelled · `2` verify found problems · `3` bad arguments. A CI build is three
calls: `apply`, the build, `restore`. The recompile after `apply` happens after the method returns, so a compile error
surfaces in the following build / `verify` step. *(The batch driver is compile-checked; the interactive window is the
tested path.)*

---

## Safety rules (built in)

Each of these once broke a processed project **silently** — compiled clean, failed in Play mode — and is now guarded:
names read back from a string at runtime (JSON-bound fields, `Type.GetType`, reflection members, scene objects used as
string keys, enum members used as strings, Spine atlas texture names); a literal that equals an asset / folder /
Resources key must not follow a class rename; a serialized field named like its type gets `[FormerlySerializedAs]`;
reflection-driven YAML (`TargetFieldName:`, `*FieldValue:`, `FieldType: enum:X`) is mapped like code; assembly names,
script folders and the cloud project name ship in the build even when no C# mentions them. Add anything your project
invents to `protectedNames` / `keepLiterals`.

**A verified, compile-clean run can still break something looked up by string. Always play-test the obfuscated build
before shipping.**

---

## Honest limits

- **Names, not logic.** Control flow, algorithms, constants and string contents (UI text, URLs, log messages) are
  unchanged. This is not encryption, does not protect secrets, and does not stop memory editing. Never ship API keys
  or server secrets in a client build, obfuscated or not.
- Rename is **textual, not semantic** — two different symbols with the same name are renamed together. *Methods* in
  particular will rename a method called e.g. `Load` wherever that word appears as an identifier.
- Names that must stay public stay public: Unity messages (`Update`, `OnEnable`…), overrides of engine / SDK types,
  everything in excluded SDK folders.
- `UnitySendMessage(gameObjectName, methodName)` from Java / ObjC can't be seen from C# — put those names in
  `protectedNames`.
- ShaderGraph (`.shadergraph`) and Addressables keys are not handled. Java plugins, `AndroidManifest.xml` in excluded
  folders and backend URLs are reported, not changed.

---

## Output

- `CodeObfuscator_Backups/<stamp>/` — originals + `manifest.txt` (used by Restore)
- `CodeObfuscator_Reports/<stamp>/` — `applied.txt`, `verification.txt`, `change_map.json` (**keep private**), `profile.json`, …
- `ProjectSettings/CodeObfuscator.profile.json` — the profile (commit it)
- `ProjectSettings/CodeObfuscator.state.json` — last-run summary read by the gate
- `Library/CodeObfuscator/state.json` — per-machine state (never commit)

Add `CodeObfuscator_Backups/` and `CodeObfuscator_Reports/` to your project's `.gitignore`.

---

## License

[MIT](LICENSE.md) © ayan29gojo
