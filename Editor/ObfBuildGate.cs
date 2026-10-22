// Code Obfuscator — build integration.
//
//   ObfBuildGate  IPreprocessBuildWithReport: refuses (or warns on) a player build when the project
//                has not been obfuscated, when the last run was interrupted, or when brand terms are
//                still present. This is the "correct state before building" guarantee — the tool
//                itself never runs inside the build; it runs BEFORE it, on the project.
//   ObfBuildFlow  "Apply & Build": after the pipeline finishes and the single recompile has run,
//                reports Compiled OK / COMPILE FAILED and, if requested, starts BuildPipeline.
//                State lives in ObfLocalState (Library/) because the recompile reloads the domain.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CodeObfuscator
{
    public class ObfBuildGate : IPreprocessBuildWithReport
    {
        public int callbackOrder => -10000;

        public void OnPreprocessBuild(BuildReport report)
        {
            var mode = ObfConfig.buildGate;
            if (mode == ObfGateMode.Off) return;
            var known = new List<string>();
            var problems = CheckReadiness(12, known);
            var sum = ObfRunSummary.Load();
            if (known.Count > 0)
                Debug.LogWarning("[CodeObfuscator] " + known.Count + " brand-term leftover(s) the run's verification already accepted (URLs / package ids / "
                               + "FormerlySerializedAs / plain English) — they ship in the build:\n  • " + string.Join("\n  • ", known.Take(20)));
            if (problems.Count == 0)
            {
                Debug.Log("[CodeObfuscator] pre-build gate passed — project obfuscated by run " + (sum != null ? sum.stamp : "?") + ".");
                return;
            }
            string msg = "[CodeObfuscator] pre-build gate: the project has not been obfuscated.\n  • " + string.Join("\n  • ", problems)
                       + "\nOpen Tools ▸ Code Obfuscator and run Scan + Apply, or set the build gate to Warn/Off in the window.";
            if (mode == ObfGateMode.Block) throw new BuildFailedException(msg);
            Debug.LogWarning(msg);
        }

        [MenuItem("Tools/Code Obfuscator/Check Build Readiness")]
        static void CheckMenu()
        {
            var known = new List<string>();
            var problems = CheckReadiness(12, known);
            string msg = problems.Count == 0 ? "Project is obfuscated — a build would pass the gate."
                                             : "Not ready:\n• " + string.Join("\n• ", problems);
            if (known.Count > 0)
                msg += "\n\nKnown leftovers accepted by the run's verification (not blocking, but they ship):\n• " + string.Join("\n• ", known.Take(12))
                     + (known.Count > 12 ? "\n• … and " + (known.Count - 12) + " more" : "");
            Debug.Log("[CodeObfuscator] " + msg);
            EditorUtility.DisplayDialog("Code Obfuscator — build readiness", msg, "OK");
        }

        /// Fast (a few seconds): run summary + brand terms in paths / scripts. Main thread.
        /// `known` (optional) receives the hits the run's own verification already listed as leftovers — the brand pass
        /// skips URLs / package ids / Java class names / FormerlySerializedAs on purpose, and a term that is also a common
        /// English word is plain text — so they never block; only NEW hits (paths, assembly names, identifiers, unlisted literals) do.
        public static List<string> CheckReadiness(int maxListed = 12, List<string> known = null)
        {
            var problems = new List<string>();
            var sum = ObfRunSummary.Load();
            var accepted = AcceptedLeftovers(sum);
            if (sum == null) problems.Add("no completed Code Obfuscator run is recorded for this project");
            else if (sum.status == "Running") problems.Add("the last run (" + sum.stamp + ") was interrupted at stage " + sum.stage + " — use 'Restore last backup' and run again");
            else if (sum.status != "Completed") problems.Add("the last run (" + sum.stamp + ") ended with status " + sum.status);

            // v3.4: every brand term the tool knows (pairs + detect-only terms + auto-detected ones), case-insensitive,
            // over paths, script identifiers/strings and .asmdef names — the same view a metadata dump of the build gives.
            // v3.5: the terms are the ones the RUN scanned with. Deriving them from the current .asmdef names after a run
            // turned the tool's own output (generated names such as QuietRouterSpace.*) into "brand terms" and blocked every build.
            var terms = CurrentTerms(sum);
            if (terms.Count > 0)
            {
                string dp = Application.dataPath.Replace('\\', '/');
                int hits = 0;
                foreach (var abs in Directory.GetFiles(Application.dataPath, "*.*", SearchOption.AllDirectories))
                {
                    string p = "Assets" + abs.Replace('\\', '/').Substring(dp.Length);
                    if (p.EndsWith(".meta") || ObfConfig.IsExcluded(p)) continue;
                    string term = ObfBrand.FirstTermIn(p, terms);
                    if (term != null) { if (hits++ < maxListed) problems.Add("brand term '" + term + "' still in path: " + p); continue; }
                    if (!p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) continue;
                    string t; try { t = File.ReadAllText(abs); } catch { continue; }
                    if (p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                    {
                        term = ObfBrand.FirstTermIn(t, terms);
                        if (term != null && hits++ < maxListed) problems.Add("brand term '" + term + "' still inside: " + p);
                        continue;
                    }
                    // .cs: comments never ship — look at code + strings only, and split the hits into the ones the run's
                    // verification already accepted (known) and new ones (blocking).
                    string code; try { code = ObfCSharpText.StripComments(t); } catch { code = t; }
                    term = StrictTermIn(code, terms);
                    if (term == null) continue;
                    string why;
                    if (IsAcceptedLeftover(p, code, terms, accepted, out why)) { known?.Add("'" + why + "' in " + p); continue; }
                    if (hits++ < maxListed) problems.Add("brand term '" + term + "' still inside: " + p);
                }
                if (hits > maxListed) problems.Add("… and " + (hits - maxListed) + " more");
            }
            return problems;
        }

        /// Script contents: a term counts only in a casing a brand name is actually written in (Zed / zed / ZED),
        /// so "SizeDelta" (…zeD…) is not a hit. Paths and assembly names stay fully case-insensitive.
        static string StrictTermIn(string s, List<string> terms)
        {
            if (string.IsNullOrEmpty(s)) return null;
            foreach (var t in terms)
            {
                if (t.Length == 0) continue;
                string pascal = char.ToUpperInvariant(t[0]) + t.Substring(1).ToLowerInvariant();
                if (s.IndexOf(t, StringComparison.Ordinal) >= 0 || s.IndexOf(pascal, StringComparison.Ordinal) >= 0
                 || s.IndexOf(t.ToLowerInvariant(), StringComparison.Ordinal) >= 0 || s.IndexOf(t.ToUpperInvariant(), StringComparison.Ordinal) >= 0)
                    return t;
            }
            return null;
        }

        static readonly System.Text.RegularExpressions.Regex LiteralRx = new System.Text.RegularExpressions.Regex(@"@?""(?:[^""\\\n]|\\.|"""")*""");

        /// The verification.txt lines of the completed run (empty when there is none).
        static HashSet<string> AcceptedLeftovers(ObfRunSummary sum)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (sum == null || sum.status != "Completed" || string.IsNullOrEmpty(sum.reportDir)) return set;
            try
            {
                string f = Path.Combine(sum.reportDir, "verification.txt");
                if (File.Exists(f)) foreach (var l in File.ReadAllLines(f)) set.Add(l.Trim());
            }
            catch { }
            return set;
        }

        /// True when every brand-term hit in `code` (comments already stripped) is a string literal the run's
        /// verification listed, or a [FormerlySerializedAs] that must keep the old name. Any hit outside such a
        /// literal (an identifier, an unlisted string) is a new leak.
        static bool IsAcceptedLeftover(string path, string code, List<string> terms, HashSet<string> accepted, out string why)
        {
            why = null;
            if (accepted.Count == 0) return false;
            bool wholeFileListed = accepted.Any(a => a.EndsWith("still inside: " + path, StringComparison.Ordinal));
            var sb = new StringBuilder(code);
            foreach (System.Text.RegularExpressions.Match m in LiteralRx.Matches(code))
            {
                string lit = m.Value;
                string term = StrictTermIn(lit, terms);
                if (term == null) continue;
                string plain = lit.StartsWith("@") ? lit.Substring(1) : lit;
                string listed = "brand term '" + term + "' still in string literal: " + (plain.Length > 100 ? plain.Substring(0, 100) + "…" : plain);
                bool fsa = m.Index >= 20 && code.LastIndexOf("FormerlySerializedAs", m.Index, Math.Min(m.Index, 40), StringComparison.Ordinal) >= 0;
                if (!fsa && !wholeFileListed && !accepted.Contains(listed)) return false;
                why = why ?? (fsa ? "FormerlySerializedAs(" + plain + ")" : plain.Length > 60 ? plain.Substring(0, 60) + "…" : plain);
                for (int i = m.Index; i < m.Index + m.Length; i++) sb[i] = ' ';   // blank the accepted literal
            }
            string rest = sb.ToString();
            string left = StrictTermIn(rest, terms);
            if (left == null) return why != null;
            // a hit outside every string literal: identifier / attribute / namespace — a real leak unless the run listed the file itself
            if (wholeFileListed) { why = why ?? "'" + left + "' (file listed by the run's verification)"; return true; }
            return false;
        }

        /// The brand terms a readiness check / the window preview should use.
        ///   • a completed run recorded its scan terms → those (the old names), minus anything the run generated;
        ///   • otherwise derive them from settings + the current .asmdef names, still skipping names a previous run
        ///     generated (read back from its change_map.json) so a processed tree never flags itself.
        public static List<string> CurrentTerms(ObfRunSummary sum)
        {
            var generated = GeneratedNames(sum);
            bool IsGenerated(string t) => generated.Any(g => g.Equals(t, StringComparison.OrdinalIgnoreCase)
                                                        || g.Split('.', '-', '_').Any(seg => seg.Equals(t, StringComparison.OrdinalIgnoreCase)));
            List<string> terms;
            if (sum != null && sum.status == "Completed" && sum.brandTerms != null && sum.brandTerms.Count > 0)
                terms = new List<string>(sum.brandTerms);
            else
                terms = ObfBrand.ResolveTerms(ObfConfig.Snapshot(), AsmdefNames().Where(a => !IsGenerated(a)).ToList(), new List<string>());
            return terms.Where(t => !IsGenerated(t)).ToList();
        }

        /// Names a run generated: the summary's list, or the AssemblyName / Namespace / Folder targets in its change_map.json.
        static HashSet<string> GeneratedNames(ObfRunSummary sum)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sum == null) return set;
            if (sum.generatedNames != null) foreach (var n in sum.generatedNames) if (!string.IsNullOrEmpty(n)) set.Add(n);
            if (set.Count > 0 || string.IsNullOrEmpty(sum.reportDir)) return set;
            try
            {
                string map = Path.Combine(sum.reportDir, "change_map.json");
                if (!File.Exists(map)) return set;
                var rx = new System.Text.RegularExpressions.Regex(@"\{""category"":""(AssemblyName|Namespace|Folder)"",""old"":""(?:[^""\\]|\\.)*"",""new"":""((?:[^""\\]|\\.)*)"",""enabled"":true");
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(map)))
                    set.Add(m.Groups[2].Value.Replace("\\\\", "\\").Replace("\\\"", "\""));
            }
            catch { }
            return set;
        }

        static List<string> AsmdefNames()
        {
            var names = new List<string>();
            try
            {
                string dp = Application.dataPath.Replace('\\', '/');
                foreach (var abs in Directory.GetFiles(Application.dataPath, "*.asmdef", SearchOption.AllDirectories))
                {
                    string ap = "Assets" + abs.Replace('\\', '/').Substring(dp.Length);
                    if (ObfConfig.IsExcluded(ap)) continue;   // third-party assemblies are not brand terms
                    var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(abs), "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    names.Add(m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(abs));
                }
            }
            catch { }
            return names;
        }
    }

    [InitializeOnLoad]
    public static class ObfBuildFlow
    {
        static ObfLocalState St => ObfLocalState.Current;

        static double _start;
        static bool _sawCompiling;
        public static bool Waiting { get; private set; }

        static ObfBuildFlow() { EditorApplication.delayCall += OnDomainLoad; }

        static void OnDomainLoad()
        {
            if (!St.awaitCompile) return;
            Arm(afterReload: true);
        }

        /// Called by the pipeline right after its final AssetDatabase.Refresh().
        public static void AfterApply(bool success, bool buildAfter)
        {
            if (!success) { St.pendingBuild = false; St.Save(); return; }
            St.pendingBuild = buildAfter;
            St.awaitCompile = true;   // survives the domain reload
            St.Save();
            Arm(afterReload: false);               // and covers the no-reload case (compile error / nothing to compile)
        }

        static void Arm(bool afterReload)
        {
            _start = EditorApplication.timeSinceStartup;
            _sawCompiling = afterReload;
            Waiting = true;
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { _sawCompiling = true; return; }
            if (!_sawCompiling && EditorApplication.timeSinceStartup - _start < 10) return; // give the compile a chance to start
            EditorApplication.update -= Poll;
            Waiting = false;
            St.awaitCompile = false;
            bool pending = St.pendingBuild;
            St.pendingBuild = false;
            St.Save();

            if (EditorUtility.scriptCompilationFailed)
            {
                ObfPipeline.LastMessage += "\n\nCOMPILE FAILED — read the Console. Fix by hand or use 'Restore last backup'." + (pending ? " Build cancelled." : "");
                Debug.LogError("[CodeObfuscator] compile failed after Apply — see Console; 'Restore last backup' undoes the run.");
                return;
            }
            ObfPipeline.LastMessage += "\n\nCompiled OK." + (pending ? " Starting build…" : " Now play-test the game and run a build.");
            if (pending) Build();
        }

        /// Asks where to build; returns false when the user cancels.
        public static bool AskBuildPath()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            string ext = target == BuildTarget.Android ? (EditorUserBuildSettings.buildAppBundle ? "aab" : "apk")
                       : target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64 ? "exe" : "";
            string def = PlayerSettings.productName;
            string path = ext.Length > 0 ? EditorUtility.SaveFilePanel("Build location (" + target + ")", "", def, ext)
                                         : EditorUtility.SaveFolderPanel("Build folder (" + target + ")", "", def);
            if (string.IsNullOrEmpty(path)) return false;
            St.pendingBuildPath = path; St.Save();
            return true;
        }

        static void Build()
        {
            string path = St.pendingBuildPath;
            if (string.IsNullOrEmpty(path)) { Debug.LogError("[CodeObfuscator] no build path stored — build skipped."); return; }
            var target = EditorUserBuildSettings.activeBuildTarget;
            var opts = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = path,
                target = target,
                targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                options = EditorUserBuildSettings.development ? BuildOptions.Development : BuildOptions.None
            };
            Debug.Log("[CodeObfuscator] project processed — starting " + target + " build → " + path);
            BuildReport report;
            try { report = BuildPipeline.BuildPlayer(opts); }
            catch (Exception ex) { Debug.LogException(ex); ObfPipeline.LastMessage += "\nBUILD FAILED: " + ex.Message; return; }
            var s = report.summary;
            string msg = "Build " + s.result + " in " + ObfPipeline.FormatTime(s.totalTime.TotalSeconds) + " — " + s.outputPath
                       + (s.totalErrors > 0 ? " (" + s.totalErrors + " errors)" : "");
            ObfPipeline.LastMessage += "\n" + msg;
            if (s.result == BuildResult.Succeeded) Debug.Log("[CodeObfuscator] " + msg); else Debug.LogError("[CodeObfuscator] " + msg);
        }
    }
}
