using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// PAYTERMC (cobol/online/po/PAYTERMC.cob): payment terms against the company policy and the statutory maximum (D-25,
/// PO-027) - CHECK, APPLY (CHECK + due date) and DUEDATE. Terms through TERMCALC, limits through PARMGET; no table access
/// of its own.
/// </summary>
internal sealed class PaytermcProgram(IPaymentTermsCalculator termcalc, IParameterService parmget)
{
    private const int DefPolicy = 30;
    private const int DefStatutory = 60;
    private const int DefSme = 60;

    private sealed class Work
    {
        public bool Fatal;
    }

    public async Task RunAsync(CaHeader ca, PaytermcArea pt, CancellationToken ct)
    {
        var w = new Work();
        Init(ca, pt, w);
        if (w.Fatal) return;
        switch (ca.Function)
        {
            case "CHECK":
                await CheckAsync(ca, pt, w, ct);
                break;
            case "APPLY":
                await CheckAsync(ca, pt, w, ct);
                if (!w.Fatal) await DueDateAsync(ca, pt, ct);
                break;
            case "DUEDATE":
                await ResolveAsync(ca, pt, w, ct);
                if (!w.Fatal) await DueDateAsync(ca, pt, ct);
                break;
            default:
                ca.ReasonCode = "PTC0006";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by PAYTERMC");
                ca.ReturnCode = 8;
                break;
        }
    }

