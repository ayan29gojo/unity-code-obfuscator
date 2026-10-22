// Code Obfuscator — region-aware C# text processor.
//
// One single-pass lexer (linear in the input length — every loop iteration consumes at least one
// character, so it can never hang), four uses:
//   TransformIdentifiers(src, map)  rename whole-word identifiers in CODE only (skips comments,
//                                   strings, chars; still renames inside $"{...}" holes)
//   StripComments(src)              delete // and /* */ comments (block comments -> one space)
//   CodeOnly(src)                   comments removed + string/char contents blanked: use this as
//                                   the input for any declaration/usage regex so text inside
//                                   strings and comments can never produce false positives
//   CodeOnlyAligned(src)            like CodeOnly but comments are blanked, not removed, so every
//                                   offset matches the original text (used to edit the original in place)
// Preprocessor lines (#if, #region, ...) are copied verbatim and never renamed.
//
// FindDeclarations(...) is the linear, line-based declaration finder that replaced the v2 regexes
// (`[\w<>\[\].,?\s]+?\s+name` and the FormerlySerializedAs pattern) whose nested quantifiers
// backtracked exponentially on long generic type names.
using System;
using System.Collections.Generic;
using System.Text;

namespace CodeObfuscator
{
    public static class ObfCSharpText
    {
        sealed class Opt
        {
            public Dictionary<string, string> map;
            public bool stripComments;
            public bool blankStrings;
            public bool aligned;      // keep length: blank instead of delete
            public int replaced;
        }

        public static string TransformIdentifiers(string src, Dictionary<string, string> map, out int replaced)
        {
            replaced = 0;
            if (string.IsNullOrEmpty(src) || map == null || map.Count == 0) return src;
            var o = new Opt { map = map };
            string r = Run(src, o);
            replaced = o.replaced;
            return r;
        }

        public static string StripComments(string src) =>
            string.IsNullOrEmpty(src) ? src : Run(src, new Opt { stripComments = true });

        public static string CodeOnly(string src) =>
            string.IsNullOrEmpty(src) ? src : Run(src, new Opt { stripComments = true, blankStrings = true });

        public static string CodeOnlyAligned(string src) =>
            string.IsNullOrEmpty(src) ? src : Run(src, new Opt { stripComments = true, blankStrings = true, aligned = true });

        static string Run(string s, Opt o)
        {
            var sb = new StringBuilder(s.Length + 64);
            int i = 0;
            ScanCode(s, ref i, sb, o, inHole: false);
            return sb.ToString();
        }

        // a character that is being stripped: dropped, or blanked when aligned
        static void Drop(StringBuilder sb, Opt o, char c)
        {
            if (o.aligned) sb.Append(c == '\n' || c == '\r' ? c : ' ');
        }

        static void ScanCode(string s, ref int i, StringBuilder sb, Opt o, bool inHole)
        {
            int n = s.Length;
            int braceDepth = 0;

            while (i < n)
            {
                char c = s[i];

                if (inHole && c == '}' && braceDepth == 0) return;

                // preprocessor directive: '#' as first non-blank char on the line -> copy line verbatim
                // (#region / #endregion carry human labels, so they go with the comments when stripping)
                if (c == '#' && AtLineStart(s, i))
                {
                    bool region = o.stripComments && (string.CompareOrdinal(s, i, "#region", 0, 7) == 0 || string.CompareOrdinal(s, i, "#endregion", 0, 10) == 0);
                    if (region) { if (!o.aligned) TrimTrailingSpaces(sb); while (i < n && s[i] != '\n') { Drop(sb, o, s[i]); i++; } }
                    else while (i < n && s[i] != '\n') { sb.Append(s[i]); i++; }
                    continue;
                }

                if (IsIdentStart(c))
                {
                    int start = i; i++;
                    while (i < n && IsIdentPart(s[i])) i++;
                    string ident = s.Substring(start, i - start);
                    if (o.map != null && o.map.TryGetValue(ident, out string repl)) { sb.Append(repl); o.replaced++; }
                    else sb.Append(ident);
                    continue;
                }

                if (c == '/' && i + 1 < n && s[i + 1] == '/')
                {
                    if (o.stripComments)
                    {
                        // drop text up to (not including) the newline; also drop trailing spaces before '//'
                        if (!o.aligned) TrimTrailingSpaces(sb);
                        while (i < n && s[i] != '\n') { Drop(sb, o, s[i]); i++; }
                    }
                    else while (i < n && s[i] != '\n') { sb.Append(s[i]); i++; }
                    continue;
                }
                if (c == '/' && i + 1 < n && s[i + 1] == '*')
                {
                    if (!o.stripComments) sb.Append("/*"); else { Drop(sb, o, '/'); Drop(sb, o, '*'); }
                    i += 2;
                    while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/')) { if (!o.stripComments) sb.Append(s[i]); else Drop(sb, o, s[i]); i++; }
                    if (i < n)
                    {
                        if (!o.stripComments) sb.Append("*/");
                        else if (o.aligned) { Drop(sb, o, '*'); Drop(sb, o, '/'); }
                        else sb.Append(' ');
                        i += 2;
                    }
                    continue;
                }

                if (c == '$' && i + 1 < n && s[i + 1] == '"')
                { sb.Append("$\""); i += 2; ScanInterp(s, ref i, sb, o, false); continue; }
                if (c == '$' && i + 2 < n && s[i + 1] == '@' && s[i + 2] == '"')
                { sb.Append("$@\""); i += 3; ScanInterp(s, ref i, sb, o, true); continue; }
                if (c == '@' && i + 2 < n && s[i + 1] == '$' && s[i + 2] == '"')
                { sb.Append("@$\""); i += 3; ScanInterp(s, ref i, sb, o, true); continue; }
                if (c == '@' && i + 1 < n && s[i + 1] == '"')
                { sb.Append("@\""); i += 2; ScanVerbatim(s, ref i, sb, o); continue; }
                if (c == '"')
                { sb.Append('"'); i++; ScanString(s, ref i, sb, o); continue; }
                if (c == '\'')
                { sb.Append('\''); i++; ScanChar(s, ref i, sb, o); continue; }

                if (c == '{') braceDepth++;
                else if (c == '}' && braceDepth > 0) braceDepth--;

                sb.Append(c); i++;
            }
        }

