using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace STERP.Testing;

/// <summary>Minimal JSON path: $, .name, [index], [*] (any element).</summary>
public static partial class JsonPathLite
{
    public static IReadOnlyList<JsonNode?> Select(JsonNode? root, string path)
    {
        var current = new List<JsonNode?> { root };
        foreach (Match token in Tokens().Matches(path.StartsWith('$') ? path[1..] : path))
        {
            var next = new List<JsonNode?>();
            foreach (var node in current)
            {
                if (token.Groups["name"].Success)
                {
                    if (node is JsonObject obj)
                    {
                        var name = token.Groups["name"].Value;
                        var match = obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
                        if (match.Key is not null) next.Add(match.Value);
                    }
                }
                else if (token.Groups["star"].Success)
                {
                    if (node is JsonArray arr) next.AddRange(arr);
                }
                else if (token.Groups["index"].Success && node is JsonArray arr2)
                {
                    var i = int.Parse(token.Groups["index"].Value, CultureInfo.InvariantCulture);
                    if (i < arr2.Count) next.Add(arr2[i]);
                }
            }
            current = next;
        }
        return current;
    }

    public static string? AsText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    public static bool ValueEquals(JsonNode? actual, JsonNode? expected)
    {
        if (expected is null) return actual is null;
        if (actual is null) return false;
        if (expected is JsonValue ev && actual is JsonValue av)
        {
            if (TryDecimal(ev, out var e) && TryDecimal(av, out var a)) return a == e;
            return string.Equals(AsText(actual)?.TrimEnd(), AsText(expected)?.TrimEnd(), StringComparison.Ordinal);
        }
        return JsonNode.DeepEquals(actual, expected);
    }

    public static bool TryDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<decimal>(out value)) return true;
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<long>(out var l)) { value = l; return true; }
        if (v.TryGetValue<double>(out var d)) { value = (decimal)d; return true; }
        if (v.TryGetValue<string>(out var s))
        {
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }
        return false;
    }

    [GeneratedRegex(@"\.(?<name>[A-Za-z0-9_\-@$]+)|\[(?<star>\*)\]|\[(?<index>\d+)\]|\[['""](?<name>[^'""]+)['""]\]")]
    private static partial Regex Tokens();
}

/// <summary>
/// Comparison normalization of Agent 06 contracts section 11: generated identifiers, document numbers,
/// correlation ids, timestamps and run folders compare by role and format, everything else exactly.
/// </summary>
public static partial class Normalization
{
    public static bool MatchesRole(string role, string? actual, string? expected)
    {
        if (actual is null) return false;
        return role switch
        {
            "generated-id" => Integer().IsMatch(actual) || Guid.TryParse(actual, out _),
            "document-number" => expected is null || Shape(actual) == Shape(expected),
            "correlation-id" => actual.Length is > 0 and <= 36,
            "timestamp" => DateTime.TryParse(actual, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "run-folder" => actual.Contains('\\') || actual.Contains('/'),
            _ => throw new ArgumentException($"Unknown normalization role {role}"),
        };
    }

    private static string Shape(string text) => Digits().Replace(text.Trim(), "9");

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Integer();

    [GeneratedRegex(@"\d")]
    private static partial Regex Digits();
}
