// Code Obfuscator — per-machine, per-project state that must survive a domain reload but is
// not part of the profile: the last SDK detection, the last backup / report folders, the message
// shown in the window, and the "Apply & Build" hand-over across the recompile.
// Stored in Library/CodeObfuscator/state.json (Library is per machine and never committed), so
// two projects open on the same machine never share or overwrite each other's state — v3 kept these
// in global EditorPrefs keys and did exactly that.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CodeObfuscator
{
    [Serializable]
    public class ObfLocalState
    {
        public string lastMessage = "Scan the project to begin.";
        public string lastBackupDir = "";
        public string lastReportDir = "";

        // Apply & Build hand-over
        public bool awaitCompile;
        public bool pendingBuild;
        public string pendingBuildPath = "";

        // last SDK detection ("path|score|reason" per folder)
        public List<string> sdkFolders = new List<string>();
        public List<string> sdkFiles = new List<string>();
        public List<string> sdkAssets = new List<string>();

        public static string Path => System.IO.Path.Combine(ObfProfile.ProjectRoot, "Library", "CodeObfuscator", "state.json").Replace('\\', '/');

        static ObfLocalState s_Current;
        public static ObfLocalState Current
        {
            get
            {
                if (s_Current != null) return s_Current;
                try { if (File.Exists(Path)) s_Current = JsonUtility.FromJson<ObfLocalState>(File.ReadAllText(Path)); }
                catch (Exception ex) { Debug.LogWarning("[CodeObfuscator] could not read local state: " + ex.Message); }
                if (s_Current == null) s_Current = new ObfLocalState();
                return s_Current;
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.WriteAllText(Path, JsonUtility.ToJson(this, true));
            }
            catch (Exception ex) { Debug.LogWarning("[CodeObfuscator] could not write local state: " + ex.Message); }
        }

        public SdkDetection ToDetection()
        {
            var det = new SdkDetection();
            foreach (var l in sdkFolders)
            {
                if (string.IsNullOrEmpty(l)) continue;
                var p = l.Split('|');
                var f = new SdkFolder { path = p[0], score = p.Length > 1 && int.TryParse(p[1], out int sc) ? sc : 0 };
                if (p.Length > 2) f.reasons.Add(p[2]);
                det.folders.Add(f);
            }
            det.files = sdkFiles.Where(x => !string.IsNullOrEmpty(x)).ToList();
            det.protectedAssets = sdkAssets.Where(x => !string.IsNullOrEmpty(x)).ToList();
            return det;
        }

        public void StoreDetection(SdkDetection det)
        {
            sdkFolders = det.folders.Select(f => f.path + "|" + f.score + "|" + f.Reason.Replace("|", "/")).ToList();
            sdkFiles = new List<string>(det.files);
            sdkAssets = new List<string>(det.protectedAssets);
            Save();
        }
    }
}
