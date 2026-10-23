using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using STERP.Testing;

namespace STERP.Legacy.Characterization;

/// <summary>
/// Pinned legacy observations. In capture mode (STERP_CHARACTERIZATION_CAPTURE=1) the live legacy output
/// becomes the fixture (the oracle run); otherwise the live output must equal the fixture after the
/// normalization rules below. Fixtures are never written in verify mode.
/// </summary>
public static partial class Characterization
{
    public static bool CaptureMode => ParityEnvironment.Get("STERP_CHARACTERIZATION_CAPTURE", "0") == "1";

    public static string FixtureRoot => Path.Combine(ParityEnvironment.TestsRoot, "legacy", "STERP.Legacy.Characterization.Tests", "fixtures");

    /// <summary>
    /// NR-01 LocalDB instance pipe name in SQL Server messages; NR-02 absolute isolated legacy root;
    /// NR-03 day counts computed from the system clock ("N days past").
    /// </summary>
    public static string Normalize(string line)
    {
        var text = LocalDbServer().Replace(line, "Server <isolated-server>,");
        text = text.Replace(LegacyRuntime.Root, "<legacy-root>", StringComparison.OrdinalIgnoreCase);
        text = DaysPast().Replace(text, "<n> days past");
        return text.TrimEnd();
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string> lines) => lines.Select(Normalize).ToList();

    public static IEnumerable<string> FixtureNames(string folder, string pattern)
    {
        var dir = Path.Combine(FixtureRoot, folder);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, pattern).Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n, StringComparer.Ordinal)
            : [];
    }

    /// <summary>Compares (or captures) a text slice. Returns the fixture path.</summary>
    public static string Pin(string relativePath, IReadOnlyList<string> observed)
    {
        var file = Path.Combine(FixtureRoot, relativePath);
        var normalized = Normalize(observed);
        if (CaptureMode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, normalized, new UTF8Encoding(false));
            return file;
        }
        Assert.True(File.Exists(file), $"No pinned legacy observation {relativePath}; run once with STERP_CHARACTERIZATION_CAPTURE=1 on the isolated legacy runtime.");
        var expected = File.ReadAllLines(file, Encoding.UTF8);
        var diff = FirstDifference(expected, normalized);
        Assert.True(diff is null, $"Legacy behaviour differs from the pinned observation {relativePath}: {diff}");
        return file;
    }

    public static string PinJson(string relativePath, JsonNode observed)
    {
        var file = Path.Combine(FixtureRoot, relativePath);
        var text = observed.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (CaptureMode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text + "\n", new UTF8Encoding(false));
            return file;
        }
        Assert.True(File.Exists(file), $"No pinned legacy observation {relativePath}");
        var expected = JsonNode.Parse(File.ReadAllText(file));
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(text)),
            $"Legacy result differs from the pinned observation {relativePath}");
        return file;
    }

    public static void WriteRunOutput(string relativePath, IEnumerable<string> lines)
    {
        var file = Path.Combine(LegacyRuntime.OutputRoot, RunStamp, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    public static string RunStamp { get; } = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

    private static string? FirstDifference(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            var e = i < expected.Count ? expected[i].TrimEnd() : "<end>";
            var a = i < actual.Count ? actual[i].TrimEnd() : "<end>";
            if (!string.Equals(e, a, StringComparison.Ordinal)) return $"line {i + 1}: expected '{e}' but observed '{a}'";
        }
        return null;
    }

    [GeneratedRegex(@"Server [^,\s]+\\LOCALDB#[0-9A-F]+,", RegexOptions.IgnoreCase)]
    private static partial Regex LocalDbServer();

    [GeneratedRegex(@"\b\d+ days past\b")]
    private static partial Regex DaysPast();
}
