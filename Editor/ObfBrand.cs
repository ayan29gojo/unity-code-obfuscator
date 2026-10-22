// Code Obfuscator — brand pass.
//
// A brand term is a studio / product word (Acme, Orion, "Space Blaster") that leaks into identifiers,
// strings, asset & folder names, scene text, the bundle id and product name. The user gives pairs like
//     Space Blaster => Star Racer
// and this pass derives every casing variant (SpaceBlaster, spaceBlaster, space_blaster, SPACE_BLASTER,
// space-blaster, spaceblaster, "Space Blaster", "space blaster", …) and applies the SAME function to
//   1. every text file in scope  (per-file, on the worker — ObfTextTransforms.TransformBrandText)
//   2. every asset file and folder name in scope (GUID-preserving, deepest first — RenamePlan)
//   3. Player Settings (product / company / bundle id)
// Because one deterministic function is applied to code strings AND to file names, Resources.Load
// paths, Find() paths, LoadScene() names etc. stay consistent by construction.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace CodeObfuscator
{
    public static class ObfBrand
    {
        public static readonly HashSet<string> TextExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",".shader",".cginc",".hlsl",".compute",".unity",".prefab",".asset",".anim",".controller",
            ".overrideController",".mat",".asmdef",".asmref",".physicMaterial",".physicsMaterial2D",".playable",
            ".spriteatlas",".preset",".mixer",".renderTexture",".cubemap",".flare",".fontsettings",".guiskin",
            ".signal",".terrainlayer",".lighting",".shadervariants",".giparams",".mask",".brush",".inputactions",
            ".uxml",".uss",".tss",".txt",".json",".xml",".md",".csv"
        };

        public static readonly HashSet<string> NeverRenameExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".dll",".so",".a",".aar",".jar",".bundle",".framework",".jslib",".mm",".m",".h",".cpp",".c",".java",".kt",".swift",".plist",".pdb",".mdb",".xcconfig" };

        public struct Pair { public string from, to; }

        // "Space Blaster => Star Racer"  (one per line; '=>' or '->' or '=')
        public static List<Pair> ParsePairs(string text, List<string> problems)
        {
            var list = new List<Pair>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var m = Regex.Match(line, @"^(.+?)\s*(?:=>|->|=)\s*(.+)$");
                if (!m.Success) { problems.Add("unparsed brand line: " + line); continue; }
                string from = m.Groups[1].Value.Trim(), to = m.Groups[2].Value.Trim();
                if (from.Length < 3) { problems.Add("brand term too short (min 3 chars): " + from); continue; }
                if (Regex.IsMatch(from.Replace(" ", ""), @"^[0-9a-fA-F]+$")) { problems.Add("brand term is pure hex and could hit GUIDs — skipped: " + from); continue; }
                if (Regex.IsMatch(to, @"^[0-9]")) { problems.Add("new term must not start with a digit: " + to); continue; }
                list.Add(new Pair { from = from, to = to });
            }
            return list;
        }

        /// v4: exact, case-sensitive substring rules from the profile ("old_ => new_"), no variants derived. A rule's
        /// 'from' must be ≥ 3 chars and must not be pure hex (GUIDs); the 'to' side may be empty (= delete the substring).
        public static List<Pair> ParseCustom(string[] lines, List<string> problems)
        {
            var list = new List<Pair>();
            foreach (var raw in lines ?? new string[0])
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var m = Regex.Match(line, @"^(.+?)\s*(?:=>|->)\s*(.*)$");
                if (!m.Success) { problems?.Add("unparsed custom rule (use old => new): " + line); continue; }
                string from = m.Groups[1].Value.Trim(), to = m.Groups[2].Value.Trim();
                if (from.Length < 3) { problems?.Add("custom rule too short (min 3 chars): " + from); continue; }
                if (Regex.IsMatch(from, @"^[0-9a-fA-F]+$")) { problems?.Add("custom rule is pure hex and could hit GUIDs — skipped: " + from); continue; }
                if (from == to) continue;
                list.Add(new Pair { from = from, to = to });
            }
            return list;
        }

        // Expands each pair into all casing variants, longest 'from' first.
        public static List<Pair> Variants(List<Pair> pairs)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in pairs)
            {
                var wo = Words(p.from); var wn = Words(p.to);
                if (wo.Count == 0 || wn.Count == 0) continue;
                void Add(string f, string t) { if (f.Length > 0 && f != t && !seen.ContainsKey(f)) seen[f] = t; }

                Add(Join(wo, "", Case.Pascal), Join(wn, "", Case.Pascal));
                Add(Join(wo, "", Case.Camel),  Join(wn, "", Case.Camel));
                Add(Join(wo, "", Case.Lower),  Join(wn, "", Case.Lower));
                Add(Join(wo, "", Case.Upper),  Join(wn, "", Case.Upper));
                foreach (var sep in new[] { " ", "_", "-", "." })
                {
                    Add(Join(wo, sep, Case.Pascal), Join(wn, sep, Case.Pascal));
                    Add(Join(wo, sep, Case.Lower),  Join(wn, sep, Case.Lower));
                    Add(Join(wo, sep, Case.Upper),  Join(wn, sep, Case.Upper));
                }
                Add(p.from, p.to); // raw pair last: only used when it is not one of the derived forms
            }
            return seen.OrderByDescending(kv => kv.Key.Length).Select(kv => new Pair { from = kv.Key, to = kv.Value }).ToList();
        }

        public static string Apply(string s, List<Pair> variants)
        {
            if (string.IsNullOrEmpty(s) || variants.Count == 0) return s;
            foreach (var v in variants) s = s.Replace(v.from, v.to);
            return s;
        }

        // ---------------------------------------------------------------------------------------------
        // v3.4 — brand TERMS (detection) as opposed to brand PAIRS (replacement)
        //
        // A before/after comparison of the shipped build showed that the leaks which survive a full run are
        // exactly the ones no pair was written for: the old product name inside the Unity Cloud project name and
        // the Resources logo, the studio word inside property / event / override names (no category renames
        // those), assembly names (Acme.Orion.dll), and the script folders that Unity 6 IL2CPP embeds as
        // "\Assets\Scripts\Acme\Game\Foo.cs" for every script. Terms are therefore derived automatically from
        // what the project itself says its name is, and the user can add detect-only terms without a pair.
        // ---------------------------------------------------------------------------------------------

        // Words that appear in product / assembly names but identify nothing: never a brand term on their own.
        static readonly HashSet<string> GenericWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game","games","base","core","common","runtime","editor","scripts","shared","utils","util","tools","tool","main",
            "app","apps","client","server","sdk","plugin","plugins","lib","libs","framework","studio","studios","inc","ltd",
            "llc","gmbh","com","net","org","io","android","ios","mobile","unity","assembly","csharp","firstpass","tests","test",
            "internal","public","private","default","company","product","the","and","for","new","free","pro","lite","hd",
            "puzzle","casual","arcade","action","fever","jump","match","blast","saga","quest","world","land","story","king",
            "star","super","mega","ultra","big","little","one","two","dev","demo","beta","alpha","project","engine","system",
            "haptics","haptic","input","audio","video","network","physics","render","rendering","graphics","animation",
            "localization","analytics","ads","iap","purchasing","social","cloud","services","service","data","config",
            "settings","model","view","controller","manager","helper","helpers","extensions","extension","module","modules"
        };

        /// Every brand term this run works with, longest first: the 'from' side of each pair, the detect-only list,
        /// and (when enabled) words derived from the old product name, company name, bundle id and assembly names.
        /// A derived word must be ≥ 4 chars, not a generic word, and — when `codeIdentifiers` is given — actually
        /// occur inside some identifier of the project, otherwise it is noise.
        public static List<string> ResolveTerms(ObfSettings s, IEnumerable<string> asmdefNames, ICollection<string> codeIdentifiers)
        {
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in ParsePairs(s.brandPairsText, new List<string>())) AddTerm(terms, p.from);
            foreach (var raw in (s.brandTermsText ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                AddTerm(terms, line);
            }
            if (s.detectBrandTerms)
            {
                // The NEW names are not leaks. When Player Settings were already changed by hand (product already = "Star Racer")
                // the "current" product / company / bundle id IS the new brand, so every word of the new names is excluded.
                var newWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in new[] { s.newProductName, s.newCompanyName, s.newBundleId })
                {
                    if (string.IsNullOrEmpty(src)) continue;
                    var tmp = new HashSet<string>(StringComparer.OrdinalIgnoreCase); AddTerm(tmp, src);
                    foreach (var t in tmp) newWords.Add(t);
                    foreach (var w in Words(src)) newWords.Add(w);
                    foreach (var seg in src.Split('.')) newWords.Add(seg);
                }
                bool IsNewBrand(string w) => newWords.Contains(w) || newWords.Any(n => n.Length >= 4 && (n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
                // Names the project calls itself: these ARE the old brand, kept even when no identifier carries them
                // (they still sit in shader paths, menu strings, asset names and the cloud project name).
                var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in new[] { s.oldProductName, s.oldCompanyName, s.cloudProjectName })
                {
                    if (string.IsNullOrEmpty(src)) continue;
                    AddTerm(named, src);                                     // "Space Blaster" as a whole (spaces → variants)
                    foreach (var w in Words(src)) named.Add(w);              // Space, Blaster — filtered below
                }
                // Segments of technical names: only kept when some identifier in the code actually carries them.
                var technical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(s.oldBundleId))
                    foreach (var seg in s.oldBundleId.Split('.')) technical.Add(seg);   // com.acme.spaceblaster → acme, spaceblaster
                foreach (var asm in asmdefNames ?? Enumerable.Empty<string>())
                    foreach (var seg in asm.Split('.', '-', '_')) technical.Add(seg);   // Acme.Orion → Acme, Orion
                foreach (var d in named.Concat(technical))
                {
                    string w = d.Trim();
                    if (w.Length < 4 || GenericWords.Contains(w) || Regex.IsMatch(w, @"^\d+$") || IsNewBrand(w)) continue;
                    if (!named.Contains(w) && codeIdentifiers != null && codeIdentifiers.Count > 0 && !codeIdentifiers.Any(id => id.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    terms.Add(w);
                }
            }
            return terms.Where(t => t.Length >= 3).OrderByDescending(t => t.Length).ToList();
        }

        static void AddTerm(HashSet<string> into, string term)
        {
            term = (term ?? "").Trim();
            if (term.Length < 3) return;
            into.Add(term);
            var w = Words(term);
            if (w.Count > 1) { into.Add(string.Join("", w)); into.Add(string.Join("_", w)); into.Add(string.Join("-", w)); }
        }

        /// Case-insensitive: the first term found inside s, or null.
        public static string FirstTermIn(string s, List<string> terms)
        {
            if (string.IsNullOrEmpty(s)) return null;
            foreach (var t in terms) if (s.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return t;
            return null;
        }

        /// Pairs the user did not have to type: old product name => new product name, old company => new company.
        /// Only when the new value is set and differs; the raw pair expands into every casing variant like a typed one.
        public static List<Pair> AutoPairs(ObfSettings s, List<string> notes)
        {
            var list = new List<Pair>();
            void Add(string from, string to, string what)
            {
                from = (from ?? "").Trim(); to = (to ?? "").Trim();
                if (from.Length < 3 || to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
                if (Regex.IsMatch(from.Replace(" ", ""), @"^[0-9a-fA-F]+$") || Regex.IsMatch(to, @"^[0-9]")) return;
                list.Add(new Pair { from = from, to = to });
                notes?.Add("auto brand pair (" + what + "): " + from + " => " + to);
            }
            Add(s.oldProductName, s.newProductName, "product name");
            if (!string.Equals(s.cloudProjectName, s.oldProductName, StringComparison.OrdinalIgnoreCase)) Add(s.cloudProjectName, s.newProductName, "cloud project name");
            Add(s.oldCompanyName, s.newCompanyName, "company name");
            return list;
        }

        // Substrings the brand pass must never rewrite: URLs, reverse-DNS package ids and Java class names
        // (com.acme.android.AdvertisingIdFetcher is looked up by name inside an .aar the tool cannot edit;
        // https://cdn.acme-games.com is a live backend). They are reported for a manual decision.
        static readonly Regex ProtectedSpan = new Regex(
            @"[A-Za-z][A-Za-z0-9+.\-]*://[^\s""'<>)\]]+|(?<![\w.])[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*){2,}(?:\.[A-Za-z_]\w*)*(?![\w.])",
            RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        /// Apply() that leaves URLs / package ids / Java class names untouched and reports the ones that carry a term.
        public static string ApplyGuarded(string s, List<Pair> variants, List<string> review, string where)
        {
            if (string.IsNullOrEmpty(s) || variants.Count == 0) return s;
            var kept = new List<string>();
            string masked;
            try
            {
                masked = ProtectedSpan.Replace(s, m =>
                {
                    if (!ContainsAny(m.Value, variants)) return m.Value;
                    kept.Add(m.Value);
                    return "OBFKEEP" + (kept.Count - 1) + "";
                });
            }
            catch (RegexMatchTimeoutException) { masked = s; kept.Clear(); }
            string outp = Apply(masked, variants);
            for (int i = 0; i < kept.Count; i++)
            {
                outp = outp.Replace("OBFKEEP" + i + "", kept[i]);
                review?.Add(where + " | " + kept[i]);
            }
            return outp;
        }

        /// Folders whose name is (or is dot-joined from) a renamed namespace / assembly / brand term, deepest first.
        /// GUID-preserving: AssetDatabase.RenameAsset on a folder keeps every asset reference intact.
        public static List<RenameOp> FolderPlan(ObfProjectIndex idx, Dictionary<string, string> segmentMap, List<Pair> brandVariants, List<string> terms, ObfNameGenerator gen)
        {
            var ops = new List<RenameOp>();
            foreach (var dir in idx.dirs.Where(d => !idx.IsExcludedPath(d)).OrderByDescending(d => d.Count(c => c == '/')))
            {
                string leaf = Path.GetFileName(dir);
                if (leaf == "Resources" || leaf == "Editor" || leaf == "Plugins" || leaf == "StreamingAssets" || leaf == "Gizmos" || leaf == "Standard Assets" || leaf.StartsWith(".")) continue;
                if (dir.Split('/').Length <= 2 && leaf == "Assets") continue;
                string newLeaf = MapFolderName(leaf, segmentMap, brandVariants, terms, gen);
                if (newLeaf != leaf) ops.Add(new RenameOp { path = dir, newStem = newLeaf, isDir = true });
            }
            return ops;
        }

        public static string MapFolderName(string leaf, Dictionary<string, string> segmentMap, List<Pair> brandVariants, List<string> terms, ObfNameGenerator gen)
        {
            var segs = leaf.Split('.');
            bool changed = false;
            for (int i = 0; i < segs.Length; i++)
            {
                if (segmentMap != null && segmentMap.TryGetValue(segs[i], out var nv)) { segs[i] = nv; changed = true; continue; }
                string b = brandVariants != null ? Apply(segs[i], brandVariants) : segs[i];
                if (b != segs[i]) { segs[i] = b; changed = true; continue; }
                if (terms != null && gen != null && FirstTermIn(segs[i], terms) != null) { segs[i] = gen.ForClass(segs[i]); changed = true; }
            }
            return changed ? string.Join(".", segs) : leaf;
        }

        public static bool ContainsAny(string s, List<Pair> variants)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var v in variants) if (s.IndexOf(v.from, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public struct RenameOp { public string path; public string newStem; public bool isDir; }

        /// Files + folders whose name contains a brand variant, deepest first (pure; worker-safe).
        public static List<RenameOp> RenamePlan(ObfProjectIndex idx, List<Pair> variants)
        {
            var ops = new List<RenameOp>();
            if (variants.Count == 0) return ops;
            var paths = idx.files.Where(f => !f.isMeta && !f.excluded && !NeverRenameExt.Contains(f.ext)).Select(f => f.path)
                .Concat(idx.dirs.Where(d => !idx.IsExcludedPath(d)))
                .Distinct()
                .OrderByDescending(ap => ap.Count(c => c == '/'))
                .ToList();
            var dirSet = new HashSet<string>(idx.dirs, StringComparer.Ordinal);
            foreach (var ap in paths)
            {
                bool isDir = dirSet.Contains(ap);
                string leaf = Path.GetFileName(ap);
                string stem = isDir ? leaf : Path.GetFileNameWithoutExtension(leaf);
                if (stem == "Resources" || stem == "Editor" || stem == "Plugins" || stem == "StreamingAssets" || stem == "Gizmos") continue;
                string newStem = Apply(stem, variants);
                if (newStem == stem) continue;
                ops.Add(new RenameOp { path = ap, newStem = newStem, isDir = isDir });
            }
            return ops;
        }

        /// Product / company / bundle id. Empty override => brand-substitute the current value. Main thread.
        public static void ApplyPlayerSettings(List<Pair> variants, string product, string company, string bundleId, List<string> log)
        {
#pragma warning disable 618
            string p = string.IsNullOrEmpty(product) ? Apply(PlayerSettings.productName, variants) : product;
            string c = string.IsNullOrEmpty(company) ? Apply(PlayerSettings.companyName, variants) : company;
            if (p != PlayerSettings.productName) { log.Add("PLAYER productName: " + PlayerSettings.productName + " -> " + p); PlayerSettings.productName = p; }
            if (c != PlayerSettings.companyName) { log.Add("PLAYER companyName: " + PlayerSettings.companyName + " -> " + c); PlayerSettings.companyName = c; }
            foreach (var g in new[] { BuildTargetGroup.Android, BuildTargetGroup.iOS, BuildTargetGroup.Standalone })
            {
                string cur = PlayerSettings.GetApplicationIdentifier(g);
                string nv = string.IsNullOrEmpty(bundleId) ? Apply(cur, variants).ToLowerInvariant().Replace(" ", "") : bundleId;
                if (!string.IsNullOrEmpty(nv) && nv != cur) { PlayerSettings.SetApplicationIdentifier(g, nv); log.Add("PLAYER bundle id (" + g + "): " + cur + " -> " + nv); }
            }
#pragma warning restore 618
            // v3.4: the Unity Cloud project name ("projectName:" in ProjectSettings.asset) ships inside the build's
            // PlayerSettings object and still carried the old product name after every run. It is not exposed by PlayerSettings,
            // so it is edited through the serialized asset. Empty product override => brand-substitute it.
            try
            {
                var so = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
                var prop = so.FindProperty("projectName");
                if (prop != null)
                {
                    string cur = prop.stringValue ?? "";
                    string nv = string.IsNullOrEmpty(product) ? Apply(cur, variants) : product;
                    if (cur.Length > 0 && nv != cur) { prop.stringValue = nv; so.ApplyModifiedPropertiesWithoutUndo(); log.Add("PLAYER cloud projectName: " + cur + " -> " + nv); }
                }
            }
            catch (Exception ex) { log.Add("PLAYER cloud projectName not updated: " + ex.Message); }
            AssetDatabase.SaveAssets();
        }

        /// Replays RENAME records over a path (handles file rename followed by parent-folder rename).
        public static string FinalPath(string assetPath, IEnumerable<string> manifest)
        {
            foreach (var l in manifest)
            {
                var p = l.Split('|');
                if (p[0] != "RENAME" || p.Length != 3) continue;
                if (assetPath == p[1]) assetPath = p[2];
                else if (assetPath.StartsWith(p[1] + "/", StringComparison.Ordinal)) assetPath = p[2] + assetPath.Substring(p[1].Length);
            }
            return assetPath;
        }

        public static void FixBuildSettings(List<string> manifest, List<string> log)
        {
            var scenes = EditorBuildSettings.scenes;
            bool changed = false;
            for (int i = 0; i < scenes.Length; i++)
            {
                string np = FinalPath(scenes[i].path, manifest);
                if (np != scenes[i].path) { log.Add("BUILD  scene " + scenes[i].path + " -> " + np); scenes[i] = new EditorBuildSettingsScene(np, scenes[i].enabled); changed = true; }
            }
            if (changed) EditorBuildSettings.scenes = scenes;
        }

        // ---- casing helpers ----
        enum Case { Pascal, Camel, Lower, Upper }

        static List<string> Words(string s)
        {
            // split on separators and camel boundaries: "SpaceBlaster" -> Space, Blaster ; "space blaster" -> space, blaster
            var parts = Regex.Split(s, @"[\s_\-.]+").Where(x => x.Length > 0);
            var words = new List<string>();
            foreach (var p in parts)
                foreach (Match m in Regex.Matches(p, @"[A-Z]+(?![a-z])|[A-Z]?[a-z0-9]+|[0-9]+"))
                    words.Add(m.Value);
            return words;
        }

        static string Join(List<string> w, string sep, Case c)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < w.Count; i++)
            {
                string x = w[i].ToLowerInvariant();
                switch (c)
                {
                    case Case.Pascal: x = char.ToUpperInvariant(x[0]) + x.Substring(1); break;
                    case Case.Camel:  x = i == 0 ? x : char.ToUpperInvariant(x[0]) + x.Substring(1); break;
                    case Case.Upper:  x = x.ToUpperInvariant(); break;
                }
                if (i > 0) sb.Append(sep);
                sb.Append(x);
            }
            return sb.ToString();
        }
    }
}
