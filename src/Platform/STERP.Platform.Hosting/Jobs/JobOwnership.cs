using System.Text.RegularExpressions;
using Sterp.Platform.Hosting.Contracts;

namespace Sterp.Platform.Hosting.Jobs;

/// <summary>
/// The target slice that owns a legacy batch program or job stream (contracts 8.1): every slice OpenAPI document declares
/// POST /api/{segment}/v1/jobs/{jobCode}/runs for its own programs and streams ('JOB.' catalogue codes name the stream). Used
/// to name the slice in the 501 API0501 answer for a program or stream no delivered module has registered; the catalogue
/// Module is the fallback for programs without a submission path.
/// </summary>
public static partial class JobOwnership
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Map = new(Build);

    /// <summary>Owning slice of a program or stream name (or catalogue code), else the slice named by the catalogue module, else null.</summary>
    public static string? SliceOf(string programOrStream, string? module = null)
    {
        var code = Normalize(programOrStream);
        if (code.Length > 0 && Map.Value.TryGetValue(code, out var slice)) return slice;
        if (!string.IsNullOrWhiteSpace(module))
        {
            var candidate = "STERP-" + module.Trim().ToUpperInvariant();
            if (ContractCatalog.Waves.ContainsKey(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Every job code with its slice (contract submission paths).</summary>
    public static IReadOnlyDictionary<string, string> All => Map.Value;

    private static string Normalize(string code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        return c.StartsWith("JOB.", StringComparison.Ordinal) ? c[4..] : c;
    }

    private static IReadOnlyDictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ContractCatalog.Instance.Operations)
        {
            if (!string.Equals(op.Method, "POST", StringComparison.OrdinalIgnoreCase)) continue;
            var m = JobPath().Match(op.Path);
            if (m.Success) map.TryAdd(Normalize(m.Groups["code"].Value), op.Slice);
        }
        return map;
    }

    [GeneratedRegex("^/api/[a-z]+/v1/jobs/(?<code>[^/{}]+)/runs$")]
    private static partial Regex JobPath();
}
