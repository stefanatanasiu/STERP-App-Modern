using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Itm.Online;

/// <summary>
/// Item and Part Maintenance reads (FR-STERP-ITM-010): ItemService.Header and ItemService.Part (ItemService.vb:36-73) through
/// the SqlHelper family (one short autocommit connection per call; empty string -> NULL).
/// </summary>
public static class ItemEndpoints
{
    /// <summary>ItemService.Part statements by operation (ItemService.vb:44-71), filtered by company and item, no ORDER BY.</summary>
    public static readonly IReadOnlyDictionary<string, string> TabStatements = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["itm_listItemRevisions"] =
            "SELECT Revision, Description, Status, EffectiveFrom, EffectiveTo, IsInterchangeable, " +
            "DrawingRef, ChangeReason, ApprovedBy, ApprovedAt FROM inv.ItemRevision",
        ["itm_listItemUnits"] =
            "SELECT UomCode, UsageType, StockUnitsPerUom, PackDescription, EffectiveFrom, EffectiveTo FROM inv.ItemUom",
        ["itm_listItemSupplyRules"] =
            "SELECT SiteCode, DefaultWarehouseCode, DefaultLocationCode, MakeBuy, LeadTimeDays, MinOrderQty, " +
            "PurchaseMultipleQty, ReorderPointQty, SafetyStockQty, MaxStockQty, AbcClass FROM inv.ItemSite",
        ["itm_listItemPartnerRefs"] =
            "SELECT PartnerType, PartnerCode, PartnerItemCode, PartnerDescription, UomCode, QtyBreak, " +
            "CurrencyCode, UnitPrice, LeadTimeDays, PreferenceRank, IsApprovedSource, EffectiveFrom, " +
            "EffectiveTo FROM inv.ItemPartner",
        ["itm_listItemLots"] =
            "SELECT LotCode, Revision, SupplierLotRef, SupplierCode, ManufacturedDate, ReceivedDate, " +
            "ExpiryDate, QualityStatus, CertificateRef, OriginCountry FROM inv.Lot",
        ["itm_listItemVatLiability"] =
            "SELECT Direction, VatCode, EffectiveFrom, EffectiveTo, EvidenceRef, ApprovedBy FROM inv.ItemVatClass",
        ["itm_listItemCustoms"] =
            "SELECT EffectiveFrom, CommodityCode, CountryOfOrigin, PreferentialOrigin, PreferenceEvidenceRef, " +
            "DutyRatePct, NetWeightKgPerStockUnit FROM inv.ItemCustoms",
        ["itm_listItemPackaging"] =
            "SELECT PackagingMaterial, PackagingType, WeightGrams, RecycledContentPct, NationOfSupply, " +
            "IsPlasticPackagingTax FROM inv.ItemPackaging",
        ["itm_listItemSubstitutes"] =
            "SELECT SubstituteItemCode, ConversionRatio, ContextPurchase, ContextSales, ContextKit, " +
            "ContextBom, RequiresCustomerAgreement, Restrictions, ValidFrom, ValidTo FROM inv.ItemSubstitute",
    };

    public static void Map(OperationRegistry ops)
    {
        ops.Handle("itm_getItem", GetItemAsync);
        foreach (var (operationId, sql) in TabStatements)
        {
            var statement = sql + " WHERE CompanyCode = @c AND ItemCode = @i";
            ops.Handle(operationId, ctx => ListTabAsync(ctx, statement));
        }
    }

    /// <summary>itm_getItem: ItemService.Header (ItemService.vb:36-39); 404 text of frmItemMaster.vb:93-96.</summary>
    public static async Task<IResult> GetItemAsync(OperationContext ctx)
    {
        ctx.Session.LegacyProgram = "ItemService.Header";
        var item = ctx.Route("itemCode");
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync("SELECT * FROM inv.Item WHERE CompanyCode = @c AND ItemCode = @i",
            new { c = ctx.Session.CompanyCode, i = item }, ctx.Aborted);
        if (rows.Count == 0)
        {
            return ctx.Problem(StatusCodes.Status404NotFound, ProblemKinds.NotFound,
                string.Format(CultureInfo.InvariantCulture, "Item {0} does not exist in {1}.", item, ctx.Session.CompanyCode));
        }
        return ctx.Direct(WithRowVersionName((JsonObject)rows[0]!));
    }

    /// <summary>Contracts 4.3: the rowversion column (RowVer) is published as rowVersion.</summary>
    public static JsonObject WithRowVersionName(JsonObject row)
    {
        if (!row.ContainsKey("rowVer")) return row;
        var copy = new JsonObject();
        foreach (var (k, v) in row.ToList())
        {
            row.Remove(k);
            copy[k == "rowVer" ? "rowVersion" : k] = v;
        }
        return copy;
    }

    private static async Task<IResult> ListTabAsync(OperationContext ctx, string statement)
    {
        ctx.Session.LegacyProgram = "ItemService.Part";
        var sql = ctx.Get<IProcedureRunner>();
        var rows = await sql.QueryJsonAsync(statement, new { c = ctx.Session.CompanyCode, i = ctx.Route("itemCode") }, ctx.Aborted);
        return ctx.List(rows);
    }
}
