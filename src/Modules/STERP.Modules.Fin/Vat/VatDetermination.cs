using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Vat;

/// <summary>VATDETCA body fields of the RATE function (cobol/copy/VATDETCA.cpy).</summary>
public sealed class VatDetArea
{
    public string VatCode { get; set; } = "";
    public int TaxPoint { get; set; }
    public string Direction { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal FxRate { get; set; }

    public decimal RatePct { get; set; }
    public int RateFrom { get; set; }
    public string Liability { get; set; } = "";
    public string Mechanism { get; set; } = "";
    public string Recoverable { get; set; } = "N";
    public string SelfAccounts { get; set; } = "N";
    public int OutVatBox { get; set; }
    public int InVatBox { get; set; }
    public int OutNetBox { get; set; }
    public int InNetBox { get; set; }
    public int VatBox { get; set; }
    public int NetBox { get; set; }
    public string InvoiceWording { get; set; } = "";
    public string EvidenceReqd { get; set; } = "";

    public JsonObject ToJson() => new()
    {
        ["vatCode"] = VatCode,
        ["taxPoint"] = Bank.BankRecArea.CobolDateText(TaxPoint),
        ["direction"] = Direction,
        ["ratePct"] = CobolNumeric.WithScale(RatePct, 4),
        ["rateFrom"] = Bank.BankRecArea.CobolDateText(RateFrom),
        ["liability"] = Liability,
        ["mechanism"] = Mechanism,
        ["recoverable"] = Recoverable,
        ["selfAccounts"] = SelfAccounts,
        ["outVatBox"] = OutVatBox,
        ["inVatBox"] = InVatBox,
        ["outNetBox"] = OutNetBox,
        ["inNetBox"] = InNetBox,
        ["vatBox"] = VatBox,
        ["netBox"] = NetBox,
        ["invoiceWording"] = InvoiceWording,
        ["evidenceReqd"] = EvidenceReqd,
    };
}

/// <summary>VATDET (cobol/online/fin/VATDET.cob) function RATE: the VAT code treatment and the rate in force at a tax point.</summary>
public sealed class VatDetermination(ISqlRunner sql)
{
    private const string Program = "VATDET";

    public async Task<CaHeader> RunAsync(CaRequest req, VatDetArea vd, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-017");
        Initialise(req, vd);
        try
        {
            if (req.Function == "RATE")
            {
                await RateAsync(ca, vd, ct);
            }
            else
            {
                ca.Reject("VAT0009", "VATDET does not support function " + req.Function.Trim());
            }
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
        }
        return ca;
    }

    // 0100-INITIALISE
    private static void Initialise(CaRequest req, VatDetArea vd)
    {
        vd.RatePct = 0;
        vd.RateFrom = 0;
        vd.OutVatBox = vd.InVatBox = vd.OutNetBox = vd.InNetBox = vd.VatBox = vd.NetBox = 0;
        vd.Liability = vd.Mechanism = vd.InvoiceWording = vd.EvidenceReqd = "";
        vd.Recoverable = vd.SelfAccounts = "N";
        vd.VatCode = CobolText.Upper(vd.VatCode);
        if (CobolText.IsSpaces(vd.Direction) || vd.Direction.All(c => c == '\0')) vd.Direction = "OUTPUT";
        vd.Direction = CobolText.Upper(vd.Direction);
        if (vd.TaxPoint == 0) vd.TaxPoint = req.BusinessDate;
        if (CobolText.IsSpaces(vd.Currency)) vd.Currency = "GBP";
        if (vd.FxRate == 0) vd.FxRate = 1;
    }

    // 1000-RATE
    private async Task RateAsync(CaHeader ca, VatDetArea vd, CancellationToken ct)
    {
        if (CobolText.IsSpaces(vd.VatCode) || vd.VatCode.All(c => c == '\0'))
        {
            ca.Reject("VAT0001", "A VAT code is required on every taxable line");
            return;
        }
        await ReadCodeAsync(ca, vd, ct);
        if (ca.Failed) return;
        await ReadRateAsync(ca, vd, ct);
    }

