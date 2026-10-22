// Code Obfuscator — collision-free new-name generator (same old name -> same new name).
//
// Every Pick is O(1) amortised. The word pools are small (a few thousand combinations per
// category) while a real project has tens of thousands of identifiers, so most names end up with
// a numeric suffix; a per-base-name counter hands those out without re-scanning. (v2 tried up to
// 4096 combinations per name and then searched suffixes from 2 upwards each time — quadratic once
// the pool was exhausted, which stalled the scan for minutes on a project this size.)
using System.Collections.Generic;

namespace CodeObfuscator
{
    public class ObfNameGenerator
    {
        readonly HashSet<string> _used = new HashSet<string>();
        readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        readonly Dictionary<string, int> _nextSuffix = new Dictionary<string, int>();

        readonly string[] Nouns, Adjs, Verbs;

        public ObfNameGenerator() : this(null) { }

        /// v4: the word pools can be replaced from the profile (one word per line), so two projects built
        /// on the same codebase — or two projects of the same studio — do not end up with the same generated vocabulary.
        public ObfNameGenerator(ObfSettings s)
        {
            Nouns = Pool(s?.nameNouns, DefaultNouns);
            Adjs  = Pool(s?.nameAdjectives, DefaultAdjs);
            Verbs = Pool(s?.nameVerbs, DefaultVerbs);
        }

        static string[] Pool(string[] custom, string[] fallback)
        {
            if (custom == null) return fallback;
            var list = new List<string>();
            foreach (var w in custom)
            {
                if (string.IsNullOrEmpty(w)) continue;
                string t = w.Trim();
                if (t.Length < 2 || !System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z][A-Za-z0-9]*$")) continue;
                t = char.ToUpperInvariant(t[0]) + t.Substring(1);
                if (!list.Contains(t)) list.Add(t);
            }
            return list.Count >= 4 ? list.ToArray() : fallback;   // fewer than 4 words is not a pool
        }

        static readonly string[] DefaultNouns =
        {
            "Core","Handler","Controller","Manager","Renderer","Builder","Provider","Service","Unit","Node",
            "Module","System","Engine","Factory","Registry","Binder","Adapter","Pool","Cache","Store",
            "Runner","Driver","Bridge","Emitter","Tracker","Router","Loader","Writer","Reader","Context",
            "Shape","Block","Panel","Widget","Frame","Layer","Slot","Cell","Grid","Zone","Beacon","Relay"
        };
        static readonly string[] DefaultAdjs =
        {
            "Prime","Quiet","Rapid","Solid","Fluent","Basic","Swift","Calm","Bright","Nimble",
            "Alpha","Delta","Vector","Nova","Lumen","Aero","Flux","Orbit","Pulse","Arc","Ember","Vale"
        };
        static readonly string[] DefaultVerbs =
        {
            "Resolve","Build","Fetch","Apply","Compute","Prepare","Emit","Bind","Refresh","Handle",
            "Gather","Store","Update","Track","Route","Wrap","Scale","Compose","Init","Sync","Collect","Map"
        };

        public void Reserve(IEnumerable<string> existing)
        {
            foreach (var n in existing) if (!string.IsNullOrEmpty(n)) _used.Add(n);
        }

        // Identifier categories share the cache key (old name only) so a name that is e.g. both a
        // namespace segment and a class maps to ONE new identifier — otherwise the rename map
        // would contain two conflicting targets for the same token.
        public string ForNamespace(string old) => Pick(old, s => Adjs[s % Adjs.Length] + Nouns[(s / Adjs.Length) % Nouns.Length] + "Space");
        public string ForClass(string old)     => Pick(old, s => Adjs[s % Adjs.Length] + Nouns[(s / Adjs.Length) % Nouns.Length]);
        public string ForMethod(string old)    => Pick(old, s => Verbs[s % Verbs.Length] + Nouns[(s / Verbs.Length) % Nouns.Length]);
        public string ForEnumMember(string old)=> Pick(old, s => Adjs[s % Adjs.Length] + Nouns[(s / Adjs.Length) % Nouns.Length] + "Kind");
        public string ForGameObject(string old)=> Pick("go:" + old, s => Adjs[s % Adjs.Length] + Nouns[(s / Adjs.Length) % Nouns.Length]);
        public string ForAssetLeaf(string old) => Pick("as:" + old, s => Lower(Adjs[s % Adjs.Length]) + "_" + Lower(Nouns[(s / Adjs.Length) % Nouns.Length]));
        public string ForKey(string old)       => Pick("ky:" + old, s => Lower(Adjs[s % Adjs.Length]) + Nouns[(s / Adjs.Length) % Nouns.Length]);

        public string ForField(string old)
        {
            bool underscore = old.StartsWith("_") || old.StartsWith("s_") || old.StartsWith("m_");
            return Pick(old, s =>
            {
                string b = Lower(Nouns[s % Nouns.Length]) + Nouns[(s / Nouns.Length) % Nouns.Length];
                return underscore ? "_" + b : b;
            });
        }

        const int k_Attempts = 24;

        string Pick(string key, System.Func<int, string> build)
        {
            if (_cache.TryGetValue(key, out var v)) return v;
            int seed = Hash(key);
            string first = null;
            for (int i = 0; i < k_Attempts; i++)
            {
                string name = build(seed);
                if (first == null) first = name;
                if (_used.Add(name)) { _cache[key] = name; return name; }
                seed = Mix(seed);
            }
            // pool exhausted around this key: numbered variant, counter per base so it never re-scans
            _nextSuffix.TryGetValue(first, out int suffix);
            if (suffix < 2) suffix = 2;
            string fallback;
            do { fallback = first + suffix; suffix++; } while (!_used.Add(fallback));
            _nextSuffix[first] = suffix;
            _cache[key] = fallback;
            return fallback;
        }

        static string Lower(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        static int Hash(string s)
        {
            unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h & 0x7fffffff; }
        }

        static int Mix(int s)
        {
            unchecked { uint x = (uint)s * 2654435761u + 0x9E3779B9u; x ^= x >> 15; return (int)(x & 0x7fffffff); }
        }
    }
}
