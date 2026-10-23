using Sterp.Modules.Kit.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Kit.Application;

/// <summary>GENEALOGCA (cobol/copy/GENEALOGCA.cpy).</summary>
internal sealed class GenealogArea : KitArea
{
    public string Direction { get; set; } = "";
    public string KitRef { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public string LotCode { get; set; } = "";
    public string SerialNo { get; set; } = "";
    public long CompletionId { get; set; }
    public int MaxDepth { get; set; }
    public string IncludeRet { get; set; } = "";
    public string IncludeScrap { get; set; } = "";

    // result
    public int LineTotal { get; set; }
    public int DepthReached { get; set; }
    public int SubstituteCnt { get; set; }
    public int ReturnCnt { get; set; }
    public int ScrapCnt { get; set; }
    public int KitCnt { get; set; }
    public string Truncated { get; set; } = "";
    public string SummaryText { get; set; } = "";

    // paging window
    public int FromLine { get; set; }
    public int WinMax { get; set; }
    public int WinCnt { get; set; }
    public string MoreLines { get; set; } = "";
    public GenealogLine[] Lines { get; } = Enumerable.Range(0, 20).Select(_ => new GenealogLine()).ToArray();
}

/// <summary>One GN-LINES occurrence (also the shape of a Ws-Res-Line row; Seq is only set in the window).</summary>
internal sealed class GenealogLine
{
    public int Seq { get; set; }
    public int Depth { get; set; }
    public string KitRef { get; set; } = "";
    public string Parent { get; set; } = "";
    public string ParLot { get; set; } = "";
    public string ParSerial { get; set; } = "";
    public string Item { get; set; } = "";
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public decimal Qty { get; set; }
    public string Uom { get; set; } = "";
    public string Planned { get; set; } = "";
    public string Substitute { get; set; } = "";
    public string Event { get; set; } = "";
    public int When { get; set; }
    public string Operator { get; set; } = "";
    public string Note { get; set; } = "";

    public GenealogLine Copy() => (GenealogLine)MemberwiseClone();
}

/// <summary>
/// GENEALOG (cobol/online/kit/GENEALOG.cob:113-800) - FORWARD, BACKWARD and WHEREUSD over kit.KitGenealogy, kit.KitIssue
/// and the open kit lines. The trace is re-run on every call and the window cut from the collected answer (Ws-Result,
/// 400 rows; breadth-first queue of 60 identities; 40 kits counted). Read only.
/// </summary>
internal sealed class Genealog(ISqlRunner sql)
{
    private const int ResMax = 400;
    private const int QueueMax = 60;
    private const int KitMax = 40;

    private const string GenealogySelect =
        "SELECT g.KitRef, g.ParentItemCode," +
        " ISNULL(g.ParentLotCode,''), ISNULL(g.ParentSerialNo,'')," +
        " g.ComponentItemCode, ISNULL(g.ComponentLotCode,'')," +
        " ISNULL(g.ComponentSerialNo,'')," +
        " CONVERT(varchar(26),g.ConsumedQty)," +
        " CONVERT(varchar(2),g.WasSubstitute)," +
        " ISNULL(g.PlannedItemCode,'')," +
        " ISNULL(i.UomCode,'')," +
        " ISNULL(CONVERT(char(8),i.ActualDate,112),'')," +
        " ISNULL(i.OperatorUser,'')," +
        " ISNULL(i.ReasonCode,'')" +
        " FROM kit.KitGenealogy g" +
        " LEFT JOIN kit.KitIssue i ON i.KitIssueId = g.KitIssueId" +
        " WHERE g.CompanyCode = ?";

    private const string ForwardIdentity =
        " AND (? = '' OR ISNULL(g.ParentLotCode,'') = ?)" +
        " AND (? = '' OR ISNULL(g.ParentSerialNo,'') = ?)";