    private static void Init(CaHeader ca, PaytermcArea pt, Work w)
    {
        ca.Program = "PAYTERMC";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        pt.SettDiscPct = 0; pt.SettDiscDays = 0;
        pt.DueDate = 0; pt.ExcessDays = 0; pt.ApprovalId = 0;
        pt.TermsBasis = ""; pt.TermsSnapshot = "";
        pt.Outcome = "WITHIN";
        pt.Reportable = "N";
        pt.NeedsException = "N";
        if (Cob.Spaces(pt.LimitSource)) pt.LimitSource = "LOOKUP";
        if (Cob.Spaces(pt.SupplierSize)) pt.SupplierSize = "LARGE";
        if (pt.OrderDate == 0) pt.OrderDate = ca.BusinessDate;
        if (Cob.Spaces(ca.Company))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "PTC0001";
            ca.Message = Cob.Msg("A company code is needed to check payment terms against the company payment policy");
            w.Fatal = true;
        }
    }

    private async Task CheckAsync(CaHeader ca, PaytermcArea pt, Work w, CancellationToken ct)
    {
        await ResolveAsync(ca, pt, w, ct);
        if (w.Fatal) return;
        await LimitsAsync(ca, pt, ct);
        if (w.Fatal) return;
        Assess(ca, pt);
        Snapshot(pt);
    }

    /// <summary>3000-RESOLVE: a terms code is read through TERMCALC GETTERMS.</summary>
    private async Task ResolveAsync(CaHeader ca, PaytermcArea pt, Work w, CancellationToken ct)
    {
        if (Cob.Spaces(pt.TermsCode) && pt.TermsDays == 0)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "PTC0001";
            ca.Message = Cob.Msg("Either a payment terms code or a day count is needed before the terms can be checked");
            w.Fatal = true;
            return;
        }
        if (Cob.Spaces(pt.TermsCode))
        {
            pt.TermsBasis = "INVOICE";
            return;
        }
        var caller = new KernelCaller(ca.Company, ca.Site, ca.User, pt.OrderDate, ca.CorrelationId);
        var tc = await termcalc.CallAsync(caller, "GETTERMS", a =>
        {
            a.TermsCode = pt.TermsCode;
            a.InvoiceDate = pt.OrderDate;
        }, ct);
        if (tc.Header.ReturnCode >= 8)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "PTC0002";
            ca.Message = Cob.Msg("Payment terms ", Cob.Trim(pt.TermsCode), " are not on file: ",
                Sterp.Platform.Kernel.Cobol.CobolText.Fit(tc.Header.Message, 200)[..120]);
            w.Fatal = true;
            return;
        }
        pt.TermsBasis = Cob.X(tc.BasisType, 20);
        pt.SettDiscPct = Cob.T(tc.DiscountPct, 5, 4);
        pt.SettDiscDays = (int)Cob.T(tc.DiscountDays, 5, 0);
        pt.TermsDays = (int)Cob.T(tc.TermDays != 0 ? tc.TermDays : tc.Days, 5, 0);
        if (pt.TermsDays < 0)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "PTC0007";
            ca.Message = Cob.Msg("Payment terms ", Cob.Trim(pt.TermsCode), " resolve to a negative number of days");
            w.Fatal = true;
        }
    }

    /// <summary>3500-LIMITS: policy and statutory maximum (PASSED by the caller, else PARMGET with the defaults).</summary>
    private async Task LimitsAsync(CaHeader ca, PaytermcArea pt, CancellationToken ct)
    {
        if (pt.LimitSource == "PASSED")
        {
            pt.PolicyDays = pt.PolicyDaysIn;
            pt.StatMaxDays = pt.StatMaxIn;
        }
        else
        {
            pt.PolicyDays = (int)Cob.T(await ParmAsync(ca, pt, "PO.TERMS.POLICYDAYS", DefPolicy, ct), 5, 0);
            pt.StatMaxDays = (int)Cob.T(await ParmAsync(ca, pt, "PO.TERMS.STATMAX", DefStatutory, ct), 5, 0);
            if (pt.SupplierSize is "SME" or "MICRO")
            {
                var sme = await ParmAsync(ca, pt, "PO.TERMS.SMEMAX", DefSme, ct);
                if (sme > 0 && sme < pt.StatMaxDays) pt.StatMaxDays = (int)Cob.T(sme, 5, 0);
            }
        }
        if (pt.PolicyDays <= 0) pt.PolicyDays = DefPolicy;
        if (pt.StatMaxDays <= 0) pt.StatMaxDays = DefStatutory;
        pt.EffectiveLimit = pt.StatMaxDays;
    }

    /// <summary>3600-PARM: PARMGET GETNUM with the default text; RC >= 8 or 0 gives the default.</summary>
    private async Task<decimal> ParmAsync(CaHeader ca, PaytermcArea pt, string code, int dflt, CancellationToken ct)
    {
        var dayEd = Cob.Ed(dflt, "-(4)9");
        var caller = new KernelCaller(ca.Company, ca.Site, ca.User, pt.OrderDate, "");
        var pm = await parmget.GetAsync(caller, code, pt.OrderDate, dayEd, numeric: true, ct);
        var value = pm.ValueNum;
        if (pm.Header.ReturnCode >= 8 || value == 0) value = Cob.NumVal(dayEd);
        return value;
    }

    /// <summary>4000-ASSESS.</summary>
    private static void Assess(CaHeader ca, PaytermcArea pt)
    {
        if (pt.TermsDays > pt.PolicyDays) pt.Reportable = "Y";
        if (pt.TermsDays > pt.EffectiveLimit)
        {
            pt.ExcessDays = (int)Cob.T(pt.TermsDays - pt.EffectiveLimit, 5, 0);
            pt.NeedsException = "Y";
            pt.Outcome = "BREACH";
            ca.ReturnCode = 6;
            ca.ReasonCode = "PTC0004";
            var msg = Cob.Str(200, "Payment terms of ", Cob.Ed(pt.TermsDays, "-(4)9"), " days exceed the maximum of ");
            ca.Message = Cob.Msg(Cob.Trim(msg), " ", Cob.Ed(pt.EffectiveLimit, "-(4)9"), " days allowed for a ", Cob.Trim(pt.SupplierSize),
                " supplier and need an authorised exception");
            if (!Cob.Spaces(pt.ExceptionBy) && !Cob.Spaces(pt.ExceptionReason))
            {
                pt.Outcome = "EXCEPTED";
                pt.NeedsException = "N";
                ca.ReturnCode = 4;
                ca.ReasonCode = "PTC0004";
                ca.Message = Cob.Msg("Terms above the statutory maximum authorised by ", Cob.Trim(pt.ExceptionBy),
                    " - reported in payment practices data");
            }
            else if (!Cob.Spaces(pt.ExceptionBy) || !Cob.Spaces(pt.ExceptionReason))
            {
                ca.ReasonCode = "PTC0005";
                ca.Message = Cob.Msg("An exception to the payment terms maximum needs both an approver and a reason");
            }
            return;
        }
        if (pt.TermsDays > pt.PolicyDays)
        {
            pt.ExcessDays = (int)Cob.T(pt.TermsDays - pt.PolicyDays, 5, 0);
            pt.Outcome = "WARN";
            ca.ReturnCode = 4;
            ca.ReasonCode = "PTC0003";
            var msg = Cob.Str(200, "Payment terms of ", Cob.Ed(pt.TermsDays, "-(4)9"), " days are longer than the company policy of ");
            ca.Message = Cob.Msg(Cob.Trim(msg), " ", Cob.Ed(pt.PolicyDays, "-(4)9"), " days and will appear in the payment practices report");
            return;
        }
        pt.Outcome = "WITHIN";
        pt.ExcessDays = 0;
        ca.ReturnCode = 0;
    }

    /// <summary>4500-SNAPSHOT: the self-contained terms text.</summary>
    private static void Snapshot(PaytermcArea pt)
    {
        var days = Cob.Ed(pt.TermsDays, "-(4)9");
        pt.TermsSnapshot = pt.TermsBasis switch
        {
            "EOM" => Cob.Str(100, days, " days from end of month"),
            "DAYOFNEXTMONTH" => Cob.Str(100, "day ", days, " of the following month"),
            _ => Cob.Str(100, days, " days from receipt of a valid invoice"),
        };
        if (pt.SettDiscPct > 0)
        {
            var msg = Cob.Str(200, Cob.Trim(pt.TermsSnapshot), ", ", Cob.Ed(pt.SettDiscPct, "-(3)9.99"), "% settlement discount within ",
                Cob.Ed(pt.SettDiscDays, "-(4)9"), " days");
            pt.TermsSnapshot = Cob.X(msg, 100);
        }
    }

    /// <summary>5000-DUE-DATE: TERMCALC DUEDATE from the invoice (order) date.</summary>
    private async Task DueDateAsync(CaHeader ca, PaytermcArea pt, CancellationToken ct)
    {
        if (pt.InvoiceDate == 0) pt.InvoiceDate = pt.OrderDate;
        var caller = new KernelCaller(ca.Company, ca.Site, ca.User, pt.InvoiceDate, ca.CorrelationId);
        var tc = await termcalc.CallAsync(caller, "DUEDATE", a =>
        {
            a.TermsCode = pt.TermsCode;
            if (Cob.Spaces(pt.TermsCode))
            {
                a.BasisType = pt.TermsBasis;
                a.Days = pt.TermsDays;
            }
            a.InvoiceDate = pt.InvoiceDate;
            a.ReceivedDate = pt.InvoiceDate;
            a.NetAmount = pt.ValueGbp;
        }, ct);
        if (tc.Header.ReturnCode >= 8)
        {
            if (ca.ReturnCode < 8)
            {
                ca.ReturnCode = 8;
                ca.ReasonCode = "PTC0008";
                ca.Message = Cob.X(tc.Header.Message, 200);
            }
            return;
        }
        pt.DueDate = tc.DueDate;
    }
}
