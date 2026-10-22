// Code Obfuscator — project scanner (stage: Analyze).
//
// Pure: works only on the ObfProjectIndex snapshot + file contents, so it runs on the worker thread
// under the pipeline's deadline and cancellation token. No AssetDatabase, no EditorPrefs.
//
// All declaration/usage analysis runs on ObfCSharpText.CodeOnly(text) so comments and string
// contents can never produce false positives. Excluded folders (SDKs, plugins) are never
// scanned for declarations and never edited; their identifiers are only RESERVED so generated
// names cannot collide with them.
//
// v3 complexity fixes (each of these was a hidden multi-minute / multi-hour pass in v2):
//   • method/field declarations: linear line-based finder instead of nested-quantifier regexes
//   • enum-member "foreign owner" test: one corpus pass building qualifier→member pairs, O(1) lookup
//   • string-literal counts: one linear pass building a literal→count table instead of one
//     IndexOf sweep over the whole corpus per candidate
//   • every regex has a match timeout; a file that still times out is skipped and reported
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodeObfuscator
{
    public static class ObfScanner
    {
        // C# keywords + contextual keywords. A textual rename must never touch these: v2 turned
        // `where T : class` into `RapidUnit2 T : class` because `class\s+where` matched its type regex.
        static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal",
            "default","delegate","do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for",
            "foreach","goto","if","implicit","in","int","interface","internal","is","lock","long","namespace","new","null","object",
            "operator","out","override","params","private","protected","public","readonly","ref","return","sbyte","sealed","short",
            "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked",
            "unsafe","ushort","using","virtual","void","volatile","while",
            "add","alias","and","ascending","args","async","await","by","descending","dynamic","equals","file","from","get","global",
            "group","init","into","join","let","managed","nameof","nint","not","notnull","nuint","on","or","orderby","partial",
            "record","remove","required","scoped","select","set","unmanaged","value","var","when","where","with","yield"
        };

        // Public member names of every assembly that is NOT one of the project's own script assemblies are
        // off-limits: a game method called `Play` / `LogEvent` / `Forget` or a field called `order` would otherwise
        // be renamed inside `audioSource.Play()`, `FirebaseAnalytics.LogEvent(...)`, `task.Forget()`,
        // `[CreateAssetMenu(order = 1)]`. SDK DLLs (Firebase, UniTask, DOTween…) never show those names as text.
        static readonly string[] OwnAssemblyNames = { "Assembly-CSharp", "Assembly-CSharp-Editor", "Assembly-CSharp-firstpass", "Assembly-CSharp-Editor-firstpass" };

        static readonly HashSet<string> Reserved = new HashSet<string>
        {
            "MonoBehaviour","ScriptableObject","GameObject","Transform","Vector2","Vector3","Vector4","Quaternion",
            "Mesh","Material","Texture","Texture2D","Shader","Color","Rect","Bounds","Object","Component","Behaviour",
            "Collider","Rigidbody","Camera","Sprite","AudioClip","AudioSource","Animator","Animation","Coroutine",
            "IEnumerator","List","Dictionary","HashSet","Action","Func","String","Int32","Single","Boolean","Math",
            "Mathf","Debug","Time","Input","Application","Resources","Void","Task","Nullable","Vector2Int",
            "Vector3Int","LayerMask","Ray","RaycastHit","Gradient","Editor","EditorWindow",
            "Awake","Start","Update","FixedUpdate","LateUpdate","OnEnable","OnDisable","OnDestroy","Reset",
            "OnApplicationPause","OnApplicationQuit","OnApplicationFocus","OnValidate","OnGUI","OnDrawGizmos",
            "OnDrawGizmosSelected","OnTriggerEnter","OnTriggerExit","OnTriggerStay","OnCollisionEnter",
            "OnCollisionExit","OnCollisionStay","OnTriggerEnter2D","OnTriggerExit2D","OnTriggerStay2D",
            "OnCollisionEnter2D","OnCollisionExit2D","OnCollisionStay2D","OnMouseDown","OnMouseUp","OnMouseEnter",
            "OnMouseExit","OnMouseDrag","OnMouseOver","OnBecameVisible","OnBecameInvisible","OnAnimatorIK",
            "OnAnimatorMove","OnPreRender","OnPostRender","OnRenderImage","OnRenderObject","OnWillRenderObject",
            "OnLevelWasLoaded","OnParticleCollision","OnJointBreak","OnTransformChildrenChanged",
            "OnTransformParentChanged","OnRectTransformDimensionsChange","OnBeforeTransformParentChanged",
            "OnCanvasGroupChanged","OnAudioFilterRead","OnPointerClick","OnPointerDown","OnPointerUp",
            "OnPointerEnter","OnPointerExit","OnDrag","OnBeginDrag","OnEndDrag","OnDrop","OnScroll","OnSelect",
            "OnDeselect","OnSubmit","OnCancel","OnMove","OnBeforeSerialize","OnAfterDeserialize",
            "ToString","Equals","GetHashCode","Main","Dispose","MoveNext","Current","GetEnumerator","CompareTo",
            "get","set","value","this","base","Instantiate","Destroy","Invoke","InvokeRepeating","StartCoroutine",
            "StopCoroutine","name","tag","enabled","transform","gameObject","hideFlags"
        };

        static readonly HashSet<string> ReservedNamespaceSegments = new HashSet<string>
        {
            "System","Unity","UnityEngine","UnityEditor","TMPro","Microsoft","Google","GoogleMobileAds","Firebase",
            "Facebook","Photon","DG","Tweening","Sirenix","Newtonsoft","Json","Collections","Generic","Linq","IO",
            "Text","Threading","Tasks","UI","Events","Serialization","Networking","Audio","Rendering","Purchasing",
            "Advertisements","Analytics","Security","Cryptography","Globalization","Reflection","AppLovin","IronSource"
        };

        static readonly HashSet<string> DefaultGameObjectNames = new HashSet<string>
        {
            "Main Camera","Directional Light","EventSystem","Canvas","GameObject","Camera","Light","Cube","Sphere",
            "Plane","Quad","Capsule","Cylinder","Text","Image","Button","Panel","Slider","Toggle","Scrollbar",
            "Dropdown","InputField","Scroll View","RawImage","Text (TMP)","Button (TMP)","Viewport","Content",
            "Background","Fill Area","Fill","Handle Slide Area","Handle","Checkmark","Label","Placeholder","Template",
            "Item","Item Background","Item Checkmark","Item Label","Arrow","Scrollbar Vertical","Scrollbar Horizontal",
            "Sliding Area","Text Area","Caret"
        };

        static readonly HashSet<string> CtrlKeywords = new HashSet<string>
            { "if","for","while","switch","foreach","return","new","using","lock","catch","fixed","sizeof","typeof","nameof","default","throw","await","yield","checked","unchecked","delegate","operator","is","as","in","out","ref","var" };

        static readonly HashSet<string> NoRenameExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".cs",".shader",".cginc",".hlsl",".compute",".anim",".asmdef",".asmref",".dll",".so",".a",".aar",".jar",".bundle",
              ".framework",".jslib",".mm",".m",".h",".cpp",".c",".java",".kt",".swift",".plist",".pdb",".mdb",".xml",".json",".txt" };

        public static ScanResult Scan(ObfProjectIndex idx, ObfSettings s, ObfProgress prog, CancellationToken ct)
        {
            var res = new ScanResult();
            var gen = new ObfNameGenerator(s);
            void Prog(string what, int done, int total) => prog?.Set("Scanning", what, done, total);

            // ---------- 1. inventory ----------
            var allCsIncludingExcluded = new List<string>();
            var allIncludedAssets = new List<string>();
            foreach (var f in idx.files)
            {
                if (f.isMeta) continue;
                if (f.ext == ".cs") { allCsIncludingExcluded.Add(f.path); if (!f.excluded) res.csFiles.Add(f.path); continue; }
                if (f.excluded) continue;
                allIncludedAssets.Add(f.path);
                switch (f.ext)
                {
                    case ".unity": res.sceneFiles.Add(f.path); break;
                    case ".prefab": res.prefabFiles.Add(f.path); break;
                    case ".asset": res.assetFiles.Add(f.path); break;
                    case ".anim": res.animFiles.Add(f.path); break;
                    case ".asmdef": res.asmdefFiles.Add(f.path); break;
                    case ".shader": res.shaderFiles.Add(f.path); break;
                }
            }

            var raw = new Dictionary<string, string>();   // included cs: raw text
            var code = new Dictionary<string, string>();  // included cs: code-only text
            int n = 0;
            foreach (var p in res.csFiles)
            {
                ct.ThrowIfCancellationRequested();
                if ((++n & 63) == 0) Prog(p, n, res.csFiles.Count);
                string t = idx.Read(p);
                raw[p] = t; code[p] = ObfCSharpText.CodeOnly(t);
            }
            string allRaw = string.Join("\n", raw.Values);
            string allCode = string.Join("\n", code.Values);

            Prog("indexing identifiers", 0, 1);
            // identifier frequency in code (single pass)
            var freq = new Dictionary<string, int>();
            foreach (Match m in ObfRegex.Matches(allCode, @"[A-Za-z_]\w*"))
            { freq.TryGetValue(m.Value, out int c); freq[m.Value] = c + 1; }
            int Count(string id) => freq.TryGetValue(id, out int c) ? c : 0;

            // string-literal counts in raw code, one pass (replaces per-candidate IndexOf sweeps)
            var literals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in raw.Values) CountLiterals(t, literals);
            int Lit(string sLit) => !string.IsNullOrEmpty(sLit) && literals.TryGetValue(sLit, out int c) ? c : 0;

            ct.ThrowIfCancellationRequested();
            // reserve every identifier in the whole project (including SDK code) + file stems
            // the tool's own sources are excluded from renaming but must not influence the analysis
            // (their help text mentions the very brand words the user wants gone)
            string tool = idx.toolFolder + "/";
            var excludedText = string.Join("\n", allCsIncludingExcluded.Where(p => !raw.ContainsKey(p) && !p.StartsWith(tool, StringComparison.Ordinal)).Select(idx.Read));
            gen.Reserve(freq.Keys);
            gen.Reserve(ObfRegex.Matches(excludedText, @"[A-Za-z_]\w*").Cast<Match>().Select(m => m.Value));
            gen.Reserve(allCsIncludingExcluded.Select(Path.GetFileNameWithoutExtension));
            // existing asset stems too: a generated asset name must never equal a file that is already there
            gen.Reserve(idx.files.Where(f => !f.isMeta).Select(f => Path.GetFileNameWithoutExtension(f.path)));

            // namespace segments used by SDK code are off-limits
            var sdkNsSegments = new HashSet<string>();
            foreach (Match m in ObfRegex.Matches(excludedText, @"(?<![\w.])namespace\s+([\w.]+)"))
                foreach (var seg in m.Groups[1].Value.Split('.')) sdkNsSegments.Add(seg);

            // ---------- protected identifiers: never renamed in any category ----------
            // keywords · every identifier appearing in excluded (SDK) code · attribute names and attribute
            // named arguments used in our code · public type/member names of the engine + .NET assemblies
            Prog("protected identifiers", 0, 1);
            var protectedIds = new HashSet<string>(CSharpKeywords, StringComparer.Ordinal);
            // v4: profile "protected names" — identifiers the user knows are read back by name (UnitySendMessage targets,
            // JSON-bound fields the heuristics miss, analytics parameter names …)
            foreach (var pn in s.protectedNames ?? new string[0]) if (!string.IsNullOrEmpty(pn)) protectedIds.Add(pn);
            // code only: a brand word inside an SDK string or comment (AssemblyInfo titles, log messages) is not an identifier
            foreach (Match m in ObfRegex.Matches(ObfCSharpText.CodeOnly(excludedText), @"[A-Za-z_]\w*")) protectedIds.Add(m.Value);
            CollectAttributeNames(allCode, protectedIds);
            var ownAssemblies = new HashSet<string>(OwnAssemblyNames, StringComparer.OrdinalIgnoreCase);
            foreach (var ad in res.asmdefFiles)
            {
                var nm = ObfRegex.Match(idx.Read(ad), "\"name\"\\s*:\\s*\"([^\"]+)\"");
                ownAssemblies.Add(nm.Success ? nm.Groups[1].Value : Path.GetFileNameWithoutExtension(ad));
            }
            int frameworkNames = CollectFrameworkMembers(protectedIds, ownAssemblies);
            int protectedHits = 0;
            bool Protected(string id) { if (!protectedIds.Contains(id)) return false; protectedHits++; return true; }

            // ---------- v3.2: names that something reads back BY STRING at runtime ----------
            // A rename is textual; anything that resolves a name from a string (reflection, JSON, a data file) is
            // invisible to it and breaks silently. Each of these was found by play-testing a processed build.
            // members resolved by reflection: typeof(X).GetMethod("Foo") / GetField / GetProperty …
            var reflectedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in ObfRegex.Matches(allRaw, @"\.(?:GetMethod|GetField|GetProperty|GetMember|GetEvent|GetNestedType|GetRuntimeMethod|GetRuntimeField|GetRuntimeProperty)\s*\(\s*""([A-Za-z_]\w*)"""))
                reflectedNames.Add(m.Groups[1].Value);
            // JSON keys (and every quoted word) of the project's .json / .txt data files: remote-config mirrors,
            // StreamingAssets, level bundles. A field with such a name is filled from JSON by name — renaming it
            // silently empties the object (the level-bundle envelope field `enc` → 0 levels → empty GameScene).
            var jsonKeys = new HashSet<string>(StringComparer.Ordinal);
            var jsonStrings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in idx.files)
            {
                if (f.isMeta || f.excluded || (f.ext != ".json" && f.ext != ".txt")) continue;
                if (f.path.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase) || f.size > 8_000_000) continue;
                string jt = idx.Read(f.path);
                if (string.IsNullOrEmpty(jt)) continue;
                foreach (Match m in ObfRegex.Matches(jt, @"""([A-Za-z_]\w*)""\s*(:)?"))
                { jsonStrings.Add(m.Groups[1].Value); if (m.Groups[2].Success) jsonKeys.Add(m.Groups[1].Value); }
            }
            // Spine atlas pages: SpineAtlasAsset matches material.mainTexture.name against the page line of the
            // .atlas file, so the texture's file name must stay exactly that page name.
            var atlasPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in idx.files)
            {
                if (f.isMeta || !(f.path.EndsWith(".atlas.txt", StringComparison.OrdinalIgnoreCase) || f.ext == ".atlas")) continue;
                foreach (Match m in ObfRegex.Matches(idx.Read(f.path), @"^[ \t]*([^\r\n:]+?\.(?:png|jpg|jpeg|tga))[ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    atlasPages.Add(m.Groups[1].Value.Trim());
            }
            // literals inside Find("…") calls are synced per segment; the same name used as ANY OTHER string in
            // code (a lookup key, a comparison, a const) is not — such scene objects keep their names.
            var findLiteralCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in ObfRegex.Matches(allRaw, @"(?:GameObject\.Find|transform\.Find|\.Find)\s*\(\s*""([^""\n]+)"""))
            { string k = m.Groups[1].Value; findLiteralCount.TryGetValue(k, out int c); findLiteralCount[k] = c + 1; }
            int FindUses(string lit) => findLiteralCount.TryGetValue(lit, out int c) ? c : 0;
            int dataBoundHits = 0;

            // native-bridge files: method/field names there are looked up by name from Java/ObjC/native
            var nativeFiles = new HashSet<string>();
            var nativeRe = ObfRegex.New(@"\b(AndroidJavaProxy|DllImport|MonoPInvokeCallback|UnitySendMessage|AndroidJNI)\b");
            foreach (var kv in code)
                if (nativeRe.IsMatch(kv.Value))
                {
                    nativeFiles.Add(kv.Key);
                    res.entries.Add(new RenameEntry
                    {
                        category = ObfCategory.NativeBridge, reviewOnly = true, risk = ObfRisk.High,
                        oldName = Path.GetFileName(kv.Key), newName = "(manual)", primaryPath = kv.Key,
                        note = "native interop present — methods/fields here are NOT renamed (looked up by name from native code)"
                    });
                }

            var seen = new HashSet<string>();
            bool Add(RenameEntry e) { if (!seen.Add(e.category + "|" + e.oldName)) return false; res.entries.Add(e); return true; }

            // ---------- 2. namespaces ----------
            Prog("namespaces", 0, 1);
            var nsRe = ObfRegex.New(@"(?<![\w.])namespace\s+([\w.]+)");
            foreach (var kv in code)
                foreach (Match m in nsRe.Matches(kv.Value))
                    foreach (var seg in m.Groups[1].Value.Split('.'))
                    {
                        if (ReservedNamespaceSegments.Contains(seg) || sdkNsSegments.Contains(seg) || Reserved.Contains(seg) || Protected(seg)) continue;
                        Add(new RenameEntry
                        {
                            category = ObfCategory.Namespace, oldName = seg, newName = gen.ForNamespace(seg),
                            primaryPath = kv.Key, occurrences = Count(seg), risk = ObfRisk.Low,
                            note = "renamed in namespace/using/qualified refs + YAML ns: / m_TargetAssemblyTypeName + asmdef rootNamespace"
                        });
                    }

            // ---------- 3. classes ----------
            Prog("types", 0, 1);
            ct.ThrowIfCancellationRequested();
            var classNames = new HashSet<string>();
            var typeRe = ObfRegex.New(@"(?<![\w.])(?:class|struct|interface|enum|record)\s+([A-Za-z_]\w*)");
            foreach (var kv in code)
                foreach (Match m in typeRe.Matches(kv.Value))
                {
                    string name = m.Groups[1].Value;
                    if (Reserved.Contains(name) || name.EndsWith("Attribute") || Protected(name)) continue;
                    classNames.Add(name);
                    bool primary = Path.GetFileNameWithoutExtension(kv.Key) == name;
                    Add(new RenameEntry
                    {
                        category = ObfCategory.Class, oldName = name, newName = gen.ForClass(name),
                        primaryPath = kv.Key, occurrences = Count(name), risk = ObfRisk.Low,
                        note = primary ? "primary type — .cs file renamed too (GUID kept)" : ""
                    });
                }

            // ---------- 3b. JSON-bound types (v3.2) ----------
            // Types that JsonUtility / Newtonsoft / a REST client map by FIELD NAME: the generic argument of
            // FromJson<T> / DeserializeObject<T> / SerializeObject<T> …, the generic argument of any project
            // wrapper method whose body calls one of those, types carrying Json*/DataContract attributes, types
            // named like wire objects (Dto/Envelope/Request/Response/Payload) — plus everything they reference
            // (a JSON object graph is deserialized as a whole). Every field declared in those types is protected.
            Prog("JSON-bound types", 0, 1);
            var jsonTypes = new HashSet<string>(StringComparer.Ordinal);
            var jsonCallRe = ObfRegex.New(@"\b(?:FromJson|FromJsonOverwrite|DeserializeObject|DeserializeAnonymousType|SerializeObject|ToObject|Populate|Deserialize|Serialize)\s*<([^()]+?)>\s*\(");
            var jsonWordRe = ObfRegex.New(@"\b(?:JsonUtility|JsonConvert|JsonSerializer|FromJson|DeserializeObject|SerializeObject|MiniJSON|JObject|JsonMapper)\b");
            void AddJsonTypeArg(string arg) { foreach (Match im in ObfRegex.Matches(arg, @"[A-Za-z_]\w*")) if (classNames.Contains(im.Value)) jsonTypes.Add(im.Value); }
            var wrapperNames = new HashSet<string>(StringComparer.Ordinal);
            var genericMethodRe = ObfRegex.New(@"(?<![\w.])([A-Za-z_]\w*)\s*<\s*T\w*\s*>\s*\([^)]*\)[^{;]*\{");
            foreach (var kv in code)
            {
                foreach (Match m in jsonCallRe.Matches(kv.Value)) AddJsonTypeArg(m.Groups[1].Value);
                // generic wrappers: `static T FromJson<T>(string s) { … JsonUtility.FromJson<T>(s) … }`
                foreach (Match m in genericMethodRe.Matches(kv.Value))
                {
                    string body = BraceBody(kv.Value, m.Index + m.Length - 1);
                    if (jsonWordRe.IsMatch(body)) wrapperNames.Add(m.Groups[1].Value);
                }
            }
            if (wrapperNames.Count > 0)
            {
                var wrapCallRe = ObfRegex.New(@"\b(?:" + string.Join("|", wrapperNames.Select(Regex.Escape)) + @")\s*<([^()]+?)>\s*\(");
                foreach (var kv in code) foreach (Match m in wrapCallRe.Matches(kv.Value)) AddJsonTypeArg(m.Groups[1].Value);
            }
            var typeBodyRe = ObfRegex.New(@"(?<![\w.])(?:class|struct)\s+([A-Za-z_]\w*)\b[^{;]*\{");
            var typeBodies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in code)
                foreach (Match m in typeBodyRe.Matches(kv.Value))
                {
                    string tn = m.Groups[1].Value;
                    string body = BraceBody(kv.Value, m.Index + m.Length - 1);
                    if (!typeBodies.ContainsKey(tn)) typeBodies[tn] = body;
                    if (ObfRegex.IsMatch(body, @"\[(?:Json\w*|DataContract|DataMember|Preserve)\b") || ObfRegex.IsMatch(tn, @"(Dto|DTO|Envelope|Request|Response|Payload|Wire)$"))
                        if (classNames.Contains(tn)) jsonTypes.Add(tn);
                }
            // transitive closure over the identifiers used inside each JSON type's body (field types, generic args, nested types)
            var jsonQueue = new Queue<string>(jsonTypes);
            while (jsonQueue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string tn = jsonQueue.Dequeue();
                if (!typeBodies.TryGetValue(tn, out var body)) continue;
                foreach (Match im in ObfRegex.Matches(body, @"[A-Za-z_]\w*"))
                    if (classNames.Contains(im.Value) && jsonTypes.Add(im.Value)) jsonQueue.Enqueue(im.Value);
            }
            var jsonFieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tn in jsonTypes)
            {
                if (!typeBodies.TryGetValue(tn, out var body)) continue;
                List<ObfCSharpText.Decl> fdecls;
                try { fdecls = ObfCSharpText.FindDeclarations(body); } catch { continue; }
                foreach (var d in fdecls) if (d.kind == ObfCSharpText.DeclKind.Field) jsonFieldNames.Add(d.name);
                // fields without an access modifier (`string enc;`) are not found by the finder but are still JSON-bound
                foreach (Match m in ObfRegex.Matches(body, @"(?m)^[ \t]*(?:public|internal|private|protected)?[ \t]*(?:readonly[ \t]+)?[\w.<>\[\],?]+[ \t]+([A-Za-z_]\w*)[ \t]*(?:=[^;]*)?;"))
                    jsonFieldNames.Add(m.Groups[1].Value);
            }
            res.notes.Add(jsonTypes.Count + " JSON-bound type(s) found — their " + jsonFieldNames.Count + " field name(s) are kept (mapped from JSON by name).");

            // interface members must keep their names on implementing classes
            var interfaceMembers = new HashSet<string>();
            var ifaceRe = ObfRegex.New(@"(?<![\w.])interface\s+[A-Za-z_]\w*[^{;]*\{");
            var memberRe = ObfRegex.New(@"\b([A-Za-z_]\w*)\s*(?:\(|\{|;|=>)");
            foreach (var kv in code)
                foreach (Match m in ifaceRe.Matches(kv.Value))
                {
                    string body = BraceBody(kv.Value, m.Index + m.Length - 1);
                    foreach (Match mm in memberRe.Matches(body)) interfaceMembers.Add(mm.Groups[1].Value);
                }

            // ---------- 4. enum members ----------
            Prog("enum members", 0, 1);
            ct.ThrowIfCancellationRequested();
            var enumDeclRe = ObfRegex.New(@"(?<![\w.])enum\s+([A-Za-z_]\w*)");
            var enumNames = new HashSet<string>(code.Values.SelectMany(t => enumDeclRe.Matches(t).Cast<Match>().Select(m => m.Groups[1].Value)));
            // qualifier→member pairs from the whole corpus, ONE pass (lookahead keeps "A.B.C" → (A,B),(B,C))
            var qualifiers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (Match m in ObfRegex.Matches(allCode, @"\b([A-Za-z_]\w*)\s*\.\s*(?=([A-Za-z_]\w*))"))
            {
                string member = m.Groups[2].Value;
                if (!qualifiers.TryGetValue(member, out var set)) qualifiers[member] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(m.Groups[1].Value);
            }
            var enumBodyRe = ObfRegex.New(@"(?<![\w.])enum\s+([A-Za-z_]\w*)[^{;]*\{");
            var enumMemberRe = ObfRegex.New(@"(?:^|,|\{)\s*(?:\[[^\]\n]*\]\s*)*([A-Za-z_]\w*)\s*(?:=|,|\}|$)", RegexOptions.Multiline);
            foreach (var kv in code)
                foreach (Match m in enumBodyRe.Matches(kv.Value))
                {
                    string body = BraceBody(kv.Value, m.Index + m.Length - 1);
                    foreach (Match mm in enumMemberRe.Matches(body))
                    {
                        string member = mm.Groups[1].Value;
                        if (Reserved.Contains(member) || classNames.Contains(member) || Protected(member)) continue;
                        // if the member is ever qualified by something that is not one of our enums, another type owns
                        // a member with the same name (e.g. TextAnchor.MiddleCenter) — textual rename would hit it too.
                        bool foreignOwner = qualifiers.TryGetValue(member, out var q) && q.Any(x => !enumNames.Contains(x));
                        if (foreignOwner) continue;
                        // Enum.Parse / JSON "Mode":"Daily" / string compares read the member NAME — keep it
                        if (jsonStrings.Contains(member) || Lit(member) > 0) { dataBoundHits++; continue; }
                        Add(new RenameEntry
                        {
                            category = ObfCategory.EnumMember, oldName = member, newName = gen.ForEnumMember(member),
                            primaryPath = kv.Key, occurrences = Count(member), risk = ObfRisk.Medium,
                            note = "serialized as int (safe); breaks Enum.Parse(\"" + member + "\") / string compares if any"
                        });
                    }
                }

            // ---------- 5 + 6. methods and fields (linear declaration finder) ----------
            Prog("methods & fields", 0, 1);
            ct.ThrowIfCancellationRequested();
            var methodNames = new HashSet<string>();
            var pendingFields = new List<KeyValuePair<string, ObfCSharpText.Decl>>();
            n = 0;
            foreach (var kv in code)
            {
                if ((++n & 63) == 0) { ct.ThrowIfCancellationRequested(); Prog(kv.Key, n, code.Count); }
                if (nativeFiles.Contains(kv.Key)) continue;
                List<ObfCSharpText.Decl> decls;
                try { decls = ObfCSharpText.FindDeclarations(kv.Value); }
                catch (Exception ex) { res.skipped.Add(kv.Key + " | declarations: " + ex.Message); continue; }
                foreach (var d in decls)
                {
                    if (d.kind == ObfCSharpText.DeclKind.Method)
                    {
                        string name = d.name;
                        if (Reserved.Contains(name) || classNames.Contains(name) || interfaceMembers.Contains(name) || Protected(name)) continue;
                        if (CtrlKeywords.Contains(name) || name.StartsWith("get_") || name.StartsWith("set_")) continue;
                        if (reflectedNames.Contains(name)) { dataBoundHits++; continue; } // GetMethod("name") somewhere
                        if (!Add(new RenameEntry
                        {
                            category = ObfCategory.Method, oldName = name, newName = gen.ForMethod(name),
                            primaryPath = kv.Key, occurrences = Count(name), risk = ObfRisk.High,
                            note = "synced: coroutine/Invoke strings, UnityEvent m_MethodName, AnimationEvent functionName"
                        })) continue;
                        methodNames.Add(name);
                    }
                    else pendingFields.Add(new KeyValuePair<string, ObfCSharpText.Decl>(kv.Key, d));
                }
            }
            foreach (var pf in pendingFields)
            {
                var d = pf.Value; string name = d.name;
                if (Reserved.Contains(name) || classNames.Contains(name) || methodNames.Contains(name) || interfaceMembers.Contains(name) || Protected(name)) continue;
                if (CtrlKeywords.Contains(name)) continue;
                // read back by name at runtime: JSON mapping (field of a JSON-bound type, or a key that exists in a
                // project data file) or reflection (GetField("name")) — a textual rename would empty / null it
                if (jsonFieldNames.Contains(name) || jsonKeys.Contains(name) || reflectedNames.Contains(name)) { dataBoundHits++; continue; }
                bool isEvent = d.modifiers.Contains("event");
                bool serialized = !isEvent && ((d.isPublic && !d.modifiers.Contains("static") && !d.modifiers.Contains("const") && !d.modifiers.Contains("readonly"))
                                  || d.attributes.Contains("SerializeField"));
                if (serialized) res.serializedFieldNames.Add(name);
                Add(new RenameEntry
                {
                    category = ObfCategory.Field, oldName = name, newName = gen.ForField(name),
                    primaryPath = pf.Key, occurrences = Count(name), risk = serialized ? ObfRisk.Medium : ObfRisk.Low,
                    note = serialized ? "serialized — [FormerlySerializedAs] is injected so inspector data survives" : isEvent ? "event" : ""
                });
            }

            // ---------- 7. Resources.Load ----------
            Prog("Resources.Load", 0, 1);
            ct.ThrowIfCancellationRequested();
            var resLit = ObfRegex.New(@"Resources\.(?:Load|LoadAsync|LoadAll)\s*(?:<[^>\n]*>)?\s*\(\s*""([^""\n]+)""");
            var resDyn = ObfRegex.New(@"Resources\.(?:Load|LoadAsync|LoadAll)\s*(?:<[^>\n]*>)?\s*\(\s*(?![""\s)])");
            // how often each path appears as the literal argument of a Resources.Load call (vs. anywhere as a string)
            var callCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in raw)
                foreach (Match m in resLit.Matches(kv.Value))
                { string p = m.Groups[1].Value; callCount.TryGetValue(p, out int c); callCount[p] = c + 1; }
            foreach (var kv in raw)
            {
                foreach (Match m in resLit.Matches(kv.Value))
                {
                    string rawPath = m.Groups[1].Value;
                    string path = rawPath.Replace('\\', '/').TrimEnd('/');
                    string asset = idx.ResolveResource(path);
                    if (asset != null) res.resourcePathToAsset[path] = asset;
                    // The same literal used elsewhere (a const, a field default, UI text). A path with '/' is
                    // unmistakably this resource and every occurrence is rewritten; a plain word ("Loading") could
                    // be anything, so the asset keeps its name rather than risk a broken lookup.
                    int elsewhere = Lit(rawPath) - (callCount.TryGetValue(rawPath, out int cc) ? cc : 0);
                    bool ambiguous = elsewhere > 0 && path.IndexOf('/') < 0;
                    Add(new RenameEntry
                    {
                        category = ObfCategory.ResourcePath, oldName = path,
                        newName = ReplaceLeaf(path, KeepCompoundExt(Leaf(path), gen.ForAssetLeaf(Leaf(path)))),
                        primaryPath = asset ?? kv.Key, occurrences = Lit(rawPath),
                        risk = asset == null || ambiguous ? ObfRisk.High : ObfRisk.Low, enabled = asset != null && !ambiguous,
                        note = asset == null ? "no single asset resolved (folder / LoadAll / missing) — left disabled"
                             : ambiguous ? "same string also used outside Resources.Load (const / text) and it is not a path — left disabled"
                             : elsewhere > 0 ? "asset file + every \"" + path + "\" string literal renamed together (also used in a const/field)"
                             : "asset file + Resources.Load string renamed together"
                    });
                }
                string stripped = code[kv.Key];
                foreach (Match m in resDyn.Matches(stripped))
                    Add(new RenameEntry
                    {
                        category = ObfCategory.DynamicLoad, reviewOnly = true, risk = ObfRisk.High,
                        oldName = Snippet(ObfCSharpText.StripComments(kv.Value), m.Index, m.Value),
                        newName = "(manual)", primaryPath = kv.Key, note = "computed path — cannot auto-remap"
                    });
            }

            // ---------- 8. shaders ----------
            Prog("shaders", 0, 1);
            var shaderNameRe = ObfRegex.New("^\\s*Shader\\s+\"([^\"\\n]+)\"", RegexOptions.Multiline);
            foreach (var sp in res.shaderFiles)
            {
                var mm = shaderNameRe.Match(idx.Read(sp));
                if (!mm.Success) continue;
                string inner = mm.Groups[1].Value;
                res.shaderNameToFile[inner] = sp;
                Add(new RenameEntry
                {
                    category = ObfCategory.Shader, oldName = inner, newName = ReplaceLeaf(inner, gen.ForClass(Leaf(inner))),
                    primaryPath = sp, occurrences = Lit(inner), risk = ObfRisk.Medium,
                    note = "inner Shader name + Shader.Find + Fallback/UsePass in other shaders + file name (GUID kept)"
                });
            }

            // ---------- 9. scene / prefab GameObject names ----------
            Prog("scene objects", 0, 1);
            ct.ThrowIfCancellationRequested();
            var goNames = new Dictionary<string, string>();
            n = 0;
            foreach (var f in res.sceneFiles.Concat(res.prefabFiles))
            {
                if ((++n & 31) == 0) { ct.ThrowIfCancellationRequested(); Prog(f, n, res.sceneFiles.Count + res.prefabFiles.Count); }
                try { CollectGameObjectNames(idx.Read(f), goNames, f); }
                catch (Exception ex) { res.skipped.Add(f + " | GameObject names: " + ex.Message); }
            }
            foreach (var kv in goNames)
            {
                if (DefaultGameObjectNames.Contains(kv.Key)) continue;
                int inCode = Lit(kv.Key);
                // "CoinTarget" as a FlowingObject target key, "Level" as a dictionary key…: the literal is not inside a
                // Find() call, so it cannot be synced — the object keeps its name (v3.2)
                bool plainString = inCode > FindUses(kv.Key);
                Add(new RenameEntry
                {
                    category = ObfCategory.SceneObject, oldName = kv.Key, newName = gen.ForGameObject(kv.Key),
                    primaryPath = kv.Value, occurrences = inCode, enabled = !plainString,
                    risk = plainString ? ObfRisk.High : inCode > 0 ? ObfRisk.Medium : ObfRisk.Low,
                    note = plainString ? "name is used as a plain string in code (not a Find path) — left disabled"
                         : "synced: Find(\"…\") paths in code + .anim curve paths" + (inCode > 0 ? " — referenced by a Find string in code" : "")
                });
            }

            // ---------- 10. PlayerPrefs ----------
            var ppRe = ObfRegex.New(@"PlayerPrefs\.(?:GetInt|GetFloat|GetString|SetInt|SetFloat|SetString|HasKey|DeleteKey)\s*\(\s*""([^""\n]+)""");
            foreach (var kv in raw)
                foreach (Match m in ppRe.Matches(kv.Value))
                    Add(new RenameEntry
                    {
                        category = ObfCategory.PlayerPrefsKey, oldName = m.Groups[1].Value, newName = gen.ForKey(m.Groups[1].Value),
                        primaryPath = kv.Key, occurrences = Lit(m.Groups[1].Value), risk = ObfRisk.Low,
                        note = "resets existing saves for this key"
                    });

            // ---------- 10b. asset files (GUID-referenced => safe to rename) ----------
            Prog("asset files", 0, 1);
            ct.ThrowIfCancellationRequested();
            var sceneLoadRe = ObfRegex.New(@"(?:LoadScene|LoadSceneAsync|LoadLevel|LoadLevelAsync|LoadLevelAdditive)\s*\(\s*""([^""\n]+)""");
            var sceneStrings = new HashSet<string>(raw.Values.SelectMany(t => sceneLoadRe.Matches(t).Cast<Match>().Select(m => m.Groups[1].Value)));
            foreach (var ap in allIncludedAssets)
            {
                string ext = Path.GetExtension(ap);
                if (NoRenameExt.Contains(ext)) continue;
                if (ap.IndexOf("/Resources/", StringComparison.Ordinal) >= 0) continue; // handled by ResourcePath entries; unreferenced ones may be loaded dynamically
                string stem = Path.GetFileNameWithoutExtension(ap);
                if (stem.Length == 0) continue;
                bool isScene = ext.Equals(".unity", StringComparison.OrdinalIgnoreCase);
                int lit = Lit(stem);
                bool nameUsedInCode = lit > 0 && !(isScene && sceneStrings.Contains(stem));
                bool atlasPage = atlasPages.Contains(Path.GetFileName(ap));
                Add(new RenameEntry
                {
                    category = ObfCategory.AssetFile, oldName = ap, newName = KeepCompoundExt(stem, gen.ForAssetLeaf(stem)),
                    primaryPath = ap, occurrences = lit, enabled = !nameUsedInCode && !atlasPage,
                    risk = nameUsedInCode || atlasPage ? ObfRisk.High : isScene ? ObfRisk.Medium : ObfRisk.Low,
                    note = atlasPage ? "Spine atlas page — the texture name must match the .atlas page line — left disabled"
                         : nameUsedInCode ? "asset name appears as a string in code (e.g. .name == / GetSprite) — left disabled"
                         : isScene ? "scene: LoadScene(\"…\") strings + Build Settings synced" : "GUID-referenced — references survive"
                });
            }

            // ==========================================================================================
            //  v3.4 — the leaks a before/after comparison of the shipped build still found after a full run
            // ==========================================================================================
            Prog("brand terms", 0, 1);
            ct.ThrowIfCancellationRequested();
            var asmdefNames = new List<string>();
            foreach (var ad in res.asmdefFiles)
            {
                var nm = ObfRegex.Match(idx.Read(ad), "\"name\"\\s*:\\s*\"([^\"]+)\"");
                string an = nm.Success ? nm.Groups[1].Value : Path.GetFileNameWithoutExtension(ad);
                asmdefNames.Add(an);
                res.asmdefNameToFile[an] = ad;
            }
            res.brandTerms = ObfBrand.ResolveTerms(s, asmdefNames, freq.Keys);
            var terms = res.brandTerms;
            var nsMapNow = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in res.entries) if (e.category == ObfCategory.Namespace && e.enabled) nsMapNow[e.oldName] = e.newName;

            // ---------- 10c. assembly names ----------
            // "Acme.Orion" is the .asmdef name → Acme.Orion.dll in the build and the tail of every
            // assembly-qualified type name in the metadata; the namespace pass never touched it.
            if (s.renameAssemblies)
                foreach (var kv in res.asmdefNameToFile)
                {
                    string name = kv.Key;
                    if (name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("Assembly-CSharp", StringComparison.Ordinal)) continue;
                    string mapped = string.Join(".", name.Split('.').Select(seg => nsMapNow.TryGetValue(seg, out var nv) ? nv : seg));
                    if (mapped == name && ObfBrand.FirstTermIn(name, terms) != null) mapped = gen.ForClass(name.Replace(".", ""));
                    if (mapped == name) continue;
                    Add(new RenameEntry
                    {
                        category = ObfCategory.AssemblyName, oldName = name, newName = mapped, primaryPath = kv.Value,
                        occurrences = Lit(name), risk = ObfRisk.Medium,
                        note = "asmdef name + file, \"references\" in other asmdefs, link.xml, InternalsVisibleTo and \"Type, Assembly\" strings synced"
                    });
                }

            // ---------- 10d. brand-bearing identifiers (any kind) ----------
            // Properties (AcmeHaptic), events (OnOrionManagerInit), override / interface methods
            // (CreateOrionEntityDescriptions) and parameters are not covered by the Field / Method categories
            // — and those categories are usually off. Anything that still carries a brand term is renamed as a
            // whole-word identifier; what cannot be renamed safely is listed for review instead of being forgotten.
            int sweepAdded = 0, sweepBlocked = 0;
            if (s.sweepBrandIdentifiers && terms.Count > 0)
            {
                var already = new HashSet<string>(res.entries.Where(e => !e.reviewOnly).Select(e => e.oldName), StringComparer.Ordinal);
                foreach (var id in freq.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (id.Length < 4 || CSharpKeywords.Contains(id) || Reserved.Contains(id) || already.Contains(id)) continue;
                    string term = ObfBrand.FirstTermIn(id, terms);
                    if (term == null) continue;
                    string firstFile = code.FirstOrDefault(kv => kv.Value.IndexOf(id, StringComparison.Ordinal) >= 0).Key ?? "";
                    string why = null;
                    if (protectedIds.Contains(id)) why = "also exists in SDK / engine / .NET code (or is an attribute name) — rename by hand";
                    else if (reflectedNames.Contains(id) || jsonKeys.Contains(id) || jsonFieldNames.Contains(id)) why = "read back by name at runtime (reflection / JSON key) — rename by hand together with the data";
                    else if (Lit(id) > 0) why = "also used as a plain string in code — rename by hand";
                    if (why != null)
                    {
                        sweepBlocked++;
                        Add(new RenameEntry
                        {
                            category = ObfCategory.BrandReview, reviewOnly = true, risk = ObfRisk.High, oldName = id, newName = "(manual)",
                            primaryPath = firstFile, occurrences = Count(id), note = "identifier carries brand term '" + term + "' but " + why
                        });
                        continue;
                    }
                    bool pascal = char.IsUpper(id[0]);
                    Add(new RenameEntry
                    {
                        category = ObfCategory.BrandIdentifier, oldName = id, newName = pascal ? gen.ForClass(id) : gen.ForField(id),
                        primaryPath = firstFile, occurrences = Count(id), risk = ObfRisk.High,
                        note = "carries brand term '" + term + "' — not covered by the Field/Method categories (property, event, override, parameter …); renamed as a whole-word identifier, [FormerlySerializedAs] injected where serialized"
                    });
                    sweepAdded++;
                }
                // entries a safety rule left disabled that still carry a brand term: the term ships unless someone decides
                foreach (var e in res.entries.Where(e => !e.enabled && !e.reviewOnly).ToList())
                {
                    string term = ObfBrand.FirstTermIn(e.oldName, terms);
                    if (term == null) continue;
                    sweepBlocked++;
                    Add(new RenameEntry
                    {
                        category = ObfCategory.BrandReview, reviewOnly = true, risk = ObfRisk.High, oldName = e.oldName, newName = "(manual)",
                        primaryPath = e.primaryPath, occurrences = e.occurrences,
                        note = e.category + " left disabled by a safety rule but carries brand term '" + term + "': " + e.note
                    });
                }
                // brand terms inside strings the brand pass must not touch (URLs, package ids, native class names)
                var spanRe = ObfRegex.New(@"[A-Za-z][A-Za-z0-9+.\-]*://[^\s""'<>)\]]+|(?<![\w.])[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*){2,}(?:\.[A-Za-z_]\w*)*(?![\w.])");
                var spansSeen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in raw)
                    foreach (Match m in spanRe.Matches(kv.Value))
                    {
                        string span = m.Value;
                        if (ObfBrand.FirstTermIn(span, terms) == null || !spansSeen.Add(span)) continue;
                        Add(new RenameEntry
                        {
                            category = ObfCategory.BrandReview, reviewOnly = true, risk = ObfRisk.High, oldName = span, newName = "(manual)",
                            primaryPath = kv.Key, note = "brand term inside a URL / package id / native class name — the brand pass leaves it untouched; point it at YOUR backend / plugin or remove the feature"
                        });
                    }
            }

            // ---------- 10e. folders named after namespaces / assemblies / brand terms ----------
            // Unity 6 IL2CPP writes "\Assets\Scripts\Acme\Game\Foo.cs" for every script into global-metadata.dat.
            if (s.renameAssemblies)
            {
                var segMap = new Dictionary<string, string>(nsMapNow, StringComparer.Ordinal);
                foreach (var e in res.entries) if (e.category == ObfCategory.AssemblyName && e.enabled) foreach (var seg in e.oldName.Split('.')) if (!segMap.ContainsKey(seg) && ObfBrand.FirstTermIn(seg, terms) != null) segMap[seg] = gen.ForClass(seg);
                foreach (var op in ObfBrand.FolderPlan(idx, segMap, null, terms, gen))
                    Add(new RenameEntry
                    {
                        category = ObfCategory.Folder, oldName = op.path, newName = op.newStem, primaryPath = op.path, risk = ObfRisk.Low,
                        note = "folder name carries a namespace / assembly / brand term — every script's project path ships in the IL2CPP metadata (GUID kept, references survive)"
                    });
            }
            res.notes.Add("Brand terms in effect: " + (terms.Count == 0 ? "none" : string.Join(", ", terms)) + ". Identifier sweep: " + sweepAdded + " renamed, " + sweepBlocked + " for manual review.");

            // ---------- 11. Animator strings (review only) ----------
            var animRe = ObfRegex.New(@"\.(?:Play|CrossFade|CrossFadeInFixedTime|SetTrigger|ResetTrigger|SetBool|SetFloat|SetInteger|GetBool|GetFloat|GetInteger)\s*\(\s*""([^""\n]+)""");
            foreach (var kv in raw)
                foreach (Match m in animRe.Matches(kv.Value))
                    Add(new RenameEntry
                    {
                        category = ObfCategory.AnimatorString, reviewOnly = true, risk = ObfRisk.High,
                        oldName = m.Groups[1].Value, newName = "(manual)", primaryPath = kv.Key,
                        note = "must match state/parameter inside the Animator Controller — rename both by hand"
                    });

            res.entries = res.entries.OrderBy(e => e.category).ThenBy(e => e.oldName, StringComparer.Ordinal).ToList();
            res.notes.Add(protectedHits + " candidate name(s) left untouched because they are C# keywords, attribute names, or also exist in SDK / engine / .NET code ("
                          + frameworkNames + " framework names reserved).");
            res.notes.Add(dataBoundHits + " member name(s) kept because they are read back by name at runtime (JSON keys in " + jsonKeys.Count
                          + " data-file keys, reflection GetMethod/GetField strings, enum names used as strings).");
            Prog("done", 1, 1);
            return res;
        }

        /// Attribute names `[Foo(` / `[Foo]` and their named arguments `order = 1` in our own code.
        static void CollectAttributeNames(string code, HashSet<string> into)
        {
            foreach (Match m in ObfRegex.Matches(code, @"(?:^|[\s(,])\[\s*([A-Za-z_][\w.]*)", RegexOptions.Multiline))
            {
                string name = m.Groups[1].Value;
                int dot = name.LastIndexOf('.'); if (dot >= 0) name = name.Substring(dot + 1);
                into.Add(name); into.Add(name + "Attribute");
                // named arguments inside the balanced (...) that follows
                int i = m.Index + m.Length;
                while (i < code.Length && (code[i] == ' ' || code[i] == '\t')) i++;
                if (i < code.Length && code[i] == '(')
                {
                    int depth = 0, start = i;
                    for (; i < code.Length; i++) { if (code[i] == '(') depth++; else if (code[i] == ')') { depth--; if (depth == 0) break; } if (i - start > 4000) break; }
                    string args = code.Substring(start, Math.Min(code.Length, i + 1) - start);
                    foreach (Match a in ObfRegex.Matches(args, @"(?<![\w.])([A-Za-z_]\w*)\s*=(?!=)")) into.Add(a.Groups[1].Value);
                }
            }
        }

        /// Public type and member names of every loaded assembly except the project's own script assemblies
        /// (engine, .NET, packages, and SDK DLLs alike).
        static int CollectFrameworkMembers(HashSet<string> into, HashSet<string> ownAssemblies)
        {
            int before = into.Count;
            Assembly[] asms; try { asms = AppDomain.CurrentDomain.GetAssemblies(); } catch { return 0; }
            foreach (var asm in asms)
            {
                string n; try { n = asm.GetName().Name; } catch { continue; }
                if (ownAssemblies.Contains(n) || asm.IsDynamic) continue;
                // v4: the tool's own assembly (when installed with its .asmdef) must not protect the project's names —
                // its public members are called Scan / Build / Apply / Map / Restore, which are perfectly good game method names.
                // Compared by identity, not by name, so renaming the .asmdef cannot break it.
                if (asm == typeof(ObfScanner).Assembly) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                {
                    try
                    {
                        if (!t.IsPublic && !t.IsNestedPublic) continue;
                        into.Add(StripGeneric(t.Name));
                        // namespace segments too: a game method called `Firebase` would otherwise rewrite `using Firebase;`
                        if (!string.IsNullOrEmpty(t.Namespace)) foreach (var seg in t.Namespace.Split('.')) into.Add(seg);
                        if (t.IsEnum) { foreach (var en in Enum.GetNames(t)) into.Add(en); continue; }
                        // public AND protected members: a game subclass of UnityEngine.UI.Image touches the inherited
                        // protected field `m_Material`; renaming that identifier in the subclass breaks the build
                        foreach (var mi in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            if (IsInheritable(mi)) into.Add(StripGeneric(mi.Name));
                    }
                    catch { }
                }
            }
            return into.Count - before;
        }

        static string StripGeneric(string s) { int i = s.IndexOf('`'); return i < 0 ? s : s.Substring(0, i); }

        static bool Fam(MethodBase m) => m != null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly);

        /// public or protected — anything a class outside the assembly could see or inherit
        static bool IsInheritable(MemberInfo mi)
        {
            switch (mi)
            {
                case FieldInfo f: return f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly;
                case MethodBase m: return Fam(m);
                case PropertyInfo p: return p.GetAccessors(true).Any(Fam);
                case EventInfo e: return Fam(e.GetAddMethod(true)) || Fam(e.GetRemoveMethod(true));
                case Type nt: return nt.IsNestedPublic || nt.IsNestedFamily || nt.IsNestedFamORAssem;
                default: return false;
            }
        }

        // ---------------- rename-target collision check (runs right before Apply, main thread) ----------------
        /// Every planned file rename gets a destination that does not exist on disk and is not claimed by another
        /// entry; colliding names get a numeric suffix. Without this, a rename fails while the code string was
        /// already rewritten, and every asset loaded by that name goes missing at runtime.
        public static int ResolveNameCollisions(ScanResult res, ObfProjectIndex idx, ObfSettings s, List<string> warnings)
        {
            var onDisk = new HashSet<string>(idx.files.Select(f => f.path).Concat(idx.dirs), StringComparer.OrdinalIgnoreCase);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int bumped = 0;

            string Free(string dir, string stem, string ext, string what)
            {
                string cand = stem; int n = 2;
                while (onDisk.Contains(dir + "/" + cand + ext) || claimed.Contains(dir + "/" + cand + ext))
                { cand = stem + n++; if (n > 500) break; }
                claimed.Add(dir + "/" + cand + ext);
                if (cand != stem) { bumped++; warnings.Add("name collision avoided: " + what + " -> " + cand + " (" + stem + " already exists)"); }
                return cand;
            }
            string DirOf(string p) { int i = p.LastIndexOf('/'); return i < 0 ? "" : p.Substring(0, i); }

            foreach (var e in res.entries)
            {
                if (!e.enabled || e.reviewOnly || string.IsNullOrEmpty(e.newName) || e.newName == e.oldName) continue;
                switch (e.category)
                {
                    case ObfCategory.ResourcePath when s.remapResources:
                        if (!res.resourcePathToAsset.TryGetValue(e.oldName, out var ap)) break;
                        e.newName = ReplaceLeaf(e.newName, Free(DirOf(ap), Leaf(e.newName), Path.GetExtension(ap), ap));
                        break;
                    case ObfCategory.Shader when s.renameShaders:
                        if (!res.shaderNameToFile.TryGetValue(e.oldName, out var sp)) break;
                        e.newName = ReplaceLeaf(e.newName, Free(DirOf(sp), Leaf(e.newName), Path.GetExtension(sp), sp));
                        break;
                    case ObfCategory.Class when s.renameClasses:
                        foreach (var cs in res.csFiles.Where(p => Path.GetFileNameWithoutExtension(p) == e.oldName))
                            e.newName = Free(DirOf(cs), e.newName, ".cs", cs); // same class name in two folders: the later folder decides
                        break;
                    case ObfCategory.AssetFile when s.renameAssetFiles:
                        e.newName = Free(DirOf(e.oldName), e.newName, Path.GetExtension(e.oldName), e.oldName);
                        break;
                }
            }
            return bumped;
        }

        // ---------------- helpers ----------------

        // returns the text between the '{' at openIdx and its matching '}' (exclusive)
        static string BraceBody(string s, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return s.Substring(openIdx + 1, i - openIdx - 1); }
            }
            return s.Substring(openIdx + 1);
        }

        /// Counts every "…" literal (same line, backslash-escape aware) — one linear pass per file.
        static void CountLiterals(string t, Dictionary<string, int> into)
        {
            int i = 0, n = t.Length;
            while (i < n)
            {
                if (t[i] != '"') { i++; continue; }
                int j = i + 1;
                while (j < n && t[j] != '"' && t[j] != '\n') { if (t[j] == '\\') j++; j++; }
                if (j < n && t[j] == '"')
                {
                    string lit = t.Substring(i + 1, j - i - 1);
                    if (lit.Length > 0 && lit.Length <= 200) { into.TryGetValue(lit, out int c); into[lit] = c + 1; }
                    i = j + 1;
                }
                else i = j;
            }
        }

        static string Leaf(string p) { int i = p.LastIndexOf('/'); return i < 0 ? p : p.Substring(i + 1); }

        // "Leo.skel" + "swift_frame" -> "swift_frame.skel": Spine (.skel.bytes / .atlas.txt), ".legacy.asset" etc. are
        // recognised by importers through the compound extension, so the part after the first dot must survive.
        static string KeepCompoundExt(string oldStem, string newStem)
        {
            int dot = oldStem.IndexOf('.');
            return dot > 0 ? newStem + oldStem.Substring(dot) : newStem;
        }
        static string ReplaceLeaf(string p, string leaf) { int i = p.LastIndexOf('/'); return i < 0 ? leaf : p.Substring(0, i + 1) + leaf; }

        static void CollectGameObjectNames(string yaml, Dictionary<string, string> into, string file)
        {
            if (string.IsNullOrEmpty(yaml)) return;
            foreach (var d in yaml.Split(new[] { "--- !u!" }, StringSplitOptions.None))
            {
                if (!d.StartsWith("1 ")) continue; // !u!1 GameObject
                int at = d.IndexOf("\n  m_Name: ", StringComparison.Ordinal);
                if (at < 0) at = d.IndexOf("\n\tm_Name: ", StringComparison.Ordinal);
                if (at < 0) continue;
                int start = d.IndexOf(':', at) + 1;
                int end = d.IndexOf('\n', start); if (end < 0) end = d.Length;
                string name = d.Substring(start, end - start).Trim();
                if (name.Length == 0 || name.Contains("/")) continue; // '/' would break Find paths
                if (!into.ContainsKey(name)) into[name] = file;
            }
        }

        static string Snippet(string text, int hint, string fallback)
        {
            if (hint < 0 || hint >= text.Length) return fallback;
            int len = Math.Min(70, text.Length - hint);
            return text.Substring(hint, len).Replace('\n', ' ').Replace('\r', ' ').Trim();
        }
    }
}
