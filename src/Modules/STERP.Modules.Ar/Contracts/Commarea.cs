using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Contracts;

/// <summary>
/// The COBOL picture of one COMMAREA field. Drives the contracts 4.4 input normalization (CobolCodec marshalling) and the
/// contracts 4.3 JSON representation of the area (OpenAPI component schemas Ar*Commarea).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PicAttribute(string picture) : Attribute
{
    public string Picture { get; } = picture;

    /// <summary>PIC 9(8) yyyymmdd presented as an ISO date (null when zero).</summary>
    public bool Date { get; init; }

    /// <summary>COMP-5: a native 4-byte integer (no digit truncation).</summary>
    public bool Binary { get; init; }
}

/// <summary>OCCURS table emitted as <c>{ group: { name: [...] } }</c> holding the first <c>count</c> occurrences.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OccursAttribute(int times, string group, string countProperty) : Attribute
{
    public int Times { get; } = times;
    public string Group { get; } = group;
    public string CountProperty { get; } = countProperty;
}

/// <summary>A parsed COBOL picture.</summary>
public readonly record struct Picture(bool Alphanumeric, int Length, int IntDigits, int Scale, bool Signed)
{
    public int TotalDigits => IntDigits + Scale;

    public static Picture Parse(string pic)
    {
        var p = pic.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (p.StartsWith('X')) return new Picture(true, Count(p, 'X'), 0, 0, false);
        var signed = p.StartsWith('S');
        if (signed) p = p[1..];
        var v = p.IndexOf('V');
        var intPart = v < 0 ? p : p[..v];
        var decPart = v < 0 ? "" : p[(v + 1)..];
        return new Picture(false, 0, Count(intPart, '9'), decPart.Length == 0 ? 0 : Count(decPart, '9'), signed);
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

/// <summary>AR helpers over the kernel COMMAREA (Sterp.Platform.Kernel.Services.Commarea / CommareaHeader).</summary>
public static class CommareaExtensions
{
    /// <summary>The body as the envelope data of contracts 4.6 (header excluded).</summary>
    public static JsonObject ToJson(this Commarea area) => CommareaCodec.ToJson(area);

    /// <summary>The outcome as the .NET gateway read it back (CobolGateway.Link, CobolGateway.cs:139-143), CA-REQ-REF as documentRef.</summary>
    public static LegacyResult Outcome(this CommareaHeader h) =>
        new(h.ReturnCode, (h.ReasonCode ?? "").Trim(), (h.Message ?? "").Trim(), h.MessageCount, (h.RequestRef ?? "").Trim());

    /// <summary>
    /// CobolGateway.PrepareHeader (CobolGateway.cs:67-83) for an API call: program and function upper-cased, company, site,
    /// user and business date of the request, the correlation id (Idempotency-Key or a new GUID), result fields cleared.
    /// </summary>
    public static void PrepareLink(this CommareaHeader h, SterpContext session, string program, string function)
    {
        h.Prepare(KernelCaller.From(session), CobolText.Trimmed(function.ToUpperInvariant(), 8));
        h.Program = CobolText.Trimmed(program.ToUpperInvariant(), 8);
        h.Company = CobolText.Trimmed(h.Company, 10);
        h.Site = CobolText.Trimmed(h.Site, 10);
        h.User = CobolText.Trimmed(h.User, 20);
        h.CorrelationId = CobolText.Trimmed(h.CorrelationId, 36);
        h.RequestRef = "";
    }
}
/// <summary>
/// Reflection codec over the <see cref="PicAttribute"/>-annotated properties of an area: legacy input normalization
/// (contracts 4.4: Windows-1252 + right truncation for text, half-away-from-zero rounding to the scale, high-order
/// truncation, absolute value when unsigned) and the contracts 4.3 JSON form (text right-trimmed, decimals with their
/// scale, PIC 9(8) dates as yyyy-MM-dd or null, OCCURS tables cut to their used count).
/// </summary>
public static class CommareaCodec
{
    private sealed record Member(PropertyInfo Property, string JsonName, PicAttribute? Pic, Picture Picture, OccursAttribute? Occurs);

    private static readonly ConcurrentDictionary<Type, Member[]> Members = new();

    private static Member[] Of(Type t) => Members.GetOrAdd(t, type => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetCustomAttribute<PicAttribute>() is not null || p.GetCustomAttribute<OccursAttribute>() is not null)
        .OrderBy(p => p.MetadataToken)
        .Select(p =>
        {
            var pic = p.GetCustomAttribute<PicAttribute>();
            return new Member(p, JsonNamingPolicy.CamelCase.ConvertName(p.Name), pic,
                pic is null ? default : Picture.Parse(pic.Picture), p.GetCustomAttribute<OccursAttribute>());
        })
        .ToArray());

    /// <summary>Applies the CobolCodec marshalling to every field of the area (and of every table occurrence).</summary>
    public static void Normalize(object area)
    {
        NormalizeObject(area);
    }

    private static void NormalizeObject(object target)
    {
        foreach (var m in Of(target.GetType()))
        {
            if (m.Occurs is not null)
            {
                if (m.Property.GetValue(target) is string[] texts)
                {
                    for (var i = 0; i < texts.Length; i++) texts[i] = CobolText.Trimmed(texts[i], m.Picture.Length);
                }
                else if (m.Property.GetValue(target) is IEnumerable items)
                {
                    foreach (var item in items) if (item is not null) NormalizeObject(item);
                }
                continue;
            }
            var pic = m.Picture;
            var value = m.Property.GetValue(target);
            switch (value)
            {
                case string s when pic.Alphanumeric:
                    m.Property.SetValue(target, CobolText.Trimmed(s, pic.Length));
                    break;
                case null when m.Property.PropertyType == typeof(string):
                    m.Property.SetValue(target, "");
                    break;
                case decimal d:
                    m.Property.SetValue(target, CobolNumeric.Normalize(d, pic.TotalDigits, pic.Scale, pic.Signed));
                    break;
                case int i when !m.Pic!.Binary:
                    m.Property.SetValue(target, (int)CobolNumeric.Normalize(i, pic.TotalDigits, 0, pic.Signed));
                    break;
            }
        }
    }

    /// <summary>The area body as JSON (the envelope data of contracts 4.6; header excluded).</summary>
    public static JsonObject ToJson(object area)
    {
        var o = new JsonObject();
        foreach (var m in Of(area.GetType()))
        {
            if (m.Occurs is not null)
            {
                var count = Convert.ToInt32(area.GetType().GetProperty(m.Occurs.CountProperty)!.GetValue(area), CultureInfo.InvariantCulture);
                count = Math.Clamp(count, 0, m.Occurs.Times);
                var arr = new JsonArray();
                if (m.Property.GetValue(area) is IList list)
                {
                    for (var i = 0; i < count && i < list.Count; i++)
                    {
                        arr.Add(list[i] switch
                        {
                            null => null,
                            string s => JsonValue.Create(CobolText.Trimmed(s, m.Picture.Length)),
                            var item => ToJson(item),
                        });
                    }
                }
                o[m.Occurs.Group] = new JsonObject { [m.JsonName] = arr };
                continue;
            }
            o[m.JsonName] = Node(m, m.Property.GetValue(area));
        }
        return o;
    }

    private static JsonNode? Node(Member m, object? value)
    {
        var pic = m.Picture;
        return value switch
        {
            null => pic.Alphanumeric ? JsonValue.Create("") : null,
            string s => JsonValue.Create(CobolText.Trimmed(s, pic.Length)),
            decimal d => JsonValue.Create(CobolNumeric.WithScale(d, pic.Scale)),
            int i when m.Pic!.Date => CobolDate.FromYyyymmdd(i) is { } date
                ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : null,
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>A fixed-size OCCURS table initialised like COBOL INITIALIZE (every occurrence spaces/zeros).</summary>
    public static T[] Table<T>(int times) where T : new()
    {
        var t = new T[times];
        for (var i = 0; i < times; i++) t[i] = new T();
        return t;
    }

    /// <summary>A fixed-size OCCURS table of PIC X items (all SPACES).</summary>
    public static string[] Texts(int times) => Enumerable.Repeat("", times).ToArray();
}
