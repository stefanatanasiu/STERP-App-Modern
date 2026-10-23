using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>FXCONVCA body (FXCONVCA.cpy:31-57).</summary>
public sealed class FxconvArea : Commarea
{
    /// <summary>FX-CURRENCY X(3) - upper-cased by the service.</summary>
    public string Currency { get; set; } = "";

    /// <summary>FX-RATE-DATE 9(8).</summary>
    public int RateDate { get; set; }

    /// <summary>FX-RATE-TYPE X(8): SPOT by default.</summary>
    public string RateType { get; set; } = "";

    /// <summary>FX-RATE S9(9)V9(8): foreign units per GBP 1.</summary>
    public decimal Rate { get; set; }

    /// <summary>FX-RATE-ENTERED S9(9)V9(8): the quotation as entered.</summary>
    public decimal RateEntered { get; set; }

    /// <summary>FX-QUOTE-BASIS X(10): FCYPERGBP (default) or GBPPERFCY.</summary>
    public string QuoteBasis { get; set; } = "";

    /// <summary>FX-ALLOW-INVERT X: "Y" allows a GBPPERFCY quotation to be turned round.</summary>
    public string AllowInvert { get; set; } = "";

    public string RateSource { get; set; } = "";
    public int RateFoundDate { get; set; }

    /// <summary>FX-FOREIGN-AMOUNT S9(13)V9(6).</summary>
    public decimal ForeignAmount { get; set; }

    /// <summary>FX-GBP-AMOUNT S9(13)V9(6).</summary>
    public decimal GbpAmount { get; set; }

    /// <summary>FX-PLACES S9(4): 0 becomes 2 (so 0 places cannot be requested, BRF-FXCONV-03); valid 0..6.</summary>
    public int Places { get; set; }

    /// <summary>FX-RATE-2 S9(9)V9(8): the settlement rate (REALISED).</summary>
    public decimal Rate2 { get; set; }

    public int Rate2Date { get; set; }

    /// <summary>FX-GBP-AMOUNT-2 S9(13)V9(6): settled sterling (REALISED).</summary>
    public decimal GbpAmount2 { get; set; }

    /// <summary>FX-REALISED S9(13)V9(6): positive = gain.</summary>
    public decimal Realised { get; set; }

    /// <summary>FX-POSITION X(10): ASSET (default) or LIABILITY.</summary>
    public string Position { get; set; } = "";

    /// <summary>FX-UNROUNDED S9(13)V9(12).</summary>
    public decimal Unrounded { get; set; }

    /// <summary>FX-RESIDUAL S9(13)V9(6).</summary>
    public decimal Residual { get; set; }

    /// <summary>FX-RATE-TEXT X(30): "&lt;rate 8 decimals&gt; &lt;ccy&gt; per GBP 1".</summary>
    public string RateText { get; set; } = "";
}

/// <summary>FXCONV - currency conversion at foreign units per GBP 1 (FR-STERP-PLATFORM-021, COM-010, D-14).</summary>
public interface IFxConverter
{
    /// <summary>CALL "FXCONV" USING area: GETRATE, TOGBP, TOFCY, REALISED (other FX0010 after validation).</summary>
    Task<LegacyResult> ExecuteAsync(FxconvArea area, CancellationToken ct = default);

    /// <summary>Builds an area with the caller's header, lets the caller fill it and executes it.</summary>
    Task<FxconvArea> CallAsync(KernelCaller caller, string function, Action<FxconvArea> fill, CancellationToken ct = default);
}

/// <summary>
/// FXCONV.cob:77-483: core.FxRate read on SQLIO slot 5, rounding through RNDALLOC ROUND. The sticky "rate table
/// unavailable" indicator is not reproduced: a probe or read failure answers RC 16 FX0001 for that call only (BC-PLATFORM-06).
/// core.usp_CheckFxStaleness (and its FX.MAX.AGE.DAYS / FX.MAXAGE.DAYS parameter mismatch, SME-1207) is not part of FXCONV
/// and stays with the legacy SQL Agent job, unchanged.
/// </summary>
public sealed class FxConverter(ISqlRunner sql, IRoundingAllocator rounding) : IFxConverter
{
    private ISqlRunner Sql => sql;

