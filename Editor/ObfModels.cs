// Code Obfuscator — data models, the settings snapshot, the config facade and the run summary.
// Menu: Tools ▸ Code Obfuscator
//
// v4: ObfConfig is a thin facade over ObfProfile (a JSON file inside the project) — nothing is read
// from EditorPrefs any more. Per-machine state (last detection, last backup, build hand-over) is in
// ObfLocalState under Library/. Worker threads only ever see the immutable ObfSettings snapshot.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace CodeObfuscator
{
    public enum ObfCategory
    {
        Namespace,
        Class,
        EnumMember,
        Method,
        Field,
        ResourcePath,
        Shader,
        SceneObject,
        PlayerPrefsKey,
        AssetFile,      // prefabs, ScriptableObjects, materials, textures, audio, scenes … (GUID-safe)
        AnimatorString, // review-only
        DynamicLoad,    // review-only
        NativeBridge,   // review-only
        // ---- v3.4: the leaks a before/after build comparison still found after a full run ----
        BrandIdentifier,// any code identifier (property, event, override, parameter …) carrying a brand term — renamed regardless of the category toggles
        AssemblyName,   // .asmdef "name" (ships as Foo.dll + every assembly-qualified type name in the metadata)
        Folder,         // folders named after a namespace / assembly / brand term (Unity 6 IL2CPP embeds every script's project path)
        BrandReview     // review-only: brand term inside a URL / package id / native class name / protected identifier — decide by hand
    }

    public enum ObfRisk { Low, Medium, High }

    [Serializable]
    public class RenameEntry
    {
        public ObfCategory category;
        public string oldName;
        public string newName;
        public string primaryPath;
        public int occurrences;
        public ObfRisk risk;
        public bool enabled = true;
        public bool reviewOnly;
        public string note;
    }

    public class ScanResult
    {
        public List<RenameEntry> entries = new List<RenameEntry>();

        public List<string> csFiles = new List<string>();      // included (non-excluded) scripts
        public List<string> sceneFiles = new List<string>();
        public List<string> prefabFiles = new List<string>();
        public List<string> assetFiles = new List<string>();   // .asset (ScriptableObjects etc.)
        public List<string> animFiles = new List<string>();
        public List<string> asmdefFiles = new List<string>();
        public List<string> shaderFiles = new List<string>();  // included .shader files

        public Dictionary<string, string> resourcePathToAsset = new Dictionary<string, string>();
        public Dictionary<string, string> shaderNameToFile = new Dictionary<string, string>();
        public Dictionary<string, string> asmdefNameToFile = new Dictionary<string, string>();   // v3.4: assembly name → .asmdef path
        public HashSet<string> serializedFieldNames = new HashSet<string>(); // public or [SerializeField]
        /// v3.4: the brand terms the run works with — pair 'from' sides + explicit terms + auto-detected ones.
        public List<string> brandTerms = new List<string>();

        /// Files the scanner had to give up on (per-file budget / regex timeout / read error) — "path | reason".
        public List<string> skipped = new List<string>();
        /// Informational notes from the scan (e.g. how many names were left alone because they collide with framework/SDK members).
        public List<string> notes = new List<string>();

        public List<RenameEntry> Enabled(ObfCategory cat) =>
            entries.Where(e => e.category == cat && e.enabled && !e.reviewOnly).ToList();

        public Dictionary<string, string> Map(ObfCategory cat)
        {
            var d = new Dictionary<string, string>();
            foreach (var e in Enabled(cat)) if (!string.IsNullOrEmpty(e.newName) && e.newName != e.oldName) d[e.oldName] = e.newName;
            return d;
        }
    }

    public enum ObfGateMode { Block = 0, Warn = 1, Off = 2 }

    /// Immutable snapshot of the profile, taken on the main thread before a run.
    /// Worker threads only ever see this object — never the profile file, EditorPrefs or the Unity API.
    public class ObfSettings
    {
        public bool renameNamespaces, renameClasses, renameEnumMembers, renameMethods, renameFields;
        public bool remapResources, renameShaders, renameSceneObjects, renamePlayerPrefs;
        public bool injectUnusedVars, stripComments, renameAssetFiles, brandPass;
        public string brandPairsText, newProductName, newCompanyName, newBundleId;
        public int perFileBudgetSec, regexTimeoutSec, totalBudgetMin;
        // ---- v3.4 ----
        public bool detectBrandTerms = true;     // derive brand terms from product / company / bundle id / assembly names
        public bool sweepBrandIdentifiers = true;// rename every code identifier that carries a brand term (all kinds, all categories)
        public bool renameAssemblies = true;     // .asmdef names + folders named after namespaces / assemblies / brand terms
        public bool syncClassLiterals = true;    // log tags and other literals naming a renamed class
        public bool scrubSpine = true;           // blank the developer paths Spine exports into .skel / .json skeletons
        public string brandTermsText;            // extra brand terms (detect-only, no replacement) one per line
        public string oldProductName, oldCompanyName, oldBundleId, cloudProjectName; // captured on the main thread for the worker
        // ---- v4: profile extensions (all optional, empty arrays = built-in behaviour) ----
        public string[] customReplacements = new string[0];   // "old => new" exact substring rules (brand stage, text + names)
        public string[] protectedNames = new string[0];       // identifiers never renamed
        public string[] keepLiterals = new string[0];         // literals never rewritten
        public string[] vendorFolders = new string[0];        // extra SDK folder leaf names
        public string[] vendorNamespaces = new string[0];     // extra SDK root namespaces
        public string[] sdkClasses = new string[0];           // extra global SDK class names
        public string[] sdkAssetStems = new string[0];        // extra SDK settings asset stems
        public string[] reconstructionMarkers = new string[0];// regex markers of the project's own game code
        public string[] nameNouns = new string[0], nameAdjectives = new string[0], nameVerbs = new string[0];
        public string outputRoot = "";
    }

    /// Facade over ObfProfile.Current (per-project JSON) — kept so the rest of the tool reads like v3.
    /// Setters save the profile only when the value actually changed (the window assigns every repaint).
    public static class ObfConfig
    {
        public const string Version = "5.0.0";

        static ObfProfile Pf => ObfProfile.Current;
        static void Set<T>(ref T field, T value) where T : IEquatable<T>
        {
            if (field == null ? value == null : field.Equals(value)) return;
            field = value; Pf.Save();
        }

        public static bool renameNamespaces    { get => Pf.renameNamespaces;    set => Set(ref Pf.renameNamespaces, value); }
        public static bool renameClasses       { get => Pf.renameClasses;       set => Set(ref Pf.renameClasses, value); }
        public static bool renameEnumMembers   { get => Pf.renameEnumMembers;   set => Set(ref Pf.renameEnumMembers, value); }
        public static bool renameMethods       { get => Pf.renameMethods;       set => Set(ref Pf.renameMethods, value); }
        public static bool renameFields        { get => Pf.renameFields;        set => Set(ref Pf.renameFields, value); }
        public static bool remapResources      { get => Pf.remapResources;      set => Set(ref Pf.remapResources, value); }
        public static bool renameShaders       { get => Pf.renameShaders;       set => Set(ref Pf.renameShaders, value); }
        public static bool renameSceneObjects  { get => Pf.renameSceneObjects;  set => Set(ref Pf.renameSceneObjects, value); }
        public static bool renamePlayerPrefs   { get => Pf.renamePlayerPrefs;   set => Set(ref Pf.renamePlayerPrefs, value); }
        public static bool injectUnusedVars    { get => Pf.injectUnusedVars;    set => Set(ref Pf.injectUnusedVars, value); }
        public static bool stripComments       { get => Pf.stripComments;       set => Set(ref Pf.stripComments, value); }
        public static bool renameAssetFiles    { get => Pf.renameAssetFiles;    set => Set(ref Pf.renameAssetFiles, value); }
        public static bool brandPass           { get => Pf.brandPass;           set => Set(ref Pf.brandPass, value); }
        public static bool detectBrandTerms      { get => Pf.detectBrandTerms;      set => Set(ref Pf.detectBrandTerms, value); }
        public static bool sweepBrandIdentifiers { get => Pf.sweepBrandIdentifiers; set => Set(ref Pf.sweepBrandIdentifiers, value); }
        public static bool renameAssemblies      { get => Pf.renameAssemblies;      set => Set(ref Pf.renameAssemblies, value); }
        public static bool syncClassLiterals     { get => Pf.syncClassLiterals;     set => Set(ref Pf.syncClassLiterals, value); }
        public static bool scrubSpine            { get => Pf.scrubSpine;            set => Set(ref Pf.scrubSpine, value); }

        // ---- safety limits ----
        public static int perFileBudgetSec  { get => Pf.perFileBudgetSec;  set => Set(ref Pf.perFileBudgetSec, Mathf.Clamp(value, 2, 600)); }
        public static int regexTimeoutSec   { get => Pf.regexTimeoutSec;   set => Set(ref Pf.regexTimeoutSec, Mathf.Clamp(value, 1, 120)); }
        public static int totalBudgetMin    { get => Pf.totalBudgetMin;    set => Set(ref Pf.totalBudgetMin, Mathf.Clamp(value, 1, 1440)); }
        public static ObfGateMode buildGate  { get => (ObfGateMode)Pf.buildGate; set => Set(ref Pf.buildGate, (int)value); }

        // ---- brand / player settings ----
        public static string BrandPairsText { get => Pf.brandPairs;     set => Set(ref Pf.brandPairs, value ?? ""); }
        public static string BrandTermsText { get => Pf.brandTerms;     set => Set(ref Pf.brandTerms, value ?? ""); }
        public static string NewProductName { get => Pf.newProductName; set => Set(ref Pf.newProductName, value ?? ""); }
        public static string NewCompanyName { get => Pf.newCompanyName; set => Set(ref Pf.newCompanyName, value ?? ""); }
        public static string NewBundleId    { get => Pf.newBundleId;    set => Set(ref Pf.newBundleId, value ?? ""); }

        // ---- v4 custom rules ----
        public static string CustomReplacementsText { get => Pf.customReplacements;    set => Set(ref Pf.customReplacements, value ?? ""); }
        public static string ProtectedNamesText     { get => Pf.protectedNames;        set => Set(ref Pf.protectedNames, value ?? ""); }
        public static string KeepLiteralsText       { get => Pf.keepLiterals;          set => Set(ref Pf.keepLiterals, value ?? ""); }
        public static string VendorFoldersText      { get => Pf.vendorFolders;         set => Set(ref Pf.vendorFolders, value ?? ""); }
        public static string VendorNamespacesText   { get => Pf.vendorNamespaces;      set => Set(ref Pf.vendorNamespaces, value ?? ""); }
        public static string SdkClassesText         { get => Pf.sdkClasses;            set => Set(ref Pf.sdkClasses, value ?? ""); }
        public static string SdkAssetStemsText      { get => Pf.sdkAssetStems;         set => Set(ref Pf.sdkAssetStems, value ?? ""); }
        public static string ReconstructionMarkersText { get => Pf.reconstructionMarkers; set => Set(ref Pf.reconstructionMarkers, value ?? ""); }
        public static string NameNounsText          { get => Pf.nameNouns;             set => Set(ref Pf.nameNouns, value ?? ""); }
        public static string NameAdjectivesText     { get => Pf.nameAdjectives;        set => Set(ref Pf.nameAdjectives, value ?? ""); }
        public static string NameVerbsText          { get => Pf.nameVerbs;             set => Set(ref Pf.nameVerbs, value ?? ""); }
        public static string OutputRootText         { get => Pf.outputRoot;            set => Set(ref Pf.outputRoot, value ?? ""); }

        public const string BackupRootName = "CodeObfuscator_Backups";
        public const string ReportRootName = "CodeObfuscator_Reports";

        /// Absolute folder the backup / report folders are created in (profile `outputRoot`, default: project root).
        public static string OutputRoot(string projectRoot, string setting)
        {
            string s = (setting ?? "").Trim().Replace('\\', '/');
            if (s.Length == 0) return projectRoot;
            if (Path.IsPathRooted(s)) return s.TrimEnd('/');
            return (projectRoot + "/" + s).TrimEnd('/');
        }

        public static ObfSettings Snapshot() => Pf.ToSettings();

        // ---- the tool's own folder is never scanned or edited, wherever it was dropped ----
        static string s_ToolFolder;
        public static string ToolFolder
        {
            get
            {
                if (s_ToolFolder != null) return s_ToolFolder;
                s_ToolFolder = ComputeToolFolder();
                return s_ToolFolder;
            }
        }

        /// "Assets/…" when the tool lives under Assets, "" when it is installed as a package (Packages/ is never scanned anyway).
        static string ComputeToolFolder([CallerFilePath] string here = "")
        {
            string dp = Application.dataPath.Replace('\\', '/');
            try
            {
                string dir = Path.GetDirectoryName(here.Replace('\\', '/')).Replace('\\', '/');
                if (dir.StartsWith(dp + "/", StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                    return "Assets" + dir.Substring(dp.Length);
                if (Directory.Exists(dir)) return "";   // compiled from a package folder
            }
            catch { }
            // project moved since compile: locate ourselves by file name
            try
            {
                var hit = Directory.GetFiles(Application.dataPath, "ObfPipeline.cs", SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return "Assets" + Path.GetDirectoryName(hit).Replace('\\', '/').Substring(dp.Length);
            }
            catch { }
            return "";
        }

        // Manual excludes are a safety net; ObfSdkDetector finds the rest automatically.
        public static readonly string[] DefaultExcludes =
        {
            "Assets/Plugins", "Assets/StreamingAssets", "Assets/Gizmos", "Assets/ExternalDependencyManager", "Assets/TextMesh Pro"
        };

        public static string ExcludeText { get => Pf.excludes; set => Set(ref Pf.excludes, value ?? ""); }

        public static string[] Excludes =>
            ObfProfile.Lines(ExcludeText).Select(s => s.Replace('\\', '/').TrimEnd('/')).ToArray();

        // ---- auto-detected SDKs (per machine, Library/) ----
        public static SdkDetection Detection
        {
            get { if (_det == null) _det = ObfLocalState.Current.ToDetection(); return _det; }
        }
        static SdkDetection _det;
        static HashSet<string> _include;

        public static HashSet<string> IncludeAnyway
        {
            get { if (_include == null) _include = new HashSet<string>(ObfProfile.Lines(Pf.includeAnyway)); return _include; }
        }

        public static void SaveIncludeAnyway() { Pf.includeAnyway = ObfProfile.Join(IncludeAnyway); Pf.Save(); }

        /// Stores a detection result produced by the worker (main thread only).
        public static void StoreDetection(SdkDetection det)
        {
            _det = det;
            ObfLocalState.Current.StoreDetection(det);
        }

        /// Drop cached views after the profile was reloaded / imported.
        public static void InvalidateCaches() { _include = null; }

        /// Main-thread only. Worker threads use the flags baked into ObfProjectIndex.
        public static bool IsExcluded(string assetPath)
        {
            string tool = ToolFolder;
            if (tool.Length > 0 && (assetPath == tool || assetPath.StartsWith(tool + "/", StringComparison.Ordinal))) return true;
            if (assetPath.Contains("/CodeObfuscator/")) return true;
            if (IncludeAnyway.Contains(assetPath)) return false;
            foreach (var ex in Excludes)
                if (assetPath == ex || assetPath.StartsWith(ex + "/", StringComparison.Ordinal)) return true;
            var d = Detection;
            foreach (var f in d.folders)
                if (!IncludeAnyway.Contains(f.path) && (assetPath == f.path || assetPath.StartsWith(f.path + "/", StringComparison.Ordinal))) return true;
            if (d.files.Contains(assetPath)) return true;
            if (d.protectedAssets.Contains(assetPath)) return true;
            return false;
        }
    }

    /// Summary of the last Apply run. Written to ProjectSettings/CodeObfuscator.state.json so the
    /// pre-build gate (and a fresh editor session) can tell whether the project has been processed,
    /// and whether a run was interrupted (status still "Running" after a crash / kill).
    [Serializable]
    public class ObfRunSummary
    {
        public string stamp;
        public string status;        // Running | Completed | Cancelled | Failed | Restored
        public string startedAt;
        public string finishedAt;
        public string stage;         // last stage reached
        public int changes;
        public int skippedFiles;
        public int warnings;
        public int verifyIssues;
        public string reportDir;
        public string backupDir;
        public string error;
        /// v3.5: the brand terms the run scanned with (old names), so the build gate checks the same view instead of
        /// re-deriving terms from the CURRENT .asmdef names — which, after a run, are the tool's own generated names.
        public List<string> brandTerms = new List<string>();
        /// v3.5: every name the run generated (asmdef / namespace / folder) — never a leak.
        public List<string> generatedNames = new List<string>();
        /// v4: the profile the run used (name + file), for the report and for reproducing the run elsewhere.
        public string profileName;

        public static string Path => System.IO.Path.Combine(ObfProfile.ProjectRoot, "ProjectSettings", "CodeObfuscator.state.json");

        public void Save()
        {
            try { File.WriteAllText(Path, JsonUtility.ToJson(this, true)); }
            catch (Exception ex) { Debug.LogWarning("[CodeObfuscator] could not write state file: " + ex.Message); }
        }

        public static ObfRunSummary Load()
        {
            try { if (File.Exists(Path)) return JsonUtility.FromJson<ObfRunSummary>(File.ReadAllText(Path)); }
            catch { }
            return null;
        }
    }
}
