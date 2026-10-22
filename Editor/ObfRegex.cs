// Code Obfuscator — timeout-guarded regex helpers.
//
// Every regex the tool runs against project text goes through here so a pathological pattern/input
// combination throws RegexMatchTimeoutException (caught per file by the pipeline → file skipped and
// reported) instead of spinning the editor for hours. Backtracking blow-ups were the root cause of
// the v2 "stuck on one script" hang.
using System;
using System.Text.RegularExpressions;

namespace CodeObfuscator
{
    public static class ObfRegex
    {
        /// Set by the pipeline from ObfSettings.regexTimeoutSec before a run starts.
        public static TimeSpan Timeout = TimeSpan.FromSeconds(5);

        public static Regex New(string pattern, RegexOptions opts = RegexOptions.None) => new Regex(pattern, opts, Timeout);

        public static string Replace(string input, string pattern, MatchEvaluator ev, RegexOptions opts = RegexOptions.None) => New(pattern, opts).Replace(input, ev);
        public static string Replace(string input, string pattern, string replacement, RegexOptions opts = RegexOptions.None) => New(pattern, opts).Replace(input, replacement);
        public static bool IsMatch(string input, string pattern, RegexOptions opts = RegexOptions.None) => New(pattern, opts).IsMatch(input);
        public static Match Match(string input, string pattern, RegexOptions opts = RegexOptions.None) => New(pattern, opts).Match(input);
        public static MatchCollection Matches(string input, string pattern, RegexOptions opts = RegexOptions.None) => New(pattern, opts).Matches(input);

        /// Regex.Escape plus '$' doubling for use inside a replacement string.
        public static string Repl(string literal) => literal.Replace("$", "$$");
    }
}
