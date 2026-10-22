// Code Obfuscator — the staged, pre-build project-processing pipeline.
//
// Replaces the v2 Engine.Apply() monolith (one synchronous button handler that could not be
// cancelled, reported progress only through a modal bar, and wrote its restore manifest only at
// the very end).
//
//   Scan  :  Inventory → Detect SDKs → Analyze                        → ReviewReady (user edits the list)
//   Apply :  Preflight → TransformText → RefreshDb → RenameAssets → BrandText → BrandRename
//            → PlayerSettings → ReopenScenes → Verify → Report        → Done  (then compile → optional build)
//
// Execution model
//   • The pipeline is an editor coroutine advanced once per EditorApplication.update tick, so the
//     editor stays responsive and the progress bar's Cancel button works between items.
//   • Pure text work (detect, scan, per-file transforms, verify) runs on a background thread
//     (ObfWorkLoop / ObfJob). Every file write and every AssetDatabase call happens on the main thread.
//   • Watchdog: a file that exceeds the per-file budget is skipped and reported, the worker thread is
//     abandoned and a fresh one continues with the next file. Regexes carry a match timeout as well.
//   • A total run budget turns a runaway run into a clean "Cancelled" with a restorable journal.
//   • Assembly reload and auto-refresh are locked for the whole Apply so a mid-run recompile cannot
//     wipe the pipeline state; the single recompile happens once everything (renames included) is done.
//   • Every change is journalled immediately (ObfJournal), so "Restore last backup" works even after a
//     crash or a killed editor.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace CodeObfuscator
{
    public enum ObfStage
    {
        Idle,
        Inventory, Detect, Scan, ReviewReady,
        Preflight, TransformText, RefreshDb, RenameAssets, RenameFolders, BrandText, BrandRename, ScrubBinaries, PlayerSettings, ReopenScenes, Verify, Report,
        Done, Cancelled, Failed
    }

    public class ObfPipeline
    {
        public static ObfPipeline Current { get; private set; }
        // v4: per project + per machine (Library/), never a global EditorPrefs key shared between projects
        public static string LastMessage { get => ObfLocalState.Current.lastMessage; set { var st = ObfLocalState.Current; st.lastMessage = value ?? ""; st.Save(); } }
        public static string LastBackupDir { get => OrLastRun(ObfLocalState.Current.lastBackupDir, s => s.backupDir); private set { var st = ObfLocalState.Current; st.lastBackupDir = value ?? ""; st.Save(); } }
        public static string LastReportDir { get => OrLastRun(ObfLocalState.Current.lastReportDir, s => s.reportDir); private set { var st = ObfLocalState.Current; st.lastReportDir = value ?? ""; st.Save(); } }

        /// Library/ is per machine: on a fresh checkout (or after Library was deleted) fall back to the folders the
        /// committed run summary recorded — when they exist on this machine and the run has not been restored yet.
        static string OrLastRun(string local, Func<ObfRunSummary, string> pick)
        {
            if (!string.IsNullOrEmpty(local)) return local;
            var sum = ObfRunSummary.Load();
            string dir = sum != null && sum.status != "Restored" ? pick(sum) : null;
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : "";
        }

        public ObfStage Stage { get; private set; } = ObfStage.Idle;
        public readonly ObfProgress Progress = new ObfProgress();
        public ScanResult Result { get; private set; }
        public string Outcome { get; private set; } = "";
        public int SkippedCount => _journal != null ? _journal.skipped.Count : (Result != null ? Result.skipped.Count : 0);
        public int Changes => _journal != null ? _journal.changes : 0;
        public double ElapsedSec => _sw.Elapsed.TotalSeconds;
        public bool IsRunning => Stage != ObfStage.Idle && Stage != ObfStage.ReviewReady && Stage != ObfStage.Done && Stage != ObfStage.Cancelled && Stage != ObfStage.Failed;
        public bool BuildAfter { get; private set; }

        readonly Stopwatch _sw = new Stopwatch();
        ObfSettings _s;
        ObfProjectIndex _idx;
        ObfJournal _journal;
        ObfRunSummary _summary;
        IEnumerator _routine;
        bool _cancelRequested; string _cancelReason;
        bool _locked;
        int _failedRenames;
        List<string> _openScenes = new List<string>();

        // ==========================================================================================
        //  entry points
        // ==========================================================================================
        public static ObfPipeline StartScan()
        {
            if (Current != null && Current.IsRunning) return Current;
            var p = new ObfPipeline();
            p._s = ObfConfig.Snapshot();
            ObfRegex.Timeout = TimeSpan.FromSeconds(p._s.regexTimeoutSec);
            p._routine = p.ScanRoutine();
            p.Begin(ObfStage.Inventory);
            return p;
        }

        public static ObfPipeline StartApply(ScanResult reviewed, bool buildAfter)
        {
            if (Current != null && Current.IsRunning) return Current;
            var p = new ObfPipeline { Result = reviewed, BuildAfter = buildAfter };
            p._s = ObfConfig.Snapshot();
            ObfRegex.Timeout = TimeSpan.FromSeconds(p._s.regexTimeoutSec);
            p._routine = p.ApplyRoutine();
            p.Begin(ObfStage.Preflight);
            return p;
        }

        void Begin(ObfStage first)
        {
            Current = this;
            Stage = first;                       // must be a running stage before the first Tick, or Tick unsubscribes itself
            Progress.Set(first.ToString(), "", 0, 0);
            _sw.Start();
            EditorApplication.update += Tick;
        }

        public void Cancel(string reason = "cancelled by user")
        {
            if (!IsRunning || _cancelRequested) return;
            _cancelRequested = true; _cancelReason = reason;
        }

        /// v4: advance the pipeline by one step from outside the editor loop (headless / batch mode, where
        /// EditorApplication.update is not pumped while -executeMethod runs). Returns false when the run is over.
        public bool Pump()
        {
            if (!IsRunning) return false;
            Tick();
            return IsRunning;
        }

        // ==========================================================================================
        //  tick
        // ==========================================================================================
        void Tick()
        {
            if (!IsRunning) { EditorApplication.update -= Tick; return; }
            try
            {
                if (_sw.Elapsed.TotalMinutes > _s.totalBudgetMin && !_cancelRequested)
                    Cancel("total run budget of " + _s.totalBudgetMin + " min exceeded");

                string info = Progress.Current;
                float frac = Progress.Total > 0 ? Mathf.Clamp01(Progress.Done / (float)Progress.Total) : 0f;
                if (EditorUtility.DisplayCancelableProgressBar("Code Obfuscator — " + Stage + "  (" + FormatTime(ElapsedSec) + ")", Truncate(info, 90), frac))
                    Cancel();

                if (!_routine.MoveNext()) { /* routine sets the final stage itself */ }
            }
            catch (OperationCanceledException)
            {
                FinishRun(ObfStage.Cancelled, "Cancelled — " + _cancelReason);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                FinishRun(ObfStage.Failed, "ERROR in stage " + Stage + ": " + ex.Message);
            }
            if (!IsRunning)
            {
                EditorApplication.update -= Tick;
                EditorUtility.ClearProgressBar();
            }
        }

        void CheckCancel()
        {
            if (_cancelRequested) throw new OperationCanceledException();
        }

        void SetStage(ObfStage st, string what = "")
        {
            Stage = st;
            Progress.Set(st.ToString(), what, 0, 0);
            Debug.Log("[CodeObfuscator] stage " + st + (what.Length > 0 ? " — " + what : "") + "  (" + FormatTime(ElapsedSec) + ")");
            if (_journal != null) _journal.Note("stage " + st + " @ " + FormatTime(ElapsedSec));
            if (_summary != null) { _summary.stage = st.ToString(); _summary.Save(); }
        }

        // ==========================================================================================
        //  SCAN routine
        // ==========================================================================================
        IEnumerator ScanRoutine()
        {
            SetStage(ObfStage.Inventory, "walking Assets/");
            yield return null;
            _idx = ObfProjectIndex.Build(ObfConfig.IsExcluded);
            CheckCancel();

            SetStage(ObfStage.Detect, "scoring folders");
            var detJob = new ObfJob<SdkDetection>(ct => ObfSdkDetector.Detect(_idx, _s, ct, Progress));
            detJob.Start();
            while (!detJob.Done)
            {
                if (_cancelRequested) { detJob.Cancel(); CheckCancel(); }
                if (detJob.ElapsedSec > Math.Max(60, _s.perFileBudgetSec * 30)) { detJob.Cancel(); throw new TimeoutException("SDK detection exceeded its budget"); }
                yield return null;
            }
            if (detJob.Error != null) throw detJob.Error;
            ObfConfig.StoreDetection(detJob.Result);
            _idx.ApplyExclusions(ObfConfig.IsExcluded);

            SetStage(ObfStage.Scan, "reading scripts");
            var scanJob = new ObfJob<ScanResult>(ct => ObfScanner.Scan(_idx, _s, Progress, ct));
            scanJob.Start();
            while (!scanJob.Done)
            {
                if (_cancelRequested) { scanJob.Cancel(); CheckCancel(); }
                yield return null;
            }
            if (scanJob.Error != null) throw scanJob.Error;
            Result = scanJob.Result;

            var d = ObfConfig.Detection;
            Outcome = "Found " + Result.entries.Count(e => !e.reviewOnly) + " renameable items, "
                    + Result.entries.Count(e => e.reviewOnly) + " for manual review. Scripts in scope: " + Result.csFiles.Count
                    + ". Auto-excluded: " + d.folders.Count + " SDK folders, " + d.files.Count + " loose SDK files, " + d.protectedAssets.Count
                    + " protected assets. Scan took " + FormatTime(ElapsedSec) + "."
                    + (Result.skipped.Count > 0 ? " " + Result.skipped.Count + " file(s) skipped during analysis (see window)." : "")
                    + (Result.notes.Count > 0 ? " " + string.Join(" ", Result.notes) : "")
                    + " Review, adjust, then Apply.";
            LastMessage = Outcome;
            Stage = ObfStage.ReviewReady;
        }

        // ==========================================================================================
        //  APPLY routine
        // ==========================================================================================
        class TextItem { public string path; public string kind; }
        class TextResult { public string orig; public string text; public List<string> warnings = new List<string>(); }

        IEnumerator ApplyRoutine()
        {
            // ---- preflight (main thread, all-or-nothing before anything is touched) ----
            SetStage(ObfStage.Preflight, "checking editor state");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode first.");
            if (EditorApplication.isCompiling) throw new InvalidOperationException("Wait for script compilation to finish first.");
            if (StageUtility.GetCurrentStageHandle() != StageUtility.GetMainStageHandle())
                throw new InvalidOperationException("Exit Prefab mode first (scene/prefab YAML is rewritten on disk).");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Cancel("open scenes must be saved before applying");
                CheckCancel();
            }
            _openScenes.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!string.IsNullOrEmpty(sc.path)) _openScenes.Add(sc.path);
            }
            // Close the open scenes before their YAML is rewritten on disk: otherwise the next
            // AssetDatabase.Refresh pops the modal "scene(s) have been modified externally" dialog,
            // which stops EditorApplication.update and therefore the whole pipeline until a human
            // clicks it. They are reopened (at their post-rename paths) in the ReopenScenes stage.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return null;

            _idx = ObfProjectIndex.Build(ObfConfig.IsExcluded);
            var warnings = new List<string>();
            int bumped = ObfScanner.ResolveNameCollisions(Result, _idx, _s, warnings);
            if (bumped > 0) Debug.LogWarning("[CodeObfuscator] " + bumped + " rename target(s) adjusted to avoid existing files (see warnings.txt)");
            var maps = ObfApplyMaps.Build(Result, _s, warnings, _idx);

            _journal = new ObfJournal(_idx.projectRoot, _s.outputRoot);
            _journal.warnings.AddRange(warnings);
            LastBackupDir = _journal.backupDir; LastReportDir = _journal.reportDir;
            _summary = new ObfRunSummary
            {
                stamp = _journal.stamp, status = "Running", startedAt = DateTime.Now.ToString("s"),
                reportDir = _journal.reportDir, backupDir = _journal.backupDir, profileName = ObfProfile.Current.name
            };
            _summary.Save();
            // v4: the run is reproducible elsewhere — the profile it used is part of the report
            try { File.Copy(ObfProfile.Path, _journal.reportDir + "/profile.json", true); } catch { }

            EditorApplication.LockReloadAssemblies();
            AssetDatabase.DisallowAutoRefresh();
            _locked = true;

            // ---- 1. per-file text transforms (worker + watchdog) ----
            var items = new List<TextItem>();
            foreach (var p in Result.csFiles) items.Add(new TextItem { path = p, kind = "cs" });
            foreach (var p in Result.shaderFiles) items.Add(new TextItem { path = p, kind = "shader" });
            foreach (var p in Result.sceneFiles.Concat(Result.prefabFiles).Concat(Result.assetFiles)) items.Add(new TextItem { path = p, kind = "yaml" });
            foreach (var p in Result.animFiles) items.Add(new TextItem { path = p, kind = "anim" });
            foreach (var p in Result.asmdefFiles) items.Add(new TextItem { path = p, kind = "asmdef" });
            // v3.4: link.xml preserve lists name assemblies by their old name
            foreach (var f in _idx.Included) if (f.path.EndsWith("/link.xml", StringComparison.OrdinalIgnoreCase)) items.Add(new TextItem { path = f.path, kind = "linkxml" });
            // drop paths that vanished / are excluded now (detection may have changed since the scan)
            items = items.Where(it => _idx.byPath.TryGetValue(it.path, out var f) && !f.excluded).ToList();

            SetStage(ObfStage.TransformText, items.Count + " files");
            var idx = _idx;
            var loop = new ObfWorkLoop<TextItem, TextResult>(items, (it, ct) =>
            {
                var r = new TextResult { orig = idx.Read(it.path) };
                switch (it.kind)
                {
                    case "cs":     r.text = ObfTextTransforms.TransformCs(r.orig, maps, r.warnings); break;
                    case "shader": r.text = ObfTextTransforms.TransformShader(r.orig, maps); break;
                    case "yaml":   r.text = ObfTextTransforms.TransformYaml(r.orig, maps, r.warnings, it.path); break;
                    case "anim":   r.text = ObfTextTransforms.TransformAnim(r.orig, maps); break;
                    case "asmdef": r.text = ObfTextTransforms.TransformAsmdef(r.orig, maps); break;
                    case "linkxml": r.text = ObfTextTransforms.TransformLinkXml(r.orig, maps); break;
                }
                return r;
            });
            foreach (var _ in RunLoop(loop, items, (it, r) =>
            {
                if (r.text != null) _journal.BackupAndWrite(it.path, r.orig, r.text, it.kind.ToUpperInvariant());
                _journal.warnings.AddRange(r.warnings);
            }, it => it.path)) yield return null;

            // ---- 2. let Unity see the new text (assembly reload stays locked) ----
            SetStage(ObfStage.RefreshDb, "AssetDatabase.Refresh");
            yield return null;
            AssetDatabase.Refresh(); // explicit refresh is allowed while auto-refresh is disallowed; reload stays locked
            yield return null;

            // ---- 3. GUID-preserving renames (main thread, chunked) ----
            var ops = new List<ObfBrand.RenameOp>();
            foreach (var kv in maps.resourceMap)
                if (Result.resourcePathToAsset.TryGetValue(kv.Key, out string ap)) ops.Add(new ObfBrand.RenameOp { path = ap, newStem = Leaf(kv.Value) });
            foreach (var kv in maps.shaderMap)
                if (Result.shaderNameToFile.TryGetValue(kv.Key, out string sp)) ops.Add(new ObfBrand.RenameOp { path = sp, newStem = Leaf(kv.Value) });
            foreach (var kv in maps.classMap)
                foreach (var path in Result.csFiles.Where(p => Path.GetFileNameWithoutExtension(p) == kv.Key))
                    ops.Add(new ObfBrand.RenameOp { path = path, newStem = kv.Value });
            foreach (var kv in maps.assetFileMap) ops.Add(new ObfBrand.RenameOp { path = kv.Key, newStem = kv.Value });
            // v3.4: the .asmdef file follows its assembly name (the "name" inside was rewritten in TransformText)
            foreach (var kv in maps.asmMap)
                if (Result.asmdefNameToFile.TryGetValue(kv.Key, out string adp)) ops.Add(new ObfBrand.RenameOp { path = adp, newStem = kv.Value });
            SetStage(ObfStage.RenameAssets, ops.Count + " renames");
            foreach (var _ in RunRenames(ops, "RENAME")) yield return null;
            AssetDatabase.SaveAssets();

            // ---- 3b. folders named after namespaces / assemblies / brand terms (v3.4), deepest first ----
            // Unity 6 IL2CPP embeds "\Assets\Scripts\<Namespace>\…\Foo.cs" for every script; file renames above do
            // not move folders, so the scan-time folder paths are still valid here. GUIDs survive a folder rename.
            if (maps.folderMap.Count > 0)
            {
                var fops = maps.folderMap.OrderByDescending(kv => kv.Key.Count(c => c == '/'))
                                         .Select(kv => new ObfBrand.RenameOp { path = kv.Key, newStem = kv.Value, isDir = true }).ToList();
                SetStage(ObfStage.RenameFolders, fops.Count + " folders");
                foreach (var _ in RunRenames(fops, "FOLDER")) yield return null;
                AssetDatabase.SaveAssets();
            }

            // ---- 4. brand pass: text (worker) then names (main) — on a fresh index, paths changed above ----
            if (maps.brand.Count > 0)
            {
                _idx = ObfProjectIndex.Build(ObfConfig.IsExcluded);
                var bidx = _idx;
                var bitems = _idx.Included.Where(f => ObfBrand.TextExt.Contains(f.ext)).Select(f => new TextItem { path = f.path, kind = "brand" }).ToList();
                SetStage(ObfStage.BrandText, bitems.Count + " text files");
                var bloop = new ObfWorkLoop<TextItem, TextResult>(bitems, (it, ct) =>
                {
                    var r = new TextResult { orig = bidx.Read(it.path) };
                    var review = new List<string>();
                    r.text = ObfTextTransforms.TransformBrandText(r.orig, maps, review, it.path);
                    foreach (var rv in review) r.warnings.Add("BRAND REVIEW (left untouched — URL / package id / native class): " + rv);
                    return r;
                });
                foreach (var _ in RunLoop(bloop, bitems, (it, r) =>
                {
                    if (r.text != null) _journal.BackupAndWrite(it.path, r.orig, r.text, "BRAND");
                    _journal.warnings.AddRange(r.warnings);
                }, it => it.path)) yield return null;

                var bops = ObfBrand.RenamePlan(_idx, maps.brand);
                SetStage(ObfStage.BrandRename, bops.Count + " renames");
                foreach (var _ in RunRenames(bops, "BRAND")) yield return null;
                AssetDatabase.SaveAssets();
            }

            // ---- 4b. Spine skeletons: blank the artist's machine paths (v3.4) — on the final paths ----
            if (_s.scrubSpine)
            {
                _idx = ObfProjectIndex.Build(ObfConfig.IsExcluded);
                var spineFiles = _idx.Included.Where(f => f.path.EndsWith(".skel.bytes", StringComparison.OrdinalIgnoreCase) || f.ext == ".skel" || f.ext == ".json").Select(f => f.path).ToList();
                SetStage(ObfStage.ScrubBinaries, spineFiles.Count + " candidates");
                int done = 0, scrubbed = 0;
                foreach (var sp in spineFiles)
                {
                    CheckCancel();
                    try
                    {
                        if (sp.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            string orig = _idx.Read(sp);
                            string nt = ObfSpine.ScrubJson(orig);
                            if (nt != null) { _journal.BackupAndWrite(sp, orig, nt, "SPINE"); _journal.log.Add("SPINE   " + sp + " (json images/audio path blanked)"); scrubbed++; }
                        }
                        else
                        {
                            byte[] data = File.ReadAllBytes(_idx.Abs(sp));
                            byte[] nb = ObfSpine.ScrubBinary(data, out string note);
                            if (nb != null) { _journal.BackupAndWriteBytes(sp, nb, "SPINE"); _journal.log.Add("SPINE   " + sp + " (" + note + ")"); scrubbed++; }
                        }
                    }
                    catch (Exception ex) { _journal.skipped.Add(sp + " | ScrubBinaries | " + ex.Message); }
                    if ((++done & 15) == 0) { Progress.Set(Stage.ToString(), sp, done, spineFiles.Count); yield return null; }
                }
                _journal.Note("spine scrub: " + scrubbed + " skeleton(s) cleaned");
                if (scrubbed > 0) AssetDatabase.Refresh();
            }

            // ---- 5. player settings + build settings (backed up first so Restore can undo them) ----
            SetStage(ObfStage.PlayerSettings, "ProjectSettings");
            yield return null;
            if (maps.brand.Count > 0 || !string.IsNullOrEmpty(_s.newProductName) || !string.IsNullOrEmpty(_s.newCompanyName) || !string.IsNullOrEmpty(_s.newBundleId))
            {
                _journal.Backup("ProjectSettings/ProjectSettings.asset");
                ObfBrand.ApplyPlayerSettings(maps.brand, _s.newProductName, _s.newCompanyName, _s.newBundleId, _journal.log);
            }
            if (_journal.manifest.Any(l => l.StartsWith("RENAME|", StringComparison.Ordinal) && l.Contains(".unity|")))
            {
                _journal.Backup("ProjectSettings/EditorBuildSettings.asset");
                ObfBrand.FixBuildSettings(_journal.manifest, _journal.log);
            }
            AssetDatabase.SaveAssets();
            yield return null;

            // ---- 6. reopen scenes at their post-rename paths ----
            SetStage(ObfStage.ReopenScenes, "");
            yield return null;
            for (int i = 0; i < _openScenes.Count; i++)
            {
                string sp = ObfBrand.FinalPath(_openScenes[i], _journal.manifest);
                if (File.Exists(_idx.Abs(sp)))
                    EditorSceneManager.OpenScene(sp, i == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive);
            }
            yield return null;

            // ---- 7. verification (worker) ----
            SetStage(ObfStage.Verify, "re-reading project");
            _idx = ObfProjectIndex.Build(ObfConfig.IsExcluded);
            var vidx = _idx; var manifestSnapshot = _journal.manifest.ToList(); var scanRes = Result;
            var vjob = new ObfJob<List<string>>(ct => Verify(vidx, maps, scanRes, manifestSnapshot, Progress, ct));
            vjob.Start();
            while (!vjob.Done)
            {
                if (_cancelRequested) { vjob.Cancel(); break; }
                if (vjob.ElapsedSec > Math.Max(120, _s.perFileBudgetSec * 30)) { vjob.Cancel(); _journal.warnings.Add("verification exceeded its budget and was skipped"); break; }
                yield return null;
            }
            if (vjob.Done && vjob.Error != null) _journal.warnings.Add("verification failed: " + vjob.Error.Message);
            else if (vjob.Done) _journal.verify.AddRange(vjob.Result);

            // ---- 8. reports ----
            SetStage(ObfStage.Report, "writing reports");
            yield return null;
            FinishRun(ObfStage.Done, null);
        }

        // ------------------------------------------------------------------------------------------
        /// Drives a work loop from the coroutine: drains results, enforces the per-file watchdog.
        IEnumerable RunLoop<TItem>(ObfWorkLoop<TItem, TextResult> loop, List<TItem> items, Action<TItem, TextResult> apply, Func<TItem, string> name)
        {
            loop.Start();
            int done = 0;
            while (!loop.Finished)
            {
                if (_cancelRequested) { loop.Cancel(); CheckCancel(); }
                int drained = 0;
                while (drained < 64 && loop.TryDequeue(out var r))
                {
                    drained++; done++;
                    var it = items[r.index];
                    if (r.error != null) _journal.skipped.Add(name(it) + " | " + Stage + " | " + r.error.GetType().Name + ": " + Truncate(r.error.Message, 160));
                    else
                    {
                        try { apply(it, r.result); }
                        catch (Exception ex) { _journal.skipped.Add(name(it) + " | write | " + ex.Message); }
                    }
                }
                if (loop.CurrentElapsedSec > _s.perFileBudgetSec)
                {
                    int i = loop.SkipCurrentAndRestart();
                    if (i >= 0) { done++; _journal.skipped.Add(name(items[i]) + " | " + Stage + " | exceeded per-file budget of " + _s.perFileBudgetSec + " s — skipped"); }
                }
                int cur = loop.CurrentIndex;
                Progress.Set(Stage.ToString(), cur >= 0 && cur < items.Count ? name(items[cur]) : "", done, items.Count);
                yield return null;
            }
            // final drain
            while (loop.TryDequeue(out var r))
            {
                var it = items[r.index];
                if (r.error != null) _journal.skipped.Add(name(it) + " | " + Stage + " | " + r.error.GetType().Name + ": " + Truncate(r.error.Message, 160));
                else { try { apply(it, r.result); } catch (Exception ex) { _journal.skipped.Add(name(it) + " | write | " + ex.Message); } }
            }
            Progress.Set(Stage.ToString(), "", items.Count, items.Count);
        }

        /// AssetDatabase renames in chunks so the editor stays responsive and Cancel works between chunks.
        IEnumerable RunRenames(List<ObfBrand.RenameOp> ops, string tag)
        {
            const int chunk = 150;
            for (int start = 0; start < ops.Count; start += chunk)
            {
                CheckCancel();
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = start; i < Math.Min(ops.Count, start + chunk); i++)
                    {
                        var op = ops[i];
                        string abs = _idx.Abs(op.path);
                        bool isDir = Directory.Exists(abs);
                        if (!isDir && !File.Exists(abs)) { _journal.log.Add("SKIP   " + op.path + " (missing)"); continue; }
                        if (isDir && !AssetDatabase.IsValidFolder(op.path)) continue;
                        string err;
                        try { err = AssetDatabase.RenameAsset(op.path, op.newStem); }
                        catch (Exception ex) { err = ex.Message; }
                        if (string.IsNullOrEmpty(err))
                        {
                            string to = Path.GetDirectoryName(op.path).Replace('\\', '/') + "/" + op.newStem + (isDir ? "" : Path.GetExtension(op.path));
                            _journal.RecordRename(op.path, to, tag);
                        }
                        else
                        {
                            _journal.warnings.Add(tag.ToLowerInvariant() + " rename FAILED — the code string was already rewritten, fix by hand: " + op.path + " -> " + op.newStem + " (" + err + ")");
                            _failedRenames++;
                        }
                    }
                }
                finally { AssetDatabase.StopAssetEditing(); }
                Progress.Set(Stage.ToString(), ops[Math.Min(ops.Count - 1, start + chunk - 1)].path, Math.Min(ops.Count, start + chunk), ops.Count);
                yield return null;
            }
        }

        // ------------------------------------------------------------------------------------------
        /// Code-only verification (worker thread): one tokenisation pass instead of one regex per identifier.
        static List<string> Verify(ObfProjectIndex idx, ObfApplyMaps maps, ScanResult res, List<string> manifest, ObfProgress prog, CancellationToken ct)
        {
            var issues = new List<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var rawSb = new StringBuilder();
            var cs = idx.Included.Where(f => f.ext == ".cs").ToList();
            int n = 0;
            foreach (var f in cs)
            {
                ct.ThrowIfCancellationRequested();
                if ((++n & 63) == 0) prog.Set("Verify", f.path, n, cs.Count);
                string raw = idx.Read(f.path);
                rawSb.Append(raw).Append('\n');
                string code = ObfCSharpText.CodeOnly(raw);
                int i = 0, len = code.Length;
                while (i < len)
                {
                    if (ObfCSharpText.IsIdentStart(code[i]))
                    {
                        int s = i; while (i < len && ObfCSharpText.IsIdentPart(code[i])) i++;
                        ids.Add(code.Substring(s, i - s));
                    }
                    else i++;
                }
            }
            string rawNow = rawSb.ToString();
            foreach (var kv in maps.idMap)
                if (ids.Contains(kv.Key)) issues.Add("identifier still present: " + kv.Key + "  (→ " + kv.Value + ")");
            foreach (var kv in maps.resourceMap)
            {
                if (rawNow.Contains("\"" + kv.Key + "\"")) issues.Add("resource string still present: \"" + kv.Key + "\"");
                if (res.resourcePathToAsset.TryGetValue(kv.Key, out var ap))
                {
                    string expect = ObfBrand.FinalPath(ap, manifest);
                    if (!File.Exists(idx.Abs(expect))) issues.Add("resource asset missing after rename: " + expect);
                }
            }
            foreach (var kv in maps.shaderMap)
                if (rawNow.Contains("\"" + kv.Key + "\"")) issues.Add("shader name still present: \"" + kv.Key + "\"");
            // type names resolved from strings (Type.GetType / Assembly.GetType) must not still carry an old namespace or class (v3.2)
            foreach (System.Text.RegularExpressions.Match tm in ObfRegex.Matches(rawNow, @"(?:Type\.GetType|Assembly\.GetType|\.GetType)\s*\(\s*""([^"",\n]+)"))
                foreach (var seg in tm.Groups[1].Value.Split('.', '+', '/'))
                    if (maps.typeMap.ContainsKey(seg)) { issues.Add("type-name string still old: \"" + tm.Groups[1].Value + "\""); break; }
            if (maps.brand.Count > 0)
            {
                n = 0;
                foreach (var f in idx.Included)
                {
                    ct.ThrowIfCancellationRequested();
                    if ((++n & 255) == 0) prog.Set("Verify (brand)", f.path, n, idx.files.Count);
                    if (ObfBrand.ContainsAny(f.path, maps.brand)) { issues.Add("brand term still in path: " + f.path); continue; }
                    if (!ObfBrand.TextExt.Contains(f.ext)) continue;
                    string t = idx.Read(f.path);
                    foreach (var v in maps.brand)
                        if (t.IndexOf(v.from, StringComparison.Ordinal) >= 0) { issues.Add("brand term '" + v.from + "' still inside: " + f.path); break; }
                }
            }
            // v3.4: brand TERMS (pairs + detect-only + auto-detected), case-insensitive — the view a metadata dump of the
            // build gives: identifiers, folder / file paths, assembly names. Every hit here ships in the binary.
            if (maps.brandTerms.Count > 0)
            {
                foreach (var id in ids)
                {
                    string term = ObfBrand.FirstTermIn(id, maps.brandTerms);
                    if (term != null) issues.Add("brand term '" + term + "' still in identifier: " + id);
                }
                foreach (var f in idx.Included)
                {
                    ct.ThrowIfCancellationRequested();
                    string term = ObfBrand.FirstTermIn(f.path, maps.brandTerms);
                    if (term != null) issues.Add("brand term '" + term + "' still in path: " + f.path);
                    if (f.ext == ".asmdef")
                    {
                        var nm = ObfRegex.Match(idx.Read(f.path), "\"name\"\\s*:\\s*\"([^\"]+)\"");
                        string an = nm.Success ? nm.Groups[1].Value : "";
                        term = ObfBrand.FirstTermIn(an, maps.brandTerms);
                        if (term != null) issues.Add("brand term '" + term + "' still in assembly name: " + an + " (" + f.path + ")");
                    }
                }
                foreach (System.Text.RegularExpressions.Match lm in ObfRegex.Matches(rawNow, @"""(?:[^""\\\n]|\\.)*"""))
                {
                    string term = ObfBrand.FirstTermIn(lm.Value, maps.brandTerms);
                    if (term != null && issues.Count < 4000) issues.Add("brand term '" + term + "' still in string literal: " + (lm.Value.Length > 100 ? lm.Value.Substring(0, 100) + "…" : lm.Value));
                }
            }
            return issues;
        }

        // ------------------------------------------------------------------------------------------
        void FinishRun(ObfStage final, string message)
        {
            Stage = final;
            Debug.Log("[CodeObfuscator] finished: " + final + (message != null ? " — " + message : "") + "  (" + FormatTime(ElapsedSec) + ")");
            if (_journal == null) { Outcome = message ?? ""; LastMessage = Outcome; return; }

            string status = final == ObfStage.Done ? "Completed" : final == ObfStage.Cancelled ? "Cancelled" : "Failed";
            try { WriteReports(status); } catch (Exception ex) { Debug.LogException(ex); }
            _journal.Close(status.ToUpperInvariant());

            if (_summary != null)
            {
                _summary.status = status; _summary.finishedAt = DateTime.Now.ToString("s"); _summary.stage = final.ToString();
                _summary.changes = _journal.changes; _summary.skippedFiles = _journal.skipped.Count;
                _summary.warnings = _journal.warnings.Count; _summary.verifyIssues = _journal.verify.Count;
                _summary.error = final == ObfStage.Done ? "" : message;
                if (Result != null)
                {
                    _summary.brandTerms = new List<string>(Result.brandTerms ?? new List<string>());
                    _summary.generatedNames = Result.entries
                        .Where(e => e.enabled && !e.reviewOnly && (e.category == ObfCategory.AssemblyName || e.category == ObfCategory.Namespace || e.category == ObfCategory.Folder))
                        .Select(e => e.newName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                }
                _summary.Save();
            }

            if (_locked)
            {
                _locked = false;
                try { AssetDatabase.AllowAutoRefresh(); } catch { }
                try { EditorApplication.UnlockReloadAssemblies(); } catch { }
            }

            string verifyTxt = _journal.verify.Count == 0 ? "clean" : _journal.verify.Count + " issue(s)";
            if (final == ObfStage.Done)
                Outcome = "Applied " + _journal.changes + " changes in " + FormatTime(ElapsedSec) + ". Skipped files: " + _journal.skipped.Count
                        + ". Warnings: " + _journal.warnings.Count + ". Verification: " + verifyTxt
                        + (_failedRenames > 0 ? "\n\n" + _failedRenames + " FILE RENAME(S) FAILED while their code strings were rewritten — assets loaded by those names will be missing. See warnings.txt, or Restore." : "")
                        + "\nReports: " + _journal.reportDir + "\nBackup:  " + _journal.backupDir
                        + "\n\nRecompiling now — the result is shown here. Use 'Restore last backup' if anything broke.";
            else
                Outcome = message + "\n" + _journal.changes + " change(s) were made before stopping and are journalled in " + _journal.backupDir
                        + "\nUse 'Restore last backup' to undo them.";
            LastMessage = Outcome;

            // the one recompile of the whole run happens here, after every rename is done
            try { AssetDatabase.Refresh(); } catch (Exception ex) { Debug.LogException(ex); }
            ObfBuildFlow.AfterApply(final == ObfStage.Done, BuildAfter);
        }

        void WriteReports(string status)
        {
            var j = _journal;
            File.WriteAllText(j.reportDir + "/change_map.json", ToJson(Result));
            File.WriteAllLines(j.reportDir + "/applied.txt", j.log);
            File.WriteAllLines(j.reportDir + "/warnings.txt", j.warnings.Count > 0 ? j.warnings : new List<string> { "none" });
            File.WriteAllLines(j.reportDir + "/verification.txt", j.verify.Count > 0 ? j.verify : new List<string> { "clean" });
            File.WriteAllLines(j.reportDir + "/skipped.txt", j.skipped.Count > 0 ? j.skipped : new List<string> { "none" });
            var det = ObfConfig.Detection;
            File.WriteAllLines(j.reportDir + "/sdk_detection.txt",
                new[] { "== excluded folders ==" }.Concat(det.folders.Select(f => (ObfConfig.IncludeAnyway.Contains(f.path) ? "[INCLUDED BY USER] " : "") + f.path + "  (" + f.score + ": " + f.Reason + ")"))
                .Concat(new[] { "", "== loose SDK files ==" }).Concat(det.files)
                .Concat(new[] { "", "== protected assets ==" }).Concat(det.protectedAssets));
            File.WriteAllLines(j.reportDir + "/review.txt",
                Result.entries.Where(e => e.reviewOnly).Select(e => e.category + " | " + e.oldName + " | " + e.primaryPath + " | " + e.note));
            File.WriteAllLines(j.reportDir + "/run.txt", new[]
            {
                "status: " + status, "stamp: " + j.stamp, "elapsed: " + FormatTime(ElapsedSec), "changes: " + j.changes,
                "skipped: " + j.skipped.Count, "warnings: " + j.warnings.Count, "verify issues: " + j.verify.Count,
                "per-file budget: " + _s.perFileBudgetSec + " s, regex timeout: " + _s.regexTimeoutSec + " s, total budget: " + _s.totalBudgetMin + " min",
                "profile: " + ObfProfile.Current.name + " (" + ObfProfile.Path + ")",
                "custom replacement rules: " + _s.customReplacements.Length + ", protected names: " + _s.protectedNames.Length + ", kept literals: " + _s.keepLiterals.Length
            });
        }

        // ---------------- helpers ----------------
        static string Leaf(string s) { int i = s.LastIndexOf('/'); return i < 0 ? s : s.Substring(i + 1); }
        public static string FormatTime(double sec) => sec < 60 ? sec.ToString("0.0") + " s" : TimeSpan.FromSeconds(sec).ToString(@"h\:mm\:ss");
        static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s ?? "" : "…" + s.Substring(s.Length - n + 1);

        static string ToJson(ScanResult res)
        {
            var sb = new StringBuilder("{\n  \"entries\": [\n");
            var list = res.entries.Where(e => !e.reviewOnly).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                sb.Append("    {\"category\":\"").Append(e.category).Append("\",\"old\":").Append(Q(e.oldName))
                  .Append(",\"new\":").Append(Q(e.newName)).Append(",\"enabled\":").Append(e.enabled ? "true" : "false")
                  .Append(",\"risk\":\"").Append(e.risk).Append("\",\"path\":").Append(Q(e.primaryPath)).Append("}")
                  .Append(i < list.Count - 1 ? ",\n" : "\n");
            }
            return sb.Append("  ]\n}").ToString();
        }
        static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
