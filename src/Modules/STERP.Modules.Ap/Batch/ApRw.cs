using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Batch;

/// <summary>
/// Report Writer helpers of the AP listings on the shared GnuCOBOL 3.2 runtime port (<see cref="GnuReportWriter"/>). A SOURCE
/// item is moved into its report field (PIC X(n) or numeric edited) and copied into the print line without its trailing
/// spaces (reportio.c print_field: cob_field_to_string), so it never blanks an earlier overlapping column; a VALUE literal is
/// copied at its full length (RwText.Lit).
/// </summary>
internal static class ApRw
{
    /// <summary>The AP RD on an open spool (LINE SEQUENTIAL records, no form feed).</summary>
    public static GnuReportWriter Writer(SpoolWriter spool, ReportHeading heading, int pageLimit, int headingLine, int firstDetail,
        int lastDetail, IReadOnlyList<RwControl>? controls = null, RwLine[]? finalFooting = null) =>
        new(spool.WriteLine, heading, pageLimit, headingLine, firstDetail, lastDetail, controls, finalFooting);

    /// <summary>COLUMN <paramref name="column"/> PIC X(<paramref name="width"/>) SOURCE value.</summary>
    public static RwText Src(this RwText line, int column, string? value, int width)
    {
        var text = CobolText.Fit(value, width).TrimEnd(' ');
        return text.Length == 0 ? line : line.Put(column, text, text.Length);
    }

    /// <summary>COLUMN <paramref name="column"/> PIC <paramref name="picture"/> (numeric edited) SOURCE / SUM value.</summary>
    public static RwText Ed(this RwText line, int column, decimal value, string picture)
    {
        var text = CobolEdit.Field(value, picture).TrimEnd(' ');
        return text.Length == 0 ? line : line.Put(column, text, text.Length);
    }

    /// <summary>VALUE ALL "-" in PIC X(<paramref name="width"/>).</summary>
    public static RwText Dashes(this RwText line, int column, int width) => line.Lit(column, new string('-', width));

    /// <summary>The 132-character rule of the column headings (COLUMN 3 X(66) and COLUMN 69 X(64) VALUE ALL "-").</summary>
    public static string Rule => new RwText().Dashes(3, 66).Dashes(69, 64).Text;
}
