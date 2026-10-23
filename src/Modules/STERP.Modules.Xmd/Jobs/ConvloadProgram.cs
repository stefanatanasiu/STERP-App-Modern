using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>
/// CONVLOAD (cobol/batch/xmd/CONVLOAD.cob; FR-STERP-XMD-025, -026): legacy conversion load of a fixed-width extract (FL-067)
/// at a dated cut over. The records are sorted on type, natural key and sequence (stable ordinal byte order, D-XMD-12),
/// validated one by one (in-file, in-ledger and in-batch duplicate tests) and listed. COMMIT: the batch MERGE autocommits,
/// then one unit of work (D-XMD-13) holds the staging rows, the AR/AP open items, the opening balances, the ITM STKMOVE
/// OPENING movements (savepoint inside) and the proof exception; it is rolled back when the return code is above 4, else
/// committed; the batch counts UPDATE autocommits afterwards. A restart recognises the rows it already loaded (LOADED).
/// Listing FL-066 (RD PAGE LIMIT 60, FIRST DETAIL 8, LAST DETAIL 52, FOOTING 58).
/// </summary>
internal static class ConvloadProgram
{
    private static readonly int[] Widths = Enumerable.Repeat(120, 30).ToArray();

    private sealed class Rec
    {
        public string Type = "";
        public string Key = "";
        public int Seq;
        public string Data = "";
        public byte[] SortKey = [];
    }

    private sealed class State
    {
        public string LegName = "";
        public int Cutover;
        public string BaseName = "";
        public string BatchRef = "";
        public long BatchId;
        public bool TxnOpen;
        public string PrevKey = new(' ', 64);
        public decimal AcceptVal, RejectVal, Dr, Cr, Proof;
        public decimal XbAmt;
    }

    private sealed class Det
    {
        public int Seq;
        public string Type = "";
        public string Key = "";
        public string Party = "";
        public string Date = "";
        public int DateNum;
        public string Ccy = "";
        public decimal Value;
        public decimal Qty;
        public string Outcome = "ACCEPTED";
        public string Reason = "";
        public string CreType = "";
        public string CreRef = "";
    }

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
        var st = new State();
        // 0100-READ-ARGS
        var fileText = b.GetArg("FILE");
        st.LegName = CobolText.Fit(CobolText.IsSpaces(fileText) ? "data\\legacy\\CALAROPN.DAT" : fileText, 200);
        var cutText = b.GetArg("CUTOVER");
        st.Cutover = !CobolText.IsSpaces(cutText)
            ? (int)CobolNumeric.Store(CobolNumeric.NumVal(cutText), 8, 0, signed: false)
            : b.AsAt;
        st.BaseName = BaseName(st.LegName);
        st.BatchRef = CobolText.Fit("CONV-" + CobolText.Trim(st.BaseName) + "-" + Disp.D8(st.Cutover), 40).TrimEnd(' ');
        var sFile = st.LegName[..60];
        var sCut = BatchSuite.FormatDate(st.Cutover);
        var sMode = CobolText.Fit(b.Mode, 10);
        b.Display("STERP-I-0100 CONVLOAD " + CobolText.Trim(b.Co) + " " + CobolText.Trim(st.LegName) + " mode " + CobolText.Trim(b.Mode));

        result["file"] = sFile.TrimEnd(' ');
        result["cutover"] = IsoDate(st.Cutover);
        result["mode"] = sMode.TrimEnd(' ');
        result["batchRef"] = st.BatchRef;
        var records = new JsonArray();
        result["records"] = records;
        var statusText = "";

