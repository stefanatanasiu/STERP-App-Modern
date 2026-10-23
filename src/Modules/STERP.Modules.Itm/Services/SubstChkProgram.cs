using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Itm.Services;

/// <summary>
/// SUBSTCHK CHECK (cobol/online/itm/SUBSTCHK.cob; FR-STERP-ITM-025): may this substitute replace the item in this context at
/// the as-at date. Read-only. LIST, REVCHECK and APPLY are reached only by the TSUBSTCK harness and are deferred (SME-1463).
/// </summary>
internal sealed class SubstChkProgram(Sqlio sql)
{
    private const string Prog = "SUBSTCHK";

    private bool _error;
    private string _ctxColumn = "";
    private int _asAt;

    public async Task RunAsync(CaHeader ca, SubstchkCommarea sb, CancellationToken ct)
    {
        try
        {
            Initialise(ca, sb);
            Validate(ca, sb);
            if (_error) return;
            switch (ca.Function)
            {
                case "CHECK":
                    await CheckAsync(ca, sb, ct);
                    break;
                case "LIST":
                case "REVCHECK":
                case "APPLY":
                    throw new NotSupportedException($"SUBSTCHK {ca.Function} is deferred (STERP-ITM.md FR-STERP-ITM-025, SME-1463).");
                default:
                    ca.ReasonCode = "SUB0008";
                    ca.SetMessage("SUBSTCHK does not support function " + Cob.Trim(ca.Function));
                    Reject(ca, sb);
                    break;
            }
        }
        finally
        {
            sb.AsAt = Cob.Date(_asAt);
        }
    }

    private void Initialise(CaHeader ca, SubstchkCommarea sb)
    {
        _error = false;
        ca.Program = Prog;
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "ITM-019";
        sb.Lines.Clear();
        sb.QtySubstitute = 0;
        sb.ValidFrom = null;
        sb.ValidTo = null;
        sb.Permitted = "N";
        sb.Interchangeable = "N";
        sb.ApprovedBy = "";
        sb.Restrictions = "";
        sb.NeedsAgreement = "N";
        sb.Ratio = 0;
        sb.ItemCode = Cob.Upper(sb.ItemCode, 40);
        sb.Substitute = Cob.Upper(sb.Substitute, 40);
        sb.Context = Cob.Upper(sb.Context, 10);
        sb.Revision = Cob.X(sb.Revision, 4);
        sb.SubstituteRev = Cob.X(sb.SubstituteRev, 4);
        sb.AgreementRef = Cob.X(sb.AgreementRef, 40);
        sb.QtyRequired = Cob.Q(sb.QtyRequired);
        if (Cob.Spaces(sb.Context)) sb.Context = "KIT";
        _asAt = Cob.Date8(sb.AsAt);
        if (_asAt == 0) _asAt = ca.BusinessDate;
        if (_asAt == 0) _asAt = 20261005;
        _ctxColumn = sb.Context switch
        {
            "PURCHASE" => "ContextPurchase",
            "SALES" => "ContextSales",
            "KIT" => "ContextKit",
            "BOM" => "ContextBom",
            _ => "",
        };
    }

    private void Validate(CaHeader ca, SubstchkCommarea sb)
    {
        if (Cob.Spaces(ca.Company) || Cob.Spaces(sb.ItemCode))
        {
            ca.ReasonCode = "SUB0001";
            ca.SetMessage("A company and an item are required");
            Reject(ca, sb);
            return;
        }
        if (_ctxColumn.Length == 0)
        {
            ca.ReasonCode = "SUB0002";
            ca.SetMessage("Context " + Cob.Trim(sb.Context) + " is not one of PURCHASE, SALES, KIT or BOM");
            Reject(ca, sb);
        }
    }

