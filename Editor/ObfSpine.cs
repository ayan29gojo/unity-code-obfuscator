// Code Obfuscator — Spine skeleton scrub (stage: ScrubBinaries).
//
// Spine exports the artist's machine paths into every skeleton: the binary header carries
// `imagesPath` / `audioPath` ("C:/Users/<artist>/Desktop/…", "C:/Users/<studio>/Downloads/…") and the
// JSON form has "skeleton": { "images": …, "audio": … }. Both are "nonessential" data the runtime never
// reads, but they ship verbatim inside the build's TextAssets and name the people who made them.
// Pure functions (no Unity API) — safe on the worker thread. Unknown layouts are left untouched.
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeObfuscator
{
    public static class ObfSpine
    {
        /// Returns the scrubbed bytes, or null when the file is not a recognised Spine binary or has nothing to scrub.
        public static byte[] ScrubBinary(byte[] data, out string note)
        {
            note = null;
            if (data == null || data.Length < 24) return null;
            try
            {
                // ---- 3.8 … 4.1: hash is a string; 4.2+: hash is an int64 ----
                int pos = 0;
                string version = null; int headerAfterVersion = -1;
                if (TryReadString(data, ref pos, out var hashStr) && hashStr != null && hashStr.Length <= 64 && TryReadString(data, ref pos, out version) && LooksLikeVersion(version))
                    headerAfterVersion = pos;
                else
                {
                    pos = 8; // int64 hash
                    if (TryReadString(data, ref pos, out version) && LooksLikeVersion(version)) headerAfterVersion = pos;
                }
                if (headerAfterVersion < 0) return null;

                var v = ParseVersion(version);
                pos = headerAfterVersion + 16;                 // x, y, width, height (4 × float32)
                if (v.major > 4 || (v.major == 4 && v.minor >= 2)) pos += 4; // referenceScale (4.2+)
                if (pos >= data.Length) return null;
                bool nonessential = data[pos] != 0; pos++;
                if (!nonessential) return null;               // no paths stored
                pos += 4;                                     // fps
                int imagesStart = pos;
                if (!TryReadString(data, ref pos, out var images)) return null;
                int audioStart = pos;
                if (!TryReadString(data, ref pos, out var audio)) return null;
                int rest = pos;
                if (string.IsNullOrEmpty(images) && string.IsNullOrEmpty(audio)) return null;

                var outp = new byte[imagesStart + 2 + (data.Length - rest)];
                Buffer.BlockCopy(data, 0, outp, 0, imagesStart);
                outp[imagesStart] = 1; outp[imagesStart + 1] = 1;   // two empty strings (count 1 = "")
                Buffer.BlockCopy(data, rest, outp, imagesStart + 2, data.Length - rest);
                note = "Spine " + version + ": imagesPath \"" + Trunc(images) + "\", audioPath \"" + Trunc(audio) + "\" blanked";
                return outp;
            }
            catch { return null; }
        }

        /// JSON skeleton: blanks "images" / "audio" inside the "skeleton" object. Returns null when unchanged.
        public static string ScrubJson(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("\"spine\"", StringComparison.Ordinal) < 0) return null;
            var m = Regex.Match(text, @"""skeleton""\s*:\s*\{", RegexOptions.None, TimeSpan.FromSeconds(5));
            if (!m.Success) return null;
            int open = m.Index + m.Length - 1, depth = 0, close = -1;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}') { depth--; if (depth == 0) { close = i; break; } }
            }
            if (close < 0) return null;
            string head = text.Substring(open, close - open + 1);
            string scrubbed = Regex.Replace(head, @"(""(?:images|audio)""\s*:\s*"")([^""]*)("")", mm => mm.Groups[2].Value.Length == 0 ? mm.Value : mm.Groups[1].Value + mm.Groups[3].Value, RegexOptions.None, TimeSpan.FromSeconds(5));
            if (scrubbed == head) return null;
            return text.Substring(0, open) + scrubbed + text.Substring(close + 1);
        }

        // ---- Spine binary primitives (big-endian floats, varint-prefixed UTF-8 strings) ----
        static bool TryReadVarint(byte[] d, ref int pos, out int value)
        {
            value = 0; int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                if (pos >= d.Length) return false;
                int b = d[pos++];
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        static bool TryReadString(byte[] d, ref int pos, out string s)
        {
            s = null;
            if (!TryReadVarint(d, ref pos, out int count)) return false;
            if (count == 0) return true;               // null
            int len = count - 1;
            if (len < 0 || pos + len > d.Length || len > 4096) return false;
            s = Encoding.UTF8.GetString(d, pos, len);
            pos += len;
            return true;
        }

        static bool LooksLikeVersion(string v) => v != null && Regex.IsMatch(v, @"^\d+\.\d+(\.\d+)?(-\w+)?$");

        static (int major, int minor) ParseVersion(string v)
        {
            var p = v.Split('.', '-');
            int.TryParse(p[0], out int major); int minor = 0;
            if (p.Length > 1) int.TryParse(p[1], out minor);
            return (major, minor);
        }

        static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 60 ? s : s.Substring(0, 57) + "…";
    }
}
