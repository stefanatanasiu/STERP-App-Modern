using System.Text;

namespace Sterp.Platform.Kernel.Jobs;

/// <summary>
/// CMDARGS (cobol/online/common/CMDARGS.cob; FR-STERP-PLATFORM-025): tokenise at spaces or low-values outside double quotes,
/// remove every double quote, at most 80 bytes per token, ignore the first token when it has no '='; a later token without
/// '=' is CMD0001 and one starting with '=' CMD0002 (RC 08, count + 1, parsing continues); key at most 12 bytes upper-cased,
/// value at most 60 bytes (case kept); the first 20 pairs are stored, more are dropped (RC 04 CMD0004, downgrading an earlier
/// RC 08); an unclosed quote is RC 08 CMD0003; otherwise the message becomes "&lt;n&gt; argument(s) read" (overwriting the
/// CMD0001/0002 text). Named fields: CO/COMPANY -> CO (10), SITE (10), USER (20), ASAT (8), FROM (8), TO (8), PERIOD (6),
/// MODE (10, upper-cased), OUT (60) - last occurrence wins; Get returns the first stored occurrence.
/// </summary>
public sealed class CommandLineArgs
{
    public const int MaxPairs = 20;

    private readonly List<KeyValuePair<string, string>> _pairs = [];

    private CommandLineArgs(string text) => Text = text;

    public string Text { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Pairs => _pairs;

    public LegacyResult Result { get; private set; } = LegacyResult.Ok();

    public int Dropped { get; private set; }

    public string Co { get; private set; } = "";
    public string Site { get; private set; } = "";
    public string User { get; private set; } = "";
    public string AsAt { get; private set; } = "";
    public string From { get; private set; } = "";
    public string To { get; private set; } = "";
    public string Period { get; private set; } = "";
    public string Mode { get; private set; } = "";
    public string Out { get; private set; } = "";

    /// <summary>CMDLINE / PARSE.</summary>
    public static CommandLineArgs Parse(string? commandLine)
    {
        var text = Cobol.CobolText.Fit(commandLine, 256).TrimEnd();
        var a = new CommandLineArgs(text);
        a.Run();
        return a;
    }

    /// <summary>
    /// The command line a legacy batch executable received from its launcher (ReportService Process.Start, STERPOPS, the
    /// stream .cmd files): the C runtime splits the argument string into argv (white space separates arguments outside
    /// double quotes; the quotes group and are removed; 2n backslashes before a quote give n backslashes, 2n+1 give n and a
    /// literal quote), then GnuCOBOL ACCEPT FROM COMMAND-LINE joins argv with single spaces. So REASON="Lost in transit"
    /// reaches CMDARGS as REASON=Lost in transit and only "Lost" is kept, as on the legacy runs (KITDOCP).
    /// </summary>
    public static string AsSeenByProgram(string? commandLine)
    {
        var s = commandLine ?? "";
        var args = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i >= s.Length) break;
            var sb = new StringBuilder();
            var quoted = false;
            while (i < s.Length && (quoted || s[i] is not (' ' or '\t')))
            {
                if (s[i] == '\\')
                {
                    var n = 0;
                    while (i < s.Length && s[i] == '\\')
                    {
                        n++;
                        i++;
                    }
                    if (i < s.Length && s[i] == '"')
                    {
                        sb.Append('\\', n / 2);
                        if (n % 2 == 1)
                        {
                            sb.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append('\\', n);
                    }
                    continue;
                }
                if (s[i] == '"')
                {
                    quoted = !quoted;
                    i++;
                    continue;
                }
                sb.Append(s[i]);
                i++;
            }
            args.Add(sb.ToString());
        }
        return string.Join(' ', args);
    }

    /// <summary>GET: the first stored occurrence; null when not supplied (legacy RC 16 'No argument &lt;key&gt; was supplied').</summary>
    public string? Get(string key)
    {
        var k = key.Trim().ToUpperInvariant();
        foreach (var (pk, pv) in _pairs)
        {
            if (pk == k) return pv;
        }
        return null;
    }

