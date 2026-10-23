using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Modules.Xmd.Services;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>
/// CHAINRPT (cobol/batch/xmd/CHAINRPT.cob; FR-STERP-XMD-024): the printed business chain trace. CALLs CHAINTRC in process
/// (D-XMD-02, the same <see cref="IChainTraceService"/> as the online enquiry) on the job's SQLIO session; a trace return code
/// above 4 becomes the program's return code. Read only. Listing FL-065 (RD PAGE LIMIT 60, FIRST DETAIL 8, LAST DETAIL 52,
/// FOOTING 58).
/// </summary>
internal static class ChainrptProgram
{
    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var b = XmdBatch.Start(ctx);
        var result = new JsonObject();
        try
        {
            return await RunAsync(b, result);
        }
        finally
        {
            ctx.Result = result;
        }
    }

    private static async Task<int> RunAsync(XmdBatch b, JsonObject result)
    {
        // 0100-READ-ARGS
        var typeText = b.GetArg("TYPE");
        var xbType = CobolText.IsSpaces(typeText) ? "ORDER" : CobolText.Upper(CobolText.Fit(typeText, 200)[..12]);
        var xbCode = CobolText.Fit(b.GetArg("REF"), 200)[..40];
        var sType = CobolText.Fit(xbType, 12);
        var sRef = CobolText.Fit(xbCode, 40);
        b.Display("STERP-I-0100 CHAINRPT " + CobolText.Trim(xbType) + " " + CobolText.Trim(xbCode));
        result["documentType"] = sType.TrimEnd(' ');
        result["reference"] = sRef.TrimEnd(' ');
        var steps = new JsonArray();

        if (!await b.OpenSpoolAsync("CHAINRPT", "Business chain trace", "Order to cash, procure to pay and assembly chains") || b.Rc > 4)
        {
            result["steps"] = steps;
            return Finish(b, result);
        }

        // 1000-TRACE
        var ca = new ChainTraceArea { Company = b.Co, Function = "TRACE", DocType = sType, DocRef = xbCode, MaxSteps = 0 };
        var r = await b.Ctx.Get<IChainTraceService>().ExecuteAsync(ca, b.Ct);
        var sChain = CobolText.Fit(ca.ChainName, 40);
        var sParty = CobolText.Fit(ca.Party, 20);
        b.Diffs = ca.Reversals;
        string sState, statusText;
        if (ca.Broken == "Y")
        {
            sState = "INCOMPLETE CHAIN";
            statusText = "INCOMPLETE";
        }
        else if (ca.Complete == "Y")
        {
            sState = "CHAIN COMPLETE";
            statusText = "COMPLETE";
        }
        else
        {
            sState = "PARTIALLY COMPLETE";
            statusText = "PARTIAL";
        }
        if (r.ReturnCode > 4)
        {
            b.Rc = r.ReturnCode;
            b.Display("STERP-W-1001 " + CobolText.Fit(r.ReasonCode, 8) + " " + CobolText.Trim(r.Message));
        }
        result["chainName"] = sChain.TrimEnd(' ');
        result["counterparty"] = sParty.TrimEnd(' ');
        result["state"] = sState;
        result["statusText"] = statusText;

        var rw = b.Report(
        [
            RwLine.Plus1(() => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Steps traced  ").Num(16, b.RowsOut, "ZZ9")
                .Lit(24, "Separate reversal or credit steps ").Num(58, b.Diffs, "ZZ9").Put(66, statusText, 20).Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "A physical return is not a financial credit and")
                .Lit(50, "neither is a replacement order (XMD-004)").Text),
        ]);
        rw.Initiate();
        rw.Generate(
            RwLine.Plus1(() => new RwText().Lit(1, "Document type ").Put(15, sType, 12).Lit(29, "Reference ").Put(39, sRef, 40)
                .Lit(82, "Chain ").Put(88, sChain, 40).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Counterparty  ").Put(15, sParty, 20).Put(39, sState, 28)
                .Lit(70, "One step complete does not imply the next.").Text),
            RwLine.PlusN(2, () => new RwText().Lit(1, "Seq").Lit(6, "Stage").Lit(32, "Type").Lit(46, "Reference").Lit(78, "Date")
                .Lit(90, "Status").Lit(104, "Quantity").Lit(118, "Value GBP").Text),
            RwLine.Plus1(() => new string('-', 132)));

        // 2000-LIST
        for (var i = 0; i < ca.StepCount && i < ChainTraceArea.MaxStepSlots; i++)
        {
            var s = ca.Steps[i];
            var dRef = CobolText.Fit(s.Reference, 40)[..30];
            var dDate = BatchSuite.FormatDate(s.Date);
            b.RowsOut++;
            rw.Generate(
                RwLine.Plus1(() => new RwText().Num(1, s.Seq, "Z9").Put(6, s.Stage, 24).Put(32, s.DocType, 12).Put(46, dRef, 30)
                    .Put(78, s.Date == 0 ? "00/00/0000" : dDate, 10).Put(90, s.Status, 12).Num(101, s.Quantity, "ZZZ,ZZ9.999")
                    .Num(114, s.Value, "Z,ZZZ,ZZ9.99-").Text),
                RwLine.Plus1(() => new RwText().Put(8, s.Kind, 10).Put(19, s.Note, 60).Text));
            steps.Add(new JsonObject
            {
                ["seq"] = s.Seq,
                ["stage"] = CobolText.Fit(s.Stage, 24).TrimEnd(' '),
                ["documentType"] = CobolText.Fit(s.DocType, 12).TrimEnd(' '),
                ["reference"] = dRef.TrimEnd(' '),
                ["date"] = CobolDate.FromYyyymmdd(s.Date) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                ["status"] = CobolText.Fit(s.Status, 12).TrimEnd(' '),
                ["quantity"] = CobolNumeric.WithScale(s.Quantity, 3),
                ["valueGbp"] = CobolNumeric.WithScale(s.Value, 2),
                ["kind"] = CobolText.Fit(s.Kind, 10).TrimEnd(' '),
                ["note"] = CobolText.Fit(s.Note, 60).TrimEnd(' '),
            });
        }
        result["steps"] = steps;
        b.Display("STERP-I-2001 " + Disp.Signed(b.RowsOut, 9) + " chain step(s), " + Disp.Signed(b.Diffs, 9) + " reversal or credit step(s)");

        rw.Terminate();
        b.CloseSpool();
        return Finish(b, result);
    }

    private static int Finish(XmdBatch b, JsonObject result)
    {
        result["stepsTraced"] = b.RowsOut;
        result["reversalSteps"] = b.Diffs;
        var rc = b.Finish();
        result["returnCode"] = rc;
        return rc;
    }
}
