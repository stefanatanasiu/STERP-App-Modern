using System.Globalization;
using System.Text;
using Sterp.Modules.Fin.Common;
using Sterp.Modules.Fin.Vat;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// MTDJSON (cobol/batch/fin/MTDJSON.cob; FR-STERP-FIN-057/058): VATRET9 PREPARE of the period (VATPER required), box
/// identities checked, the HMRC MTD nine-box body built with GnuCOBOL JSON GENERATE (members renamed, numbers without
/// leading zeros, X items trimmed) and "finalised" unquoted in place (":true  " / ":false  "), written as one LINE
/// SEQUENTIAL record to run\mtd\MTDJSON-&lt;CO&gt;-&lt;stamp&gt;.json and announced as the run's spool. Nothing is transmitted.
/// No unit of work: the PREPARE draft write autocommits.
/// </summary>
internal sealed class MtdjsonJob(FinBatch b)
{
    private string _periodKey = "";
    private VatRet9Area? _vr;
    private string _json = "";

    public static async Task<int> RunAsync(FinBatch b) => await new MtdjsonJob(b).MainAsync();

    private async Task<int> MainAsync()
    {
        ReadArgs();
        if (b.Rc > 4) return b.End();
        await PrepareAsync();
        if (b.Rc > 4) return b.End();
        BuildJson();
        WriteFile();
        return b.End();
    }

    /// <summary>0100-READ-ARGS.</summary>
    private void ReadArgs()
    {
        _periodKey = CobolText.Trimmed(b.Arg("VATPER"), 10);
        if (CobolText.IsSpaces(_periodKey))
        {
            b.Rc = 8;
            b.Display("STERP-E-0101 VATPER=<period key> is required");
            return;
        }
        b.Display("STERP-I-0100 MTDJSON VAT extract for " + b.Co.Trim() + " period " + _periodKey.Trim());
    }

    /// <summary>1000-PREPARE: VATRET9 PREPARE as WS-USER, the box identities 1+2-3 and 3-4-5 within +/-0.004.</summary>
    private async Task PrepareAsync()
    {
        _vr = new VatRet9Area { VatPeriodKey = _periodKey };
        var vh = await b.Ctx.Get<VatReturnProgram>().RunAsync(new CaRequest("PREPARE", b.Co, "", b.User, b.AsAt, ""), _vr, b.Ct);
        if (vh.ReturnCode > 4)
        {
            b.Rc = 8;
            b.Display("STERP-E-1001 " + CobolText.Fit(vh.Message, 200));
            return;
        }
        var amt = CobolNumeric.Store(Box(1) + Box(2) - Box(3), 13, 2);
        var amt2 = CobolNumeric.Store(Box(3) - Box(4) - Box(5), 13, 2);
        if (amt > 0.004m || amt < -0.004m || amt2 > 0.004m || amt2 < -0.004m)
        {
            b.Rc = 8;
            b.Display("STERP-E-1002 box identities do not hold - the return is not submittable");
            return;
        }
        b.RowsOut++;
        b.Display("STERP-I-1003 nine boxes prepared, " + Disp.U(_vr.RecordCnt, 6) + " VAT record(s), " + Disp.U(_vr.LateCnt, 6) +
                  " late entry(ies)");
    }

    /// <summary>MTD-BODY: boxes 1-5 S9(11)V99, boxes 6-9 S9(13).</summary>
    private decimal Box(int n) => n <= 5 ? CobolNumeric.Store(_vr!.Box(n), 11, 2) : CobolNumeric.Store(_vr!.Box(n), 13, 0);

    /// <summary>2000-BUILD-JSON / 2100-UNQUOTE.</summary>
    private void BuildJson()
    {
        var finalised = _vr!.ReturnStatus is "FINALISED" or "SUBMITTED" ? "true" : "false";
        var sb = new StringBuilder("{");
        sb.Append("\"periodKey\":").Append(Str(CobolText.Trimmed(_periodKey, 8)));
        string[] names =
        [
            "vatDueSales", "vatDueAcquisitions", "totalVatDue", "vatReclaimedCurrPeriod", "netVatDue", "totalValueSalesExVAT",
            "totalValuePurchasesExVAT", "totalValueGoodsSuppliedExVAT", "totalAcquisitionsExVAT",
        ];
        for (var i = 1; i <= 9; i++) sb.Append(",\"").Append(names[i - 1]).Append("\":").Append(Num(Box(i), i <= 5 ? 2 : 0));
        sb.Append(",\"finalised\":").Append(Str(finalised)).Append('}');
        var text = sb.ToString();
        text = text.Replace(":\"true\"", ":true  ", StringComparison.Ordinal).Replace(":\"false\"", ":false  ", StringComparison.Ordinal);
        _json = text;
    }

    /// <summary>JSON GENERATE of a numeric item: sign, integer digits without leading zeros (at least one), the decimals.</summary>
    private static string Num(decimal v, int scale)
    {
        var abs = Math.Abs(v);
        var text = abs.ToString(scale == 0 ? "0" : "0." + new string('0', scale), CultureInfo.InvariantCulture);
        return v < 0 ? "-" + text : text;
    }

    /// <summary>JSON GENERATE of an alphanumeric item: trailing spaces removed, quotes and backslashes escaped.</summary>
    private static string Str(string s) => "\"" + s.TrimEnd(' ').Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>3000-WRITE-FILE: RUNPATH BUILD MTD MTDJSON-&lt;CO&gt;-&lt;stamp&gt;.json, one 500-byte record per 500 characters.</summary>
    private void WriteFile()
    {
        var built = b.Ctx.Paths.Build("MTD", "MTDJSON", b.Co, "json", null, true, out var path);
        if (built.ReturnCode > 4)
        {
            b.Rc = 12;
            b.Display("STERP-E-3001 cannot build the mtd path");
            return;
        }
        b.SetSpoolName(path);
        FinSpool file;
        try
        {
            file = new FinSpool(path);
            b.Ctx.SpoolFiles.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            b.Rc = 12;
            b.Display("STERP-E-3002 cannot open " + path.Trim());
            return;
        }
        using (file)
        {
            var record = CobolText.Fit(_json, 2000);
            file.WriteLine(record[..500].TrimEnd(' '));
            if (_json.Length > 500) file.WriteLine(record.Substring(500, 500).TrimEnd(' '));
        }
        b.Display("STERP-I-3003 " + DispBinary(_json.Length) + " byte(s) written");
        b.Ctx.AnnounceSpool(path.Trim());
    }

    /// <summary>DISPLAY of a PIC S9(9) COMP-5 item: leading sign and the ten digits of its binary capacity.</summary>
    private static string DispBinary(long v) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString("D10", CultureInfo.InvariantCulture);
}