    // 1100-READ-CODE
    private async Task ReadCodeAsync(CaHeader ca, VatDetArea vd, CancellationToken ct)
    {
        var row = await sql.SingleAsync(
            "SELECT v.Name, v.Liability, v.Direction, v.Mechanism, CAST(v.IsRecoverable AS char(1)), " +
            "CAST(v.SelfAccounts AS char(1)), CAST(ISNULL(v.OutputVatBox,0) AS varchar(4)), " +
            "CAST(ISNULL(v.InputVatBox,0) AS varchar(4)), CAST(ISNULL(v.OutputNetBox,0) AS varchar(4)), " +
            "CAST(ISNULL(v.InputNetBox,0) AS varchar(4)), ISNULL(v.InvoiceWording,''), ISNULL(v.EvidenceRequired,''), " +
            "CAST(v.IsActive AS char(1)) FROM fin.VatCode v WHERE v.VatCode = @p1",
            vd.VatCode);
        if (row is null)
        {
            ca.Set(16, "VAT0002", "VAT code " + vd.VatCode.Trim() + " is not held");
            return;
        }
        var direction = Slot.Text(row[2], 6);
        vd.Liability = Slot.Text(row[1], 12);
        vd.Mechanism = Slot.Text(row[3], 12);
        vd.InvoiceWording = Slot.Text(row[10], 200);
        vd.EvidenceReqd = Slot.Text(row[11], 100);
        vd.OutVatBox = (int)(Slot.Int(Slot.Text(row[6], 4)) % 100);
        vd.InVatBox = (int)(Slot.Int(Slot.Text(row[7], 4)) % 100);
        vd.OutNetBox = (int)(Slot.Int(Slot.Text(row[8], 4)) % 100);
        vd.InNetBox = (int)(Slot.Int(Slot.Text(row[9], 4)) % 100);
        if (Slot.Text(row[4], 4).StartsWith('1')) vd.Recoverable = "Y";
        if (Slot.Text(row[5], 4).StartsWith('1')) vd.SelfAccounts = "Y";
        if (vd.Direction == "OUTPUT")
        {
            vd.VatBox = vd.OutVatBox;
            vd.NetBox = vd.OutNetBox;
        }
        else
        {
            vd.VatBox = vd.InVatBox;
            vd.NetBox = vd.InNetBox;
        }
        if (!Slot.Text(row[12], 4).StartsWith('1'))
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "VAT0003";
            ca.Message = "That VAT code is no longer in use for new documen";
            ca.MsgCount++;
        }
        if (direction == "SALE" && vd.Direction == "INPUT")
        {
            ca.Reject("VAT0004", "That VAT code may only be used on sales");
            return;
        }
        if (direction == "PURCH" && vd.Direction == "OUTPUT" && vd.Mechanism != "RCSERVICE" && vd.Mechanism != "PVA")
        {
            ca.Reject("VAT0005", "That VAT code may only be used on purchases");
        }
    }

    // 1200-READ-RATE
    private async Task ReadRateAsync(CaHeader ca, VatDetArea vd, CancellationToken ct)
    {
        var dateText = vd.TaxPoint.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture);
        var row = await sql.SingleAsync(
            "SELECT TOP (1) CAST(r.RatePct AS varchar(14)), CONVERT(char(8), r.EffectiveFrom, 112) " +
            "FROM fin.VatRate r WHERE r.VatCode = @p1 AND r.EffectiveFrom <= @p2 ORDER BY r.EffectiveFrom DESC",
            vd.VatCode, dateText);
        if (row is null)
        {
            ca.Reject("VAT0006", "No approved VAT rate is in force for " + vd.VatCode.Trim() + " at that tax point");
            return;
        }
        vd.RatePct = CobolNumeric.Store(Slot.Num(Slot.Text(row[0], 14)), 5, 4);
        vd.RateFrom = (int)(Slot.Int(Slot.Text(row[1], 8)) % 100000000);
    }
}