    private const string BackwardIdentity =
        " AND (? = '' OR ISNULL(g.ComponentLotCode,'') = ?)" +
        " AND (? = '' OR ISNULL(g.ComponentSerialNo,'') = ?)";

    private const string CommonFilters =
        " AND (? = '' OR g.KitRef = ?)" +
        " AND (? = 0 OR g.CompletionId = ?)" +
        " ORDER BY g.GenealogyId";

    private const string DispositionSelect =
        "SELECT i.KitRef, i.ComponentItemCode," +
        " ISNULL(i.LotCode,''), ISNULL(i.SerialNo,'')," +
        " CONVERT(varchar(26),i.Quantity), ISNULL(i.UomCode,'')," +
        " i.IssueType," +
        " ISNULL(CONVERT(char(8),i.ActualDate,112),'')," +
        " ISNULL(i.OperatorUser,''), ISNULL(i.Reason,'')" +
        " FROM kit.KitIssue i" +
        " WHERE i.CompanyCode = ?" +
        " AND i.IssueType IN (?,?)" +
        " AND (? = '' OR i.ComponentItemCode = ?)" +
        " AND (? = '' OR ISNULL(i.LotCode,'') = ?)" +
        " AND (? = '' OR ISNULL(i.SerialNo,'') = ?)" +
        " AND (? = '' OR i.KitRef = ?)" +
        " ORDER BY i.KitIssueId";

    private const string WhereUsedSelect =
        "SELECT k.KitRef, k.ParentItemCode, l.ComponentItemCode," +
        " CONVERT(varchar(26),l.RemainingQty), ISNULL(l.UomCode,'')," +
        " k.Status, ISNULL(CONVERT(char(8),k.NeedDate,112),'')," +
        " ISNULL(k.OwnerUser,''), CONVERT(varchar(12),l.LineNum)" +
        " FROM kit.ExecutionKitLine l" +
        " INNER JOIN kit.ExecutionKit k" +
        "   ON k.ExecutionKitId = l.ExecutionKitId" +
        " WHERE k.CompanyCode = ?" +
        " AND (l.ComponentItemCode = ?" +
        "      OR ISNULL(l.SubstituteItemCode,'') = ?)" +
        " AND k.Status IN ('DRAFT','REVIEWED','RELEASED'," +
        "   'PARTISSUED','ISSUED','PARTCOMPLETE')" +
        " AND l.RemainingQty > 0" +
        " ORDER BY k.NeedDate, k.KitRef, l.LineNum";

    private static readonly int[] Wide = [.. Enumerable.Repeat(100, 20)];

    private sealed record QueueEntry(string Item, string Lot, string Serial, int Depth);

    private readonly List<GenealogLine> _res = [];
    private string _trunc = "N";
    private readonly List<QueueEntry> _queue = [];
    private int _qPtr;
    private readonly List<string> _kits = [];
    private int _depth;
    private string _curItem = "";
    private string _curLot = "";
    private string _curSerial = "";

    public async Task LinkAsync(GenealogArea ca, CancellationToken ct)
    {
        ca.Program = "GENEALOG";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "KIT-019";
        ClearAnswer(ca);
        switch (ca.Function)
        {
            case "FORWARD": await TraceAsync(ca, true, ct); break;
            case "BACKWARD": await TraceAsync(ca, false, ct); break;
            case "WHEREUSD": await WhereUsedOpenKitsAsync(ca, ct); break;
            default:
                ca.Set(8, "KIT0002", Pic.Msg("GENEALOG does not support the function ", Pic.Trim(ca.Function)));
                break;
        }
        if (!ca.Failed)
        {
            FillWindow(ca);
            Summarise(ca);
        }
    }

    private static void SetSqlFailure(GenealogArea ca, SqlioException ex) => ca.Set(12, "SQL0001", Pic.X(ex.DriverText, 200));

