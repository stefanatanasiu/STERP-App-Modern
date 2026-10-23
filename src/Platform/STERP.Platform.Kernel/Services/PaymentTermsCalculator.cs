using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>TERMCALCCA body (TERMCALCCA.cpy:32-59).</summary>
public sealed class TermcalcArea : Commarea
{
    /// <summary>TC-TERMS-CODE X(10) - upper-cased by the service.</summary>
    public string TermsCode { get; set; } = "";

    /// <summary>TC-BASIS-TYPE X(16): INVOICE, EOM, DAYOFNEXTMONTH, STATUTORY (upper-cased by the service).</summary>
    public string BasisType { get; set; } = "";

    public int Days { get; set; }
    public int DayOfMonth { get; set; }
    public int DiscountDays { get; set; }

    /// <summary>TC-DISCOUNT-PCT S9(5)V9(4).</summary>
    public decimal DiscountPct { get; set; }

    public int InvoiceDate { get; set; }
    public int SupplyDate { get; set; }
    public int ReceivedDate { get; set; }
    public int DueDate { get; set; }
    public int DueDateRaw { get; set; }
    public int DiscountDate { get; set; }

    /// <summary>TC-NET-AMOUNT S9(13)V9(6).</summary>
    public decimal NetAmount { get; set; }

    /// <summary>TC-VAT-AMOUNT S9(13)V9(6).</summary>
    public decimal VatAmount { get; set; }

    /// <summary>TC-DISCOUNT-AMT S9(13)V9(6).</summary>
    public decimal DiscountAmount { get; set; }

    /// <summary>TC-DISCOUNT-BASIS X(8): NET (default) or exactly GROSS (case-sensitive).</summary>
    public string DiscountBasis { get; set; } = "";

    /// <summary>TC-BACS-ADJUST X: exactly "Y" moves the due date (case-sensitive).</summary>
    public string BacsAdjust { get; set; } = "";

    /// <summary>TC-BACS-DIRECTION X(4): NEXT (default) or PREV.</summary>
    public string BacsDirection { get; set; } = "";

    /// <summary>TC-CALENDAR X(10): BACS by default.</summary>
    public string Calendar { get; set; } = "";

    public int TermDays { get; set; }

    /// <summary>TC-MAX-TERM-DAYS: 0 becomes 60 (D-25).</summary>
    public int MaxTermDays { get; set; }

    public string LongTermFlag { get; set; } = "";

    /// <summary>TC-SOURCE X(10): TERMS, EXPLICIT or STATUTORY.</summary>
    public string Source { get; set; } = "";

    public string TermsDescription { get; set; } = "";
    public string DueDateUk { get; set; } = "";
    public string DiscountDateUk { get; set; } = "";
}

/// <summary>TERMCALC - payment terms, due and settlement discount dates (FR-STERP-PLATFORM-020, COM-012).</summary>
public interface IPaymentTermsCalculator
{
    /// <summary>CALL "TERMCALC" USING area: DUEDATE, GETTERMS, DISCOUNT (other TRM0009).</summary>
    Task<LegacyResult> ExecuteAsync(TermcalcArea area, CancellationToken ct = default);

    /// <summary>Builds an area with the caller's header (INITIALIZEd body), lets the caller fill it and executes it.</summary>
    Task<TermcalcArea> CallAsync(KernelCaller caller, string function, Action<TermcalcArea> fill, CancellationToken ct = default);
}

/// <summary>
/// TERMCALC.cob:66-613: terms from core.PaymentTerms (SQLIO slot 3), dates through DATECALC and the discount through RNDALLOC.
/// The sticky "terms table unavailable" indicator after a read failure is not reproduced: the failure applies to that call
/// only (statutory default RC 04 TRM0002 for DUEDATE, RC 16 TRM0002 for GETTERMS; BC-PLATFORM-06).
/// </summary>
public sealed class PaymentTermsCalculator(ISqlRunner sql, IBusinessCalendar calendar, IRoundingAllocator rounding) : IPaymentTermsCalculator
{
    private ISqlRunner Sql => sql;

