using Sterp.Modules.Bom.Data;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Bom.Domain;

/// <summary>BOM-SUP-CTL (cobol/copy/BOMSUPCT.cpy): one BOMSUPPL call.</summary>
internal sealed class SupplyCall
{
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string Source { get; set; } = "";
    public string Item { get; set; } = "";
    public string Rev { get; set; } = "";
    public decimal Free { get; set; }
    public decimal Inbound { get; set; }
    public decimal Want { get; set; }
    public decimal Taken { get; set; }
    public decimal TakenInbound { get; set; }
    public decimal Remain { get; set; }
    public string UseInbound { get; set; } = "";
    public string Status { get; set; } = "OK";
    public string Message { get; set; } = "";
}

/// <summary>
/// BOMSUPPL (cobol/online/bom/BOMSUPPL.cob): the eligible supply pool of one netted calculation (200 entries). Live free
/// stock is read on demand (accepted, owned, STOCK locations, net of reservations); TRIAL rows are declared by the caller.
/// TAKE is the only way stock leaves the pool, so a stock unit is used once (CS-BOM-03).
/// </summary>
internal sealed class SupplyPool(ISqlRunner sql)
{
    private const int PoolMax = 200;

    private const string LiveSql =
        "SELECT ISNULL(SUM(CASE WHEN b.QtyOnHand - b.QtyReserved > 0 THEN b.QtyOnHand - b.QtyReserved ELSE 0 END),0), " +
        "ISNULL(SUM(b.QtyInTransit),0) FROM inv.StockBalance b JOIN inv.Location l ON l.CompanyCode = b.CompanyCode " +
        "AND l.SiteCode = b.SiteCode AND l.WarehouseCode = b.WarehouseCode AND l.LocationCode = b.LocationCode " +
        "WHERE b.CompanyCode = ? AND b.ItemCode = ? AND (? = '' OR b.SiteCode = ?) AND b.QualityStatus = 'ACCEPTED' " +
        "AND b.OwnerType = 'OWNED' AND l.LocationType = 'STOCK'";

    private sealed class Entry
    {
        public string Item = "";
        public string Rev = "";
        public decimal Free;
        public decimal Inbound;
        public decimal SnapFree;
        public decimal SnapInbound;
        public string Loaded = "N";
    }

    private readonly List<Entry> _pool = [];

    public void Clear() => _pool.Clear();

    private int Find(string item, string rev)
    {
        for (var i = 0; i < _pool.Count; i++)
        {
            var e = _pool[i];
            if (e.Item == item && (e.Rev == rev || Pic.Blank(rev) || Pic.Blank(e.Rev))) return i;
        }
        return -1;
    }

    private int Add(SupplyCall c)
    {
        if (_pool.Count >= PoolMax)
        {
            c.Status = "FULL";
            c.Message = "The eligible supply pool is full";
            return -1;
        }
        _pool.Add(new Entry { Item = c.Item, Rev = c.Rev });
        return _pool.Count - 1;
    }

    /// <summary>SET: declares (or replaces) the free and inbound quantities of an entry.</summary>
    public void Set(SupplyCall c)
    {
        var hit = Find(c.Item, c.Rev);
        if (hit < 0)
        {
            hit = Add(c);
            if (c.Status == "FULL") return;
        }
        _pool[hit].Free = c.Free;
        _pool[hit].Inbound = c.Inbound;
        _pool[hit].Loaded = "Y";
    }

    /// <summary>FETCH: live free stock and in-transit of the item (read once, then held).</summary>
    public async Task FetchAsync(SupplyCall c, CancellationToken ct)
    {
        var hit = Find(c.Item, c.Rev);
        if (hit >= 0 && _pool[hit].Loaded == "Y")
        {
            c.Free = _pool[hit].Free;
            c.Inbound = _pool[hit].Inbound;
            return;
        }
        try
        {
            var row = await sql.WithReaderAsync(BomSql.Q(LiveSql), BomSql.Args(c.Company, c.Item, c.Site, c.Site), async r =>
            {
                if (!await r.ReadAsync(ct)) return ((decimal, decimal)?)null;
                return (BomSql.Num(r, 0), BomSql.Num(r, 1));
            }, ct);
            if (row is null)
            {
                c.Free = 0m;
                c.Inbound = 0m;
            }
            else
            {
                c.Free = Pic.N(row.Value.Item1, 15, 6);
                c.Inbound = Pic.N(row.Value.Item2, 15, 6);
            }
        }
        catch (SqlioException ex)
        {
            c.Status = "SQLFAIL";
            c.Message = BomSql.ErrMsg(ex, 200);
            return;
        }
        Set(c);
    }

    /// <summary>TAKE: free stock first, then expected receipts when the caller allows them.</summary>
    public async Task TakeAsync(SupplyCall c, CancellationToken ct)
    {
        c.Status = "OK";
        c.Message = "";
        c.Taken = 0m;
        c.TakenInbound = 0m;
        c.Remain = c.Want;
        if (c.Want <= 0) return;
        var hit = Find(c.Item, c.Rev);
        if (hit < 0 && c.Source == "LIVE")
        {
            await FetchAsync(c, ct);
            hit = Find(c.Item, c.Rev);
        }
        if (hit < 0) return;
        var e = _pool[hit];
        if (e.Free > 0)
        {
            var take = e.Free >= c.Want ? c.Want : e.Free;
            e.Free = Pic.N(e.Free - take, 15, 6);
            c.Taken = take;
        }
        c.Remain = Pic.N(c.Want - c.Taken, 15, 6);
        if (c.Remain > 0 && c.UseInbound == "Y" && e.Inbound > 0)
        {
            var take = e.Inbound >= c.Remain ? c.Remain : e.Inbound;
            e.Inbound = Pic.N(e.Inbound - take, 15, 6);
            c.TakenInbound = take;
            c.Remain = Pic.N(c.Remain - take, 15, 6);
        }
    }

    /// <summary>AVAIL: the remaining free and inbound quantities (NOTFOUND when the item has no entry).</summary>
    public void Available(SupplyCall c)
    {
        var hit = Find(c.Item, c.Rev);
        if (hit < 0)
        {
            c.Free = 0m;
            c.Inbound = 0m;
            c.Status = "NOTFOUND";
            return;
        }
        c.Free = _pool[hit].Free;
        c.Inbound = _pool[hit].Inbound;
    }

    /// <summary>SNAP: remembers the pool.</summary>
    public void Snapshot()
    {
        foreach (var e in _pool)
        {
            e.SnapFree = e.Free;
            e.SnapInbound = e.Inbound;
        }
    }

    /// <summary>RESET: puts the snapshot back.</summary>
    public void Restore()
    {
        foreach (var e in _pool)
        {
            e.Free = e.SnapFree;
            e.Inbound = e.SnapInbound;
        }
    }
}