    private void ClearAnswer(GenealogArea ca)
    {
        _res.Clear();
        _trunc = "N";
        _queue.Clear();
        _qPtr = 0;
        _kits.Clear();
        ca.LineTotal = 0;
        ca.DepthReached = 0;
        ca.SubstituteCnt = 0;
        ca.ReturnCnt = 0;
        ca.ScrapCnt = 0;
        ca.KitCnt = 0;
        ca.WinCnt = 0;
        ca.Truncated = "N";
        ca.MoreLines = "N";
        ca.SummaryText = "";
        if (ca.MaxDepth <= 0) ca.MaxDepth = 5;
        if (ca.MaxDepth > 12) ca.MaxDepth = 12;
        if (ca.WinMax is <= 0 or > 20) ca.WinMax = 20;
        if (ca.FromLine <= 0) ca.FromLine = 1;
    }

    /// <summary>Validate-Identity: an enquiry with no identity at all would return the whole factory.</summary>
    private static void ValidateIdentity(GenealogArea ca)
    {
        if (Pic.Blank(ca.ItemCode) && Pic.Blank(ca.KitRef) && ca.CompletionId == 0)
            ca.Set(8, "KIT0012", "Give an item, a kit reference or a completion identity to trace.");
    }

    /// <summary>Trace-Forward / Trace-Backward: breadth first from the identity, then the return and scrap lines.</summary>
    private async Task TraceAsync(GenealogArea ca, bool forward, CancellationToken ct)
    {
        ValidateIdentity(ca);
        if (ca.Failed) return;
        SeedQueue(ca);
        while (!(_qPtr >= _queue.Count || _trunc == "Y"))
        {
            var qe = _queue[_qPtr++];
            _curItem = qe.Item;
            _curLot = qe.Lot;
            _curSerial = qe.Serial;
            _depth = qe.Depth;
            await ReadGenealogyAsync(ca, forward, ct);
            if (ca.Failed) break;
        }
        if (!ca.Failed) await AddDispositionLinesAsync(ca, ct);
        if (!ca.Failed && _res.Count == 0)
        {
            ca.Set(16, "KIT0026", Pic.Msg("No genealogy is recorded for ", Pic.Trim(ca.ItemCode), " ", Pic.Trim(ca.LotCode),
                Pic.Trim(ca.SerialNo), "."));
        }
    }

    private void SeedQueue(GenealogArea ca)
    {
        _queue.Clear();
        _qPtr = 0;
        _queue.Add(new QueueEntry(Pic.X(ca.ItemCode, 40), Pic.X(ca.LotCode, 30), Pic.X(ca.SerialNo, 40), 0));
    }

    /// <summary>Queue-Identity: an identity is queued once; untraced identities and the depth limit stop the walk.</summary>
    private void QueueIdentity(GenealogArea ca)
    {
        if (_depth + 1 >= ca.MaxDepth) return;
        if (Pic.Blank(_curLot) && Pic.Blank(_curSerial)) return;
        if (_queue.Any(q => q.Item == _curItem && q.Lot == _curLot && q.Serial == _curSerial)) return;
        if (_queue.Count >= QueueMax)
        {
            _trunc = "Y";
            return;
        }
        _queue.Add(new QueueEntry(_curItem, _curLot, _curSerial, _depth + 1));
    }

    private void NoteKit(GenealogLine rs)
    {
        if (!_kits.Contains(rs.KitRef) && _kits.Count < KitMax) _kits.Add(rs.KitRef);
    }

