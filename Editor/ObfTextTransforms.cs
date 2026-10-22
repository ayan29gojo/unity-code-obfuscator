// Code Obfuscator — per-file pure text transforms (stage: Transform).
//
// Every function here is `string in → string out (or null = unchanged)`, uses no Unity API and
// touches no shared mutable state, so it can run on the worker thread under the watchdog.
// All regexes go through ObfRegex (match timeout); the declaration work uses the linear finder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeObfuscator
{
    /// The rename maps for one Apply run, derived once from the reviewed ScanResult.
    public class ObfApplyMaps
    {
        public Dictionary<string, string> idMap = new Dictionary<string, string>();
        public Dictionary<string, string> nsMap, classMap, enumMap, methodMap, fieldMap, typeMap;
        public Dictionary<string, string> resourceMap, shaderMap, goMap, ppMap, assetFileMap, sceneStemMap;
        public Dictionary<string, string> brandIdMap, asmMap, folderMap;   // v3.4
        public HashSet<string> serializedFieldNames;
        /// v3.4: strings a safety rule decided to keep (old names of disabled / review-only entries) — never rewritten inside literals.
        public HashSet<string> keepLiterals = new HashSet<string>(StringComparer.Ordinal);
        public List<string> brandTerms = new List<string>();
        public List<ObfBrand.Pair> brand = new List<ObfBrand.Pair>();
        public ObfSettings settings;

        public static ObfApplyMaps Build(ScanResult res, ObfSettings s, List<string> warnings, ObfProjectIndex idx = null)
        {
            var m = new ObfApplyMaps { settings = s, serializedFieldNames = res.serializedFieldNames };
            // v3.4.1: a literal that equals the name of an existing asset is a lookup key (Resources.Load(ResourcePath) with
            // a const, .name comparisons, Find paths) — never rewrite it because it also happens to be a class name.
            // ThemeLibrary: class renamed, const "ThemeLibrary" followed, Resources asset kept → null library.
            if (idx != null)
            {
                foreach (var kv in idx.resources)
                {
                    m.keepLiterals.Add(kv.Key);
                    int sl = kv.Key.LastIndexOf('/'); if (sl >= 0) m.keepLiterals.Add(kv.Key.Substring(sl + 1));
                }
                foreach (var f in idx.files)
                {
                    if (f.isMeta || f.ext == ".cs") continue;   // script stems are the class names themselves — not keys
                    string leaf = f.path.Substring(f.path.LastIndexOf('/') + 1);
                    int dot = leaf.IndexOf('.');
                    m.keepLiterals.Add(dot > 0 ? leaf.Substring(0, dot) : leaf);
                }
            }
            // v4: profile "keep literals" — lookup keys the user knows about (analytics events, save keys, atlas pages …)
            foreach (var kl in s.keepLiterals ?? new string[0]) if (!string.IsNullOrEmpty(kl)) m.keepLiterals.Add(kl);
            var empty = new Dictionary<string, string>();
            m.nsMap     = s.renameNamespaces  ? res.Map(ObfCategory.Namespace)  : new Dictionary<string, string>();
            m.classMap  = s.renameClasses     ? res.Map(ObfCategory.Class)      : new Dictionary<string, string>();
            m.enumMap   = s.renameEnumMembers ? res.Map(ObfCategory.EnumMember) : new Dictionary<string, string>();
            m.methodMap = s.renameMethods     ? res.Map(ObfCategory.Method)     : new Dictionary<string, string>();
            m.fieldMap  = s.renameFields      ? res.Map(ObfCategory.Field)      : new Dictionary<string, string>();
            m.brandIdMap = s.sweepBrandIdentifiers ? res.Map(ObfCategory.BrandIdentifier) : new Dictionary<string, string>();
            foreach (var map in new[] { m.nsMap, m.classMap, m.enumMap, m.methodMap, m.fieldMap, m.brandIdMap })
                foreach (var kv in map)
                {
                    if (m.idMap.TryGetValue(kv.Key, out var existing) && existing != kv.Value)
                        warnings.Add("conflicting targets for '" + kv.Key + "': kept " + existing + ", ignored " + kv.Value);
                    else m.idMap[kv.Key] = kv.Value;
                }
            m.typeMap = new Dictionary<string, string>(m.nsMap); foreach (var kv in m.classMap) m.typeMap[kv.Key] = kv.Value;
            m.resourceMap  = s.remapResources     ? res.Map(ObfCategory.ResourcePath)   : empty;
            m.shaderMap    = s.renameShaders      ? res.Map(ObfCategory.Shader)         : empty;
            m.goMap        = s.renameSceneObjects ? res.Map(ObfCategory.SceneObject)    : empty;
            m.ppMap        = s.renamePlayerPrefs  ? res.Map(ObfCategory.PlayerPrefsKey) : empty;
            m.assetFileMap = s.renameAssetFiles   ? res.Map(ObfCategory.AssetFile)      : empty;
            m.asmMap       = s.renameAssemblies   ? res.Map(ObfCategory.AssemblyName)   : empty;
            m.folderMap    = s.renameAssemblies   ? res.Map(ObfCategory.Folder)         : empty;
            m.sceneStemMap = m.assetFileMap.Where(kv => kv.Key.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                                           .GroupBy(kv => Path.GetFileNameWithoutExtension(kv.Key))
                                           .ToDictionary(g => g.Key, g => g.First().Value);
            foreach (var e in res.entries)
                if (!e.enabled || e.reviewOnly)
                {
                    // review-only names can be URLs / code snippets — no System.IO.Path calls here (they throw on ':' etc.)
                    string old = e.oldName ?? "";
                    m.keepLiterals.Add(old);
                    int slash = old.LastIndexOf('/'); string leaf = slash >= 0 ? old.Substring(slash + 1) : old;
                    m.keepLiterals.Add(leaf);
                    int dot = leaf.LastIndexOf('.'); if (dot > 0) m.keepLiterals.Add(leaf.Substring(0, dot));
                }
            m.brandTerms = res.brandTerms ?? new List<string>();
            if (s.brandPass)
            {
                var problems = new List<string>();
                var pairs = ObfBrand.ParsePairs(s.brandPairsText, problems);
                pairs.AddRange(ObfBrand.AutoPairs(s, warnings));   // v3.4: old product / company => new ones
                m.brand = ObfBrand.Variants(pairs);
                warnings.AddRange(problems);
            }
            // v4: exact custom rules ride on the brand stage (text files + file / folder names), longest first, no variants.
            var custom = ObfBrand.ParseCustom(s.customReplacements, warnings);
            if (custom.Count > 0)
            {
                var seen = new HashSet<string>(m.brand.Select(p => p.from), StringComparer.Ordinal);
                foreach (var c in custom) if (seen.Add(c.from)) m.brand.Add(c);
                m.brand = m.brand.OrderByDescending(p => p.from.Length).ToList();
            }
            return m;
        }
    }

    public static class ObfTextTransforms
    {
        // ---------------- C# ----------------
        public static string TransformCs(string orig, ObfApplyMaps m, List<string> warnings)
        {
            if (string.IsNullOrEmpty(orig)) return null;
            string txt = orig;
            if (m.settings.stripComments) txt = ObfCSharpText.StripComments(txt);

            txt = RemapCallString(txt, @"Resources\.(?:Load|LoadAsync|LoadAll)\s*(?:<[^>\n]*>)?\s*\(\s*", m.resourceMap);
            txt = RemapCallString(txt, @"Shader\.Find\s*\(\s*", m.shaderMap);
            // path-like literals ("Folder/Asset", "Vendor/Shader Name") are unmistakable: rewrite every quoted
            // occurrence so consts / field defaults that feed Resources.Load(k_Path) or Shader.Find(k_Name) stay valid
            txt = RemapPathLiterals(txt, m.resourceMap);
            txt = RemapPathLiterals(txt, m.shaderMap);
            txt = RemapCallString(txt, @"PlayerPrefs\.(?:GetInt|GetFloat|GetString|SetInt|SetFloat|SetString|HasKey|DeleteKey)\s*\(\s*", m.ppMap);
            txt = RemapCallString(txt, @"(?:StartCoroutine|StopCoroutine|Invoke|InvokeRepeating|CancelInvoke|IsInvoking|SendMessage|SendMessageUpwards|BroadcastMessage)\s*\(\s*", m.methodMap);
            txt = RemapCallString(txt, @"(?:GetComponent|AddComponent|GetComponentInChildren|GetComponentInParent)\s*\(\s*", m.classMap);
            // type names carried as strings — Type.GetType("Ns.Class, Assembly"), Activator.CreateInstance("…"):
            // GameController is constructed that way; an unsynced string leaves GameScene empty (v3.2)
            txt = RemapTypeNameLiterals(txt, m);
            txt = RemapPathString(txt, @"(?:GameObject\.Find|transform\.Find)\s*\(\s*", m.goMap);
            // v3.4.3: hierarchy paths held in a const / static readonly string and passed to Find(k_Path) — the literal
            // above only covers inline arguments. ShopPanel: "ScrollView/Viewport/Content" with 'ScrollView' renamed →
            // Content never found → empty shop. Every '/' segment is mapped when the const feeds a Find() call.
            if (m.goMap.Count > 0) txt = RemapFindConstPaths(txt, m.goMap);
            txt = RemapCallString(txt, @"(?:LoadScene|LoadSceneAsync|LoadLevel|LoadLevelAsync|LoadLevelAdditive)\s*\(\s*", m.sceneStemMap);

            // Every renamed identifier, not only the Field category: a field named like its type
            // (`[SerializeField] LevelData LevelData;`) is renamed by the CLASS map, and without
            // [FormerlySerializedAs] the value stored in the prefab is silently lost (blank splash, silent hang).
            if (m.idMap.Count > 0) txt = InjectFormerlySerializedAs(txt, m.idMap, warnings);
            txt = ObfCSharpText.TransformIdentifiers(txt, m.idMap, out _);
            // v3.4: "[OrionManager] init failed", "Could not find OrionManager in scene" — class names inside literals
            // survived every run and are the most readable fingerprint in a metadata dump. Whole-word, compound
            // PascalCase names only; a literal a safety rule decided to keep is never touched.
            if (m.settings.syncClassLiterals && m.classMap.Count > 0) txt = SyncClassLiterals(txt, m);
            if (m.asmMap.Count > 0) txt = RemapAssemblyNames(txt, m.asmMap);
            if (m.settings.injectUnusedVars) txt = InjectUnused(txt);
            return txt == orig ? null : txt;
        }

        // ---------------- v3.4: link.xml — <assembly fullname="Acme.Orion"> ----------------
        public static string TransformLinkXml(string orig, ObfApplyMaps m)
        {
            if (string.IsNullOrEmpty(orig) || m.asmMap.Count == 0) return null;
            string txt = RemapAssemblyNames(orig, m.asmMap);
            return txt == orig ? null : txt;
        }

        // ---------------- shaders: inner name + cross-references ----------------
        public static string TransformShader(string orig, ObfApplyMaps m)
        {
            if (string.IsNullOrEmpty(orig) || m.shaderMap.Count == 0) return null;
            string txt = orig;
            foreach (var kv in m.shaderMap)
            {
                string o = Regex.Escape(kv.Key), n = ObfRegex.Repl(kv.Value);
                txt = ObfRegex.Replace(txt, "^(\\s*Shader\\s+)\"" + o + "\"", "$1\"" + n + "\"", RegexOptions.Multiline);
                txt = ObfRegex.Replace(txt, "(\\b(?:Fallback|UsePass)\\s+)\"" + o + "(/[^\"]*)?\"", "$1\"" + n + "$2\"");
            }
            return txt == orig ? null : txt;
        }

        // ---------------- YAML: scenes, prefabs, .asset ----------------
        public static string TransformYaml(string orig, ObfApplyMaps m, List<string> warnings, string path)
        {
            if (string.IsNullOrEmpty(orig)) return null;
            if (!orig.StartsWith("%YAML")) { warnings.Add("binary-serialized (Edit ▸ Project Settings ▸ Editor ▸ Asset Serialization = Force Text), skipped: " + path); return null; }
            string txt = orig;
            if (m.goMap.Count > 0) txt = RemapGameObjectNames(txt, m.goMap);
            if (m.methodMap.Count > 0) txt = ObfRegex.Replace(txt, @"(\bm_MethodName:\s*)(\w+)", mm => mm.Groups[1].Value + Map(m.methodMap, mm.Groups[2].Value));
            if (m.typeMap.Count > 0)
            {
                txt = ObfRegex.Replace(txt, @"(\bclass:\s*)([A-Za-z_]\w*)", mm => mm.Groups[1].Value + Map(m.classMap, mm.Groups[2].Value));
                txt = ObfRegex.Replace(txt, @"(\bns:\s*)([\w.]*)", mm => mm.Groups[1].Value + MapDotted(m.nsMap, mm.Groups[2].Value));
                txt = ObfRegex.Replace(txt, @"(\bm_TargetAssemblyTypeName:\s*)([\w./+]+)(,)", mm => mm.Groups[1].Value + MapTypeName(m.typeMap, mm.Groups[2].Value) + mm.Groups[3].Value);
                // any other string field holding a type name (`EventTypeName: ItemCollectedEvent`, `TypeName: Ns.Foo, Asm`)
                // is resolved with Type.GetType at runtime (v3.2). m_Name is the SceneObject map's business.
                txt = ObfRegex.Replace(txt, @"(\n[ \t]*(?:- )?(?!m_Name\b)[A-Za-z_]\w*:[ \t]+)([A-Za-z_][\w+]*(?:\.[A-Za-z_][\w+]*)*)((?:,[^\r\n]*)?)(?=\r?\n)", mm =>
                {
                    string v = mm.Groups[2].Value, nv = MapTypeString(m, v);
                    return nv == v ? mm.Value : mm.Groups[1].Value + nv + mm.Groups[3].Value;
                });
            }
            if (m.brandIdMap.Count > 0) txt = ObfRegex.Replace(txt, @"(\bm_MethodName:\s*)(\w+)", mm => mm.Groups[1].Value + Map(m.brandIdMap, mm.Groups[2].Value));
            if (m.asmMap.Count > 0) txt = RemapAssemblyNames(txt, m.asmMap);   // "Ns.Type, Acme.Orion" tails (v3.4)
            // v3.4.2: data-driven reflection. Tutorial step assets name a component FIELD to read (`TargetFieldName: m_SubType`),
            // an expected enum VALUE compared through ToString (`TargetFieldValue: PowerUp_1`, `Value: OneShot`) and an enum type
            // (`FieldType: enum:Ns.PowerUpType`). The code side was renamed, the data was not → the step never resolves and the
            // tutorial blocks all input. Only exact matches of a renamed name are touched.
            if (m.idMap.Count > 0)
                txt = ObfRegex.Replace(txt, @"(\n[ \t]*(?:- )?(?:Target|DragEnd)?(?:Field|Property|Member)Name:[ \t]+)([A-Za-z_]\w*)(?=[ \t]*\r?\n)", mm => mm.Groups[1].Value + Map(m.idMap, mm.Groups[2].Value));
            if (m.enumMap.Count > 0)
                txt = ObfRegex.Replace(txt, @"(\n[ \t]*(?:- )?(?:TargetFieldValue|DragEndFieldValue|Value):[ \t]+)([A-Za-z_]\w*)(?=[ \t]*\r?\n)", mm => mm.Groups[1].Value + Map(m.enumMap, mm.Groups[2].Value));
            if (m.typeMap.Count > 0)
                txt = ObfRegex.Replace(txt, @"(\bFieldType:[ \t]*enum:)([\w.+/]+)", mm => mm.Groups[1].Value + MapTypeName(m.typeMap, mm.Groups[2].Value));
            return txt == orig ? null : txt;
        }

        // ---------------- .anim: curve paths + AnimationEvents ----------------
        public static string TransformAnim(string orig, ObfApplyMaps m)
        {
            if (string.IsNullOrEmpty(orig) || !orig.StartsWith("%YAML")) return null;
            string txt = orig;
            if (m.goMap.Count > 0) txt = ObfRegex.Replace(txt, @"(\b(?:path|m_Path):[ \t]*)([^\r\n]+)", mm => mm.Groups[1].Value + MapPath(m.goMap, mm.Groups[2].Value.Trim()));
            if (m.methodMap.Count > 0) txt = ObfRegex.Replace(txt, @"(\bfunctionName:\s*)(\w+)", mm => mm.Groups[1].Value + Map(m.methodMap, mm.Groups[2].Value));
            // animated script fields are bound by serialized field name ("attribute: speed" / "attribute: cfg.speed")
            if (m.idMap.Count > 0) txt = ObfRegex.Replace(txt, @"(\battribute:[ \t]*)([\w.]+)", mm => mm.Groups[1].Value + MapDotted(m.idMap, mm.Groups[2].Value));
            return txt == orig ? null : txt;
        }

        // ---------------- asmdef rootNamespace ----------------
        public static string TransformAsmdef(string orig, ObfApplyMaps m)
        {
            if (string.IsNullOrEmpty(orig) || (m.nsMap.Count == 0 && m.asmMap.Count == 0)) return null;
            string txt = orig;
            if (m.nsMap.Count > 0)
                txt = ObfRegex.Replace(txt, "(\"rootNamespace\"\\s*:\\s*\")([^\"]*)(\")", mm => mm.Groups[1].Value + MapDotted(m.nsMap, mm.Groups[2].Value) + mm.Groups[3].Value);
            if (m.asmMap.Count > 0)
            {
                // v3.4: the assembly's own name + by-name references to renamed assemblies ("GUID:…" references are untouched)
                txt = ObfRegex.Replace(txt, "(\"name\"\\s*:\\s*\")([^\"]*)(\")", mm => mm.Groups[1].Value + Map(m.asmMap, mm.Groups[2].Value) + mm.Groups[3].Value);
                txt = ObfRegex.Replace(txt, "(\"references\"\\s*:\\s*\\[)([^\\]]*)(\\])", mm =>
                    mm.Groups[1].Value + ObfRegex.Replace(mm.Groups[2].Value, "\"([^\"]+)\"", r => "\"" + Map(m.asmMap, r.Groups[1].Value) + "\"") + mm.Groups[3].Value);
            }
            return txt == orig ? null : txt;
        }

        // ---------------- brand pass: plain substring substitution over any text file ----------------
        public static string TransformBrandText(string orig, ObfApplyMaps m) => TransformBrandText(orig, m, null, "");

        /// v3.4: URLs, reverse-DNS package ids and Java class names are skipped and reported in `review`
        /// ("file | span") — the live backend and native plugin lookups must not be rewritten blindly.
        public static string TransformBrandText(string orig, ObfApplyMaps m, List<string> review, string path)
        {
            if (string.IsNullOrEmpty(orig) || m.brand.Count == 0) return null;
            if (orig.IndexOf('\0') >= 0) return null; // binary-serialized asset with a text extension
            string txt = ObfBrand.ApplyGuarded(orig, m.brand, review, path);
            return txt == orig ? null : txt;
        }

        // ---------------- v3.4 helpers ----------------
        static readonly Regex LiteralRe = new Regex(@"@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""", RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        static readonly Regex PascalTokenRe = new Regex(@"[A-Z][A-Za-z0-9_]{5,}", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        /// Renamed class names inside string literals, whole-word. Only compound PascalCase names (≥ 6 chars) qualify,
        /// and a literal equal to a name some safety rule kept (asset / scene-object / resource key) is left alone.
        public static string SyncClassLiterals(string txt, ObfApplyMaps m)
        {
            if (txt.IndexOf('"') < 0) return txt;
            try
            {
                return LiteralRe.Replace(txt, lit =>
                {
                    string s = lit.Value;
                    bool verbatim = s[0] == '@';
                    string inner = verbatim ? s.Substring(2, s.Length - 3) : s.Substring(1, s.Length - 2);
                    if (inner.Length < 6 || m.keepLiterals.Contains(inner)) return s;
                    // [FormerlySerializedAs("LevelData")] names the OLD serialized name on purpose — a field named
                    // like its type would lose its prefab value if that string followed the class rename (v3.4 boot hang).
                    int back = Math.Max(0, lit.Index - 40);
                    if (txt.LastIndexOf("FormerlySerializedAs", lit.Index, lit.Index - back, StringComparison.Ordinal) >= 0) return s;
                    string changed = PascalTokenRe.Replace(inner, tok =>
                    {
                        if (!m.classMap.TryGetValue(tok.Value, out var nv) || !IsCompoundTypeName(tok.Value)) return tok.Value;
                        // whole word: not glued to identifier chars on either side
                        int a = tok.Index, b = tok.Index + tok.Length;
                        if (a > 0 && (char.IsLetterOrDigit(inner[a - 1]) || inner[a - 1] == '_')) return tok.Value;
                        if (b < inner.Length && (char.IsLetterOrDigit(inner[b]) || inner[b] == '_')) return tok.Value;
                        return nv;
                    });
                    if (changed == inner) return s;
                    return verbatim ? "@\"" + changed + "\"" : "\"" + changed + "\"";
                });
            }
            catch (RegexMatchTimeoutException) { return txt; }
        }

        static readonly Regex ConstPathRe = new Regex(@"(\b(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*"")([^""\n]*/[^""\n]*)("")", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        /// `const string k_Path = "A/B/C"` that some Find(k_Path) in the same file consumes: each segment mapped by the
        /// scene-object map (whole-segment matches only).
        public static string RemapFindConstPaths(string txt, Dictionary<string, string> goMap)
        {
            if (txt.IndexOf('/') < 0 || txt.IndexOf("Find(", StringComparison.Ordinal) < 0) return txt;
            try
            {
                return ConstPathRe.Replace(txt, m =>
                {
                    string name = m.Groups[2].Value, value = m.Groups[3].Value;
                    if (!ObfRegex.IsMatch(txt, @"\bFind\s*\(\s*" + Regex.Escape(name) + @"\s*[,)]")) return m.Value;
                    string nv = MapPath(goMap, value);
                    return nv == value ? m.Value : m.Groups[1].Value + nv + m.Groups[4].Value;
                });
            }
            catch (RegexMatchTimeoutException) { return txt; }
        }

        /// "Acme.Orion" wherever it stands as a whole quoted value or as the assembly tail of an assembly-qualified
        /// type name: "Ns.Type, Acme.Orion", "…, Acme.Orion, Version=…", fullname="Acme.Orion", InternalsVisibleTo("Acme.Orion").
        public static string RemapAssemblyNames(string txt, Dictionary<string, string> asmMap)
        {
            foreach (var kv in asmMap)
                txt = ObfRegex.Replace(txt, @"(?<=[""',]\s*)" + Regex.Escape(kv.Key) + @"(?=\s*[""',])", ObfRegex.Repl(kv.Value));
            return txt;
        }

        // ---------------- helpers ----------------
        public static string Map(Dictionary<string, string> m, string s) => m.TryGetValue(s, out var v) ? v : s;
        public static string MapDotted(Dictionary<string, string> m, string dotted) => string.Join(".", dotted.Split('.').Select(seg => Map(m, seg)));
        public static string MapPath(Dictionary<string, string> m, string path) => string.Join("/", path.Split('/').Select(seg => Map(m, seg)));
        // "Ns.Sub.Outer/Nested"  — namespaces + type names, Unity nests types with '/' and '+'
        public static string MapTypeName(Dictionary<string, string> m, string t) =>
            string.Join(".", t.Split('.').Select(seg => string.Join("/", seg.Split('/').Select(s2 => string.Join("+", s2.Split('+').Select(s3 => Map(m, s3)))))));

        /// A string that names a type. "Ns.Sub.Type" is mapped when its first segment is a renamed namespace
        /// (unmistakably one of ours); a bare "SomeEventClass" only when it is a compound PascalCase class name,
        /// so UI words that happen to equal a short class name ("Level", "Coin") are never rewritten.
        public static string MapTypeString(ObfApplyMaps m, string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int dot = s.IndexOf('.');
            if (dot > 0) return m.nsMap.ContainsKey(s.Substring(0, dot)) ? MapTypeName(m.typeMap, s) : s;
            return m.classMap.TryGetValue(s, out var nv) && IsCompoundTypeName(s) ? nv : s;
        }
        static bool IsCompoundTypeName(string s) => s.Length >= 6 && ObfRegex.IsMatch(s, @"^[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+$");

        // Type names carried as string literals in C#: Type.GetType("Ns.Class, Assembly"), Assembly.GetType("…"),
        // Activator.CreateInstance("…"). Dotted literals are mapped wherever they start with a renamed namespace;
        // a bare class name only as the argument of a type-resolving call.
        static string RemapTypeNameLiterals(string text, ObfApplyMaps m)
        {
            if (m.typeMap.Count == 0 || text.IndexOf('"') < 0) return text;
            text = ObfRegex.Replace(text, @"""([A-Za-z_][\w+]*(?:\.[A-Za-z_][\w+]*)+)((?:\s*,\s*[^""\n]*)?)""", mm =>
            {
                string v = mm.Groups[1].Value, nv = MapTypeString(m, v);
                return nv == v ? mm.Value : "\"" + nv + mm.Groups[2].Value + "\"";
            });
            if (m.classMap.Count > 0)
                text = ObfRegex.Replace(text, @"((?:Type\.GetType|Assembly\.GetType|\.GetType|GetTypeByName|CreateInstance|GetNestedType)\s*\(\s*"")([A-Za-z_]\w*)("")",
                    mm => mm.Groups[1].Value + Map(m.classMap, mm.Groups[2].Value) + mm.Groups[3].Value);
            return text;
        }

        static string RemapCallString(string text, string prefix, Dictionary<string, string> map)
        {
            if (map.Count == 0) return text;
            return ObfRegex.Replace(text, prefix + "\"([^\"\\n]+)\"", mm =>
            {
                string key = mm.Groups[1].Value;
                return map.TryGetValue(key, out var nv) ? mm.Value.Substring(0, mm.Value.Length - key.Length - 1) + nv + "\"" : mm.Value;
            });
        }

        static string RemapPathLiterals(string text, Dictionary<string, string> map)
        {
            if (map.Count == 0 || text.IndexOf('"') < 0) return text;
            foreach (var kv in map)
            {
                if (kv.Key.IndexOf('/') < 0) continue;
                string q = "\"" + kv.Key + "\"";
                if (text.IndexOf(q, StringComparison.Ordinal) >= 0) text = text.Replace(q, "\"" + kv.Value + "\"");
            }
            return text;
        }

        // like RemapCallString but the literal is a '/'-separated hierarchy path; each segment is mapped
        static string RemapPathString(string text, string prefix, Dictionary<string, string> map)
        {
            if (map.Count == 0) return text;
            return ObfRegex.Replace(text, prefix + "\"([^\"\\n]+)\"", mm =>
            {
                string key = mm.Groups[1].Value, nv = MapPath(map, key);
                return nv == key ? mm.Value : mm.Value.Substring(0, mm.Value.Length - key.Length - 1) + nv + "\"";
            });
        }

        static string RemapGameObjectNames(string yaml, Dictionary<string, string> map)
        {
            string[] parts = yaml.Split(new[] { "--- !u!" }, StringSplitOptions.None);
            var sb = new StringBuilder(yaml.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string d = parts[i];
                if (i > 0) sb.Append("--- !u!");
                if (i > 0 && d.StartsWith("1 "))
                    d = ObfRegex.Replace(d, @"(\n[ \t]*m_Name:[ \t]*)([^\r\n]+)", mm =>
                    {
                        string name = mm.Groups[2].Value.Trim();
                        return map.TryGetValue(name, out var nv) ? mm.Groups[1].Value + nv : mm.Value;
                    });
                sb.Append(d);
            }
            return sb.ToString();
        }

        // Adds [FormerlySerializedAs("old")] in front of renamed fields that Unity serializes, so
        // scene/prefab values survive the rename. Linear: one pass over the declarations found by
        // ObfCSharpText.FindDeclarations (v2 compiled one backtracking regex per renamed field and ran
        // it over every file — the root cause of the multi-hour hang).
        // Keys Unity writes into every serialized object's YAML regardless of the script's own fields.
        const string k_UnityReservedKeys = "m_Enabled|m_Name|m_Script|m_GameObject|m_ObjectHideFlags|m_EditorHideFlags|m_EditorClassIdentifier|m_CorrespondingSourceObject|m_PrefabInstance|m_PrefabAsset";

        static string InjectFormerlySerializedAs(string txt, Dictionary<string, string> renamed, List<string> warnings)
        {
            string aligned = ObfCSharpText.CodeOnlyAligned(txt);
            var decls = ObfCSharpText.FindDeclarations(aligned);
            var inserts = new List<KeyValuePair<int, string>>();
            foreach (var d in decls)
            {
                if (d.kind != ObfCSharpText.DeclKind.Field) continue;
                if (!renamed.ContainsKey(d.name)) continue;
                if (d.modifiers.Contains("static") || d.modifiers.Contains("const") || d.modifiers.Contains("readonly") || d.modifiers.Contains("event")) continue;
                // Unity serializes public fields and [SerializeField]/[SerializeReference] fields; skip [NonSerialized]
                bool serialized = (d.isPublic || d.attributes.Contains("SerializeField") || d.attributes.Contains("SerializeReference")) && !d.attributes.Contains("NonSerialized");
                if (!serialized) continue;
                if (aligned.IndexOf("FormerlySerializedAs(\"" + d.name + "\")", StringComparison.Ordinal) >= 0) continue; // already there
                if (d.multiDeclarator) { warnings.Add("multi-declarator field '" + d.name + "' renamed without FormerlySerializedAs (split 'int a, b;' by hand)"); continue; }
                inserts.Add(new KeyValuePair<int, string>(d.insertAt, "[FormerlySerializedAs(\"" + d.name + "\")] "));
            }
            if (inserts.Count == 0) return txt;
            var sb = new StringBuilder(txt.Length + inserts.Count * 48);
            int last = 0;
            foreach (var ins in inserts.OrderBy(k => k.Key))
            {
                sb.Append(txt, last, ins.Key - last).Append(ins.Value);
                last = ins.Key;
            }
            sb.Append(txt, last, txt.Length - last);
            string result = sb.ToString();
            // v3.3: a pre-existing [FormerlySerializedAs("m_Enabled")] (or any other name Unity itself
            // always writes into every object's YAML) must not survive next to the injected one. Once
            // the field no longer matches by its own name Unity falls back to the former names, and
            // `m_Enabled: 1` (the built-in Object flag) exists in every asset — so the feature
            // "enabled" flag read as true even where the asset stored `m_IsEnabled: 0`
            // (several disabled features switched themselves on).
            string before = result;
            result = ObfRegex.Replace(result, @"^[ \t]*\[FormerlySerializedAs\(""(?:" + k_UnityReservedKeys + @")""\)\][ \t]*\r?\n", "", RegexOptions.Multiline);
            result = ObfRegex.Replace(result, @"\[FormerlySerializedAs\(""(?:" + k_UnityReservedKeys + @")""\)\][ \t]*", "");
            if (!ReferenceEquals(before, result) && before != result)
                warnings.Add("dropped a [FormerlySerializedAs] pointing at a Unity built-in key (m_Enabled / m_Name / m_Script …) — it would shadow the renamed field's real value");
            if (!ObfRegex.IsMatch(result, @"^\s*using\s+UnityEngine\.Serialization\s*;", RegexOptions.Multiline))
            {
                var lastUsing = ObfRegex.Matches(result, @"^[ \t]*using\s+[\w.]+\s*;[ \t]*\r?\n", RegexOptions.Multiline).Cast<Match>().LastOrDefault();
                int at = lastUsing != null ? lastUsing.Index + lastUsing.Length : 0;
                result = result.Substring(0, at) + "using UnityEngine.Serialization;\n" + result.Substring(at);
            }
            return result;
        }

        static string InjectUnused(string txt)
        {
            var m = ObfRegex.Match(ObfCSharpText.CodeOnly(txt), @"(?<![\w.])(?:class|struct)\s+[A-Za-z_]\w*[^\n{;]*\{");
            if (!m.Success) return txt;
            // CodeOnly does not preserve offsets — locate the same declaration in the real text
            string cls = ObfRegex.Match(m.Value, @"(?:class|struct)\s+([A-Za-z_]\w*)").Groups[1].Value;
            var real = ObfRegex.Match(txt, @"(?<![\w.])(?:class|struct)\s+" + Regex.Escape(cls) + @"\b[^\n{;]*\{");
            if (!real.Success) return txt;
            int at = real.Index + real.Length;
            string name = "_prState" + (Math.Abs(txt.GetHashCode()) % 9973);
            int guard = 0;
            while (txt.IndexOf(name, StringComparison.Ordinal) >= 0 && guard++ < 32) name += "x";
            return txt.Substring(0, at) + "\n        private static int " + name + ";\n" + txt.Substring(at); // static: legal in static classes & structs
        }
    }
}