        static void Lit(StringBuilder sb, Opt o, char c) => sb.Append(o.blankStrings && c != '\n' && c != '\r' ? ' ' : c);

        static void ScanString(string s, ref int i, StringBuilder sb, Opt o)
        {
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < n) { Lit(sb, o, c); Lit(sb, o, s[i + 1]); i += 2; continue; }
                if (c == '"') { sb.Append('"'); i++; return; }
                if (c == '\n') { sb.Append('\n'); i++; return; }
                Lit(sb, o, c); i++;
            }
        }

        static void ScanChar(string s, ref int i, StringBuilder sb, Opt o)
        {
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < n) { Lit(sb, o, c); Lit(sb, o, s[i + 1]); i += 2; continue; }
                if (c == '\'') { sb.Append('\''); i++; return; }
                if (c == '\n') { sb.Append('\n'); i++; return; }
                Lit(sb, o, c); i++;
            }
        }

        static void ScanVerbatim(string s, ref int i, StringBuilder sb, Opt o)
        {
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '"')
                {
                    if (i + 1 < n && s[i + 1] == '"') { Lit(sb, o, '"'); Lit(sb, o, '"'); i += 2; continue; }
                    sb.Append('"'); i++; return;
                }
                Lit(sb, o, c); i++;
            }
        }

        static void ScanInterp(string s, ref int i, StringBuilder sb, Opt o, bool verbatim)
        {
            int n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (!verbatim && c == '\\' && i + 1 < n) { Lit(sb, o, c); Lit(sb, o, s[i + 1]); i += 2; continue; }
                if (c == '{')
                {
                    if (i + 1 < n && s[i + 1] == '{') { Lit(sb, o, '{'); Lit(sb, o, '{'); i += 2; continue; }
                    sb.Append('{'); i++;
                    ScanCode(s, ref i, sb, o, inHole: true);
                    if (i < n && s[i] == '}') { sb.Append('}'); i++; }
                    continue;
                }
                if (c == '}')
                {
                    if (i + 1 < n && s[i + 1] == '}') { Lit(sb, o, '}'); Lit(sb, o, '}'); i += 2; continue; }
                    Lit(sb, o, '}'); i++; continue;
                }
                if (c == '"')
                {
                    if (verbatim && i + 1 < n && s[i + 1] == '"') { Lit(sb, o, '"'); Lit(sb, o, '"'); i += 2; continue; }
                    sb.Append('"'); i++; return;
                }
                Lit(sb, o, c); i++;
            }
        }

        static bool AtLineStart(string s, int i)
        {
            for (int k = i - 1; k >= 0; k--)
            {
                char c = s[k];
                if (c == '\n') return true;
                if (c != ' ' && c != '\t' && c != '\r') return false;
            }
            return true;
        }

        static void TrimTrailingSpaces(StringBuilder sb)
        {
            int k = sb.Length;
            while (k > 0 && (sb[k - 1] == ' ' || sb[k - 1] == '\t')) k--;
            sb.Length = k;
        }

        public static bool IsIdentStart(char c) => c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        public static bool IsIdentPart(char c) => IsIdentStart(c) || (c >= '0' && c <= '9');

        // ============================================================================================
        //  Linear declaration finder
        // ============================================================================================

        public enum DeclKind { Field, Method }

        public class Decl
        {
            public DeclKind kind;
            public string name;
            public int lineStart;          // offset of the first char of the line in the source
            public int insertAt;           // offset of the first non-blank char of the declaration line
            public HashSet<string> modifiers = new HashSet<string>(StringComparer.Ordinal);
            public string attributes = ""; // raw attribute text on this + preceding attribute-only lines
            public bool multiDeclarator;   // "int a, b;" — only the first name is reported
            public bool hasAccessModifier => modifiers.Contains("public") || modifiers.Contains("private") || modifiers.Contains("protected") || modifiers.Contains("internal");
            public bool isPublic => modifiers.Contains("public");
        }

        static readonly HashSet<string> Modifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public","private","protected","internal","static","readonly","const","volatile","new","virtual","sealed",
            "abstract","override","async","unsafe","extern","partial","event","fixed","ref","required"
        };

        static readonly HashSet<string> NotANameOrType = new HashSet<string>(StringComparer.Ordinal)
        {
            "if","for","while","switch","foreach","return","using","lock","catch","throw","await","yield","case","else","do",
            "try","finally","goto","break","continue","namespace","class","struct","interface","enum","delegate","operator",
            "get","set","add","remove","init","where","in","out","params","this","base","is","as","typeof","sizeof","nameof",
            "default","checked","unchecked","implicit","explicit","record","var"
        };

        /// Finds member-level field/method declarations. `aligned` must be CodeOnlyAligned(src) (or
        /// CodeOnly(src) when offsets are not needed). Runs in O(length) — one pass over the lines,
        /// no regular expressions.
        public static List<Decl> FindDeclarations(string aligned)
        {
            var result = new List<Decl>();
            if (string.IsNullOrEmpty(aligned)) return result;
            string pendingAttrs = "";
            int pos = 0, n = aligned.Length;
            while (pos < n)
            {
                int eol = aligned.IndexOf('\n', pos); if (eol < 0) eol = n;
                int lineStart = pos;
                pos = eol + 1;

                // trim
                int a = lineStart, b = eol;
                while (a < b && (aligned[a] == ' ' || aligned[a] == '\t' || aligned[a] == '\r')) a++;
                while (b > a && (aligned[b - 1] == ' ' || aligned[b - 1] == '\t' || aligned[b - 1] == '\r')) b--;
                if (a >= b) { continue; }
                if (aligned[a] == '#' || aligned[a] == '}' || aligned[a] == '{') { pendingAttrs = ""; continue; }

                // leading attributes
                int p = a; var attrs = new StringBuilder(pendingAttrs);
                while (p < b && aligned[p] == '[')
                {
                    int depth = 0, q = p;
                    for (; q < b; q++) { if (aligned[q] == '[') depth++; else if (aligned[q] == ']') { depth--; if (depth == 0) break; } }
                    if (q >= b) break; // unbalanced — give up on this line
                    attrs.Append(aligned, p, q - p + 1).Append(' ');
                    p = q + 1;
                    while (p < b && (aligned[p] == ' ' || aligned[p] == '\t')) p++;
                }
                if (p >= b) { pendingAttrs = attrs.ToString(); continue; } // attribute-only line: carry to next
                pendingAttrs = "";

                // head: up to the first structural boundary
                int bound = -1; char bc = '\0'; bool arrow = false;
                for (int q = p; q < b; q++)
                {
                    char c = aligned[q];
                    if (c == '=' || c == ';' || c == '(' || c == '{')
                    {
                        if (c == '=' && q + 1 < b && (aligned[q + 1] == '>' || aligned[q + 1] == '=')) { arrow = true; break; }
                        bound = q; bc = c; break;
                    }
                }
                if (arrow || bound < 0) continue;
                if (bc == '{') continue; // property / type / block

                string head = aligned.Substring(p, bound - p).Trim();
                if (head.Length == 0) continue;
                var tokens = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue; // needs at least "type name"

                var d = new Decl { lineStart = lineStart, insertAt = a, attributes = attrs.ToString() };
                int t = 0;
                while (t < tokens.Length && Modifiers.Contains(tokens[t])) { d.modifiers.Add(tokens[t]); t++; }
                if (!d.hasAccessModifier) continue; // v2 semantics: only explicitly-scoped members
                if (tokens.Length - t < 2) continue; // type + name required

                string last = tokens[tokens.Length - 1];
                if (bc == '(')
                {
                    // method: strip trailing generic parameter list
                    if (last.EndsWith(">"))
                    {
                        int lt = last.IndexOf('<'); if (lt <= 0) continue;
                        last = last.Substring(0, lt);
                    }
                    if (last.Contains(".")) continue; // explicit interface implementation
                    if (d.modifiers.Contains("event") || d.modifiers.Contains("override") || head.Contains("operator")) continue;
                    if (!IsIdentifier(last) || NotANameOrType.Contains(last)) continue;
                    d.kind = DeclKind.Method; d.name = last;
                }
                else
                {
                    // field / event: name is the last identifier token before '=' or ';'
                    if (head.IndexOf('(') >= 0) continue;
                    string nm = last;
                    int comma = nm.IndexOf(',');
                    if (comma >= 0) { nm = nm.Substring(0, comma); d.multiDeclarator = true; }
                    if (!IsIdentifier(nm) || NotANameOrType.Contains(nm)) continue;
                    // "int a, b;" where "a," was its own token
                    if (tokens.Length >= 3 && tokens[tokens.Length - 2].EndsWith(",")) d.multiDeclarator = true;
                    d.kind = DeclKind.Field; d.name = nm;
                }
                result.Add(d);
            }
            return result;
        }

        public static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || !IsIdentStart(s[0])) return false;
            for (int i = 1; i < s.Length; i++) if (!IsIdentPart(s[i])) return false;
            return true;
        }
    }
}