    /// <summary>Read-Children (FORWARD) / Read-Parents (BACKWARD) with the identity and common filters.</summary>
    private async Task ReadGenealogyAsync(GenealogArea ca, bool forward, CancellationToken ct)
    {
        var stmt = GenealogySelect + (forward ? " AND g.ParentItemCode = ?" : " AND g.ComponentItemCode = ?") +
                   (forward ? ForwardIdentity : BackwardIdentity) + CommonFilters;
        var completion = SqlioParm.Integer(ca.CompletionId);
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(stmt,
            [
                SqlioParm.Text(Pic.Pad(ca.Company, 10)),
                SqlioParm.Text(_curItem),
                SqlioParm.Text(_curLot), SqlioParm.Text(_curLot),
                SqlioParm.Text(_curSerial), SqlioParm.Text(_curSerial),
                SqlioParm.Text(Pic.Pad(ca.KitRef, 30)), SqlioParm.Text(Pic.Pad(ca.KitRef, 30)),
                completion, completion,
            ], Wide, ct);
        }
        catch (SqlioException ex)
        {
            SetSqlFailure(ca, ex);
            return;
        }
        foreach (var row in rows)
        {
            StoreGenealogyRow(ca, row, forward);
            if (_trunc == "Y") break;
        }
    }

    private void StoreGenealogyRow(GenealogArea ca, SqlioRow row, bool forward)
    {
        if (_res.Count >= ResMax)
        {
            _trunc = "Y";
            return;
        }
        var rs = new GenealogLine
        {
            Depth = (int)Pic.N(_depth + 1, 4, 0),
            KitRef = Pic.X(row.Raw(0), 30),
            Parent = Pic.X(row.Raw(1), 40),
            ParLot = Pic.X(row.Raw(2), 30),
            ParSerial = Pic.X(row.Raw(3), 40),
            Item = Pic.X(row.Raw(4), 40),
            Lot = Pic.X(row.Raw(5), 30),
            Serial = Pic.X(row.Raw(6), 40),
            Qty = Pic.Q(row.NumVal(7)),
            Uom = Pic.X(row.Raw(10), 10),
            Planned = Pic.X(row.Raw(9), 40),
            Event = "CONSUMED",
            When = DateSlot8(row.Raw(11)),
            Operator = Pic.X(row.Raw(12), 20),
        };
        _res.Add(rs);
        // Mark-Substitution: the planned part stays on the line beside the part really fitted
        if (row.Raw(8).StartsWith('1'))
        {
            rs.Substitute = "Y";
            ca.SubstituteCnt = (int)Pic.N(ca.SubstituteCnt + 1, 4, 0);
            rs.Note = Pic.Str(60, "Substituted for ", Pic.Trim(rs.Planned));
        }
        else
        {
            rs.Substitute = "N";
            rs.Note = "";
            if (Pic.Blank(rs.Planned)) rs.Planned = rs.Item;
        }
        if (rs.Depth > ca.DepthReached) ca.DepthReached = rs.Depth;
        NoteKit(rs);
        // Queue-Next-Identity
        _curItem = forward ? rs.Item : rs.Parent;
        _curLot = forward ? rs.Lot : rs.ParLot;
        _curSerial = forward ? rs.Serial : rs.ParSerial;
        QueueIdentity(ca);
    }