        if (!await b.OpenSpoolAsync("CONVLOAD", "Legacy conversion load", "Per record validation, duplicate control and load") || b.Rc > 4)
        {
            return Finish(b, st, result, statusText);
        }
        await OpenBatchAsync(b, st);
        await BeginWorkAsync(b, st);
        var rw = b.Report(
        [
            RwLine.Plus1(() => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Records read").Num(14, b.RowsIn, "Z(6)9").Lit(24, "Accepted ")
                .Num(33, b.Accepted, "Z(6)9").Lit(43, "Rejected ").Num(52, b.Rejected, "Z(6)9")
                .Lit(62, "Duplicates ").Num(73, b.Duplicate, "Z(6)9").Lit(83, "Already loaded    ")
                .Num(101, b.Skipped, "Z(6)9").Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Accepted value  ").Num(18, st.AcceptVal, "ZZZ,ZZZ,ZZ9.99-")
                .Lit(38, "Rejected value  ").Num(55, st.RejectVal, "ZZZ,ZZZ,ZZ9.99-").Lit(76, "Debits    ")
                .Num(86, st.Dr, "ZZZ,ZZZ,ZZ9.99").Lit(103, "Credits ").Num(112, st.Cr, "ZZZ,ZZZ,ZZ9.99").Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Trial balance proof     ").Num(26, st.Proof, "ZZZ,ZZZ,ZZ9.99-")
                .Put(48, statusText, 20).Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "A rejected record is not loaded and is not")
                .Lit(48, "counted in the totals. Correct and re-run.").Text),
        ]);
        rw.Initiate();
        rw.Generate(
            RwLine.Plus1(() => new RwText().Lit(1, "Source file ").Put(13, sFile, 60).Lit(76, "Cut over   ").Put(87, sCut, 10)
                .Lit(100, "Mode  ").Put(106, sMode, 10).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Batch ref   ").Put(13, st.BatchRef, 40)
                .Lit(56, "Duplicate tests: in file, in ledger, in batch.").Text),
            RwLine.PlusN(2, () => new RwText().Lit(1, "Seq").Lit(9, "Type").Lit(15, "Key").Lit(61, "Party").Lit(75, "Date")
                .Lit(87, "Ccy").Lit(94, "Value / qty ").Lit(110, "Outcome").Lit(121, "Reason").Text),
            RwLine.Plus1(() => new string('-', 132)));

        // SORT ... INPUT PROCEDURE 1000-READ-INPUT, OUTPUT PROCEDURE 2000-PROCESS
        var input = ReadInput(b, st);
        input.Sort(static (x, y) =>
        {
            var c = x.SortKey.AsSpan().SequenceCompareTo(y.SortKey);
            return c != 0 ? c : x.Seq.CompareTo(y.Seq);
        });
        foreach (var rec in input) await OneRecordAsync(b, st, rw, rec, records);

        statusText = await ProveAsync(b, st);
        await EndWorkAsync(b, st);
        rw.Terminate();
        b.CloseSpool();
        await CloseBatchAsync(b, st);
        return Finish(b, st, result, statusText);
    }

    private static int Finish(XmdBatch b, State st, JsonObject result, string statusText)
    {
        result["read"] = b.RowsIn;
        result["accepted"] = b.Accepted;
        result["rejected"] = b.Rejected;
        result["duplicate"] = b.Duplicate;
        result["alreadyLoaded"] = b.Skipped;
        result["posted"] = b.Posted;
        result["acceptedValue"] = CobolNumeric.WithScale(st.AcceptVal, 2);
        result["rejectedValue"] = CobolNumeric.WithScale(st.RejectVal, 2);
        result["totalDebitGbp"] = CobolNumeric.WithScale(st.Dr, 2);
        result["totalCreditGbp"] = CobolNumeric.WithScale(st.Cr, 2);
        result["proofGbp"] = CobolNumeric.WithScale(st.Proof, 2);
        result["proofStatus"] = statusText.Length > 0 ? statusText : null;
        var rc = b.Finish();
        result["returnCode"] = rc;
        return rc;
    }

    /// <summary>0150-BASENAME: the last path element (after the last '\' or '/') up to its first '.', 30 characters.</summary>
    private static string BaseName(string legName)
    {
        var name = CobolText.Fit(legName, 200);
        var j = 0;
        for (var i = 200; i >= 1; i--)
        {
            if (j == 0 && (name[i - 1] == '\\' || name[i - 1] == '/')) j = i + 1;
        }
        if (j == 0) j = 1;
        var bn = (name + new string(' ', 30)).Substring(j - 1, 30).ToCharArray();
        for (var i = 1; i <= 30; i++)
        {
            if (bn[i - 1] == '.')
            {
                for (var k = i - 1; k < 30; k++) bn[k] = ' ';
            }
        }
        return new string(bn);
    }

    /// <summary>0300-OPEN-BATCH (COMMIT only): MERGE stg.ImportBatch (autocommit) and read its BatchId back.</summary>
    private static async Task OpenBatchAsync(XmdBatch b, State st)
    {
        if (!b.IsCommit) return;
        try
        {
            await b.Sql.ExecAsync(
                "MERGE stg.ImportBatch AS t USING (SELECT ? AS CompanyCode, ? AS BatchRef) AS s ON t.CompanyCode = s.CompanyCode AND " +
                "t.BatchRef = s.BatchRef WHEN MATCHED THEN UPDATE SET Status = 'OPEN', Mode = 'COMMIT' WHEN NOT MATCHED THEN INSERT " +
                "(CompanyCode, BatchRef, ImportType, SourceFile, SubmittedBy, Mode, Status) VALUES (?,?,?,?,?,'COMMIT','OPEN');",
                [
                    SqlioParm.Text(b.Co), SqlioParm.Text(st.BatchRef), SqlioParm.Text(b.Co), SqlioParm.Text(st.BatchRef),
                    SqlioParm.Text("LEGACY"), SqlioParm.Text(st.LegName), SqlioParm.Text(b.User),
                ], b.Ct);
        }
        catch (SqlioException)
        {
            // EXEC result not tested
        }
        SqlioRow? row = null;
        try
        {
            row = await b.Sql.SingleAsync("SELECT CONVERT(varchar(12),BatchId) FROM stg.ImportBatch WHERE CompanyCode = ? AND BatchRef = ?",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.BatchRef)], Widths[..1], b.Ct);
        }
        catch (SqlioException)
        {
        }
        if (row is not null && row.Found)
        {
            st.BatchId = (long)CobolNumeric.Store(row.NumVal(0), 9, 0, signed: false);
        }
        else
        {
            b.Rc = 12;
            b.Display("STERP-E-0301 import batch not created");
        }
    }

    /// <summary>0400-BEGIN-WORK: SQLIO BEGN of the load unit of work.</summary>
    private static async Task BeginWorkAsync(XmdBatch b, State st)
    {
        st.TxnOpen = false;
        if (!b.IsCommit || st.BatchId == 0 || b.Rc > 4) return;
        try
        {
            await b.Ctx.Get<IUnitOfWork>().BeginAsync(b.Ct);
            st.TxnOpen = true;
        }
        catch (Exception ex) when (ex is SqlioException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            b.Rc = 12;
            b.Display("STERP-E-0401 cannot start the load transaction");
        }
    }

    /// <summary>1000-READ-INPUT + 1100-NATURAL-KEY: every non-blank line (130 bytes, padded/truncated) with its natural key.</summary>
    private static List<Rec> ReadInput(XmdBatch b, State st)
    {
        var list = new List<Rec>();
        var path = CobolText.Trim(st.LegName);
        var root = b.Ctx.Get<IOptions<XmdOptions>>().Value.LegacyRoot;
        byte[] bytes;
        try
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var status = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => "35",
                UnauthorizedAccessException => "37",
                _ => "30",
            };
            b.Rc = 8;
            b.Display("STERP-E-1001 cannot open " + CobolText.Trim(st.LegName) + " status " + status);
            return list;
        }
        var seq = 0;
        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var next = end < 0 ? bytes.Length : end + 1;
            var len = (end < 0 ? bytes.Length : end) - start;
            if (len > 0 && end >= 0 && bytes[start + len - 1] == (byte)'\r') len--;
            var line = CobolText.Ansi.GetString(bytes, start, Math.Min(len, 130)).PadRight(130);
            start = next;
            if (CobolText.IsSpaces(line)) continue;
            b.RowsIn++;
            seq++;
            var type = line[..4];
            var key = type == "STKB" ? (line.Substring(4, 20) + line.Substring(24, 40)).PadRight(60) : line.Substring(4, 20).PadRight(60);
            list.Add(new Rec
            {
                Type = type,
                Key = key,
                Seq = seq % 1_000_000,
                Data = line,
                SortKey = CobolText.Ansi.GetBytes(type + key),
            });
        }
        return list;
    }

    /// <summary>2100-ONE-RECORD.</summary>
    private static async Task OneRecordAsync(XmdBatch b, State st, GnuReportWriter rw, Rec rec, JsonArray records)
    {
        var d = new Det
        {
            Seq = rec.Seq,
            Type = rec.Type,
            Key = rec.Key[..44],
            CreType = rec.Type,
            CreRef = rec.Key[..40],
        };
        var input = rec.Data;
        switch (rec.Type)
        {
            case "AROP":
                await ValidOpenItemAsync(b, st, d, input, "SELECT CONVERT(varchar(12),COUNT(*)) FROM ar.Customer WHERE CompanyCode = ? AND CustomerCode = ?",
                    "Customer is not on file at the cut over");
                break;
            case "APOP":
                await ValidOpenItemAsync(b, st, d, input, "SELECT CONVERT(varchar(12),COUNT(*)) FROM ap.Supplier WHERE CompanyCode = ? AND SupplierCode = ?",
                    "Supplier is not on file at the cut over");
                break;
            case "STKB":
                await ValidStockAsync(b, st, d, input);
                break;
            case "GLTB":
                await ValidGlAsync(b, st, d, input);
                break;
            default:
                d.Outcome = "REJECTED";
                d.Reason = "Unknown record type in the extract";
                break;
        }
        if (d.Outcome == "ACCEPTED" && rec.Type == st.PrevKey[..4] && rec.Key == st.PrevKey.Substring(4, 60))
        {
            d.Outcome = "DUPLICATE";
            d.Reason = "The same key appears earlier in the file";
        }
        st.PrevKey = rec.Type + rec.Key;
        if (d.Outcome == "ACCEPTED") await AlreadyLoadedAsync(b, st, d, rec);
        if (d.Outcome == "ACCEPTED") await LedgerDupAsync(b, st, d, rec);
        if (d.Outcome == "ACCEPTED") await PostRecordAsync(b, st, d, rec);
        Count(b, st, d, rec);
        rw.Generate(RwLine.Plus1(() => new RwText().Num(1, d.Seq, "ZZZZZ9").Put(9, d.Type, 4).Put(15, d.Key, 44).Put(61, d.Party, 12)
            .Put(75, d.Date, 10).Put(87, d.Ccy, 3).Num(92, d.Value, "ZZZ,ZZZ,ZZ9.99-").Put(110, d.Outcome, 10).Put(121, d.Reason, 12).Text));
        if (d.Outcome != "ACCEPTED" && !CobolText.IsSpaces(d.Reason))
        {
            rw.Generate(RwLine.Plus1(() => new RwText().Lit(17, "Reason:   ").Put(27, d.Reason, 52).Text));
        }
        await WriteRowAsync(b, st, d, rec);
        records.Add(new JsonObject
        {
            ["seq"] = d.Seq,
            ["type"] = d.Type.TrimEnd(' '),
            ["key"] = d.Key.TrimEnd(' '),
            ["party"] = CobolText.Fit(d.Party, 12).TrimEnd(' '),
            ["date"] = CobolText.IsSpaces(d.Date) ? null : IsoDate(d.DateNum),
            ["currency"] = CobolText.Fit(d.Ccy, 3).TrimEnd(' '),
            ["value"] = CobolNumeric.WithScale(d.Value, 2),
            ["outcome"] = d.Outcome,
            ["reason"] = CobolText.Fit(d.Reason, 52).TrimEnd(' '),
        });
    }

    /// <summary>2200-VALID-AR / 2300-VALID-AP: the open item layout, 2250-COMMON-OPEN-ITEM and the party on file.</summary>
    private static async Task ValidOpenItemAsync(XmdBatch b, State st, Det d, string input, string partySql, string partyReason)
    {
        var ar = ArFields(input);
        d.Party = ar.Party[..10];
        d.Ccy = ar.Ccy;
        CommonOpenItem(st, d, ar);
        if (d.Outcome != "ACCEPTED") return;
        if (await CountAsync(b, partySql, [SqlioParm.Text(b.Co), SqlioParm.Text(ar.Party)]) == 0)
        {
            d.Outcome = "REJECTED";
            d.Reason = partyReason;
        }
    }

    private sealed record ArRec(string Party, string DocNo, string Kind, string DocDate, string DueDate, string Ccy, string Amt, string Terms, string Ref);

    /// <summary>WS-AR-REC = WS-IN(5:80) (76 bytes).</summary>
    private static ArRec ArFields(string input)
    {
        var r = input.Substring(4, 76);
        return new ArRec(r[..10], r.Substring(10, 10), r.Substring(20, 2), r.Substring(22, 8), r.Substring(30, 8), r.Substring(38, 3),
            r.Substring(41, 15), r.Substring(56, 10), r.Substring(66, 10));
    }

    /// <summary>2250-COMMON-OPEN-ITEM.</summary>
    private static void CommonOpenItem(State st, Det d, ArRec ar)
    {
        if (CobolText.IsSpaces(ar.DocNo))
        {
            Reject(d, "Document number is blank");
            return;
        }
        var docDate = CheckDate(ar.DocDate);
        if (docDate == 0)
        {
            Reject(d, "Document date is not a real calendar date");
            return;
        }
        d.Date = BatchSuite.FormatDate(docDate);
        d.DateNum = docDate;
        if (docDate > st.Cutover)
        {
            Reject(d, "Document date is after the cut over date");
            return;
        }
        if (CheckDate(ar.DueDate) == 0)
        {
            Reject(d, "Due date is missing or not a calendar date");
            return;
        }
        if (!CheckNum(st, ar.Amt))
        {
            Reject(d, "Amount is not numeric in the extract");
            return;
        }
        d.Value = st.XbAmt;
        if (d.Value == 0) Reject(d, "Open item of nil value is not converted");
    }

    private sealed record StRec(string Site, string Wh, string Item, string Rev, string Lot, string Qty, string Cost, string Loc);

    /// <summary>WS-ST-REC = WS-IN(5:115).</summary>
    private static StRec StFields(string input)
    {
        var r = input.Substring(4, 115);
        return new StRec(r[..10], r.Substring(10, 10), r.Substring(20, 40), r.Substring(60, 5), r.Substring(65, 10), r.Substring(75, 15),
            r.Substring(90, 15), r.Substring(105, 10));
    }

    /// <summary>2400-VALID-ST.</summary>
    private static async Task ValidStockAsync(XmdBatch b, State st, Det d, string input)
    {
        var s = StFields(input);
        d.Party = s.Site[..10];
        if (!CheckNum(st, s.Qty))
        {
            Reject(d, "Quantity is not numeric in the extract");
            return;
        }
        d.Qty = CobolNumeric.Store(st.XbAmt, 11, 3);
        if (d.Qty < 0)
        {
            Reject(d, "Negative opening stock is refused (D-09)");
            return;
        }
        if (!CheckNum(st, s.Cost))
        {
            Reject(d, "Unit cost is not numeric in the extract");
            return;
        }
        d.Value = CobolNumeric.Store(d.Qty * st.XbAmt, 13, 2, rounded: true);
        if (await CountAsync(b, "SELECT CONVERT(varchar(12),COUNT(*)) FROM inv.Item WHERE CompanyCode = ? AND ItemCode = ?",
                [SqlioParm.Text(b.Co), SqlioParm.Text(s.Item)]) == 0)
        {
            Reject(d, "Item is not on file at the cut over");
            return;
        }
        if (await CountAsync(b, "SELECT CONVERT(varchar(12),COUNT(*)) FROM fin.Period WHERE CompanyCode = ? AND CONVERT(date,?,112) BETWEEN " +
                "StartDate AND EndDate AND Status IN ('OPEN','SOFT')", [SqlioParm.Text(b.Co), SqlioParm.Text(Disp.D8(b.AsAt))]) == 0)
        {
            Reject(d, "No open period to book the opening stock in");
        }
    }

    private sealed record GlRec(string Acct, string Date, string Dr, string Cr, string Narr, string Cc);

    /// <summary>WS-GL-REC = WS-IN(5:90).</summary>
    private static GlRec GlFields(string input)
    {
        var r = input.Substring(4, 90);
        return new GlRec(r[..12], r.Substring(12, 8), r.Substring(20, 15), r.Substring(35, 15), r.Substring(50, 30), r.Substring(80, 10));
    }

    /// <summary>2500-VALID-GL.</summary>
    private static async Task ValidGlAsync(XmdBatch b, State st, Det d, string input)
    {
        var g = GlFields(input);
        d.Party = g.Cc[..10];
        var date = CheckDate(g.Date);
        if (date == 0)
        {
            Reject(d, "Balance date is not a real calendar date");
            return;
        }
        d.Date = BatchSuite.FormatDate(date);
        d.DateNum = date;
        if (!CheckNum(st, g.Dr))
        {
            Reject(d, "Debit amount is not numeric");
            return;
        }
        var amt2 = st.XbAmt;
        if (!CheckNum(st, g.Cr))
        {
            Reject(d, "Credit amount is not numeric");
            return;
        }
        d.Value = CobolNumeric.Store(amt2 - st.XbAmt, 13, 2);
        if (await CountAsync(b, "SELECT CONVERT(varchar(12),COUNT(*)) FROM fin.Account WHERE CompanyCode = ? AND AccountCode = ? AND " +
                "AccountKind <> 'SUMMARY'", [SqlioParm.Text(b.Co), SqlioParm.Text(g.Acct)]) == 0)
        {
            Reject(d, "Account is not a posting account on file");
        }
    }

    /// <summary>2600-LEDGER-DUP: the ledger already holds the reference.</summary>
    private static async Task LedgerDupAsync(XmdBatch b, State st, Det d, Rec rec)
    {
        string sql;
        SqlioParm[] parms;
        switch (rec.Type)
        {
            case "AROP":
                sql = "SELECT CONVERT(varchar(12),COUNT(*)) FROM ar.OpenItem WHERE CompanyCode = ? AND DocumentNumber = ?";
                parms = [SqlioParm.Text(b.Co), SqlioParm.Text(ArFields(rec.Data).DocNo)];
                break;
            case "APOP":
                sql = "SELECT CONVERT(varchar(12),COUNT(*)) FROM ap.OpenItem WHERE CompanyCode = ? AND DocumentNumber = ?";
                parms = [SqlioParm.Text(b.Co), SqlioParm.Text(ArFields(rec.Data).DocNo)];
                break;
            case "GLTB":
                sql = "SELECT CONVERT(varchar(12),COUNT(*)) FROM fin.OpeningBalance WHERE CompanyCode = ? AND AccountCode = ? AND " +
                      "ISNULL(BatchRef,'') <> ?";
                parms = [SqlioParm.Text(b.Co), SqlioParm.Text(GlFields(rec.Data).Acct), SqlioParm.Text(st.BatchRef)];
                break;
            case "STKB":
                var s = StFields(rec.Data);
                sql = "SELECT CONVERT(varchar(12),COUNT(*)) FROM inv.StockMovement WHERE CompanyCode = ? AND ItemCode = ? AND MovementType = " +
                      "'OPENING' AND LocationCode = ? AND ISNULL(LotCode,'') = ?";
                parms = [SqlioParm.Text(b.Co), SqlioParm.Text(s.Item), SqlioParm.Text(s.Loc), SqlioParm.Text(s.Lot)];
                break;
            default:
                return;
        }
        if (await CountAsync(b, sql, parms) > 0)
        {
            d.Outcome = "DUPLICATE";
            d.Reason = "The ledger already holds this reference";
        }
    }

    /// <summary>2700-ALREADY-LOADED: a VALID processed staging row of this batch with the same natural key.</summary>
    private static async Task AlreadyLoadedAsync(XmdBatch b, State st, Det d, Rec rec)
    {
        if (st.BatchId == 0) return;
        var k = 0m;
        try
        {
            var row = await b.Sql.SingleAsync(
                "SELECT CONVERT(varchar(12),COUNT(*)) FROM stg.ImportRow WHERE BatchId = ? AND NaturalKey = ? AND ValidationOutcome = 'VALID' " +
                "AND ProcessedAt IS NOT NULL",
                [SqlioParm.Text(Disp.Unsigned(st.BatchId, 9)), SqlioParm.Text(NaturalKey(rec))], Widths[..1], b.Ct);
            if (row.Found) k = CobolNumeric.Store(row.NumVal(0), 4, 0);
        }
        catch (SqlioException)
        {
        }
        if (k > 0)
        {
            d.Outcome = "LOADED";
            d.Reason = "Loaded by an earlier run of this batch";
        }
    }

    /// <summary>2800-COUNT.</summary>
    private static void Count(XmdBatch b, State st, Det d, Rec rec)
    {
        switch (d.Outcome)
        {
            case "ACCEPTED":
                b.Accepted++;
                st.AcceptVal = CobolNumeric.Store(st.AcceptVal + d.Value, 13, 2);
                if (rec.Type == "GLTB")
                {
                    if (d.Value >= 0) st.Dr = CobolNumeric.Store(st.Dr + d.Value, 13, 2);
                    else st.Cr = CobolNumeric.Store(st.Cr - d.Value, 13, 2);
                }
                break;
            case "DUPLICATE":
                b.Duplicate++;
                st.RejectVal = CobolNumeric.Store(st.RejectVal + d.Value, 13, 2);
                break;
            case "LOADED":
                b.Skipped++;
                break;
            default:
                b.Rejected++;
                st.RejectVal = CobolNumeric.Store(st.RejectVal + d.Value, 13, 2);
                break;
        }
        b.RowsOut++;
    }

    /// <summary>2850-POST-RECORD: an accepted record becomes a live ledger item inside the batch unit of work (COMMIT only).</summary>
    private static async Task PostRecordAsync(XmdBatch b, State st, Det d, Rec rec)
    {
        d.CreType = rec.Type;
        d.CreRef = rec.Key[..40];
        if (!b.IsCommit || st.BatchId == 0) return;
        switch (rec.Type)
        {
            case "AROP":
            case "APOP":
                await OpenItemAsync(b, st, d, rec);
                break;
            case "STKB":
                await StockAsync(b, st, d, rec);
                break;
            case "GLTB":
                d.CreType = "OPENINGBAL";
                d.CreRef = GlFields(rec.Data).Acct;
                await OpeningBalanceAsync(b, st, d, rec);
                break;
        }
    }

    /// <summary>2860-OPEN-ITEM: INSERT ar.OpenItem / ap.OpenItem at the converted residual (WHERE NOT EXISTS).</summary>
    private static async Task OpenItemAsync(XmdBatch b, State st, Det d, Rec rec)
    {
        var ar = ArFields(rec.Data);
        var itemType = ar.Kind == "CR" ? "CREDIT" : "INVOICE";
        string sql;
        SqlioParm[] parms;
        var common = new List<SqlioParm>
        {
            SqlioParm.Text(Disp.Mon(d.Value)), SqlioParm.Text(ar.Ccy), SqlioParm.Text(Disp.D8(st.Cutover)), SqlioParm.Text(b.Co),
            SqlioParm.Text(ar.Party), SqlioParm.Text(itemType), SqlioParm.Text(ar.DocNo),
        };
        if (rec.Type == "AROP")
        {
            sql = "DECLARE @a decimal(18,2) = ?, @c char(3) = ?, @d date = CONVERT(date,?,112); DECLARE @r decimal(18,8) = ISNULL((SELECT TOP 1 " +
                  "f.UnitsPerGBP FROM core.FxRate f WHERE f.CurrencyCode = @c AND f.RateDate <= @d ORDER BY f.RateDate DESC),1); INSERT INTO " +
                  "ar.OpenItem (CompanyCode, CustomerCode, ItemType, DocumentNumber, DocumentDate, DueDate, PostingDate, CurrencyCode, FxRate, " +
                  "OriginalAmount, OriginalAmountGBP, ResidualAmount, ResidualAmountGBP, TermsCode, SettlementStatus, CreatedBy) SELECT ?,?,?,?," +
                  "CONVERT(date,?,112),CONVERT(date,?,112),@d,@c,@r,@a,ROUND(@a/@r,2),@a,ROUND(@a/@r,2),NULLIF(?,''),'OPEN',? WHERE NOT EXISTS " +
                  "(SELECT 1 FROM ar.OpenItem o WHERE o.CompanyCode = ? AND o.DocumentNumber = ? AND o.ItemType = ?);";
            d.CreType = "AROPENITEM";
            common.AddRange([
                SqlioParm.Text(ar.DocDate), SqlioParm.Text(ar.DueDate), SqlioParm.Text(ar.Terms), SqlioParm.Text(b.User), SqlioParm.Text(b.Co),
                SqlioParm.Text(ar.DocNo), SqlioParm.Text(itemType),
            ]);
        }
        else
        {
            sql = "DECLARE @a decimal(18,2) = ?, @c char(3) = ?, @d date = CONVERT(date,?,112); DECLARE @r decimal(18,8) = ISNULL((SELECT TOP 1 " +
                  "f.UnitsPerGBP FROM core.FxRate f WHERE f.CurrencyCode = @c AND f.RateDate <= @d ORDER BY f.RateDate DESC),1); INSERT INTO " +
                  "ap.OpenItem (CompanyCode, SupplierCode, ItemType, DocumentNumber, SupplierDocRef, DocumentDate, DueDate, PostingDate, " +
                  "CurrencyCode, FxRate, OriginalAmount, OriginalAmountGBP, ResidualAmount, ResidualAmountGBP, TermsCode, SettlementStatus, " +
                  "CreatedBy) SELECT ?,?,?,?,NULLIF(?,''),CONVERT(date,?,112),CONVERT(date,?,112),@d,@c,@r,@a,ROUND(@a/@r,2),@a,ROUND(@a/@r,2)," +
                  "NULLIF(?,''),'OPEN',? WHERE NOT EXISTS (SELECT 1 FROM ap.OpenItem o WHERE o.CompanyCode = ? AND o.DocumentNumber = ? AND " +
                  "o.ItemType = ?);";
            d.CreType = "APOPENITEM";
            common.AddRange([
                SqlioParm.Text(ar.Ref), SqlioParm.Text(ar.DocDate), SqlioParm.Text(ar.DueDate), SqlioParm.Text(ar.Terms), SqlioParm.Text(b.User),
                SqlioParm.Text(b.Co), SqlioParm.Text(ar.DocNo), SqlioParm.Text(itemType),
            ]);
        }
        parms = common.ToArray();
        try
        {
            await b.Sql.ExecAsync(sql, parms, b.Ct);
            d.CreRef = ar.DocNo;
            b.Posted++;
        }
        catch (SqlioException ex)
        {
            d.Outcome = "REJECTED";
            d.Reason = CobolText.Fit("The ledger refused the open item: " + CobolText.Fit(ex.DriverText, 18), 52);
            b.Rc = 8;
        }
    }

    /// <summary>2880-STOCK: STKMOVE OPENING through the ITM contract on the batch unit of work.</summary>
    private static async Task StockAsync(XmdBatch b, State st, Det d, Rec rec)
    {
        var s = StFields(rec.Data);
        CheckNum(st, s.Cost);
        var dateEd = Disp.D8(st.Cutover);
        var stem = CobolText.Fit("CVL|" + CobolText.Trim(st.BaseName) + "|" + dateEd, 200)[..26];
        var correlation = CobolText.Fit(CobolText.Trim(stem) + "|" + Disp.Unsigned(rec.Seq, 9), 36).TrimEnd(' ');
        var sm = new StkmoveCommarea
        {
            ItemCode = s.Item,
            Revision = s.Rev[..4],
            Site = s.Site,
            Warehouse = s.Wh,
            Location = s.Loc,
            LotCode = s.Lot,
            Quantity = d.Qty,
            UnitCost = st.XbAmt,
            MovementDate = CobolDate.FromYyyymmdd(st.Cutover),
            PostingDate = CobolDate.FromYyyymmdd(b.AsAt),
            ReasonCode = "OPENING",
            SourceModule = "XMD",
            SourceDocType = "CONVERSION",
            SourceDocRef = st.BatchRef,
            SourceDocLine = rec.Seq,
            Reason = CobolText.Fit("Legacy conversion load line " + CobolText.Trim(st.BatchRef), 200).TrimEnd(' '),
        };
        var call = new ItmCall(b.Co, CobolText.Fit(s.Site, 10).TrimEnd(' '), b.User, CobolDate.FromYyyymmdd(st.Cutover), correlation);
        var r = await b.Ctx.Get<IStockMovementService>().ExecuteAsync(StkmoveFunctions.Opening, sm, call, b.Ct);
        if (r.ReturnCode < 8)
        {
            d.CreType = "STOCKMOVE";
            d.CreRef = Disp.Int(sm.MovementId);
            b.Posted++;
            if (sm.Duplicate == "Y")
            {
                d.Outcome = "DUPLICATE";
                d.Reason = "Stock already holds this opening balance";
            }
        }
        else
        {
            d.Outcome = "REJECTED";
            d.Reason = CobolText.Fit(CobolText.Fit(r.Message, 200)[..52], 52);
            if (r.ReturnCode > 8) b.Rc = 12;
            else if (b.Rc < 4) b.Rc = 4;
        }
    }

    /// <summary>2900-WRITE-ROW (COMMIT only, not for LOADED rows): MERGE stg.ImportRow (failure: RC 4, STERP-W-2901).</summary>
    private static async Task WriteRowAsync(XmdBatch b, State st, Det d, Rec rec)
    {
        if (!b.IsCommit || st.BatchId == 0 || d.Outcome == "LOADED") return;
        var batch = SqlioParm.Text(Disp.Unsigned(st.BatchId, 9));
        var seq = SqlioParm.Text(Disp.Unsigned(rec.Seq, 9));
        var key = SqlioParm.Text(NaturalKey(rec));
        var data = SqlioParm.Text(rec.Data[..130]);
        var outcome = SqlioParm.Text(d.Outcome switch { "ACCEPTED" => "VALID", "DUPLICATE" => "DUPLICATE", _ => "INVALID" });
        var reason = SqlioParm.Text(CobolText.Fit(d.Reason, 52));
        var creType = SqlioParm.Text(CobolText.Fit(d.CreType, 20));
        var creRef = SqlioParm.Text(CobolText.Fit(d.CreRef, 40));
        var user = SqlioParm.Text(b.User);
        try
        {
            await b.Sql.ExecAsync(
                "MERGE stg.ImportRow AS t USING (SELECT ? AS BatchId, ? AS RowNum) AS s ON t.BatchId = s.BatchId AND t.RowNum = s.RowNum WHEN " +
                "MATCHED THEN UPDATE SET NaturalKey = ?, RawData = ?, ValidationOutcome = ?, ValidationMessage = ?, CreatedDocumentType = ?, " +
                "CreatedDocumentRef = ?, ProcessedAt = SYSDATETIME(), ProcessedBy = ? WHEN NOT MATCHED THEN INSERT (BatchId, RowNum, NaturalKey, " +
                "RawData, ValidationOutcome, ValidationMessage, CreatedDocumentType, CreatedDocumentRef, ProcessedAt, ProcessedBy) VALUES " +
                "(?,?,?,?,?,?,?,?,SYSDATETIME(),?);",
                [batch, seq, key, data, outcome, reason, creType, creRef, user, batch, seq, key, data, outcome, reason, creType, creRef, user], b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Display("STERP-W-2901 " + CobolText.Fit(ex.DriverText, 90));
            b.Rc = 4;
        }
    }

    /// <summary>2950-OPENING-BALANCE: INSERT fin.OpeningBalance (WHERE NOT EXISTS) (failure: RC 4, STERP-W-2951).</summary>
    private static async Task OpeningBalanceAsync(XmdBatch b, State st, Det d, Rec rec)
    {
        var g = GlFields(rec.Data);
        var co = SqlioParm.Text(b.Co);
        var batch = SqlioParm.Text(st.BatchRef);
        var cut = SqlioParm.Text(Disp.D8(st.Cutover));
        var acct = SqlioParm.Text(g.Acct);
        var dr = d.Value >= 0 ? SqlioParm.Text(Disp.Mon(d.Value)) : SqlioParm.Text("0.00");
        var cr = d.Value >= 0 ? SqlioParm.Text("0.00") : SqlioParm.Text(Disp.Mon(CobolNumeric.Store(0 - d.Value, 13, 2)));
        try
        {
            await b.Sql.ExecAsync(
                "INSERT INTO fin.OpeningBalance (CompanyCode, BatchRef, AsAtDate, AccountCode, SourceType, CurrencyCode, DebitAmountGBP, " +
                "CreditAmountGBP, DimCostCentre, Narrative, Status, LoadedBy) SELECT ?,?,CONVERT(date,?,112),?,'LEDGER','GBP',?,?,?,?,'LOADED',? " +
                "WHERE NOT EXISTS (SELECT 1 FROM fin.OpeningBalance WHERE CompanyCode = ? AND BatchRef = ? AND AccountCode = ? AND " +
                "AsAtDate = CONVERT(date,?,112))",
                [co, batch, cut, acct, dr, cr, SqlioParm.Text(g.Cc), SqlioParm.Text(g.Narr), SqlioParm.Text(b.User), co, batch, acct, cut], b.Ct);
            b.Posted++;
        }
        catch (SqlioException ex)
        {
            b.Display("STERP-W-2951 " + CobolText.Fit(ex.DriverText, 90));
            b.Rc = 4;
        }
    }

    /// <summary>3000-PROVE (+ 3100-RAISE-UNBALANCED): the accepted opening trial balance must prove within 0.004.</summary>
    private static async Task<string> ProveAsync(XmdBatch b, State st)
    {
        st.Proof = CobolNumeric.Store(st.Dr - st.Cr, 13, 2);
        string statusText;
        if (st.Dr == 0 && st.Cr == 0)
        {
            statusText = "NO TRIAL BALANCE";
        }
        else if (st.Proof > 0.004m || st.Proof < -0.004m)
        {
            statusText = "OUT OF BALANCE";
            b.Rc = 8;
            b.Display("STERP-E-3001 the accepted opening trial balance does not balance");
            var text2 = CobolText.Fit("The accepted opening trial balance of " + st.BatchRef + " is out by GBP " + Disp.Mon(st.Proof) +
                                      ". Correct the extract and re-run the batch; rejected rows are not loaded.", 200);
            await b.RaiseAsync(new ExcpraisArea
            {
                Severity = "ERROR",
                Category = "CONVERSION DIFFERENCE",
                DocumentType = "IMPORTBATCH",
                DocumentRef = CobolText.Fit(st.BatchRef, 40).TrimEnd(' '),
                MessageText = text2.TrimEnd(' '),
                OwnerRole = "FINCTRL",
                Priority = 1,
                AmountGbp = st.Proof,
                DueAt = st.Cutover,
            });
        }
        else
        {
            statusText = "IN BALANCE";
        }
        if ((b.Rejected > 0 || b.Duplicate > 0) && b.Rc < 4) b.Rc = 4;
        b.Display("STERP-I-3002 read " + Disp.Signed(b.RowsIn, 9) + " accepted " + Disp.Signed(b.Accepted, 9) + " rejected " +
                  Disp.Signed(b.Rejected, 9) + " duplicate " + Disp.Signed(b.Duplicate, 9) + " already loaded " + Disp.Signed(b.Skipped, 9));
        return statusText;
    }

    /// <summary>3400-END-WORK: ROLB when the return code is above 4 (nothing loaded), else COMT.</summary>
    private static async Task EndWorkAsync(XmdBatch b, State st)
    {
        if (!st.TxnOpen) return;
        var uow = b.Ctx.Get<IUnitOfWork>();
        if (b.Rc > 4)
        {
            try
            {
                await uow.RollbackAsync(b.Ct);
            }
            catch (Exception ex) when (ex is SqlioException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                // ROLB result not tested
            }
            b.Posted = 0;
            b.Display("STERP-W-3401 the conversion was rolled back - nothing was loaded");
        }
        else
        {
            try
            {
                await uow.CommitAsync(b.Ct);
                b.Display("STERP-I-3403 committed " + Disp.Signed(b.Posted, 9) + " ledger items for " + st.BatchRef);
            }
            catch (Exception ex) when (ex is SqlioException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                b.Rc = 12;
                var text = ex is SqlioException sx ? sx.DriverText : ex.Message;
                b.Display("STERP-E-3402 the conversion commit failed " + CobolText.Fit(text, 60));
            }
        }
        st.TxnOpen = false;
    }

    /// <summary>3500-CLOSE-BATCH (COMMIT only, autocommit): the batch counts and COMMITTED / PARTIAL.</summary>
    private static async Task CloseBatchAsync(XmdBatch b, State st)
    {
        if (!b.IsCommit || st.BatchId == 0) return;
        try
        {
            await b.Sql.ExecAsync(
                "UPDATE stg.ImportBatch SET RowCountTotal = ?, RowCountAccepted = ?, RowCountRejected = ?, RowCountDuplicate = ?, Status = ?, " +
                "CompletedAt = SYSDATETIME() WHERE BatchId = ?",
                [
                    SqlioParm.Text(Disp.Unsigned(b.RowsIn, 9)), SqlioParm.Text(Disp.Unsigned(b.Accepted, 9)),
                    SqlioParm.Text(Disp.Unsigned(b.Rejected, 9)), SqlioParm.Text(Disp.Unsigned(b.Duplicate, 9)),
                    SqlioParm.Text(b.Rejected > 0 || b.Duplicate > 0 ? "PARTIAL" : "COMMITTED"), SqlioParm.Text(Disp.Unsigned(st.BatchId, 9)),
                ], b.Ct);
        }
        catch (SqlioException)
        {
            // EXEC result not tested
        }
    }

    /// <summary>7000-COUNT-CHECK: SNGL COUNT(*) (0 when the statement fails).</summary>
    private static async Task<decimal> CountAsync(XmdBatch b, string sql, SqlioParm[] parms)
    {
        try
        {
            var row = await b.Sql.SingleAsync(sql, parms, Widths[..1], b.Ct);
            return row.Found ? CobolNumeric.Store(row.NumVal(0), 4, 0) : 0;
        }
        catch (SqlioException)
        {
            return 0;
        }
    }

    /// <summary>7100-CHECK-DATE: 8 leading digits, not before 19000101 and a real date; else 0.</summary>
    private static int CheckDate(string text)
    {
        var t = CobolText.Fit(text, 8);
        if (!t.All(char.IsAsciiDigit)) return 0;
        var n = int.Parse(t, CultureInfo.InvariantCulture);
        if (n < 19000101) return 0;
        return CobolDate.TestDate(n) == 0 ? n : 0;
    }

    /// <summary>7200-CHECK-NUM: non-blank and the first 20 characters spaces, sign, point, comma or digits; then NUMVAL into XB-AMT.</summary>
    private static bool CheckNum(State st, string text)
    {
        st.XbAmt = 0;
        var t2 = CobolText.Fit(CobolText.Trim(text), 200);
        if (CobolText.IsSpaces(t2)) return false;
        var ok = true;
        for (var i = 0; i < 20; i++)
        {
            var c = t2[i];
            if (c != ' ' && c != '-' && c != '+' && c != '.' && c != ',' && !char.IsAsciiDigit(c)) ok = false;
        }
        if (ok) st.XbAmt = CobolNumeric.Store(CobolNumeric.NumVal(t2), 13, 2);
        return ok;
    }

    private static void Reject(Det d, string reason)
    {
        d.Outcome = "REJECTED";
        d.Reason = reason;
    }

    /// <summary>The staging natural key: SR-TYPE "|" SR-KEY(1:60), cut to 120.</summary>
    private static string NaturalKey(Rec rec) => CobolText.Fit(rec.Type + "|" + rec.Key, 120);

    private static string? IsoDate(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
}