    private IBusinessCalendar Calendar => calendar;

    private IRoundingAllocator Rounding => rounding;

    public async Task<TermcalcArea> CallAsync(KernelCaller caller, string function, Action<TermcalcArea> fill, CancellationToken ct = default)
    {
        var area = new TermcalcArea();
        area.Header.Prepare(caller, function);
        fill(area);
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<LegacyResult> ExecuteAsync(TermcalcArea a, CancellationToken ct = default)
    {
        var run = new Run(this, a);
        await run.ExecuteAsync(ct);
        return a.Header.Result;
    }

    private sealed class Run(PaymentTermsCalculator owner, TermcalcArea a)
    {
        private readonly CommareaHeader _h = a.Header;
        private bool _bad;
        private bool _termsFound;

        public async Task ExecuteAsync(CancellationToken ct)
        {
            Initialise();
            switch (_h.FunctionCode)
            {
                case "DUEDATE": await DueDateAsync(ct); break;
                case "GETTERMS": await GetTermsOnlyAsync(ct); break;
                case "DISCOUNT": DiscountAmount(); break;
                default:
                    Reject("TRM0009", "TERMCALC does not support function " + K.Trim(_h.Function));
                    break;
            }
        }

        /// <summary>0100-INITIALISE.</summary>
        private void Initialise()
        {
            _h.Reset("TERMCALC", "COM-012");
            a.DueDate = 0;
            a.DueDateRaw = 0;
            a.DiscountDate = 0;
            a.DiscountAmount = 0;
            a.TermDays = 0;
            a.LongTermFlag = "N";
            a.Source = "";
            a.TermsDescription = "";
            a.DueDateUk = "";
            a.DiscountDateUk = "";
            a.Days = (int)CobolNumeric.Store(a.Days, 4, 0);
            a.DayOfMonth = (int)CobolNumeric.Store(a.DayOfMonth, 4, 0);
            a.DiscountDays = (int)CobolNumeric.Store(a.DiscountDays, 4, 0);
            a.DiscountPct = CobolNumeric.Store(a.DiscountPct, 5, 4);
            a.NetAmount = CobolNumeric.Store(a.NetAmount, 13, 6);
            a.VatAmount = CobolNumeric.Store(a.VatAmount, 13, 6);
            a.MaxTermDays = (int)CobolNumeric.Store(a.MaxTermDays, 4, 0);
            if (a.MaxTermDays == 0) a.MaxTermDays = 60;
            if (K.Blank(a.Calendar, 10)) a.Calendar = "BACS";
            if (K.Blank(a.BacsDirection, 4)) a.BacsDirection = "NEXT";
            if (K.Blank(a.DiscountBasis, 8)) a.DiscountBasis = "NET";
            a.BasisType = CobolText.Upper(K.X(a.BasisType, 16)).TrimEnd(' ');
            a.TermsCode = CobolText.Upper(K.X(a.TermsCode, 10)).TrimEnd(' ');
        }

        /// <summary>1000-DUE-DATE.</summary>
        private async Task DueDateAsync(CancellationToken ct)
        {
            ValidateDates();
            if (_bad) return;
            await ResolveTermsAsync(ct);
            if (_bad) return;
            await ApplyBasisAsync(ct);
            if (_bad) return;
            a.DueDateRaw = a.DueDate;
            await DiscountDateAsync(ct);
            if (K.X(a.BacsAdjust, 1) == "Y") await BacsAdjustAsync(ct);
            await TermLengthAsync(ct);
            DiscountAmount();
            await FormatDatesAsync(ct);
            DescribeTerms();
        }

        /// <summary>1100-GET-TERMS-ONLY.</summary>
        private async Task GetTermsOnlyAsync(CancellationToken ct)
        {
            if (K.Spaces(a.TermsCode, 10))
            {
                Reject("TRM0001", "A payment terms code is required");
                return;
            }
            await ReadTermsAsync(ct);
            if (!_termsFound)
            {
                _h.ReturnCode = 16;
                _h.ReasonCode = "TRM0002";
                _h.Message = K.Msg("Payment terms ", K.Trim(a.TermsCode), " are not held for this company");
                return;
            }
            a.Source = "TERMS";
            DescribeTerms();
        }

        /// <summary>1200-VALIDATE-DATES.</summary>
        private void ValidateDates()
        {
            if (a.InvoiceDate == 0 && a.SupplyDate != 0) a.InvoiceDate = a.SupplyDate;
            if (CobolDate.TestDate(a.InvoiceDate) != 0)
            {
                Reject("TRM0003", "The invoice date is not a valid calendar date");
                return;
            }
            if (a.SupplyDate != 0 && CobolDate.TestDate(a.SupplyDate) != 0)
            {
                Reject("TRM0003", "The supply date is not a valid calendar date");
                return;
            }
            if (a.ReceivedDate != 0 && CobolDate.TestDate(a.ReceivedDate) != 0)
            {
                Reject("TRM0003", "The received date is not a valid calendar date");
            }
        }

        /// <summary>1300-RESOLVE-TERMS: terms code, else an explicit basis, else the statutory default.</summary>
        private async Task ResolveTermsAsync(CancellationToken ct)
        {
            if (!K.Spaces(a.TermsCode, 10))
            {
                await ReadTermsAsync(ct);
                if (_termsFound)
                {
                    a.Source = "TERMS";
                    return;
                }
                // an unknown terms code must not silently invent a date
                _h.ReturnCode = 4;
                _h.ReasonCode = "TRM0002";
                _h.Message = K.Msg("Payment terms ", K.Trim(a.TermsCode), " not found - the statutory default applies");
                _h.MessageCount++;
                a.BasisType = "STATUTORY";
                a.Source = "STATUTORY";
                return;
            }
            if (!K.Spaces(a.BasisType, 16) && a.BasisType != "STATUTORY")
            {
                a.Source = "EXPLICIT";
                return;
            }
            a.BasisType = "STATUTORY";
            a.Source = "STATUTORY";
        }

        /// <summary>2000-APPLY-BASIS.</summary>
        private async Task ApplyBasisAsync(CancellationToken ct)
        {
            switch (a.BasisType)
            {
                case "INVOICE":
                    a.DueDate = await AddDaysAsync(a.InvoiceDate, a.Days, ct);
                    break;
                case "EOM":
                {
                    // "30 days end of month" - the month end first, then the days
                    var eom = await owner.Calendar.EndOfMonthAsync(a.InvoiceDate, 0, ct);
                    if (eom.Header.ReturnCode != 0)
                    {
                        DateFailed(eom);
                        break;
                    }
                    a.DueDate = await AddDaysAsync(eom.ResultDate, a.Days, ct);
                    break;
                }
                case "DAYOFNEXTMONTH":
                    await DayOfNextMonthAsync(ct);
                    break;
                case "STATUTORY":
                {
                    // Late Payment of Commercial Debts (Interest) Act 1998: 30 days after the later of supply and receipt
                    var later = a.InvoiceDate;
                    if (a.SupplyDate > later) later = a.SupplyDate;
                    if (a.ReceivedDate > later) later = a.ReceivedDate;
                    a.Days = 30;
                    a.BasisType = "STATUTORY";
                    a.DueDate = await AddDaysAsync(later, 30, ct);
                    break;
                }
                default:
                    Reject("TRM0004", string.Concat("Payment terms basis ", K.Trim(a.BasisType), " is not supported"));
                    break;
            }
        }

        /// <summary>2300-DAY-OF-NEXT-MONTH (AC-005: day 31 after a January invoice gives 28/29 February).</summary>
        private async Task DayOfNextMonthAsync(CancellationToken ct)
        {
            var dom = a.DayOfMonth;
            if (dom is < 1 or > 31)
            {
                Reject("TRM0005", "Day of month must be between 1 and 31");
                return;
            }
            var firstOfMonth = a.InvoiceDate / 100 * 100 + 1;
            var eom = await owner.Calendar.EndOfMonthAsync(firstOfMonth, 1, ct);
            if (eom.Header.ReturnCode != 0)
            {
                DateFailed(eom);
                return;
            }
            var due = eom.ResultDate;
            if (dom < due % 100) due = due / 100 * 100 + dom;
            a.DueDate = due;
        }

        /// <summary>3000-DISCOUNT-DATE (calendar days, never Bacs-adjusted).</summary>
        private async Task DiscountDateAsync(CancellationToken ct)
        {
            if (a.DiscountDays <= 0)
            {
                a.DiscountDate = 0;
                return;
            }
            a.DiscountDate = await AddDaysAsync(a.InvoiceDate, a.DiscountDays, ct);
        }

        /// <summary>3100-BACS-ADJUST: DATECALC ADJUST keeping the raw date; RC 04 TRM0006 with DATECALC's message when moved.</summary>
        private async Task BacsAdjustAsync(CancellationToken ct)
        {
            var dc = await owner.Calendar.CallAsync("ADJUST", d =>
            {
                d.Date1 = a.DueDate;
                d.Calendar = a.Calendar;
                d.Direction = a.BacsDirection;
            }, ct);
            if (dc.Header.ReturnCode > 4)
            {
                DateFailed(dc);
                return;
            }
            a.DueDate = dc.ResultDate;
            if (dc.Adjusted == "Y")
            {
                _h.ReturnCode = 4;
                _h.ReasonCode = "TRM0006";
                _h.Message = K.Store(dc.Header.Message, 200);
                _h.MessageCount++;
            }
        }

        /// <summary>3200-TERM-LENGTH (D-25): later warnings overwrite earlier ones (BRF-TERMCALC-06).</summary>
        private async Task TermLengthAsync(CancellationToken ct)
        {
            var dc = await owner.Calendar.DaysBetweenAsync(a.InvoiceDate, a.DueDate, ct);
            a.TermDays = (int)CobolNumeric.Store(dc.ResultNum, 4, 0);
            if (a.TermDays > a.MaxTermDays)
            {
                a.LongTermFlag = "Y";
                _h.ReturnCode = 4;
                _h.ReasonCode = "TRM0007";
                _h.Message = K.Msg("Payment term of ", K.Ed(a.TermDays, "-(4)9"), " days needs express agreement - review it");
                _h.MessageCount++;
            }
        }

        /// <summary>4000-DISCOUNT-AMOUNT: (net, or net + VAT for exactly GROSS) x pct / 100 rounded once to 2 places via RNDALLOC.</summary>
        private void DiscountAmount()
        {
            a.DiscountAmount = 0;
            if (a.DiscountPct == 0) return;
            var basis = K.X(a.DiscountBasis, 8) == "GROSS   "
                ? CobolNumeric.Store(a.NetAmount + a.VatAmount, 13, 6)
                : a.NetAmount;
            var value = CobolNumeric.MultiplyDivide(basis, a.DiscountPct, 100m, 13, 12);
            var r = owner.Rounding.Round(value, 2);
            a.DiscountAmount = CobolNumeric.Store(r.ResultValue, 13, 6);
        }

        /// <summary>5000-FORMAT-DATES (dd/mm/yyyy).</summary>
        private async Task FormatDatesAsync(CancellationToken ct)
        {
            if (a.DueDate != 0) a.DueDateUk = (await owner.Calendar.FormatAsync(a.DueDate, "DDMMYYYY", ct)).TextOut;
            if (a.DiscountDate != 0) a.DiscountDateUk = (await owner.Calendar.FormatAsync(a.DiscountDate, "DDMMYYYY", ct)).TextOut;
        }

        /// <summary>5100-DESCRIBE-TERMS.</summary>
        private void DescribeTerms()
        {
            a.TermsDescription = a.BasisType switch
            {
                "INVOICE" => K.Store(K.Ed(a.Days, "-(4)9") + " days from invoice date", 40),
                "EOM" => K.Store(K.Ed(a.Days, "-(4)9") + " days end of month", 40),
                "DAYOFNEXTMONTH" => K.Store("Day " + K.Ed(a.DayOfMonth, "-(4)9") + " of the following month", 40),
                "STATUTORY" => K.Store("30 days - Late Payment Act default", 40),
                _ => K.Store(a.BasisType, 40),
            };
            if (K.Spaces(_h.Message, 200))
            {
                _h.Message = K.Msg("Due ", K.Trim(a.DueDateUk), " - ", K.Trim(a.TermsDescription));
            }
        }

        /// <summary>6000-READ-TERMS with 6100-PROBE-TABLE (per call; the process-wide indicator is not reproduced).</summary>
        private async Task ReadTermsAsync(CancellationToken ct)
        {
            _termsFound = false;
            string table;
            try
            {
                var probe = await owner.Sql.SingleAsync(
                    "SELECT CASE WHEN COL_LENGTH('core.PaymentTerms','TermsCode') IS NULL THEN 'NO' WHEN " +
                    "COL_LENGTH('core.PaymentTerms','CompanyCode') IS NOT NULL THEN 'CO' ELSE 'GL' END", [], [2], ct);
                table = probe.Found ? probe.Text(0) : "NO";
            }
            catch (SqlioException)
            {
                table = "NO";
            }
            if (table is not ("CO" or "GL")) return;
            SqlioRow row;
            try
            {
                row = table == "CO"
                    ? await owner.Sql.SingleAsync(
                        "SELECT TOP 1 BasisType, Days, DayOfMonth, DiscountDays, DiscountPct FROM core.PaymentTerms WHERE TermsCode = ? " +
                        "AND CompanyCode IN (?, '*') ORDER BY CASE WHEN CompanyCode = '*' THEN 2 ELSE 1 END",
                        [SqlioParm.Text(K.X(a.TermsCode, 10)), SqlioParm.Text(K.X(_h.Company, 10))], [16, 8, 8, 8, 16], ct)
                    : await owner.Sql.SingleAsync(
                        "SELECT TOP 1 BasisType, Days, DayOfMonth, DiscountDays, DiscountPct FROM core.PaymentTerms WHERE TermsCode = ?",
                        [SqlioParm.Text(K.X(a.TermsCode, 10))], [16, 8, 8, 8, 16], ct);
            }
            catch (SqlioException)
            {
                // a terms lookup failure must not pretend the code is bad
                return;
            }
            if (!row.Found) return;
            _termsFound = true;
            a.BasisType = CobolText.Upper(row.Raw(0)).TrimEnd(' ');
            a.Days = (int)CobolNumeric.Store(row.NumVal(1), 4, 0);
            a.DayOfMonth = (int)CobolNumeric.Store(row.NumVal(2), 4, 0);
            a.DiscountDays = (int)CobolNumeric.Store(row.NumVal(3), 4, 0);
            a.DiscountPct = CobolNumeric.Store(row.NumVal(4), 5, 4);
        }

        /// <summary>7000-ADD-DAYS through DATECALC ADDDAYS.</summary>
        private async Task<int> AddDaysAsync(int date, int days, CancellationToken ct)
        {
            var dc = await owner.Calendar.AddDaysAsync(date, days, ct);
            if (dc.Header.ReturnCode != 0) DateFailed(dc);
            return dc.ResultDate;
        }

        /// <summary>9200-DATE-FAILED: TRM0008 with DATECALC's message.</summary>
        private void DateFailed(DatecalcArea dc)
        {
            _h.ReasonCode = "TRM0008";
            _h.Message = K.Store(dc.Header.Message, 200);
            _bad = true;
            _h.ReturnCode = 8;
            _h.MessageCount++;
        }

        private void Reject(string reason, string message)
        {
            _bad = true;
            _h.Reject(reason, message);
        }
    }
}