    /// <summary>"IF slot (1:8) NUMERIC MOVE slot (1:8) TO PIC 9(8) ELSE 0".</summary>
    private static int DateSlot8(string slot)
    {
        var s = Pic.Pad(slot, 8)[..8];
        return s.All(char.IsAsciiDigit) ? int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Add-Disposition-Lines: returns and scrap at depth 1 (D-12); they built nothing.</summary>
    private async Task AddDispositionLinesAsync(GenealogArea ca, CancellationToken ct)
    {
        if (ca.IncludeRet != "Y" && ca.IncludeScrap != "Y") return;
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(DispositionSelect,
            [
                SqlioParm.Text(Pic.Pad(ca.Company, 10)),
                SqlioParm.Text(ca.IncludeRet == "Y" ? "RETURN" : "~NONE~"),
                SqlioParm.Text(ca.IncludeScrap == "Y" ? "SCRAP" : "~NONE~"),
                SqlioParm.Text(Pic.Pad(ca.ItemCode, 40)), SqlioParm.Text(Pic.Pad(ca.ItemCode, 40)),
                SqlioParm.Text(Pic.Pad(ca.LotCode, 30)), SqlioParm.Text(Pic.Pad(ca.LotCode, 30)),
                SqlioParm.Text(Pic.Pad(ca.SerialNo, 40)), SqlioParm.Text(Pic.Pad(ca.SerialNo, 40)),
                SqlioParm.Text(Pic.Pad(ca.KitRef, 30)), SqlioParm.Text(Pic.Pad(ca.KitRef, 30)),
            ], Wide, ct);
        }
        catch (SqlioException ex)
        {
            SetSqlFailure(ca, ex);
            return;
        }
        foreach (var row in rows)
        {
            if (_res.Count >= ResMax)
            {
                _trunc = "Y";
                break;
            }
            var rs = new GenealogLine
            {
                Depth = 1,
                KitRef = Pic.X(row.Raw(0), 30),
                Parent = "",
                ParLot = "",
                ParSerial = "",
                Item = Pic.X(row.Raw(1), 40),
                Lot = Pic.X(row.Raw(2), 30),
                Serial = Pic.X(row.Raw(3), 40),
                Qty = Pic.Q(row.NumVal(4)),
                Uom = Pic.X(row.Raw(5), 10),
                Planned = Pic.X(row.Raw(1), 40),
                Substitute = "N",
                Event = Pic.X(row.Raw(6), 12),
                When = DateSlot8(row.Raw(7)),
                Operator = Pic.X(row.Raw(8), 20),
                Note = Pic.X(row.Raw(9), 60),
            };
            _res.Add(rs);
            if (Pic.Pad(row.Raw(6), 6)[..6] == "RETURN")
                ca.ReturnCnt = (int)Pic.N(ca.ReturnCnt + 1, 4, 0);
            else
                ca.ScrapCnt = (int)Pic.N(ca.ScrapCnt + 1, 4, 0);
            NoteKit(rs);
        }
    }

    /// <summary>WHEREUSD: open kits that still carry a requirement for the item (as component or substitute), by need date.</summary>
    private async Task WhereUsedOpenKitsAsync(GenealogArea ca, CancellationToken ct)
    {
        if (Pic.Blank(ca.ItemCode))
        {
            ca.Set(8, "KIT0012", "An item code is required for a where used enquiry.");
            return;
        }
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await sql.FetchAllAsync(WhereUsedSelect,
            [
                SqlioParm.Text(Pic.Pad(ca.Company, 10)), SqlioParm.Text(Pic.Pad(ca.ItemCode, 40)), SqlioParm.Text(Pic.Pad(ca.ItemCode, 40)),
            ], Wide, ct);
        }
        catch (SqlioException ex)
        {
            SetSqlFailure(ca, ex);
            return;
        }
        foreach (var row in rows)
        {
            if (_res.Count >= ResMax)
            {
                _trunc = "Y";
                break;
            }
            var rs = new GenealogLine
            {
                Depth = 1,
                KitRef = Pic.X(row.Raw(0), 30),
                Parent = Pic.X(row.Raw(1), 40),
                ParLot = "",
                ParSerial = "",
                Item = Pic.X(row.Raw(2), 40),
                Lot = "",
                Serial = "",
                Qty = Pic.Q(row.NumVal(3)),
                Uom = Pic.X(row.Raw(4), 10),
                Planned = Pic.X(row.Raw(2), 40),
                Substitute = "N",
                Event = "REQUIRED",
                When = DateSlot8(row.Raw(6)),
                Operator = Pic.X(row.Raw(7), 20),
                Note = Pic.Str(60, "Open kit in status ", Pic.Trim(row.Raw(5)), " line ", Pic.Trim(row.Raw(8))),
            };
            _res.Add(rs);
            NoteKit(rs);
        }
    }

    /// <summary>Fill-Window: the window starts at GN-FROM-LINE (1-based, inclusive).</summary>
    private void FillWindow(GenealogArea ca)
    {
        ca.LineTotal = _res.Count;
        ca.KitCnt = _kits.Count;
        ca.Truncated = _trunc;
        ca.WinCnt = 0;
        ca.MoreLines = "N";
        for (var w = 0; w < 20; w++) ca.Lines[w] = new GenealogLine { Substitute = "N" };
        var i = ca.FromLine;
        while (!(i > _res.Count || ca.WinCnt >= ca.WinMax))
        {
            var line = _res[i - 1].Copy();
            line.Seq = i;
            ca.Lines[ca.WinCnt++] = line;
            i++;
        }
        if (i <= _res.Count) ca.MoreLines = "Y";
    }

    private static string CntEdit(int value) => CobolEdit.Format(value, "Z(8)9");

    /// <summary>Summarise + Append-Counts; a truncated answer is RC 4 KIT0026.</summary>
    private void Summarise(GenealogArea ca)
    {
        var count = CntEdit(_res.Count);
        var summary = ca.Function switch
        {
            "FORWARD" => Pic.Str(200, count, " consumption records to depth ", CobolEdit.Format(ca.DepthReached, "Z(3)9")),
            "BACKWARD" => Pic.Str(200, count, " parent records to depth ", CobolEdit.Format(ca.DepthReached, "Z(3)9")),
            _ => Pic.Str(200, count, " open kits still require this component"),
        };
        summary = Pic.Str(200, Pic.Trim(summary), "; substitutions ", CntEdit(ca.SubstituteCnt));
        summary = Pic.Str(200, Pic.Trim(summary), ", returns ", CntEdit(ca.ReturnCnt));
        summary = Pic.Str(200, Pic.Trim(summary), ", scrap ", CntEdit(ca.ScrapCnt));
        summary = Pic.Str(200, Pic.Trim(summary), ", kits ", CntEdit(ca.KitCnt), ".");
        ca.SummaryText = summary;
        if (_trunc == "Y")
            ca.Set(4, "KIT0026", "The trace was cut short - narrow the identity or reduce the depth.");
    }
}