    private IRoundingAllocator Rounding => rounding;

    public async Task<FxconvArea> CallAsync(KernelCaller caller, string function, Action<FxconvArea> fill, CancellationToken ct = default)
    {
        var area = new FxconvArea();
        area.Header.Prepare(caller, function);
        fill(area);
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<LegacyResult> ExecuteAsync(FxconvArea a, CancellationToken ct = default)
    {
        var run = new Run(this, a);
        await run.ExecuteAsync(ct);
        return a.Header.Result;
    }

    private sealed class Run(FxConverter owner, FxconvArea a)
    {
        private readonly CommareaHeader _h = a.Header;
        private bool _bad;
        private int _places;

        public async Task ExecuteAsync(CancellationToken ct)
        {
            Initialise();
            ValidateCurrency();
            if (_bad) return;
            switch (_h.FunctionCode)
            {
                case "GETRATE": await GetRateAsync(ct); break;
                case "TOGBP": await ToSterlingAsync(ct); break;
                case "TOFCY": await ToForeignAsync(ct); break;
                case "REALISED": Realised(); break;
                default:
                    Reject("FX0010", "FXCONV does not support function " + K.Trim(_h.Function));
                    break;
            }
        }

        private string Ccy => K.X(a.Currency, 3);

        /// <summary>0100-INITIALISE.</summary>
        private void Initialise()
        {
            _h.Reset("FXCONV", "COM-010");
            a.Unrounded = 0;
            a.Residual = 0;
            a.RateText = "";
            a.Currency = CobolText.Upper(K.X(a.Currency, 3)).TrimEnd(' ');
            a.Position = CobolText.Upper(K.X(a.Position, 10)).TrimEnd(' ');
            a.QuoteBasis = CobolText.Upper(K.X(a.QuoteBasis, 10)).TrimEnd(' ');
            if (K.Blank(a.RateType, 8)) a.RateType = "SPOT";
            a.RateType = CobolText.Upper(K.X(a.RateType, 8)).TrimEnd(' ');
            if (K.Blank(a.QuoteBasis, 10)) a.QuoteBasis = "FCYPERGBP";
            if (K.Blank(a.Position, 10)) a.Position = "ASSET";
            a.Places = (int)CobolNumeric.Store(a.Places, 4, 0);
            if (a.Places == 0) a.Places = 2;
            _places = a.Places;
            a.Rate = CobolNumeric.Store(a.Rate, 9, 8);
            a.RateEntered = CobolNumeric.Store(a.RateEntered, 9, 8);
            a.Rate2 = CobolNumeric.Store(a.Rate2, 9, 8);
            a.ForeignAmount = CobolNumeric.Store(a.ForeignAmount, 13, 6);
            a.GbpAmount = CobolNumeric.Store(a.GbpAmount, 13, 6);
        }

        /// <summary>0200-VALIDATE-CURRENCY.</summary>
        private void ValidateCurrency()
        {
            if (K.Blank(a.Currency, 3))
            {
                // STRING FUNCTION TRIM(CA-MESSAGE) "assumed" INTO CA-MESSAGE: the trailing space of the moved text is trimmed
                Reject("FX0005", "A currency code is required - parity is neverassumed");
                return;
            }
            if (a.Places is < 0 or > 6) Reject("FX0006", "Decimal places must be between 0 and 6");
        }

        /// <summary>1000-GET-RATE (D-14: the latest published rate on or before the date).</summary>
        private async Task GetRateAsync(CancellationToken ct)
        {
            if (Ccy == "GBP")
            {
                a.Rate = 1;
                a.RateEntered = 1;
                a.RateFoundDate = a.RateDate;
                a.RateSource = "BASE CURRENCY";
                _h.Message = "GBP is the base currency - rate 1.00000000";
                RateText();
                return;
            }
            if (CobolDate.TestDate(a.RateDate) != 0)
            {
                Reject("FX0007", "A valid rate date is required");
                return;
            }
            var found = await ReadRateAsync(ct);
            if (!found)
            {
                _h.ReturnCode = 16;
                _h.ReasonCode = "FX0001";
                _h.Message = K.Msg("No ", K.Trim(a.RateType), " rate is held for ", Ccy, " on or before ", K.D8(a.RateDate));
                _h.MessageCount++;
                return;
            }
            CheckRate();
            if (_bad) return;
            RateText();
            if (a.RateFoundDate < a.RateDate)
            {
                _h.ReturnCode = 4;
                _h.ReasonCode = "FX0008";
                _h.Message = K.Msg("No rate published on ", K.D8(a.RateDate), " - using the rate of ", K.D8(a.RateFoundDate));
                _h.MessageCount++;
            }
            else
            {
                _h.Message = K.Msg("Rate ", K.Trim(a.RateText));
            }
        }

        /// <summary>1500-CHECK-RATE (COM-010: missing, zero or ambiguous rates block confirmation).</summary>
        private void CheckRate()
        {
            if (a.RateEntered == 0) a.RateEntered = a.Rate;
            if (a.Rate == 0)
            {
                Reject("FX0001", string.Concat("An exchange rate is required for ", Ccy, " - a missing rate is not parity"));
                return;
            }
            if (a.Rate < 0)
            {
                Reject("FX0002", "An exchange rate must be positive");
                return;
            }
            if (K.X(a.QuoteBasis, 10) == "GBPPERFCY ")
            {
                if (K.X(a.AllowInvert, 1) != "Y")
                {
                    Reject("FX0003", string.Concat("Rate quoted as sterling per unit of ", Ccy, " - STERP quotes foreign units per GBP 1"));
                    return;
                }
                // keep the entered quotation and the converted rate (truncated to 8 decimals)
                a.RateEntered = a.Rate;
                a.Rate = CobolNumeric.Divide(1m, a.RateEntered, 9, 8);
                a.QuoteBasis = "FCYPERGBP";
                _h.ReturnCode = 4;
                _h.ReasonCode = "FX0004";
                _h.Message = K.Msg("Entered rate ", K.Ed(a.RateEntered, "-(5)9.9(8)"),
                    " was quoted the other way round and has been turned round on request");
                _h.MessageCount++;
            }
        }

        /// <summary>2000-TO-STERLING: foreign / rate held to 12 decimals, rounded once, residual reported.</summary>
        private async Task ToSterlingAsync(CancellationToken ct)
        {
            await EnsureRateAsync(ct);
            if (_bad || _h.ReturnCode == 16) return;
            a.Unrounded = CobolNumeric.Divide(a.ForeignAmount, a.Rate, 13, 12);
            a.GbpAmount = CobolNumeric.Store(a.Unrounded, 13, 6);
            var r = owner.Rounding.Round(a.Unrounded, _places);
            a.GbpAmount = CobolNumeric.Store(r.ResultValue, 13, 6);
            a.Residual = CobolNumeric.Store(a.Unrounded - a.GbpAmount, 13, 6);
            _h.Message = K.Msg(Ccy, " converted at ", K.Trim(a.RateText), " to GBP ", K.Ed(a.GbpAmount, "-(12)9.99"));
        }

        /// <summary>2100-TO-FOREIGN: sterling x rate held to 12 decimals, rounded once, residual reported.</summary>
        private async Task ToForeignAsync(CancellationToken ct)
        {
            await EnsureRateAsync(ct);
            if (_bad || _h.ReturnCode == 16) return;
            a.Unrounded = CobolNumeric.Multiply(a.GbpAmount, a.Rate, 13, 12);
            a.ForeignAmount = CobolNumeric.Store(a.Unrounded, 13, 6);
            var r = owner.Rounding.Round(a.Unrounded, _places);
            a.ForeignAmount = CobolNumeric.Store(r.ResultValue, 13, 6);
            a.Residual = CobolNumeric.Store(a.Unrounded - a.ForeignAmount, 13, 6);
            _h.Message = K.Msg("GBP converted at ", K.Trim(a.RateText), " to ", Ccy, " ", K.Ed(a.ForeignAmount, "-(12)9.99"));
        }

        /// <summary>2500-ENSURE-RATE: the supplied rate, or a lookup when the rate is 0 and a date is given.</summary>
        private async Task EnsureRateAsync(CancellationToken ct)
        {
            if (Ccy == "GBP")
            {
                a.Rate = 1;
                a.RateEntered = 1;
                a.RateSource = "BASE CURRENCY";
                RateText();
                return;
            }
            if (a.Rate == 0 && a.RateDate != 0)
            {
                await GetRateAsync(ct);
                return;
            }
            CheckRate();
            if (!_bad) RateText();
        }

        /// <summary>3000-REALISED (D-14): booked and settled sterling rounded separately; the settlement rate is not inverted (BRF-FXCONV-01).</summary>
        private void Realised()
        {
            CheckRate();
            if (_bad) return;
            if (a.Rate2 <= 0)
            {
                Reject("FX0009", "A positive settlement rate is required");
                return;
            }
            var gbp1 = CobolNumeric.Divide(a.ForeignAmount, a.Rate, 13, 12);
            var gbp2 = CobolNumeric.Divide(a.ForeignAmount, a.Rate2, 13, 12);
            a.GbpAmount = CobolNumeric.Store(owner.Rounding.Round(gbp1, _places).ResultValue, 13, 6);
            a.GbpAmount2 = CobolNumeric.Store(owner.Rounding.Round(gbp2, _places).ResultValue, 13, 6);
            a.Realised = K.X(a.Position, 10) == "LIABILITY "
                ? CobolNumeric.Store(a.GbpAmount - a.GbpAmount2, 13, 6)
                : CobolNumeric.Store(a.GbpAmount2 - a.GbpAmount, 13, 6);
            var amount = K.Ed(a.Realised, "-(12)9.99");
            _h.Message = a.Realised < 0
                ? K.Msg("Realised exchange loss of GBP ", amount, " on this ", K.Trim(a.Position))
                : K.Msg("Realised exchange gain of GBP ", amount, " on this ", K.Trim(a.Position));
        }

        /// <summary>5000-RATE-TEXT.</summary>
        private void RateText() => a.RateText = K.Store(string.Concat(K.Ed(a.Rate, "-(5)9.9(8)"), " ", Ccy, " per GBP 1"), 30);

        /// <summary>6000-READ-RATE with the 6100-PROBE-TABLE probe (per call).</summary>
        private async Task<bool> ReadRateAsync(CancellationToken ct)
        {
            SqlioRow row;
            try
            {
                var probe = await owner.Sql.SingleAsync(
                    "SELECT CASE WHEN COL_LENGTH('core.FxRate','UnitsPerGBP') IS NOT NULL THEN 'OK' ELSE 'NO' END", [], [2], ct);
                if (!probe.Found || probe.Text(0) == "NO") return false;
                row = await owner.Sql.SingleAsync(
                    "SELECT TOP 1 CONVERT(char(8), RateDate, 112), CONVERT(varchar(30), UnitsPerGBP), ISNULL(Source, '') FROM core.FxRate " +
                    "WHERE CurrencyCode = ? AND RateType = ? AND RateDate <= ? ORDER BY RateDate DESC",
                    [SqlioParm.Text(Ccy), SqlioParm.Text(K.X(a.RateType, 8)), SqlioParm.Digits(a.RateDate, 8)],
                    [8, 30, 20], ct);
            }
            catch (SqlioException)
            {
                return false;
            }
            if (!row.Found) return false;
            a.RateFoundDate = row.Raw(0).All(char.IsAsciiDigit) ? (int)CobolNumeric.NumVal(row.Raw(0)) : 0;
            a.Rate = CobolNumeric.Store(row.NumVal(1), 9, 8);
            a.RateEntered = a.Rate;
            a.QuoteBasis = "FCYPERGBP";
            a.RateSource = CobolText.IsSpaces(row.Raw(2)) ? "core.FxRate" : row.Text(2);
            return true;
        }

        private void Reject(string reason, string message)
        {
            _bad = true;
            _h.Reject(reason, message);
        }
    }
}
