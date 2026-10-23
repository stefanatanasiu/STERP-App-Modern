using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Vat;

public sealed class FinVatPrepareRequest
{
    public string? Basis { get; set; }
}

public sealed class FinVatErrorAssessRequest
{
    public string? VatPeriodKey { get; set; }
    public decimal? NetError { get; set; }
    public decimal? Box6Turnover { get; set; }
}

/// <summary>VAT return operations of VatService (Prepare, Drill, Record, WriteSubmissionFile, AssessError).</summary>
public static class VatEndpoints
{
    private static VatRet9Area Area(OperationContext ctx, string? basis) => new()
    {
        VatPeriodKey = In.X(ctx.Route("vatPeriodKey"), 10),
        Basis = In.X(basis, 10),
    };

    /// <summary>fin_prepareVatReturn: VatService.Prepare(key, basis) (VatService.vb:34-40) -> VATRET9 PREPARE.</summary>
    public static async Task<IResult> PrepareAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinVatPrepareRequest>();
        ctx.Session.LegacyProgram = "VATRET9";
        var vr = Area(ctx, body.Basis);
        var ca = await ctx.Get<VatReturnProgram>().RunAsync(CaRequest.From(ctx.Session, "PREPARE"), vr, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), vr.AnswerJson());
    }

    /// <summary>fin_drillVatBox: VatService.Drill (VatService.vb:104-114) -> VATRET9 DRILL, else fin.usp_VatBoxDrill directly.</summary>
    public static async Task<IResult> DrillAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "VATRET9";
        var boxNo = Periods.PeriodEndpoints.RouteLong(ctx, "boxNo");
        var vr = Area(ctx, "");
        vr.BoxNo = (int)In.N(boxNo, 2, 0, signed: false);
        var ca = await ctx.Get<VatReturnProgram>().RunAsync(CaRequest.From(ctx.Session, "DRILL"), vr, ctx.Aborted);
        JsonNode? procedureRows = null;
        if (vr.Drill.Count == 0)
        {
            procedureRows = await ctx.Get<IProcedureRunner>().TableAsync("fin.usp_VatBoxDrill", new
            {
                CompanyCode = ctx.Session.CompanyCode,
                VatPeriodKey = ctx.Route("vatPeriodKey"),
                BoxNo = boxNo,
            }, ctx.Aborted);
        }
        var data = new JsonObject
        {
            ["vatPeriodKey"] = vr.VatPeriodKey,
            ["boxNo"] = vr.BoxNo,
            ["drillCnt"] = vr.Drill.Count,
            ["records"] = vr.DrillJson(),
            ["procedureRows"] = procedureRows,
        };
        return ctx.Envelope(ca.ToResult(), data);
    }

    /// <summary>fin_finaliseVatReturn: VatService.Record("FINALISE", key, "", "") then LoadReturn (VatService.vb:117-128).</summary>
    public static async Task<IResult> FinaliseAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "VATRET9";
        var vr = Area(ctx, "");
        var ca = await ctx.Get<VatReturnProgram>().RunAsync(CaRequest.From(ctx.Session, "FINALISE"), vr, ctx.Aborted);
        var result = ca.ToResult();
        JsonNode? answer = null;
        JsonNode? saved = null;
        if (result.Succeeded)
        {
            answer = vr.AnswerJson();
            saved = await VatReadEndpoints.LoadReturnAsync(ctx.Get<IProcedureRunner>(), ctx.Session, ctx.Route("vatPeriodKey"), ctx.Aborted);
        }
        return ctx.Envelope(result, new JsonObject { ["answer"] = answer, ["savedReturn"] = saved });
    }

    /// <summary>
    /// fin_writeVatLocalFile: VatService.WriteSubmissionFile (VatService.vb:135-175) - a LOCAL hand-off file under the target
    /// run root work\vat (D-FIN-09), then VATRET9 SUBMIT records local filing; nothing is transmitted to HMRC or anywhere.
    /// </summary>
    public static async Task<IResult> WriteLocalFileAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        var sql = ctx.Get<IProcedureRunner>();
        var periodKey = ctx.Route("vatPeriodKey");
        if (!await sql.CanActAsync(s.UserCode, "FIN.VAT.SUBMIT", s.CompanyCode, s.SiteCode, ctx.Aborted))
        {
            throw new UnauthorizedAccessException("FIN.VAT.SUBMIT is required to write a local VAT file.");
        }
        var saved = await VatReadEndpoints.LoadReturnAsync(sql, s, periodKey, ctx.Aborted);
        if (Text(saved, "status") != "FINALISED")
        {
            throw new InvalidOperationException("Only a finalised VAT return may be written to a local file.");
        }
        var vrn = Text(saved, "vatRegNo");
        if (string.IsNullOrWhiteSpace(vrn)) throw new InvalidOperationException("The frozen return has no VAT registration number.");
        if (s.CompanyCode.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || periodKey.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Invalid company or period key for a VAT filename.");
        }
        var dir = Path.Combine(ctx.Get<OutputPaths>().Base, "work", "vat");
        var path = Path.Combine(dir, "VAT-" + s.CompanyCode + "-" + periodKey + "-" + Guid.NewGuid().ToString("N")[..12] + ".MTD");
        var uk = CultureInfo.GetCultureInfo("en-GB");
        var sb = new StringBuilder();
        sb.Append("STERP - Stefan's ERP - LOCAL VAT HAND-OFF (NOT TRANSMITTED)").Append("\r\n");
        sb.Append("COMPANY   " + s.CompanyCode).Append("\r\n");
        sb.Append("VRN       " + vrn).Append("\r\n");
        sb.Append("PERIODKEY " + periodKey).Append("\r\n");
        sb.Append("PERIOD    " + UkDate(Text(saved, "startDate")) + " TO " + UkDate(Text(saved, "endDate"))).Append("\r\n");
        sb.Append("EXPORTED  " + s.UserCode + " " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", uk)).Append("\r\n");
        for (var i = 1; i <= 9; i++)
        {
            sb.Append("BOX" + i.ToString(CultureInfo.InvariantCulture) + "      " + Dec(saved, "box" + i.ToString(CultureInfo.InvariantCulture)).ToString("0.00", uk).PadLeft(18)).Append("\r\n");
        }
        sb.Append("RECORDS   " + Int(saved, "records").ToString(CultureInfo.InvariantCulture) + "  LATE " +
                  Int(saved, "late").ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(path);
        try
        {
            await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ctx.Aborted);
            var vr = Area(ctx, "");
            vr.VatPeriodKey = In.X(periodKey, 10);
            vr.SubmissionRef = In.X(name, 60);
            vr.HmrcResponse = In.X("LOCAL FILE ONLY; NOT TRANSMITTED", 300);
            s.LegacyProgram = "VATRET9";
            var ca = await ctx.Get<VatReturnProgram>().RunAsync(CaRequest.From(s, "SUBMIT"), vr, ctx.Aborted);
            var result = ca.ToResult();
            if (!result.Succeeded) throw new InvalidOperationException(result.Display);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        saved = await VatReadEndpoints.LoadReturnAsync(sql, s, periodKey, ctx.Aborted);
        ctx.Get<ILoggerFactory>().CreateLogger("Sterp.Fin.Vat").LogInformation("Local VAT hand-off file written (not transmitted): {Path}", path);
        return ctx.Direct(new JsonObject
        {
            ["filePath"] = path,
            ["fileName"] = name,
            ["transmitted"] = false,
            ["notice"] = "NOT transmitted to HMRC. This is a local file reference, not an HMRC receipt.",
            ["savedReturn"] = saved,
        });
    }

    /// <summary>fin_assessVatError: VatService.AssessError(key, key, netError, False) (VatService.vb:190-202) -> VATERRC ASSESS.</summary>
    public static async Task<IResult> AssessErrorAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinVatErrorAssessRequest>();
        ctx.Session.LegacyProgram = "VATERRC";
        var key = In.X(body.VatPeriodKey, 10);
        var ve = new VatErrArea
        {
            OrigPeriodKey = key,
            CorrPeriodKey = key,
            DiscoveredDate = CobolDate.ToYyyymmdd(ctx.Session.BusinessDate),
            NetError = In.N(body.NetError, 11, 2),
            Box6Turnover = In.N(body.Box6Turnover, 13, 2),
            IsDeliberate = "N",
        };
        var ca = await ctx.Get<VatErrorProgram>().RunAsync(CaRequest.From(ctx.Session, "ASSESS"), ve, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), ve.ToJson());
    }

    private static string Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static decimal Dec(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : 0m;

    private static int Int(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    /// <summary>Fmt.Date of a yyyy-MM-dd value: dd/MM/yyyy.</summary>
    private static string UkDate(string iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
            : iso;
}
