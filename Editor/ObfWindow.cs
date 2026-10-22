// Code Obfuscator — Editor window.   Tools ▸ Code Obfuscator
//
// v4: every setting is a field of the per-project profile (ProjectSettings/CodeObfuscator.profile.json);
// the window is only a view of it. Profiles can be exported / imported so a tuned setup is reused on the
// next project, and the "Project rules" section holds the lists that used to be hard-coded.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CodeObfuscator
{
    public class ObfWindow : EditorWindow
    {
        ScanResult _res;
        Vector2 _scroll, _exclScroll;
        readonly Dictionary<ObfCategory, bool> _fold = new Dictionary<ObfCategory, bool>();
        string _filter = "";
        bool _showExcludes, _showSkipped, _showLimits = true;

        [MenuItem("Tools/Code Obfuscator/Open Window")]
        static void Open() => GetWindow<ObfWindow>("Code Obfuscator").minSize = new Vector2(820, 600);

        [MenuItem("Tools/Code Obfuscator/Open Profile File")]
        static void OpenProfile() { var _ = ObfProfile.Current; EditorUtility.RevealInFinder(ObfProfile.Path); }

        void OnEnable() { ObfProfile.Changed += OnProfileChanged; }
        void OnDisable() { ObfProfile.Changed -= OnProfileChanged; }
        void OnProfileChanged() { ObfConfig.InvalidateCaches(); _res = null; Repaint(); }

        ObfPipeline P => ObfPipeline.Current;
        bool Running => P != null && P.IsRunning;

        void Update()
        {
            if (Running || ObfBuildFlow.Waiting) Repaint();
            if (P != null && P.Stage == ObfStage.ReviewReady && _res != P.Result) { _res = P.Result; Repaint(); }
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Code Obfuscator  v" + ObfConfig.Version, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Pre-build obfuscation, so a decompiled or metadata-dumped build reveals as little as possible. Renames namespaces, classes, " +
                "members, assemblies, folders, assets, shaders, scene objects and PlayerPrefs keys directly in the project and keeps every " +
                "by-name reference in sync; strips comments; scrubs studio / product names (brand pass) from code, YAML, file / folder names " +
                "and Player Settings. SDK folders are detected and never touched. Runs as a staged pipeline (inventory → detect SDKs → analyze → " +
                "review → transform → rename → brand → verify → report) on a background worker with a per-file watchdog. Every change is " +
                "journalled immediately, so 'Restore last backup' brings the readable source back — even after a crash. " +
                "The pre-build gate refuses a player build until the project has been obfuscated.",
                MessageType.Info);

            DrawInterruptedWarning();
            using (new EditorGUI.DisabledScope(Running)) DrawProfileBar();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(Running))
                {
                    if (GUILayout.Button("Scan Project", GUILayout.Height(28))) DoScan();
                    using (new EditorGUI.DisabledScope(_res == null))
                    {
                        if (GUILayout.Button("Apply Selected", GUILayout.Height(28))) DoApply(false);
                        if (GUILayout.Button("Apply & Build…", GUILayout.Height(28), GUILayout.Width(120))) DoApply(true);
                    }
                }
                if (Running)
                {
                    var c = GUI.backgroundColor; GUI.backgroundColor = new Color(1f, .6f, .6f);
                    if (GUILayout.Button("Cancel", GUILayout.Height(28), GUILayout.Width(90))) P.Cancel();
                    GUI.backgroundColor = c;
                }
                using (new EditorGUI.DisabledScope(Running || string.IsNullOrEmpty(ObfPipeline.LastBackupDir)))
                    if (GUILayout.Button("Restore last backup", GUILayout.Height(28), GUILayout.Width(150))) DoRestore();
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(ObfPipeline.LastReportDir)))
                    if (GUILayout.Button("Open reports", GUILayout.Height(28), GUILayout.Width(110))) EditorUtility.RevealInFinder(ObfPipeline.LastReportDir);
            }

            DrawProgress();
            using (new EditorGUI.DisabledScope(Running)) DrawOptions();

            EditorGUILayout.Space();
            if (_res == null) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", GUILayout.Width(80))) SetAll(true);
                if (GUILayout.Button("Deselect All", GUILayout.Width(90))) SetAll(false);
                if (GUILayout.Button("Deselect High-risk", GUILayout.Width(120)))
                    foreach (var e in _res.entries) if (e.risk == ObfRisk.High) e.enabled = false;
                GUILayout.Space(12);
                GUILayout.Label("Filter", GUILayout.Width(36));
                _filter = EditorGUILayout.TextField(_filter);
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(_res.entries.Count(e => e.enabled && !e.reviewOnly) + " selected", GUILayout.Width(90));
            }

            if (_res.skipped.Count > 0)
            {
                _showSkipped = EditorGUILayout.Foldout(_showSkipped, _res.skipped.Count + " file(s) skipped during analysis (not renamed, see list)", true);
                if (_showSkipped) foreach (var s in _res.skipped.Take(50)) EditorGUILayout.LabelField("   " + s, EditorStyles.miniLabel);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (ObfCategory cat in Enum.GetValues(typeof(ObfCategory)))
            {
                var items = _res.entries.Where(e => e.category == cat && Pass(e)).ToList();
                if (items.Count == 0) continue;
                if (!_fold.ContainsKey(cat)) _fold[cat] = cat == ObfCategory.Class || cat == ObfCategory.Namespace;
                bool review = items[0].reviewOnly;
                bool gated = !CategoryOn(cat);

                using (new EditorGUILayout.HorizontalScope())
                {
                    _fold[cat] = EditorGUILayout.Foldout(_fold[cat],
                        cat + "  (" + items.Count + ")" + (review ? "  — review only" : gated ? "  — category OFF (toggle above)" : ""), true);
                    if (!review)
                    {
                        if (GUILayout.Button("all", GUILayout.Width(36))) items.ForEach(e => e.enabled = true);
                        if (GUILayout.Button("none", GUILayout.Width(44))) items.ForEach(e => e.enabled = false);
                    }
                }
                if (!_fold[cat]) continue;

                EditorGUI.indentLevel++;
                int shown = 0;
                foreach (var e in items)
                {
                    if (shown++ > 400) { EditorGUILayout.LabelField("      … " + (items.Count - 400) + " more (use the filter)", EditorStyles.miniLabel); break; }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(e.reviewOnly || gated))
                            e.enabled = EditorGUILayout.Toggle(e.enabled && !e.reviewOnly, GUILayout.Width(24));
                        var c = GUI.color;
                        GUI.color = e.risk == ObfRisk.High ? new Color(1f, .55f, .55f) : e.risk == ObfRisk.Medium ? new Color(1f, .85f, .4f) : new Color(.6f, 1f, .6f);
                        GUILayout.Label("●", GUILayout.Width(14));
                        GUI.color = c;
                        EditorGUILayout.LabelField(new GUIContent(Trunc(e.oldName, 42), e.primaryPath), GUILayout.Width(270));
                        GUILayout.Label("→", GUILayout.Width(18));
                        if (e.reviewOnly) EditorGUILayout.LabelField(e.note, EditorStyles.miniLabel);
                        else e.newName = EditorGUILayout.TextField(e.newName, GUILayout.Width(210));
                        GUILayout.Label(e.occurrences + "×", GUILayout.Width(40));
                    }
                    if (!e.reviewOnly && !string.IsNullOrEmpty(e.note))
                        EditorGUILayout.LabelField("      " + e.note, EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawInterruptedWarning()
        {
            if (Running) return;
            var sum = ObfRunSummary.Load();
            if (sum != null && sum.status == "Running")
                EditorGUILayout.HelpBox("The run " + sum.stamp + " was interrupted at stage " + sum.stage + " (editor closed or crashed). " +
                                        "Its journal is complete up to that point — use 'Restore last backup' to undo, then scan again.", MessageType.Warning);
        }

        void DrawProgress()
        {
            EditorGUILayout.Space();
            if (Running)
            {
                var p = P;
                string line = p.Stage + "   " + ObfPipeline.FormatTime(p.ElapsedSec)
                            + (p.Progress.Total > 0 ? "   " + p.Progress.Done + " / " + p.Progress.Total : "")
                            + (p.SkippedCount > 0 ? "   skipped: " + p.SkippedCount : "")
                            + (p.Changes > 0 ? "   changes: " + p.Changes : "");
                EditorGUILayout.LabelField(line, EditorStyles.boldLabel);
                var r = EditorGUILayout.GetControlRect(false, 18);
                float frac = p.Progress.Total > 0 ? p.Progress.Done / (float)p.Progress.Total : 0f;
                EditorGUI.ProgressBar(r, frac, string.IsNullOrEmpty(p.Progress.Current) ? p.Progress.Stage : p.Progress.Current);
                return;
            }
            string msg = P != null && !string.IsNullOrEmpty(P.Outcome) && P.Stage != ObfStage.ReviewReady ? P.Outcome : ObfPipeline.LastMessage;
            if (ObfBuildFlow.Waiting) msg += "\n\nWaiting for the recompile…";
            EditorGUILayout.LabelField(msg, EditorStyles.wordWrappedMiniLabel);
        }

        bool Pass(RenameEntry e) => string.IsNullOrEmpty(_filter)
            || e.oldName.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
            || (e.primaryPath ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;

        static bool CategoryOn(ObfCategory c)
        {
            switch (c)
            {
                case ObfCategory.Namespace: return ObfConfig.renameNamespaces;
                case ObfCategory.Class: return ObfConfig.renameClasses;
                case ObfCategory.EnumMember: return ObfConfig.renameEnumMembers;
                case ObfCategory.Method: return ObfConfig.renameMethods;
                case ObfCategory.Field: return ObfConfig.renameFields;
                case ObfCategory.ResourcePath: return ObfConfig.remapResources;
                case ObfCategory.Shader: return ObfConfig.renameShaders;
                case ObfCategory.SceneObject: return ObfConfig.renameSceneObjects;
                case ObfCategory.PlayerPrefsKey: return ObfConfig.renamePlayerPrefs;
                case ObfCategory.AssetFile: return ObfConfig.renameAssetFiles;
                case ObfCategory.BrandIdentifier: return ObfConfig.sweepBrandIdentifiers;
                case ObfCategory.AssemblyName: return ObfConfig.renameAssemblies;
                case ObfCategory.Folder: return ObfConfig.renameAssemblies;
                default: return true;
            }
        }

        void DrawOptions()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("What to change", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                ObfConfig.renameNamespaces   = GUILayout.Toggle(ObfConfig.renameNamespaces, "Namespaces", "Button");
                ObfConfig.renameClasses      = GUILayout.Toggle(ObfConfig.renameClasses, "Classes", "Button");
                ObfConfig.remapResources     = GUILayout.Toggle(ObfConfig.remapResources, "Resources", "Button");
                ObfConfig.renameShaders      = GUILayout.Toggle(ObfConfig.renameShaders, "Shaders", "Button");
                ObfConfig.renameSceneObjects = GUILayout.Toggle(ObfConfig.renameSceneObjects, "Scene Objects", "Button");
                ObfConfig.renamePlayerPrefs  = GUILayout.Toggle(ObfConfig.renamePlayerPrefs, "PlayerPrefs", "Button");
                ObfConfig.renameAssetFiles   = GUILayout.Toggle(ObfConfig.renameAssetFiles, "Asset files", "Button");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                ObfConfig.stripComments      = GUILayout.Toggle(ObfConfig.stripComments, "Strip comments", "Button");
                ObfConfig.injectUnusedVars   = GUILayout.Toggle(ObfConfig.injectUnusedVars, "Inject unused vars", "Button");
                ObfConfig.renameEnumMembers  = GUILayout.Toggle(ObfConfig.renameEnumMembers, "Enum members (medium)", "Button");
                ObfConfig.renameFields       = GUILayout.Toggle(ObfConfig.renameFields, "Fields (medium)", "Button");
                ObfConfig.renameMethods      = GUILayout.Toggle(ObfConfig.renameMethods, "Methods (risky)", "Button");
            }

            _showLimits = EditorGUILayout.Foldout(_showLimits, "Safety limits & build gate", true);
            if (_showLimits)
            {
                EditorGUI.indentLevel++;
                ObfConfig.perFileBudgetSec = EditorGUILayout.IntSlider(new GUIContent("Per-file budget (s)", "A file that takes longer is skipped and listed in skipped.txt; the run continues."), ObfConfig.perFileBudgetSec, 2, 300);
                ObfConfig.regexTimeoutSec  = EditorGUILayout.IntSlider(new GUIContent("Regex timeout (s)", "Every regex match aborts after this long; the file is skipped."), ObfConfig.regexTimeoutSec, 1, 60);
                ObfConfig.totalBudgetMin   = EditorGUILayout.IntSlider(new GUIContent("Total run budget (min)", "The whole Apply is cancelled (journal intact, restorable) after this long."), ObfConfig.totalBudgetMin, 1, 600);
                ObfConfig.buildGate = (ObfGateMode)EditorGUILayout.EnumPopup(new GUIContent("Pre-build gate", "Block: a player build fails until the project has been processed by a completed run (and no brand terms remain). Warn: logs only. Off: disabled."), ObfConfig.buildGate);
                EditorGUI.indentLevel--;
            }

            DrawBrand();
            DrawRules();
            DrawDetection();

            _showExcludes = EditorGUILayout.Foldout(_showExcludes, "Excluded folders (never scanned, never edited) — one per line", true);
            if (_showExcludes)
            {
                _exclScroll = EditorGUILayout.BeginScrollView(_exclScroll, GUILayout.Height(110));
                string t = EditorGUILayout.TextArea(ObfConfig.ExcludeText, GUILayout.ExpandHeight(true));
                if (t != ObfConfig.ExcludeText) ObfConfig.ExcludeText = t;
                EditorGUILayout.EndScrollView();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reset to defaults", GUILayout.Width(130))) ObfConfig.ExcludeText = string.Join("\n", ObfConfig.DefaultExcludes);
                    EditorGUILayout.LabelField("Add any other SDK / asset-store folder here before scanning. The tool's own folder (" + ObfConfig.ToolFolder + ") is always excluded.", EditorStyles.miniLabel);
                }
            }
        }

        // ==========================================================================================
        //  v4: profile bar + project rules
        // ==========================================================================================
        void DrawProfileBar()
        {
            var pf = ObfProfile.Current;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Profile", EditorStyles.boldLabel, GUILayout.Width(50));
                string nm = EditorGUILayout.TextField(pf.name, GUILayout.Width(160));
                if (nm != pf.name) { pf.name = nm; pf.Save(); }
                EditorGUILayout.LabelField(new GUIContent(Trunc(ObfProfile.Path.Replace(ObfProfile.ProjectRoot + "/", ""), 48), ObfProfile.Path), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Preset ▾", GUILayout.Width(70)))
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Standard — namespaces, classes, assets, brand (default)"), false, () => { pf.ApplyPreset(ObfPreset.Standard); });
                    menu.AddItem(new GUIContent("Conservative — compiler-safe renames only, no scene objects / PlayerPrefs"), false, () => { pf.ApplyPreset(ObfPreset.Conservative); });
                    menu.AddItem(new GUIContent("Aggressive — also enum members and fields"), false, () => { pf.ApplyPreset(ObfPreset.Aggressive); });
                    menu.ShowAsContext();
                }
                if (GUILayout.Button("Export…", GUILayout.Width(66)))
                {
                    string p = EditorUtility.SaveFilePanel("Export profile", "", (pf.name ?? "profile") + ".obfprofile.json", "json");
                    if (!string.IsNullOrEmpty(p) && ObfProfile.Export(p)) ObfPipeline.LastMessage = "Profile exported to " + p;
                }
                if (GUILayout.Button("Import…", GUILayout.Width(66)))
                {
                    string p = EditorUtility.OpenFilePanel("Import profile", "", "json");
                    if (!string.IsNullOrEmpty(p))
                    {
                        // DisplayDialog returns true for the FIRST button ("Rules only")
                        bool rulesOnly = EditorUtility.DisplayDialog("Import profile",
                            "Take over the project-specific values too (new product / company / bundle id, included SDK folders)?\n\n" +
                            "Choose 'Rules only' when the profile comes from another project.", "Rules only", "Everything");
                        if (ObfProfile.Import(p, takeProjectSpecific: !rulesOnly)) ObfPipeline.LastMessage = "Profile imported from " + p;
                    }
                }
                if (GUILayout.Button("Reload", GUILayout.Width(58))) ObfProfile.Reload();
            }
        }

        bool _showRules;
        Vector2 _rulesScroll;

        static string Area(string label, string tip, string value, float h = 56)
        {
            EditorGUILayout.LabelField(new GUIContent(label, tip), EditorStyles.miniBoldLabel);
            return EditorGUILayout.TextArea(value, GUILayout.MinHeight(h));
        }

        void DrawRules()
        {
            EditorGUILayout.Space();
            var s = ObfConfig.Snapshot();
            int n = s.customReplacements.Length + s.protectedNames.Length + s.keepLiterals.Length + s.vendorFolders.Length
                  + s.vendorNamespaces.Length + s.sdkClasses.Length + s.sdkAssetStems.Length + s.reconstructionMarkers.Length;
            _showRules = EditorGUILayout.Foldout(_showRules, "Project rules — " + n + " custom entries (exact replacements, protected names, kept literals, SDK detection extras, name pools)", true);
            if (!_showRules) return;

            _rulesScroll = EditorGUILayout.BeginScrollView(_rulesScroll, GUILayout.Height(330));
            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox("These lists travel with the profile. One entry per line, '#' starts a comment. Empty = built-in behaviour.", MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Exact replacements  (old => new)", "Case-sensitive substring rules applied by the brand stage to every text file and to file / folder names. Use for save-key prefixes, Resources sub-folders, analytics event prefixes. No casing variants are derived; 'to' may be empty.", ObfConfig.CustomReplacementsText);
                    if (t != ObfConfig.CustomReplacementsText) ObfConfig.CustomReplacementsText = t;
                    var probs = new List<string>();
                    var rules = ObfBrand.ParseCustom(ObfProfile.Lines(t).ToArray(), probs);
                    if (probs.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", probs), MessageType.Warning);
                    else if (rules.Count > 0) EditorGUILayout.LabelField(rules.Count + " rule(s)", EditorStyles.miniLabel);
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Protected identifiers", "Never renamed in any category: fields read from JSON the heuristics miss, GameObject / method names a native plugin calls through UnitySendMessage, reflection targets.", ObfConfig.ProtectedNamesText);
                    if (t != ObfConfig.ProtectedNamesText) ObfConfig.ProtectedNamesText = t;
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Kept literals", "String literals / asset stems never rewritten inside code or YAML: lookup keys, analytics event names, atlas page names, remote-config keys.", ObfConfig.KeepLiteralsText);
                    if (t != ObfConfig.KeepLiteralsText) ObfConfig.KeepLiteralsText = t;
                }
            }
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("SDK detection extras", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Vendor folder names", "Leaf folder names that are always third-party (in addition to the built-in list of ~200).", ObfConfig.VendorFoldersText, 44);
                    if (t != ObfConfig.VendorFoldersText) ObfConfig.VendorFoldersText = t;
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Vendor root namespaces", "Root namespaces whose scripts are third-party (e.g. MyPublisherSdk).", ObfConfig.VendorNamespacesText, 44);
                    if (t != ObfConfig.VendorNamespacesText) ObfConfig.VendorNamespacesText = t;
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Global SDK classes", "Class names in the global namespace that belong to an SDK (e.g. MySdkManager).", ObfConfig.SdkClassesText, 44);
                    if (t != ObfConfig.SdkClassesText) ObfConfig.SdkClassesText = t;
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("SDK settings assets", "Asset names (no extension) an SDK loads by name — protected from renaming wherever they are.", ObfConfig.SdkAssetStemsText, 44);
                    if (t != ObfConfig.SdkAssetStemsText) ObfConfig.SdkAssetStemsText = t;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Own-code markers (regex)", "Regex alternatives that mark a script as YOUR game code, so its folder is never mistaken for an SDK — use when your scripts carry license headers or a vendor-style root namespace. None built in.", ObfConfig.ReconstructionMarkersText, 44);
                    if (t != ObfConfig.ReconstructionMarkersText) ObfConfig.ReconstructionMarkersText = t;
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Output root", "Where CodeObfuscator_Backups / _Reports are created. Empty = project root; otherwise relative to it or absolute.", ObfConfig.OutputRootText, 22);
                    if (t != ObfConfig.OutputRootText) ObfConfig.OutputRootText = t.Replace("\n", "").Replace("\r", "");
                }
            }
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Name generator word pools (≥ 4 words each, else built-in)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Nouns", "Core, Handler, Service … — combined into class / namespace / field names.", ObfConfig.NameNounsText, 44);
                    if (t != ObfConfig.NameNounsText) ObfConfig.NameNounsText = t;
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Adjectives", "Prime, Quiet, Rapid … — the first half of class / namespace names.", ObfConfig.NameAdjectivesText, 44);
                    if (t != ObfConfig.NameAdjectivesText) ObfConfig.NameAdjectivesText = t;
                }
                using (new EditorGUILayout.VerticalScope())
                {
                    string t = Area("Verbs", "Resolve, Build, Fetch … — the first half of method names.", ObfConfig.NameVerbsText, 44);
                    if (t != ObfConfig.NameVerbsText) ObfConfig.NameVerbsText = t;
                }
            }
            EditorGUI.indentLevel--;
            EditorGUILayout.EndScrollView();
        }

        bool _showSdk = true;
        Vector2 _sdkScroll;

        void DrawDetection()
        {
            EditorGUILayout.Space();
            var d = ObfConfig.Detection;
            int excludedFolders = d.folders.Count(f => !ObfConfig.IncludeAnyway.Contains(f.path));
            using (new EditorGUILayout.HorizontalScope())
            {
                _showSdk = EditorGUILayout.Foldout(_showSdk, "Auto-detected SDK / third-party — " + excludedFolders + " folders, " + d.files.Count + " loose files, " + d.protectedAssets.Count + " protected assets (never touched)", true);
                EditorGUILayout.LabelField("(re-detected on every Scan)", EditorStyles.miniLabel, GUILayout.Width(160));
            }
            if (!_showSdk) return;
            if (d.folders.Count == 0 && d.files.Count == 0)
            {
                EditorGUILayout.HelpBox("Nothing detected yet — detection runs automatically on Scan.", MessageType.None);
                return;
            }
            _sdkScroll = EditorGUILayout.BeginScrollView(_sdkScroll, GUILayout.Height(150));
            foreach (var f in d.folders)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool excluded = !ObfConfig.IncludeAnyway.Contains(f.path);
                    bool now = EditorGUILayout.ToggleLeft(new GUIContent(f.path, f.Reason), excluded, GUILayout.Width(360));
                    if (now != excluded)
                    {
                        if (now) ObfConfig.IncludeAnyway.Remove(f.path); else ObfConfig.IncludeAnyway.Add(f.path);
                        ObfConfig.SaveIncludeAnyway(); _res = null;
                    }
                    EditorGUILayout.LabelField(f.score + "  " + f.Reason, EditorStyles.miniLabel);
                }
            }
            if (d.files.Count > 0)
            {
                EditorGUILayout.LabelField("Loose SDK files (license header / vendor namespace / known SDK class):", EditorStyles.miniBoldLabel);
                foreach (var f in d.files) EditorGUILayout.LabelField("   " + f, EditorStyles.miniLabel);
            }
            if (d.protectedAssets.Count > 0)
            {
                EditorGUILayout.LabelField("Protected assets (loaded by name from SDK code):", EditorStyles.miniBoldLabel);
                foreach (var f in d.protectedAssets) EditorGUILayout.LabelField("   " + f, EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("Untick a folder to include it in the rename anyway (only if you are sure it is your own code). Re-scan afterwards.", EditorStyles.miniLabel);
        }

        bool _showBrand = true;
        Vector2 _brandScroll;

        void DrawBrand()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                ObfConfig.brandPass = GUILayout.Toggle(ObfConfig.brandPass, "Brand pass", "Button", GUILayout.Width(90));
                _showBrand = EditorGUILayout.Foldout(_showBrand, "Brand terms (studio / game names) — old => new, one per line. Every casing variant is derived and applied to code, strings, YAML, file + folder names and Player Settings.", true);
            }
            if (!_showBrand) return;
            using (new EditorGUI.DisabledScope(!ObfConfig.brandPass))
            {
                _brandScroll = EditorGUILayout.BeginScrollView(_brandScroll, GUILayout.Height(90));
                string t = EditorGUILayout.TextArea(ObfConfig.BrandPairsText, GUILayout.ExpandHeight(true));
                if (t != ObfConfig.BrandPairsText) ObfConfig.BrandPairsText = t;
                EditorGUILayout.EndScrollView();

                var problems = new List<string>();
                var pairs = ObfBrand.ParsePairs(ObfConfig.BrandPairsText, problems);
                var variants = ObfBrand.Variants(pairs);
                if (problems.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", problems), MessageType.Warning);
                if (variants.Count > 0)
                    EditorGUILayout.LabelField(pairs.Count + " term(s) → " + variants.Count + " variants, e.g. " +
                        string.Join("   ", variants.Take(6).Select(v => v.from + "→" + v.to)), EditorStyles.miniLabel);
                else if (ObfConfig.brandPass)
                    EditorGUILayout.HelpBox("No active brand terms (lines starting with # are comments). The brand pass will only touch Player Settings below.", MessageType.None);

                EditorGUILayout.LabelField("Player Settings (blank = derive from brand terms)", EditorStyles.boldLabel);
                ObfConfig.NewProductName = EditorGUILayout.TextField(new GUIContent("Product name", "current: " + PlayerSettings.productName), ObfConfig.NewProductName);
                ObfConfig.NewCompanyName = EditorGUILayout.TextField(new GUIContent("Company name", "current: " + PlayerSettings.companyName), ObfConfig.NewCompanyName);
#pragma warning disable 618
                ObfConfig.NewBundleId    = EditorGUILayout.TextField(new GUIContent("Bundle ID", "current: " + PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android)), ObfConfig.NewBundleId);
#pragma warning restore 618
            }

            // ---- v3.4: leaks a build comparison still found after a full run ----
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Leak sweep (v3.4)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                ObfConfig.detectBrandTerms = GUILayout.Toggle(ObfConfig.detectBrandTerms, new GUIContent("Auto-detect terms", "Derive brand terms from the current product / company / cloud project name, bundle id and assembly names. Old product => new product and old company => new company become brand pairs automatically."), "Button");
                ObfConfig.sweepBrandIdentifiers = GUILayout.Toggle(ObfConfig.sweepBrandIdentifiers, new GUIContent("Identifier sweep", "Rename EVERY code identifier that carries a brand term — properties, events, override methods, parameters — even when its category is off. Protected / data-bound ones are listed for review."), "Button");
                ObfConfig.renameAssemblies = GUILayout.Toggle(ObfConfig.renameAssemblies, new GUIContent("Assemblies + folders", ".asmdef names (they ship as Foo.dll and in every assembly-qualified type name) and folders named after namespaces / assemblies / brand terms (Unity 6 IL2CPP embeds each script's project path)."), "Button");
                ObfConfig.syncClassLiterals = GUILayout.Toggle(ObfConfig.syncClassLiterals, new GUIContent("Class literals", "Rewrite renamed class names inside string literals (\"[GameManager] …\" log tags). Literals that a safety rule protected are left alone."), "Button");
                ObfConfig.scrubSpine = GUILayout.Toggle(ObfConfig.scrubSpine, new GUIContent("Spine paths", "Blank the artist's machine paths Spine exports into .skel / .json skeletons (\"C:/Users/…\")."), "Button");
            }
            _showTerms = EditorGUILayout.Foldout(_showTerms, "Detect-only brand terms (no replacement) — one per line", true);
            if (_showTerms)
            {
                string tt = EditorGUILayout.TextArea(ObfConfig.BrandTermsText, GUILayout.Height(48));
                if (tt != ObfConfig.BrandTermsText) ObfConfig.BrandTermsText = tt;
            }
            var detected = ObfBuildGate.CurrentTerms(ObfRunSummary.Load());   // v3.5: same view as the build gate (never the run's own generated names)
            if (detected.Count > 0)
                EditorGUILayout.HelpBox("Brand terms in effect (" + detected.Count + "): " + string.Join(", ", detected.Take(14)) + (detected.Count > 14 ? ", …" : "")
                    + (_res != null && _res.brandTerms.Count > 0 ? "\nLast scan used: " + string.Join(", ", _res.brandTerms.Take(14)) : ""), MessageType.None);
            else
                EditorGUILayout.HelpBox("No brand terms known. Fill brand pairs, detect-only terms, or set a new Product / Company name so the old ones can be derived.", MessageType.Warning);
        }

        bool _showTerms;

        static List<string> AsmdefNames()
        {
            var names = new List<string>();
            try
            {
                string dp = Application.dataPath.Replace('\\', '/');
                foreach (var abs in System.IO.Directory.GetFiles(Application.dataPath, "*.asmdef", System.IO.SearchOption.AllDirectories))
                {
                    string ap = "Assets" + abs.Replace('\\', '/').Substring(dp.Length);
                    if (ObfConfig.IsExcluded(ap)) continue;   // third-party assemblies are not brand terms
                    var m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(abs), "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    names.Add(m.Success ? m.Groups[1].Value : System.IO.Path.GetFileNameWithoutExtension(abs));
                }
            }
            catch { }
            return names;
        }

        void DoScan()
        {
            _res = null;
            ObfPipeline.StartScan();
        }

        void DoApply(bool build)
        {
            var prev = ObfRunSummary.Load();
            if (prev != null && prev.status == "Completed")
            {
                if (!EditorUtility.DisplayDialog("Project already processed",
                    "Run " + prev.stamp + " already renamed this project and was not restored.\n\n" +
                    "Applying again renames everything a second time: generated names collide with files from the first run, " +
                    "file renames fail while code strings are rewritten, and assets loaded by name go missing at runtime.\n\n" +
                    "Recommended: Cancel, click 'Restore last backup' (or git-reset), then Scan + Apply once.",
                    "Cancel", "Apply anyway")) return;
            }
            if (!EditorUtility.DisplayDialog("Apply changes?",
                "Rewrites scripts, scenes, prefabs, shaders, .anim, asmdef and renames asset files in place — BEFORE any build.\n\n" +
                "Every change is journalled to a backup as it is made. COMMIT TO VERSION CONTROL before continuing.\n\n" +
                (build ? "After the recompile succeeds a player build starts automatically.\n\n" : "") + "Continue?",
                "Apply", "Cancel")) return;
            if (build && !ObfBuildFlow.AskBuildPath()) return;
            var res = _res;
            _res = null;
            ObfPipeline.StartApply(res, build);
        }

        void DoRestore()
        {
            if (!EditorUtility.DisplayDialog("Restore last backup?",
                "Undo the renames and restore every file from:\n" + ObfPipeline.LastBackupDir, "Restore", "Cancel")) return;
            ObfPipeline.LastMessage = ObfJournal.Restore(ObfPipeline.LastBackupDir);
            _res = null;
        }

        void SetAll(bool v) { foreach (var e in _res.entries) if (!e.reviewOnly) e.enabled = v; }
        static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
