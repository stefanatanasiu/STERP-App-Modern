using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sterp.ApiPortal.Portal;

/// <summary>Converts a binding OpenAPI YAML document to its JSON form (quoted scalars stay strings; plain scalars per YAML 1.2 core).</summary>
public static class YamlJson
{
    public static JsonObject Convert(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || ToNode(stream.Documents[0].RootNode) is not JsonObject document)
        {
            throw new InvalidDataException("The YAML text holds no mapping document.");
        }
        return document;
    }

    private static JsonNode? ToNode(YamlNode node) => node switch
    {
        YamlMappingNode map => ToObject(map),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(ToNode).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => null,
    };

    private static JsonObject ToObject(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in map.Children)
        {
            obj[((YamlScalarNode)k).Value ?? ""] = ToNode(v);
        }
        return obj;
    }

    private static JsonNode? Scalar(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        if (s.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return JsonValue.Create(v);
        }
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }
        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }
}