/// <summary>KitExecutionService.Genealogy (KitExecutionService.vb:101-121): returns and scrap included, window 20, KitArea.Page.</summary>
internal sealed class GenealogService(Genealog genealog)
{
    public async Task<(LegacyResult Result, GenealogArea Area, List<GenealogLine> Lines)> TraceAsync(KitSession s, string direction, string kitRef,
        string item, string lot, string serial, int maxDepth, CancellationToken ct)
    {
        var ca = new GenealogArea
        {
            Direction = Pic.X(direction, 10),
            KitRef = Pic.X(kitRef, 30),
            ItemCode = Pic.X(item, 40),
            LotCode = Pic.X(lot, 30),
            SerialNo = Pic.X(serial, 40),
            MaxDepth = maxDepth,
            IncludeRet = "Y",
            IncludeScrap = "Y",
            FromLine = 0,
            WinMax = 20,
        };
        var fn = direction.Trim().ToUpperInvariant();
        if (fn != "BACKWARD" && fn != "WHEREUSD") fn = "FORWARD";
        var lines = new List<GenealogLine>();
        var r = await LinkAsync(ca, s, fn, ct);
        if (!r.Succeeded) return (r, ca, lines);
        var from = 0;
        for (var guard = 1; guard <= 100; guard++)
        {
            var n = ca.WinCnt;
            for (var i = 0; i < n; i++) lines.Add(ca.Lines[i].Copy());
            from += n;
            if (n == 0 || ca.MoreLines != "Y") break;
            ca.FromLine = from;
            r = await LinkAsync(ca, s, fn, ct);
            if (!r.Succeeded) break;
        }
        return (r, ca, lines);
    }

    private async Task<LegacyResult> LinkAsync(GenealogArea ca, KitSession s, string function, CancellationToken ct)
    {
        ca.Prepare(s, function);
        await genealog.LinkAsync(ca, ct);
        return ca.Result;
    }
}
