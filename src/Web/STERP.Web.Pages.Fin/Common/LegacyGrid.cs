using System.Text.Json.Nodes;
using Sterp.Web.Framework.Format;

namespace Sterp.Web.Pages.Fin.Common;

/// <summary>GridHelper.Bind of the legacy finance forms: the legacy DataTable column names and GridHelper.GuessType kinds.</summary>
public static class LegacyGrid
{
    /// <summary>
    /// One column of a legacy DataTable: the JSON property, the legacy column name (the caption is GridHelper.Humanise of it,
    /// "SignedGBP" -> "Signed GBP", "CL-S-CODE" -> "CL- S- CODE") and the kind GridHelper.GuessType gave its .NET type
    /// (decimal MONEY, int/long INT, DateTime DATE or STAMP, Boolean BOOL, anything else - smallint, tinyint, text - TEXT).
    /// </summary>
    public static GridColumn Col(string json, string legacyName, string kind) => new(json, kind: kind, legacyName: legacyName);

    /// <summary>The rows of a list answer ({ items } or an array) bound to the given columns.</summary>
    public static GridData Bind(JsonNode? rows, params GridColumn[] columns) => GridData.FromItems(rows ?? new JsonArray(), columns);
}
