// Code Obfuscator — headless entry point (CI / scripted builds).
//
//   Unity.exe -batchmode -nographics -projectPath <project> -executeMethod CodeObfuscator.ObfBatch.Run -quit
//             -obfAction scan|apply|verify|restore     (default: apply)
//             -obfProfile <file.json>                  optional: import this profile first (rules only …)
//             -obfProfileAll                           … or everything incl. product / company / bundle id
//             -obfBackup <CodeObfuscator_Backups/x> optional for restore (default: the last run's backup)
//             -obfDisableHighRisk                      apply: untick every High-risk entry before applying
//
// The pipeline is normally advanced by EditorApplication.update; that loop is not pumped while an
// -executeMethod call runs, so this driver pumps ObfPipeline.Pump() itself until the run is over.
// Exit codes: 0 ok · 1 run failed / cancelled · 2 verify found problems · 3 bad arguments.
//
// Note: the single recompile that follows Apply happens after this method returns; with -quit the
// editor exits before its result is known. Run a build (or a second batch call with -obfAction verify)
// afterwards — a compile error surfaces there, and 'restore' undoes the run if needed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace CodeObfuscator
{
    public static class ObfBatch
    {
        public static void Run()
        {
            int code;
            try { code = RunInternal(); }
            catch (Exception ex) { Debug.LogException(ex); code = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(code);
            else Debug.Log("[CodeObfuscator] batch finished with exit code " + code + " (not exiting: editor is interactive)");
        }

        static int RunInternal()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                for (int i = 0; i < args.Length - 1; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }
            bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

            string action = (Arg("-obfAction") ?? "apply").ToLowerInvariant();
            string profile = Arg("-obfProfile");
            if (!string.IsNullOrEmpty(profile))
            {
                if (!ObfProfile.Import(profile, !Flag("-obfProfileAll"))) { Debug.LogError("[CodeObfuscator] cannot import profile " + profile); return 3; }
                ObfConfig.InvalidateCaches();
                Debug.Log("[CodeObfuscator] profile imported from " + profile);
            }
            Debug.Log("[CodeObfuscator] batch action '" + action + "' with profile '" + ObfProfile.Current.name + "' (" + ObfProfile.Path + ")");

            switch (action)
            {
                case "scan":
                {
                    var p = Drive(ObfPipeline.StartScan());
                    if (p.Stage != ObfStage.ReviewReady) { Debug.LogError("[CodeObfuscator] scan ended in stage " + p.Stage + ": " + p.Outcome); return 1; }
                    WriteScanReport(p.Result);
                    Debug.Log("[CodeObfuscator] " + p.Outcome);
                    return 0;
                }
                case "apply":
                {
                    var prev = ObfRunSummary.Load();
                    if (prev != null && prev.status == "Completed")
                    {
                        Debug.LogError("[CodeObfuscator] the project was already processed by run " + prev.stamp + " — restore first (-obfAction restore), then apply once.");
                        return 1;
                    }
                    var scan = Drive(ObfPipeline.StartScan());
                    if (scan.Stage != ObfStage.ReviewReady) { Debug.LogError("[CodeObfuscator] scan ended in stage " + scan.Stage + ": " + scan.Outcome); return 1; }
                    var res = scan.Result;
                    if (Flag("-obfDisableHighRisk")) foreach (var e in res.entries) if (e.risk == ObfRisk.High) e.enabled = false;
                    WriteScanReport(res);
                    Debug.Log("[CodeObfuscator] applying " + res.entries.Count(e => e.enabled && !e.reviewOnly) + " entries …");
                    var apply = Drive(ObfPipeline.StartApply(res, false));
                    Debug.Log("[CodeObfuscator] " + apply.Outcome);
                    return apply.Stage == ObfStage.Done ? 0 : 1;
                }
                case "verify":
                {
                    var known = new List<string>();
                    var problems = ObfBuildGate.CheckReadiness(50, known);
                    foreach (var k in known) Debug.Log("[CodeObfuscator] known leftover: " + k);
                    foreach (var pr in problems) Debug.LogWarning("[CodeObfuscator] problem: " + pr);
                    Debug.Log("[CodeObfuscator] verify: " + problems.Count + " problem(s), " + known.Count + " known leftover(s)");
                    return problems.Count == 0 ? 0 : 2;
                }
                case "restore":
                {
                    string dir = Arg("-obfBackup") ?? ObfPipeline.LastBackupDir;
                    if (string.IsNullOrEmpty(dir)) { Debug.LogError("[CodeObfuscator] no backup to restore (pass -obfBackup <dir>)"); return 3; }
                    string msg = ObfJournal.Restore(dir);
                    Debug.Log("[CodeObfuscator] restore: " + msg);
                    try { AssetDatabase.Refresh(); } catch { }
                    return msg.StartsWith("Restore finished", StringComparison.Ordinal) && msg.Contains(" 0 failed") ? 0 : 1;
                }
                default:
                    Debug.LogError("[CodeObfuscator] unknown -obfAction '" + action + "' (scan | apply | verify | restore)");
                    return 3;
            }
        }

        /// Pumps the pipeline on the main thread until it is no longer running.
        static ObfPipeline Drive(ObfPipeline p)
        {
            ObfStage last = ObfStage.Idle;
            while (p.Pump())
            {
                if (p.Stage != last) { last = p.Stage; Debug.Log("[CodeObfuscator] … " + last + "  " + ObfPipeline.FormatTime(p.ElapsedSec)); }
                Thread.Sleep(5);
            }
            return p;
        }

        static void WriteScanReport(ScanResult res)
        {
            try
            {
                string root = ObfConfig.OutputRoot(ObfProfile.ProjectRoot, ObfProfile.Current.outputRoot) + "/" + ObfConfig.ReportRootName;
                Directory.CreateDirectory(root);
                string path = root + "/scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                File.WriteAllLines(path, res.entries.Select(e => (e.reviewOnly ? "REVIEW " : e.enabled ? "ON     " : "off    ") + e.category + " | " + e.oldName + " -> " + (e.newName ?? "") + " | " + e.risk + " | " + e.primaryPath + (string.IsNullOrEmpty(e.note) ? "" : " | " + e.note)));
                Debug.Log("[CodeObfuscator] scan list written to " + path);
            }
            catch (Exception ex) { Debug.LogWarning("[CodeObfuscator] could not write the scan list: " + ex.Message); }
        }
    }
}
