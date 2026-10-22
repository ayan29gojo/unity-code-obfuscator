// Code Obfuscator — the per-project profile.
//
// Everything that describes HOW a project is processed lives in ONE JSON file inside the project
// (ProjectSettings/CodeObfuscator.profile.json), not in EditorPrefs:
//   • it travels with the project (version control, another machine, another teammate),
//   • it can be exported and imported, so a profile tuned on one project is the starting point of the next,
//   • a headless run (ObfBatch) can be handed a profile file on the command line.
// Every list that used to be hard-coded (vendor folders, vendor namespaces, SDK settings assets,
// own-code markers, protected identifiers, literals to keep, name-generator word pools) is
// extensible from here. Multi-line fields hold one item per line; '#' starts a comment line.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CodeObfuscator
{
    public enum ObfPreset { Standard, Conservative, Aggressive }

    [Serializable]
    public class ObfProfile
    {
        public const int CurrentFormat = 1;

        public string name = "default";
        public int format = CurrentFormat;
        public string notes = "";

        // ---- categories ----
        public bool renameNamespaces = true, renameClasses = true, renameEnumMembers = false, renameMethods = false, renameFields = false;
        public bool remapResources = true, renameShaders = true, renameSceneObjects = true, renamePlayerPrefs = true;
        public bool injectUnusedVars = true, stripComments = true, renameAssetFiles = true, brandPass = true;
        public bool detectBrandTerms = true, sweepBrandIdentifiers = true, renameAssemblies = true, syncClassLiterals = true, scrubSpine = true;

        // ---- safety limits + build gate ----
        public int perFileBudgetSec = 20, regexTimeoutSec = 5, totalBudgetMin = 90;
        public int buildGate = (int)ObfGateMode.Block;

        // ---- brand ----
        /// "Old Name => New Name", one per line. Every casing variant is derived (see ObfBrand.Variants).
        public string brandPairs = "# one per line, e.g.\n# Old Game => New Game\n# OldStudio => NewStudio";
        /// Detect-only terms (no replacement): drive the identifier sweep, folder renames and verification.
        public string brandTerms = "# detect-only terms, one per line (no replacement needed)";
        public string newProductName = "", newCompanyName = "", newBundleId = "";

        // ---- custom rules (the reusable part) ----
        /// Exact, case-sensitive substring replacements applied by the brand stage to text files AND file / folder
        /// names (e.g. a save-key prefix "old_ => new_", a Resources sub-folder "OldConfigs/ => cfg/"). No variants.
        public string customReplacements = "# exact substring rules, one per line: old => new";
        /// Identifiers that must never be renamed in any category (JSON-bound fields, names an SDK calls via UnitySendMessage …).
        public string protectedNames = "# identifiers never renamed, one per line";
        /// String literals / asset stems that must never be rewritten inside code or YAML (lookup keys, analytics event names …).
        public string keepLiterals = "# literals never rewritten, one per line";
        /// Folders never scanned or edited (in addition to what SDK detection finds).
        public string excludes = "Assets/Plugins\nAssets/StreamingAssets\nAssets/Gizmos\nAssets/ExternalDependencyManager\nAssets/TextMesh Pro";
        /// Auto-detected SDK folders the user decided to process anyway.
        public string includeAnyway = "";

        // ---- SDK detection extensions ----
        public string vendorFolders = "# extra vendor folder names (leaf names), one per line";
        public string vendorNamespaces = "# extra vendor root namespaces, one per line";
        public string sdkClasses = "# extra global SDK class names, one per line";
        public string sdkAssetStems = "# extra SDK settings asset names (without extension), one per line";
        /// Regex alternatives that mark a file as the project's own game code — such files veto SDK detection for their folder.
        public string reconstructionMarkers = "# regex markers of your own game code (never treated as an SDK), one per line";

        // ---- name generator word pools (empty = built-in pools) ----
        public string nameNouns = "";
        public string nameAdjectives = "";
        public string nameVerbs = "";

        /// Where CodeObfuscator_Backups / _Reports are created: "" = project root, otherwise relative to it or absolute.
        public string outputRoot = "";

        // =========================================================================================
        public static string Path => System.IO.Path.Combine(ProjectRoot, "ProjectSettings", "CodeObfuscator.profile.json").Replace('\\', '/');
        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');

        static ObfProfile s_Current;
        public static event Action Changed;

        public static ObfProfile Current
        {
            get
            {
                if (s_Current != null) return s_Current;
                s_Current = LoadFile(Path);
                if (s_Current == null)
                {
                    s_Current = new ObfProfile();
                    s_Current.Save();
                }
                return s_Current;
            }
        }

        public static void Reload() { s_Current = null; Changed?.Invoke(); }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllText(Path, JsonUtility.ToJson(this, true));
            }
            catch (Exception ex) { Debug.LogWarning("[CodeObfuscator] could not save profile: " + ex.Message); }
        }

        public static ObfProfile LoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var p = JsonUtility.FromJson<ObfProfile>(File.ReadAllText(path));
                if (p == null) return null;
                if (p.format > CurrentFormat) Debug.LogWarning("[CodeObfuscator] profile " + path + " was written by a newer tool version (format " + p.format + ").");
                p.format = CurrentFormat;
                return p;
            }
            catch (Exception ex) { Debug.LogWarning("[CodeObfuscator] could not read profile " + path + ": " + ex.Message); return null; }
        }

        /// Writes a copy of the current profile somewhere else (to reuse it on another project).
        public static bool Export(string path)
        {
            try { File.WriteAllText(path, JsonUtility.ToJson(Current, true)); return true; }
            catch (Exception ex) { Debug.LogError("[CodeObfuscator] export failed: " + ex.Message); return false; }
        }

        /// Replaces the current profile with a file's content. Player-settings targets and includeAnyway are
        /// project-specific and are NOT taken over unless `takeProjectSpecific` is set.
        public static bool Import(string path, bool takeProjectSpecific)
        {
            var p = LoadFile(path);
            if (p == null) return false;
            if (!takeProjectSpecific)
            {
                var cur = Current;
                p.newProductName = cur.newProductName; p.newCompanyName = cur.newCompanyName; p.newBundleId = cur.newBundleId;
                p.includeAnyway = cur.includeAnyway;
            }
            s_Current = p;
            p.Save();
            Changed?.Invoke();
            return true;
        }

        public void ApplyPreset(ObfPreset preset)
        {
            var d = new ObfProfile();
            renameNamespaces = d.renameNamespaces; renameClasses = d.renameClasses; remapResources = d.remapResources;
            renameShaders = d.renameShaders; renameAssetFiles = d.renameAssetFiles; brandPass = d.brandPass;
            detectBrandTerms = d.detectBrandTerms; sweepBrandIdentifiers = d.sweepBrandIdentifiers; renameAssemblies = d.renameAssemblies;
            syncClassLiterals = d.syncClassLiterals; scrubSpine = d.scrubSpine;
            switch (preset)
            {
                case ObfPreset.Standard:
                    renameEnumMembers = false; renameMethods = false; renameFields = false;
                    renameSceneObjects = true; renamePlayerPrefs = true; injectUnusedVars = true; stripComments = true;
                    break;
                case ObfPreset.Conservative:
                    // names only where the compiler + GUID links keep everything in sync; nothing that a string lookup can miss
                    renameEnumMembers = false; renameMethods = false; renameFields = false;
                    renameSceneObjects = false; renamePlayerPrefs = false; injectUnusedVars = false; stripComments = true;
                    break;
                case ObfPreset.Aggressive:
                    renameEnumMembers = true; renameMethods = false; renameFields = true;
                    renameSceneObjects = true; renamePlayerPrefs = true; injectUnusedVars = true; stripComments = true;
                    break;
            }
            Save();
            Changed?.Invoke();
        }

        // ---- helpers for the multi-line list fields ----
        public static List<string> Lines(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                if (!list.Contains(l)) list.Add(l);
            }
            return list;
        }

        public static string Join(IEnumerable<string> lines) => string.Join("\n", lines);

        /// The profile as the immutable snapshot worker threads use.
        public ObfSettings ToSettings()
        {
            var s = new ObfSettings
            {
                renameNamespaces = renameNamespaces, renameClasses = renameClasses, renameEnumMembers = renameEnumMembers,
                renameMethods = renameMethods, renameFields = renameFields, remapResources = remapResources,
                renameShaders = renameShaders, renameSceneObjects = renameSceneObjects, renamePlayerPrefs = renamePlayerPrefs,
                injectUnusedVars = injectUnusedVars, stripComments = stripComments, renameAssetFiles = renameAssetFiles,
                brandPass = brandPass, brandPairsText = brandPairs, newProductName = newProductName,
                newCompanyName = newCompanyName, newBundleId = newBundleId,
                perFileBudgetSec = perFileBudgetSec, regexTimeoutSec = regexTimeoutSec, totalBudgetMin = totalBudgetMin,
                detectBrandTerms = detectBrandTerms, sweepBrandIdentifiers = sweepBrandIdentifiers, renameAssemblies = renameAssemblies,
                syncClassLiterals = syncClassLiterals, scrubSpine = scrubSpine, brandTermsText = brandTerms,
                customReplacements = Lines(customReplacements).ToArray(),
                protectedNames = Lines(protectedNames).ToArray(),
                keepLiterals = Lines(keepLiterals).ToArray(),
                vendorFolders = Lines(vendorFolders).ToArray(),
                vendorNamespaces = Lines(vendorNamespaces).ToArray(),
                sdkClasses = Lines(sdkClasses).ToArray(),
                sdkAssetStems = Lines(sdkAssetStems).ToArray(),
                reconstructionMarkers = Lines(reconstructionMarkers).ToArray(),
                nameNouns = Lines(nameNouns).ToArray(),
                nameAdjectives = Lines(nameAdjectives).ToArray(),
                nameVerbs = Lines(nameVerbs).ToArray(),
                outputRoot = outputRoot ?? "",
                oldProductName = SafePlayer(() => PlayerSettings.productName),
                oldCompanyName = SafePlayer(() => PlayerSettings.companyName),
#pragma warning disable 618
                oldBundleId = SafePlayer(() => PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android)),
#pragma warning restore 618
                cloudProjectName = SafePlayer(() => CloudProjectSettings.projectName)
            };
            return s;
        }

        static string SafePlayer(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    }
}