    public string GetOrDefault(string key, string fallback = "") => Get(key) ?? fallback;

    public bool Has(string key) => Get(key) is not null;

    private void Run()
    {
        var rc = 0;
        var reason = "";
        var message = "";
        var count = 0;
        var tokens = Tokenise(Text, out var unclosed);
        var first = true;
        foreach (var raw in tokens)
        {
            var token = Cobol.CobolText.Fit(raw, 80).TrimEnd();
            if (token.Length == 0) continue;
            var eq = token.IndexOf('=');
            if (first)
            {
                first = false;
                if (eq < 0) continue;
            }
            if (eq < 0)
            {
                rc = Math.Max(rc, 8);
                reason = "CMD0001";
                message = $"Argument {token} is not KEY=VALUE";
                count++;
                continue;
            }
            if (eq == 0)
            {
                rc = Math.Max(rc, 8);
                reason = "CMD0002";
                message = $"Argument {token} has no key";
                count++;
                continue;
            }
            var key = Cobol.CobolText.Fit(token[..eq].ToUpperInvariant(), 12).TrimEnd();
            var value = Cobol.CobolText.Fit(token[(eq + 1)..], 60).TrimEnd();
            if (_pairs.Count >= MaxPairs)
            {
                Dropped++;
                continue;
            }
            _pairs.Add(new(key, value));
            Named(key, value);
        }
        if (unclosed)
        {
            Result = new LegacyResult(8, "CMD0003", "Unbalanced double quote in the command line", count + 1);
            return;
        }
        if (Dropped > 0)
        {
            Result = new LegacyResult(4, "CMD0004", $"{Dropped} argument(s) ignored - only {MaxPairs} are held", count + 1);
            return;
        }
        _ = message;
        Result = new LegacyResult(rc, rc == 0 ? "" : reason, $"{_pairs.Count} argument(s) read", count);
    }

    private void Named(string key, string value)
    {
        switch (key)
        {
            case "CO" or "COMPANY": Co = Cobol.CobolText.Trimmed(value, 10); break;
            case "SITE": Site = Cobol.CobolText.Trimmed(value, 10); break;
            case "USER": User = Cobol.CobolText.Trimmed(value, 20); break;
            case "ASAT": AsAt = Cobol.CobolText.Trimmed(value, 8); break;
            case "FROM": From = Cobol.CobolText.Trimmed(value, 8); break;
            case "TO": To = Cobol.CobolText.Trimmed(value, 8); break;
            case "PERIOD": Period = Cobol.CobolText.Trimmed(value, 6); break;
            case "MODE": Mode = Cobol.CobolText.Trimmed(value.ToUpperInvariant(), 10); break;
            case "OUT": Out = Cobol.CobolText.Trimmed(value, 60); break;
        }
    }

    private static List<string> Tokenise(string text, out bool unclosed)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (!inQuote && (c == ' ' || c == '\0'))
            {
                if (sb.Length > 0) tokens.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        unclosed = inQuote;
        return tokens;
    }

    /// <summary>
    /// ReportService.BuildArguments (ReportService.vb:47-61): keys trimmed and upper-cased, KEY=VALUE separated by one space,
    /// a value containing a space wrapped in double quotes, empty values dropped.
    /// </summary>
    public static string Build(IEnumerable<KeyValuePair<string, string?>> args)
    {
        var cmd = new StringBuilder();
        foreach (var (k, raw) in args)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            var v = (raw ?? "").Trim();
            if (v.Length == 0) continue;
            if (cmd.Length > 0) cmd.Append(' ');
            cmd.Append(k.Trim().ToUpperInvariant()).Append('=');
            if (v.Contains(' ', StringComparison.Ordinal)) cmd.Append('"').Append(v).Append('"');
            else cmd.Append(v);
        }
        return cmd.ToString();
    }
}
