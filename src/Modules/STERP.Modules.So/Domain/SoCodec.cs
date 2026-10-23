using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.So.Domain;

/// <summary>One elementary COMMAREA field: its JSON name (OpenAPI component) and COBOL picture.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FAttribute(string json, string picture) : Attribute
{
    public string Json { get; } = json;

    public string Picture { get; } = picture;

    /// <summary>PIC 9(8) yyyymmdd presented as an ISO date (null when zero or not a date).</summary>
    public bool Date { get; init; }

    /// <summary>COMP-5: native binary (no decimal digit truncation).</summary>
    public bool Binary { get; init; }
}

/// <summary>An OCCURS table: JSON name, occurrences and the JSON name of its count field.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TAttribute(string json, int occurs, string countJson) : Attribute
{
    public string Json { get; } = json;

    public int Occurs { get; } = occurs;

    public string CountJson { get; } = countJson;
}

/// <summary>A parsed numeric or alphanumeric picture.</summary>
public readonly record struct Pic(bool Text, int Length, int IntDigits, int Scale, bool Signed)
{
    public static Pic Parse(string picture)
    {
        var p = picture.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (p.StartsWith('X')) return new Pic(true, Count(p, 'X'), 0, 0, false);
        var signed = p.StartsWith('S');
        if (signed) p = p[1..];
        var v = p.IndexOf('V');
        var ip = v < 0 ? p : p[..v];
        var dp = v < 0 ? "" : p[(v + 1)..];
        return new Pic(false, 0, Count(ip, '9'), dp.Length == 0 ? 0 : Count(dp, '9'), signed);
    }

    private static int Count(string part, char symbol)
    {
        var n = 0;
        for (var i = 0; i < part.Length; i++)
        {
            if (part[i] != symbol) continue;
            if (i + 1 < part.Length && part[i + 1] == '(')
            {
                var close = part.IndexOf(')', i + 2);
                n += int.Parse(part[(i + 2)..close], CultureInfo.InvariantCulture);
                i = close;
            }
            else
            {
                n++;
            }
        }
        return n;
    }
}

/// <summary>
/// COMMAREA marshalling for the SO areas: contracts 4.4 input normalization (the legacy CobolCodec PutString/PutZoned) and
/// the contracts 4.3 JSON form of the body (text right-trimmed, numbers at the picture scale, PIC 9(8) dates ISO or null,
/// OCCURS tables holding the first count occurrences).
/// </summary>
public static class SoCodec
{
    private sealed record Member(PropertyInfo Property, FAttribute? Field, TAttribute? Table, Pic Pic);

    private static readonly ConcurrentDictionary<Type, Member[]> Members = new();

    public static T[] Table<T>(int occurs) where T : new()
    {
        var a = new T[occurs];
        for (var i = 0; i < occurs; i++) a[i] = new T();
        return a;
    }

    private static Member[] Of(Type t) => Members.GetOrAdd(t, type => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .OrderBy(p => p.MetadataToken)
        .Select(p => new Member(p, p.GetCustomAttribute<FAttribute>(), p.GetCustomAttribute<TAttribute>(),
            p.GetCustomAttribute<FAttribute>() is { } f ? Pic.Parse(f.Picture) : default))
        .Where(m => m.Field is not null || m.Table is not null)
        .ToArray());

    /// <summary>The body as JSON (header excluded).</summary>
    public static JsonObject ToJson(object area)
    {
        var o = new JsonObject();
        var members = Of(area.GetType());
        foreach (var m in members)
        {
            if (m.Field is not null)
            {
                o[m.Field.Json] = Value(m, m.Property.GetValue(area));
            }
            else if (m.Table is not null)
            {
                var count = members.FirstOrDefault(x => x.Field?.Json == m.Table.CountJson);
                var n = count is null ? m.Table.Occurs : Convert.ToInt32(count.Property.GetValue(area), CultureInfo.InvariantCulture);
                n = Math.Clamp(n, 0, m.Table.Occurs);
                var rows = (Array)m.Property.GetValue(area)!;
                var arr = new JsonArray();
                for (var i = 0; i < n; i++) arr.Add(ToJson(rows.GetValue(i)!));
                o[m.Table.Json] = arr;
            }
        }
        return o;
    }

    private static JsonNode? Value(Member m, object? v)
    {
        var f = m.Field!;
        if (m.Pic.Text) return JsonValue.Create(CobolText.Trimmed(v as string, m.Pic.Length));
        if (f.Date)
        {
            var d = Convert.ToInt32(v, CultureInfo.InvariantCulture);
            var date = d <= 0 ? null : CobolDate.FromYyyymmdd(d);
            return date is null ? null : JsonValue.Create(date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        return v switch
        {
            decimal dec => JsonValue.Create(CobolNumeric.WithScale(dec, m.Pic.Scale)),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            _ => JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>
    /// Contracts 4.4 input normalization of every field of the area (and its table rows): text fitted to PIC X(n)
    /// (Windows-1252, '?' replacement, right truncation) and held right-trimmed; numbers rounded half away from zero to the
    /// scale with high-order truncation and the absolute value for unsigned pictures.
    /// </summary>
    public static void Normalize(object area)
    {
        foreach (var m in Of(area.GetType()))
        {
            if (m.Table is not null)
            {
                foreach (var row in (Array)m.Property.GetValue(area)!) Normalize(row!);
                continue;
            }
            var f = m.Field!;
            var v = m.Property.GetValue(area);
            if (m.Pic.Text)
            {
                m.Property.SetValue(area, CobolText.Trimmed(v as string, m.Pic.Length));
            }
            else if (v is decimal d)
            {
                m.Property.SetValue(area, CobolNumeric.Normalize(d, m.Pic.IntDigits + m.Pic.Scale, m.Pic.Scale, m.Pic.Signed));
            }
            else if (v is int i && !f.Binary && !f.Date)
            {
                m.Property.SetValue(area, (int)CobolNumeric.Normalize(i, m.Pic.IntDigits, 0, m.Pic.Signed));
            }
            else if (v is long l && !f.Binary)
            {
                m.Property.SetValue(area, (long)CobolNumeric.Normalize(l, m.Pic.IntDigits, 0, m.Pic.Signed));
            }
        }
    }
}
