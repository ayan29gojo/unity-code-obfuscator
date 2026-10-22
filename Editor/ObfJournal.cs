// Code Obfuscator — incremental backup journal + restore.
//
// v2 kept the manifest in memory and wrote it only at the very end, so a killed editor left a
// half-rewritten project with no way back. v3 appends and flushes every record the moment the
// change is made: kill the editor at any point and "Restore last backup" still replays it.
//
// Manifest format (unchanged so old backups still restore):
//   COPY|<path>            original content saved at <backupDir>/<path>   (once per file)
//   RENAME|<from>|<to>     AssetDatabase rename (file or folder)
//   # …                    stage markers / notes (ignored by Restore)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CodeObfuscator
{
    public class ObfJournal : IDisposable
    {
        public readonly string projectRoot, stamp, backupDir, reportDir;
        public readonly List<string> log = new List<string>();
        public readonly List<string> warnings = new List<string>();
        public readonly List<string> skipped = new List<string>();
        public readonly List<string> verify = new List<string>();
        public readonly List<string> manifest = new List<string>();
        public int changes;

        readonly HashSet<string> _backedUp = new HashSet<string>(StringComparer.Ordinal);
        StreamWriter _w;

        public ObfJournal(string projectRoot, string outputRootSetting = "")
        {
            this.projectRoot = projectRoot;
            stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outRoot = ObfConfig.OutputRoot(projectRoot, outputRootSetting);   // v4: profile `outputRoot`
            backupDir = outRoot + "/" + ObfConfig.BackupRootName + "/" + stamp;
            reportDir = outRoot + "/" + ObfConfig.ReportRootName + "/" + stamp;
            Directory.CreateDirectory(backupDir);
            Directory.CreateDirectory(reportDir);
            _w = new StreamWriter(backupDir + "/manifest.txt", false, new UTF8Encoding(false)) { AutoFlush = true };
            Note("CodeObfuscator run " + stamp + " — status: RUNNING");
        }

        string Abs(string p) => projectRoot + "/" + p;

        public void Note(string text) { Write("# " + text); }

        /// Backs the original up byte-for-byte (first touch only), writes the new content with the
        /// original's BOM state preserved, journals it. `original` is unused: a text round-trip would
        /// strip a UTF-8 BOM and make Restore leave a spurious one-byte diff in every such file.
        public void BackupAndWrite(string assetPath, string original, string updated, string tag)
        {
            string abs = Abs(assetPath);
            Backup(assetPath);
            bool bom = HasUtf8Bom(abs);
            File.WriteAllText(abs, updated, new UTF8Encoding(bom));
            log.Add(tag.PadRight(7) + assetPath);
            changes++;
        }

        /// v3.4: binary variant (Spine .skel.bytes scrub). Backed up byte-for-byte first, journalled like a text write.
        public void BackupAndWriteBytes(string assetPath, byte[] updated, string tag)
        {
            string abs = Abs(assetPath);
            Backup(assetPath);
            File.WriteAllBytes(abs, updated);
            log.Add(tag.PadRight(7) + assetPath);
            changes++;
        }

        static bool HasUtf8Bom(string abs)
        {
            try
            {
                using (var fs = File.OpenRead(abs))
                {
                    var b = new byte[3];
                    return fs.Read(b, 0, 3) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
                }
            }
            catch { return false; }
        }

        /// Backs up a project-relative file that is about to be changed by Unity itself (ProjectSettings.asset …).
        public void Backup(string projectRelPath, string original = null)
        {
            if (!_backedUp.Add(projectRelPath)) return;
            string dest = backupDir + "/" + projectRelPath;
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (original != null) File.WriteAllText(dest, original);
            else if (File.Exists(Abs(projectRelPath))) File.Copy(Abs(projectRelPath), dest, true);
            else return;
            Write("COPY|" + projectRelPath);
        }

        public void RecordRename(string from, string to, string tag)
        {
            Write("RENAME|" + from + "|" + to);
            log.Add(tag.PadRight(7) + from + " -> " + to);
            changes++;
        }

        void Write(string line)
        {
            manifest.Add(line);
            try { _w?.WriteLine(line); } catch (Exception ex) { warnings.Add("manifest write failed: " + ex.Message); }
        }

        public void Close(string status)
        {
            Note("status: " + status);
            try { _w?.Dispose(); } catch { }
            _w = null;
        }

        public void Dispose() { try { _w?.Dispose(); } catch { } _w = null; }

        // ------------------------------------------------------------------------------------------
        /// Replays a manifest in reverse: undo renames (deepest-first, last-first), then restore contents.
        /// Main thread (AssetDatabase). Returns a human-readable summary.
        public static string Restore(string backupDir)
        {
            if (string.IsNullOrEmpty(backupDir) || !File.Exists(backupDir + "/manifest.txt")) return "No backup manifest found.";
            string root = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            var lines = File.ReadAllLines(backupDir + "/manifest.txt");
            int ok = 0, fail = 0;
            var failures = new List<string>();

            // Same rule as Apply: scenes must not be open while their files are rewritten, or the
            // refresh pops the modal "modified externally" dialog and blocks the editor.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Exit Play mode first.";
            var openScenes = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!string.IsNullOrEmpty(sc.path)) openScenes.Add(sc.path);
            }
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return "Cancelled — open scenes must be saved (or discarded) before restoring.";
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);

            EditorApplication.LockReloadAssemblies();
            try
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        var p = lines[i].Split('|');
                        if (p[0] != "RENAME" || p.Length != 3) continue;
                        string cur = root + "/" + p[2];
                        bool isDir = Directory.Exists(cur);
                        if (!isDir && !File.Exists(cur))
                        {
                            string orig = root + "/" + p[1];
                            if (File.Exists(orig) || Directory.Exists(orig)) { ok++; continue; } // already back at its original name (restore re-run)
                            fail++; failures.Add("missing: " + p[2]); continue;
                        }
                        string err = AssetDatabase.RenameAsset(p[2], isDir ? Path.GetFileName(p[1]) : Path.GetFileNameWithoutExtension(p[1]));
                        if (string.IsNullOrEmpty(err)) ok++; else { fail++; failures.Add("rename " + p[2] + ": " + err); }
                    }
                }
                finally { AssetDatabase.StopAssetEditing(); }
                AssetDatabase.Refresh();

                foreach (var l in lines)
                {
                    var p = l.Split('|');
                    if (p[0] != "COPY" || p.Length != 2) continue;
                    string src = backupDir + "/" + p[1];
                    if (!File.Exists(src)) { fail++; failures.Add("backup missing: " + p[1]); continue; }
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(root + "/" + p[1]));
                        File.Copy(src, root + "/" + p[1], true); ok++;
                    }
                    catch (Exception ex) { fail++; failures.Add("copy " + p[1] + ": " + ex.Message); }
                }
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }
            AssetDatabase.Refresh();
            // reopen what was open, at the restored (original) paths
            var reverse = new List<string>();
            foreach (var l in lines) { var p = l.Split('|'); if (p[0] == "RENAME" && p.Length == 3) reverse.Add("RENAME|" + p[2] + "|" + p[1]); }
            reverse.Reverse();
            for (int i = 0; i < openScenes.Count; i++)
            {
                string sp = ObfBrand.FinalPath(openScenes[i], reverse);
                if (File.Exists(root + "/" + sp))
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(sp, i == 0 ? UnityEditor.SceneManagement.OpenSceneMode.Single : UnityEditor.SceneManagement.OpenSceneMode.Additive);
            }
            try { File.WriteAllLines(backupDir + "/restore_result.txt", failures.Count > 0 ? failures : new List<string> { "clean" }); } catch { }

            var sum = ObfRunSummary.Load();
            if (sum != null && sum.backupDir == backupDir) { sum.status = "Restored"; sum.finishedAt = DateTime.Now.ToString("s"); sum.Save(); }

            return "Restore finished: " + ok + " ok, " + fail + " failed." + (fail > 0 ? " See " + backupDir + "/restore_result.txt" : "");
        }
    }
}