    /// <summary>2000-CHECK.</summary>
    private async Task CheckAsync(CaHeader ca, SubstchkCommarea sb, CancellationToken ct)
    {
        if (Cob.Spaces(sb.Substitute))
        {
            ca.ReasonCode = "SUB0001";
            ca.SetMessage("Name the substitute being proposed");
            Reject(ca, sb);
            return;
        }
        var r = await sql.SingleAsync(
            "SELECT s.ConversionRatio, CONVERT(char(8), s.ValidFrom, 112), ISNULL(CONVERT(char(8), s.ValidTo, 112),''), " +
            "ISNULL(s.ApprovedBy,''), s.RequiresCustomerAgreement, ISNULL(s.Restrictions,''), s.ContextPurchase, s.ContextSales, " +
            "s.ContextKit, s.ContextBom, i.LifecycleStatus, i.ItemClass, i.Description FROM inv.ItemSubstitute s JOIN inv.Item i ON " +
            "i.CompanyCode = s.CompanyCode AND i.ItemCode = s.SubstituteItemCode WHERE s.CompanyCode = @p1 AND s.ItemCode = @p2 AND " +
            "s.SubstituteItemCode = @p3",
            [ca.Company, sb.ItemCode, sb.Substitute], ct);
        if (r.IsError)
        {
            _error = true;
            ca.SetMessage(r.Message200(Prog));
            ca.ReasonCode = "SQL0001";
            ca.ReturnCode = 12;
            ca.AddMessage();
            return;
        }
        if (r.NotFound)
        {
            ca.ReasonCode = "SUB0001";
            ca.SetMessage(Cob.Trim(sb.Substitute) + " is not an approved substitute for " + Cob.Trim(sb.ItemCode));
            Reject(ca, sb);
            return;
        }
        var row = r.Row;
        sb.Ratio = Cob.F(Col.Num(row, 0));
        var validFrom = Date8(Col.Num(row, 1));
        var validTo = Cob.Spaces(Col.Text(row, 2)) ? 0 : Date8(Col.Num(row, 2));
        sb.ValidFrom = Cob.Date(validFrom);
        sb.ValidTo = Cob.Date(validTo);
        sb.ApprovedBy = Col.Text(row, 3, 20);
        sb.NeedsAgreement = Col.Bit(row, 4) ? "Y" : "N";
        sb.Restrictions = Col.Text(row, 5);
        var lifecycle = Col.Text(row, 10, 10);

        // 2100-CONTEXT-TEST
        var flag = sb.Context switch
        {
            "PURCHASE" => Col.Bit(row, 6),
            "SALES" => Col.Bit(row, 7),
            "KIT" => Col.Bit(row, 8),
            "BOM" => Col.Bit(row, 9),
            _ => false,
        };
        if (!flag)
        {
            ca.ReasonCode = "SUB0002";
            ca.SetMessage("The substitution rule does not cover " + Cob.Trim(sb.Context) + "; a separate approval is needed");
            Reject(ca, sb);
            return;
        }

        // 2200-DATE-TEST: inclusive window, open end
        if (validFrom > _asAt || (validTo > 0 && validTo < _asAt))
        {
            ca.ReasonCode = "SUB0003";
            ca.SetMessage("The substitution rule is not valid on that date");
            Reject(ca, sb);
            return;
        }

        // 2300-APPROVAL-TEST
        if (Cob.Spaces(sb.ApprovedBy))
        {
            ca.ReasonCode = "SUB0006";
            ca.SetMessage("The substitution rule has not been approved by an engineering or quality authority");
            Reject(ca, sb);
            return;
        }
        if (lifecycle != "RELEASED")
        {
            ca.ReasonCode = "SUB0004";
            ca.SetMessage("Substitute " + Cob.Trim(sb.Substitute) + " is " + Cob.Trim(lifecycle) + " and may not be used");
            Reject(ca, sb);
            return;
        }
        if (sb.NeedsAgreement == "Y" && Cob.Spaces(sb.AgreementRef))
        {
            // CR1320: RC 06 is not CA-FAILED, so the check goes on to the quantity and the permitted flag
            ca.ReasonCode = "SUB0005";
            ca.SetMessage("Substituting " + Cob.Trim(sb.Substitute) + " needs the customer's recorded agreement before it may be used");
            ca.ReturnCode = 6;
            _error = true;
            ca.AddMessage();
        }

        sb.QtySubstitute = Cob.QR(sb.QtyRequired * sb.Ratio);
        sb.Permitted = "Y";
        var text = Cob.Str(200, Cob.QtyEd(sb.QtySubstitute), " of ", Cob.Trim(sb.Substitute), " replace ");
        ca.SetMessage(Cob.Trim(text) + Cob.QtyEd(sb.QtyRequired) + " of " + Cob.Trim(sb.ItemCode) + " on this document only");
    }

    private void Reject(CaHeader ca, SubstchkCommarea sb)
    {
        _error = true;
        sb.Permitted = "N";
        ca.ReturnCode = 8;
        ca.AddMessage();
    }

    private static int Date8(decimal v) => (int)(Math.Abs(decimal.Truncate(v)) % 100_000_000m);
}
