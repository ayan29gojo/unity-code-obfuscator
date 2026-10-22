// Code Obfuscator — project inventory (stage 1).
//
// One filesystem walk of Assets/, done on the main thread, produces a plain-data snapshot that the
// worker thread can use without touching any Unity API: asset paths, extensions, sizes, the
// excluded/SDK flags (decided by ObfConfig on the main thread) and a Resources-folder lookup that
// replaces the AssetDatabase.FindAssets calls the v2 scanner needed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CodeObfuscator
{
    public class ObfFile
    {
        public string path;      // "Assets/…" asset path, forward slashes
        public string ext;       // lower-case, with dot
        public long size;
        public bool excluded;    // SDK / manual exclude / protected — never edited, never scanned for declarations
        public bool isMeta;
    }

    public class ObfProjectIndex
    {
        public string dataPath;      // absolute, forward slashes
        public string projectRoot;   // absolute, forward slashes
        public string toolFolder;    // "Assets/Editor/…" — never touched
        public List<ObfFile> files = new List<ObfFile>();
        public List<string> dirs = new List<string>();                       // asset paths of every folder under Assets
        public HashSet<string> excludedDirs = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, ObfFile> byPath = new Dictionary<string, ObfFile>(StringComparer.Ordinal);
        /// "configs/foo" → asset paths of every candidate under any Resources/ folder (excluded ones filtered later)
        /// Case-insensitive: Resources.Load("Configs/PlayerConfig") resolves "Resources/configs/PlayerConfig.asset"
        public Dictionary<string, List<string>> resources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public string Abs(string assetPath) => projectRoot + "/" + assetPath;

        public string Read(string assetPath)
        {
            try { return File.ReadAllText(Abs(assetPath)); } catch { return ""; }
        }

        public IEnumerable<ObfFile> Included => files.Where(f => !f.excluded && !f.isMeta);

        /// Main thread. isExcluded is ObfConfig.IsExcluded (reads the profile) — evaluated here, once.
        public static ObfProjectIndex Build(Func<string, bool> isExcluded)
        {
            var idx = new ObfProjectIndex
            {
                dataPath = Application.dataPath.Replace('\\', '/'),
                projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/'),
                toolFolder = ObfConfig.ToolFolder
            };
            foreach (var abs in Directory.GetFiles(Application.dataPath, "*.*", SearchOption.AllDirectories))
            {
                string p = "Assets" + abs.Replace('\\', '/').Substring(idx.dataPath.Length);
                var f = new ObfFile { path = p, ext = Path.GetExtension(p).ToLowerInvariant() };
                f.isMeta = f.ext == ".meta";
                try { f.size = new FileInfo(abs).Length; } catch { f.size = 0; }
                idx.files.Add(f);
                idx.byPath[p] = f;
                int r = p.IndexOf("/Resources/", StringComparison.Ordinal);
                if (r >= 0 && !f.isMeta)
                {
                    string rel = p.Substring(r + "/Resources/".Length);
                    int dot = rel.LastIndexOf('.'); if (dot > rel.LastIndexOf('/')) rel = rel.Substring(0, dot);
                    if (!idx.resources.TryGetValue(rel, out var list)) idx.resources[rel] = list = new List<string>();
                    list.Add(p);
                }
            }
            foreach (var abs in Directory.GetDirectories(Application.dataPath, "*", SearchOption.AllDirectories))
                idx.dirs.Add("Assets" + abs.Replace('\\', '/').Substring(idx.dataPath.Length));
            idx.dirs.Sort(StringComparer.Ordinal);
            idx.ApplyExclusions(isExcluded);
            return idx;
        }

        /// Main thread. Re-flags every file after SDK detection changed ObfConfig.Detection.
        public void ApplyExclusions(Func<string, bool> isExcluded)
        {
            foreach (var f in files) f.excluded = isExcluded(f.path);
            excludedDirs.Clear();
            foreach (var d in dirs) if (isExcluded(d)) excludedDirs.Add(d);
        }

        public bool IsExcludedPath(string assetPath)
        {
            if (byPath.TryGetValue(assetPath, out var f)) return f.excluded;
            if (excludedDirs.Contains(assetPath)) return true;
            return assetPath == toolFolder || assetPath.StartsWith(toolFolder + "/", StringComparison.Ordinal);
        }

        /// Resolves a Resources.Load path to exactly one non-excluded asset, or null when missing/ambiguous.
        public string ResolveResource(string resPath)
        {
            if (!resources.TryGetValue(resPath, out var list)) return null;
            var hits = list.Where(p => byPath.TryGetValue(p, out var f) && !f.excluded).ToList();
            return hits.Count == 1 ? hits[0] : null;
        }
    }
}
