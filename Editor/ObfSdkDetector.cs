// Code Obfuscator — SDK / third-party auto-detector.
//
// Decides what must NOT be touched: ad networks, Firebase, IAP, analytics, TMP, tween libs, EDM4U,
// asset-store packages, native plugins, and the settings assets those SDKs load by name.
//
// Every folder is scored on independent signals (any one strong signal is enough):
//   known vendor folder name · native binaries (.aar/.jar/.so/.dll/.framework…) · vendor asmdef ·
//   EDM4U *Dependencies.xml / link.xml / package.json / LICENSE · vendor root namespaces in its .cs ·
//   license/copyright headers in its .cs · known global-namespace SDK classes (MaxSdk, IronSource…)
// Folders are evaluated top-down; the highest folder that qualifies is excluded as a whole.
// Loose SDK files outside those folders (a MiniJSON.cs in Scripts/) are excluded individually.
// Assets outside SDK folders that an SDK loads by name (GoogleMobileAdsSettings, IAPProductCatalog…)
// are "protected": never renamed.
// Own-code markers (regex, from the profile) veto a folder: a script that matches one is game code, never an SDK.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace CodeObfuscator
{
    public class SdkFolder
    {
        public string path; public int score; public List<string> reasons = new List<string>();
        public string Reason => string.Join(", ", reasons);
    }

    public class SdkDetection
    {
        public List<SdkFolder> folders = new List<SdkFolder>();
        public List<string> files = new List<string>();           // loose SDK .cs files
        public List<string> protectedAssets = new List<string>(); // SDK-loaded assets outside SDK folders
    }

    public static class ObfSdkDetector
    {
        public const int Threshold = 60;

        static readonly HashSet<string> KnownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Plugins","GoogleMobileAds","Firebase","ExternalDependencyManager","PlayServicesResolver","GeneratedLocalRepo","Parse",
            "TextMesh Pro","TextMeshPro","Photon","PhotonUnityNetworking","PhotonRealtime","Facebook","FacebookSDK","AppLovin","MaxSdk",
            "IronSource","LevelPlay","UnityAds","Purchasing","UnityPurchasing","UDP","Adjust","AppsFlyer","Sirenix","Demigiant","DOTween",
            "GameAnalytics","OneSignal","Chartboost","Vungle","Tapjoy","InMobi","Mintegral","Pangle","Fyber","Kidoz","Amplitude","Mixpanel",
            "Sentry","Bugsnag","Crashlytics","PlayFab","Nakama","Steamworks.NET","Mirror","FishNet","Zenject","UniRx","UniTask","Cysharp",
            "Doozy","Michsky","RTLTMPro","I2","NatSuite","Rewired","Spine","EasyMobile","SgLib","Beebyte","Yodo1","Cinemachine","ProBuilder",
            "AmplifyShaderEditor","Amplify","Odin","PathCreator","AstarPathfindingProject","TriLib","Dreamteck","Crest","PixelCrushers",
            "Ludiq","Bolt","Fungus","InControl","Gley","Hellmade","Malbers","Invector","Opsive","JetBrains","Vuforia","Oculus","Meta","XR",
            "Samples","Standard Assets","Editor Default Resources","WebGLTemplates","AddressableAssetsData","StreamingAssets","Gizmos",
            "Kochava","Singular","Branch","Tenjin","Airbridge","Bytedance","TikTok","Snap","Ionic","ICSharpCode","LitJson","YamlDotNet",
            "Protobuf","Grpc","Amazon","AWSSDK","Azure","GooglePlayGames","GPGS","StansAssets","Mopsicus","Lofelt","FMOD","Wwise","CriWare",
            "PlayMaker","HutongGames","Animancer","DarkTonic","Tayx","Graphy","Easy Save","EasySave","ES3","Obi","MoreMountains","Feel",
            "Lean","LeanTween","LeanTouch","NativeGallery","NativeShare","NativeCamera","NativeFilePicker","UnityNativeToolkit","Coffee",
            "MEC","Kilt","Unity.Notifications","Newtonsoft","Json.NET","Mono.Data","SQLite","Xsolla","SuperSonic","Supersonic","Vundle",
            "Supyrb","BayatGames","Umbra","Voxel","Cartoon FX","JMO Assets","Epic Toon FX","Polybrush","ProCore","Unity-Chan",
            "HighlightPlus","Highlight Plus","Kronnect","AssetUsageDetector","LoopScrollRectSrc","LoopScrollRect","UIEffect","UIParticle"
        };

        static readonly Regex GenericFolderRe = new Regex(@"^(sdk|sdks|plugin|plugins|thirdparty|third_party|third-party|3rdparty|vendor|vendors|external|externals|lib|libs|libraries|middleware|packages)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly HashSet<string> NativeExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".dll",".aar",".jar",".so",".a",".bundle",".framework",".jslib",".mm",".m",".java",".kt",".swift",".plist",".srcaar",".pom",".xcframework",".dylib",".lib",".pdb",".winmd" };

        static readonly string[] VendorNsRoots =
        {
            "GoogleMobileAds","Google","Firebase","UnityEngine","UnityEditor","Unity","TMPro","DG","Facebook","Photon","ExitGames","AppLovin",
            "AppLovinMax","IronSource","Sirenix","Newtonsoft","GameAnalyticsSDK","OneSignalSDK","OneSignal","AppsFlyerSDK","AppsFlyer",
            "AdjustSdk","Cinemachine","Rewired","Spine","EasyMobile","SgLib","Beebyte","Yodo1","Chartboost","Vungle","Tapjoy","InMobi",
            "Mintegral","Pangle","Fyber","Amplitude","mixpanel","Mixpanel","Sentry","BugsnagUnity","Bugsnag","ProBuilder","AmplifyShaderEditor",
            "PlayFab","Nakama","Steamworks","Mirror","FishNet","Zenject","UniRx","Cysharp","MEC","Doozy","Michsky","RTLTMPro","I2","NatSuite",
            "Lean","Coffee","Obi","MoreMountains","PathCreation","Pathfinding","TriLib","Dreamteck","Crest","PixelCrushers","Ludiq","Fungus",
            "Yarn","Ink","InControl","Gley","Hellmade","MalbersAnimations","Invector","Opsive","JetBrains","Mono","System","SQLite","Vuforia",
            "Oculus","Meta","Valve","Epic","Xsolla","Kochava","Singular","BranchSdk","Tenjin","Airbridge","ByteDance","Bytedance","TikTok","Snap",
            "Ionic","ICSharpCode","LitJson","SimpleJSON","YamlDotNet","Grpc","Amazon","Microsoft","Azure","GooglePlayGames","StansAssets",
            "Mopsicus","Lofelt","FMOD","FMODUnity","AK","CriWare","HutongGames","Animancer","DarkTonic","Tayx","ES3Internal","BayatGames",
            "Supyrb","Kilt","NUnit","Moq","Umbra","JMO","Polybrush","HighlightPlus","Kronnect","AssetUsageDetectorNamespace","Coffee.UIEffects","Coffee.UIExtensions","ToonyColorsPro"
        };

        static readonly HashSet<string> KnownGlobalClasses = new HashSet<string>
        {
            "MaxSdk","MaxSdkBase","MaxSdkCallbacks","MaxSdkUtils","IronSource","IronSourceEvents","IronSourceConfig","LeanTween","LTDescr",
            "MiniJSON","Json","SimpleJSON","NativeGallery","NativeShare","NativeCamera","NativeFilePicker","ES3","ES3Settings","ES3File",
            "GameAnalytics","OneSignal","AppsFlyer","Adjust","AdjustConfig","Tenjin","Yodo1U3dMas","ChartboostSDK","TapjoyUnity","Vungle",
            "Timing","UnityMainThreadDispatcher","GooglePlayGames","PlayGamesPlatform","IAPManager","EasyMobile","Supersonic",
            "UnityIAPBilling","DOTween","DOVirtual","Ease","Tweener","Sequence","AdColony","UnityAdsManager","MobileAds","GADU"
        };

        static readonly Regex HeaderRe = new Regex(
            @"(copyright\s*(\(c\)|©)?\s*\d{4}|licensed under the|all rights reserved|mit license|apache license|permission is hereby granted|\bgnu\b.*license|the software is provided ""as is"")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // v5: no built-in own-code markers — a project whose scripts carry SDK-like signals (license headers, a vendor-style
        // root namespace) lists its own in the profile (`reconstructionMarkers`). "(?!)" never matches.
        static readonly Regex ReconstructionRe = new Regex(@"(?!)", RegexOptions.Compiled);

        static readonly HashSet<string> KnownSdkAssetStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GoogleMobileAdsSettings","google-services","google-services-desktop","GoogleService-Info","FirebaseSettings","FirebaseAnalyticsSettings",
            "IAPProductCatalog","BillingMode","ProductCatalog","UnityPurchasingSettings","UdpSettings","UnityAdsSettings","AppLovinSettings",
            "MaxSdkSettings","IronSourceMediationSettings","IronSourceMediatedNetworkSettings","AppsFlyerSettings","AdjustSettings",
            "GameAnalytics Settings","GameAnalyticsSettings","OneSignalSettings","FacebookSettings","TMP Settings","TMP Default Font","LineBreaking Leading Characters",
            "LineBreaking Following Characters","EmojiOne","DOTweenSettings","Rewired_Data","ES3Defaults","ES3 Settings","link","csc","mcs",
            "AmplitudeSettings","ChartboostSettings","VungleSettings","TapjoySettings","InMobiSettings","MintegralSettings","PangleSettings",
            "FyberSettings","KidozSettings","YodoSettings","Yodo1MasSettings","SentryOptions","BugsnagSettings","PlayFabSharedSettings",
            "PhotonServerSettings","OculusProjectConfig","AddressableAssetSettings","GameAnalyticsSDK","AnalyticsSettings","NotificationSettings"
        };

        class FileSig { public bool native, cs, header, vendorNs, globalClass, recon, edm, meta, asmdef, vendorAsmdef; }

        /// v4: the profile's extensions merged with the built-in lists (built once per Detect call).
        class Lists
        {
            public HashSet<string> folders = new HashSet<string>(KnownFolders, StringComparer.OrdinalIgnoreCase);
            public HashSet<string> nsRoots = new HashSet<string>(VendorNsRoots, StringComparer.Ordinal);
            public HashSet<string> globals = new HashSet<string>(KnownGlobalClasses, StringComparer.Ordinal);
            public HashSet<string> assetStems = new HashSet<string>(KnownSdkAssetStems, StringComparer.OrdinalIgnoreCase);
            public Regex recon = ReconstructionRe;

            public Lists(ObfSettings s)
            {
                if (s == null) return;
                foreach (var v in s.vendorFolders ?? new string[0]) if (!string.IsNullOrEmpty(v)) folders.Add(v.Trim().TrimEnd('/'));
                foreach (var v in s.vendorNamespaces ?? new string[0]) if (!string.IsNullOrEmpty(v)) nsRoots.Add(v.Trim());
                foreach (var v in s.sdkClasses ?? new string[0]) if (!string.IsNullOrEmpty(v)) globals.Add(v.Trim());
                foreach (var v in s.sdkAssetStems ?? new string[0]) if (!string.IsNullOrEmpty(v)) assetStems.Add(v.Trim());
                var extra = (s.reconstructionMarkers ?? new string[0]).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToList();
                if (extra.Count > 0)
                {
                    try { recon = new Regex("(?:" + ReconstructionRe + ")|(?:" + string.Join(")|(?:", extra) + ")", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)); }
                    catch (ArgumentException) { recon = ReconstructionRe; }   // a bad user regex disables the markers instead of failing the scan
                }
            }
        }

        public static SdkDetection Detect(ObfProjectIndex idx, System.Threading.CancellationToken ct = default, ObfProgress prog = null)
            => Detect(idx, null, ct, prog);

        /// Pure: only reads files listed in the index. Safe to run on the worker thread; cancellable.
        public static SdkDetection Detect(ObfProjectIndex idx, ObfSettings settings, System.Threading.CancellationToken ct = default, ObfProgress prog = null)
        {
            var det = new SdkDetection();
            var L = new Lists(settings);
            string root = idx.projectRoot;
            string tool = idx.toolFolder;
            bool InTool(string ap) => (tool.Length > 0 && (ap == tool || ap.StartsWith(tool + "/", StringComparison.Ordinal))) || ap.Contains("/CodeObfuscator/");

            // ---- per-file signals (one read per .cs, header only) ----
            var sig = new Dictionary<string, FileSig>();
            int done = 0;
            foreach (var pf in idx.files)
            {
                ct.ThrowIfCancellationRequested();
                if (prog != null && (++done & 255) == 0) prog.Set("Detecting SDKs", pf.path, done, idx.files.Count);
                string ap = pf.path;
                string abs = idx.Abs(ap);
                if (InTool(ap)) continue;
                string ext = Path.GetExtension(ap);
                string stem = Path.GetFileNameWithoutExtension(ap);
                var s = new FileSig();
                if (ext.Equals(".meta", StringComparison.OrdinalIgnoreCase)) { s.meta = true; sig[ap] = s; continue; }
                if (NativeExt.Contains(ext)) s.native = true;
                if (ext.Equals(".asmdef", StringComparison.OrdinalIgnoreCase))
                {
                    s.asmdef = true;
                    string n = stem;
                    if (L.nsRoots.Any(v => n.StartsWith(v + ".", StringComparison.OrdinalIgnoreCase) || n.Equals(v, StringComparison.OrdinalIgnoreCase)) || L.folders.Contains(n.Split('.')[0]))
                        s.vendorAsmdef = true;
                }
                if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) && (stem.EndsWith("Dependencies", StringComparison.OrdinalIgnoreCase) || stem.Equals("link", StringComparison.OrdinalIgnoreCase))) s.edm = true;
                if (stem.Equals("package", StringComparison.OrdinalIgnoreCase) && ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) s.edm = true;
                if (stem.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || stem.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase) || stem.Equals("VERSION", StringComparison.OrdinalIgnoreCase)) s.edm = true;
                if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    s.cs = true;
                    string head = ReadHead(abs, 4000);
                    if (HeaderRe.IsMatch(head.Substring(0, Math.Min(head.Length, 1200)))) s.header = true;
                    try { if (L.recon.IsMatch(head)) s.recon = true; } catch (RegexMatchTimeoutException) { }
                    var ns = Regex.Match(head, @"(?<![\w.])namespace\s+([A-Za-z_]\w*)");
                    if (ns.Success && L.nsRoots.Contains(ns.Groups[1].Value)) s.vendorNs = true;
                    var cls = Regex.Match(head, @"(?<![\w.])(?:class|struct)\s+([A-Za-z_]\w*)");
                    if (cls.Success && L.globals.Contains(cls.Groups[1].Value)) s.globalClass = true;
                    if (stem == "AssemblyInfo") s.header = true;
                }
                sig[ap] = s;
            }

            // ---- folder scoring, top-down; first qualifying ancestor wins ----
            var dirs = idx.dirs
                .Where(d => !InTool(d))
                .OrderBy(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal).ToList();

            var sdkDirs = new List<SdkFolder>();
            foreach (var d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                if (sdkDirs.Any(x => d == x.path || d.StartsWith(x.path + "/", StringComparison.Ordinal))) continue;
                var f = Score(d, sig, L);
                if (f != null) sdkDirs.Add(f);
            }
            det.folders = sdkDirs;

            bool InSdk(string ap) => sdkDirs.Any(x => ap.StartsWith(x.path + "/", StringComparison.Ordinal));

            // ---- loose SDK files outside SDK folders ----
            foreach (var kv in sig)
                if (kv.Value.cs && !kv.Value.recon && (kv.Value.header || kv.Value.vendorNs || kv.Value.globalClass) && !InSdk(kv.Key))
                    det.files.Add(kv.Key);

            // ---- protected assets: known SDK settings, or referenced by name from SDK code ----
            var sdkLiterals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in sig)
            {
                if (!kv.Value.cs) continue;
                if (!(InSdk(kv.Key) || det.files.Contains(kv.Key))) continue;
                string t; try { t = File.ReadAllText(root + "/" + kv.Key); } catch { continue; }
                foreach (Match m in Regex.Matches(t, "\"([^\"\\n]{2,80})\"")) sdkLiterals.Add(m.Groups[1].Value);
            }
            foreach (var kv in sig)
            {
                if (kv.Value.meta || kv.Value.cs || InSdk(kv.Key) || det.files.Contains(kv.Key)) continue;
                string stem = Path.GetFileNameWithoutExtension(kv.Key);
                string leaf = Path.GetFileName(kv.Key);
                bool prot = L.assetStems.Contains(stem) || sdkLiterals.Contains(stem) || sdkLiterals.Contains(leaf);
                if (!prot)
                {
                    int r = kv.Key.IndexOf("/Resources/", StringComparison.Ordinal);
                    if (r >= 0)
                    {
                        string rel = kv.Key.Substring(r + 11); int dot = rel.LastIndexOf('.'); if (dot > rel.LastIndexOf('/')) rel = rel.Substring(0, dot);
                        prot = sdkLiterals.Contains(rel);
                    }
                }
                if (prot) det.protectedAssets.Add(kv.Key);
            }
            det.protectedAssets.Sort(StringComparer.Ordinal);
            det.files.Sort(StringComparer.Ordinal);
            return det;
        }

        static SdkFolder Score(string dir, Dictionary<string, FileSig> sig, Lists L)
        {
            string prefix = dir + "/";
            int total = 0, cs = 0, native = 0, header = 0, vendorNs = 0, globalCls = 0, recon = 0, edm = 0, asmdef = 0, vendorAsmdef = 0;
            foreach (var kv in sig)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) || kv.Value.meta) continue;
                var s = kv.Value; total++;
                if (s.cs) { cs++; if (s.header) header++; if (s.vendorNs) vendorNs++; if (s.globalClass) globalCls++; if (s.recon) recon++; }
                if (s.native) native++;
                if (s.edm) edm++;
                if (s.asmdef) asmdef++;
                if (s.vendorAsmdef) vendorAsmdef++;
            }
            if (total == 0) return null;

            var f = new SdkFolder { path = dir };
            string leaf = Path.GetFileName(dir);
            void Add(int pts, string why) { f.score += pts; f.reasons.Add(why); }

            if (L.folders.Contains(leaf)) Add(100, "known vendor folder");
            else if (GenericFolderRe.IsMatch(leaf)) Add(35, "generic third-party folder name");
            if (native > 0) Add(70, native + " native binar" + (native == 1 ? "y" : "ies"));
            if (vendorAsmdef > 0) Add(70, "vendor asmdef");
            else if (asmdef > 0) Add(20, "asmdef present");
            if (edm > 0) Add(50, "EDM4U/package files");
            if (cs > 0)
            {
                float hr = (float)header / cs, nr = (float)vendorNs / cs, gr = (float)globalCls / cs;
                if (hr >= 0.5f) Add((int)(60 * hr), (int)(hr * 100) + "% license headers");
                if (nr >= 0.5f) Add((int)(70 * nr), (int)(nr * 100) + "% vendor namespaces");
                if (gr >= 0.5f) Add((int)(70 * gr), "known SDK classes");
                if (recon > 0) { Add(-200, recon + " own-code file(s) (profile markers) — vetoed"); }
            }
            return f.score >= Threshold ? f : null;
        }

        static string ReadHead(string abs, int max)
        {
            try
            {
                using (var sr = new StreamReader(abs))
                {
                    var buf = new char[max]; int n = sr.Read(buf, 0, max);
                    return new string(buf, 0, Math.Max(0, n));
                }
            }
            catch { return ""; }
        }
    }
}
