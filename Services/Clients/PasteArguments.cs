using System.Collections.Generic;
using System.Text;

namespace SunshineLibrary.Services.Clients
{
    /// <summary>
    /// Windows CreateProcess-compatible argument quoting. Implements the algorithm
    /// used by the .NET CoreFX repo's PasteArguments.cs, which is the same one
    /// documented by Raymond Chen / Everyone's Favorite Windows Weirdness:
    ///
    ///   - Args with no space/tab/quote -> emitted verbatim
    ///   - Args with whitespace or quotes -> wrapped in quotes, internal quotes
    ///     doubled, and any run of backslashes immediately preceding a quote (or
    ///     the closing quote) has its backslashes doubled.
    ///
    /// net462 has no ProcessStartInfo.ArgumentList so we build the string ourselves.
    /// </summary>
    public static class PasteArguments
    {
        public static string Build(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var a in args)
            {
                if (!first) sb.Append(' ');
                first = false;
                AppendArgument(sb, a ?? string.Empty);
            }
            return sb.ToString();
        }

        public static string Build(params string[] args) => Build((IEnumerable<string>)args);

        private static void AppendArgument(StringBuilder sb, string arg)
        {
            if (arg.Length != 0 && ContainsNoWhitespaceOrQuotes(arg))
            {
                sb.Append(arg);
                return;
            }

            sb.Append('"');
            int idx = 0;
            while (idx < arg.Length)
            {
                char c = arg[idx];
                if (c == '\\')
                {
                    int run = 1;
                    while (idx + run < arg.Length && arg[idx + run] == '\\') run++;

                    if (idx + run == arg.Length)
                    {
                        // Trailing backslashes before closing quote — double them.
                        sb.Append('\\', run * 2);
                        idx += run;
                    }
                    else if (arg[idx + run] == '"')
                    {
                        // Backslashes immediately before an internal quote — double them, then escape the quote.
                        sb.Append('\\', run * 2 + 1);
                        sb.Append('"');
                        idx += run + 1;
                    }
                    else
                    {
                        sb.Append('\\', run);
                        idx += run;
                    }
                }
                else if (c == '"')
                {
                    sb.Append('\\');
                    sb.Append('"');
                    idx++;
                }
                else
                {
                    sb.Append(c);
                    idx++;
                }
            }
            sb.Append('"');
        }

        private static bool ContainsNoWhitespaceOrQuotes(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"') return false;
            }
            return true;
        }
    }
}
