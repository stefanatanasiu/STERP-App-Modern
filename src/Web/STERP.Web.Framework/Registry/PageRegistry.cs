using Microsoft.AspNetCore.Components;

namespace Sterp.Web.Framework.Registry;

/// <summary>One row of the binding page routing table (STERP-WEB.md section 7.2, contracts/gui-routes.json).</summary>
public sealed record GuiRoute(string FormClass, string Slice, string Route, string Caption, int Width = 760, int Height = 420)
{
    /// <summary>Target application id of the owning slice ("STERP-SO").</summary>
    public string TargetApplication => "STERP-" + Slice;

    /// <summary>Delivery wave of the owning slice (contracts section 3).</summary>
    public string Wave => GuiRoutes.Waves.TryGetValue(Slice, out var w) ? w : "";

    /// <summary>The route with its argument filled in (the argument segment, or ?arg= for classes routed without one).</summary>
    public string UrlFor(string? argument)
    {
        var arg = argument ?? "";
        var brace = Route.IndexOf('{');
        if (brace >= 0) return arg.Length == 0 ? Route[..(brace - 1)] : Route[..brace] + Uri.EscapeDataString(arg);
        return arg.Length == 0 ? Route : Route + "?arg=" + Uri.EscapeDataString(arg);
    }
}

/// <summary>
/// The page routing table of STERP-WEB section 7.2 (binding routes) with the legacy form captions and window sizes
/// (advisory: legacy ClientSize). Shell-owned classes (frmMain, frmSplash, frmAbout) have no route.
/// </summary>
public static class GuiRoutes
{
    /// <summary>Slice -> wave (contracts section 3).</summary>
    public static readonly IReadOnlyDictionary<string, string> Waves = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PLATFORM"] = "W0", ["ITM"] = "W1", ["AR"] = "W2", ["SO"] = "W3", ["XMD"] = "W4", ["FIN"] = "W5", ["PO"] = "W6",
        ["AP"] = "W7", ["BOM"] = "W8", ["KIT"] = "W9", ["CST"] = "W10",
    };

    /// <summary>Pseudo class of the operator console page (STERPOPS, route /operator-console, STERP-PLATFORM.md 7.2).</summary>
    public const string OperatorConsole = "STERPOPS";

    public static readonly IReadOnlyList<GuiRoute> All =
    [
        new("frmApprovals", "PLATFORM", "/platform/approvals", "Approvals Inbox"),
        new("frmAudit", "PLATFORM", "/platform/audit", "Audit Enquiry", 900, 440),
        new("frmBankRecon", "FIN", "/fin/bank-recon", "Bank Reconciliation"),
        new("frmBomBulkPreview", "BOM", "/bom/bom-bulk-preview", "Bulk Change Preview"),
        new("frmBomEditor", "BOM", "/bom/bom-editor", "Bill of Material Maintenance"),
        new("frmBomExplosion", "BOM", "/bom/bom-explosion", "Bill of Material Explosion"),
        new("frmBomWhereUsed", "BOM", "/bom/bom-where-used", "Where Used Enquiry"),
        new("frmChainTrace", "XMD", "/xmd/chain-trace", "Business Chain Trace"),
        new("frmCostEnquiry", "CST", "/cst/cost-enquiry", "Item Cost Enquiry"),
        new("frmCostRollup", "CST", "/cst/cost-rollup", "Cost Roll-up and What-if"),
        new("frmCostSet", "CST", "/cst/cost-set", "Cost Sets"),
        new("frmCreditControl", "AR", "/ar/credit-control", "Credit Control Worklist - Sales Ledger"),
        new("frmCreditOverride", "AR", "/ar/credit-override", "Credit Overrides - scoped independent approval"),
        new("frmCustomerEnquiry", "AR", "/ar/customer-enquiry", "Customer Enquiry - Sales Ledger"),
        new("frmDespatchConfirm", "SO", "/so/despatch-confirm", "Despatch Confirmation"),
        new("frmEngineDiag", "PLATFORM", "/platform/engine-diag", "Service diagnostics", 836, 492),
        new("frmExceptions", "PLATFORM", "/platform/exceptions", "Exception Worklist"),
        new("frmGenericMaint", "PLATFORM", "/platform/generic-maint/{maintCode}", "File maintenance", 880, 540),
        new("frmGoodsReceipt", "PO", "/po/goods-receipt", "Goods Receipt and Inspection"),
        new("frmInvoiceRegister", "AP", "/ap/invoice-register", "Purchase Invoice Register"),
        new("frmItemMaster", "ITM", "/itm/item-master", "Item and Part Maintenance", 820, 460),
        new("frmJobCost", "CST", "/cst/job-cost", "Job Cost Enquiry"),
        new("frmJobMonitor", "PLATFORM", "/platform/job-monitor", "Job Monitor", 900, 420),
        new("frmJournalEnquiry", "FIN", "/fin/journal-enquiry", "Journal Enquiry"),
        new("frmJournalEntry", "FIN", "/fin/journal-entry", "Journal Entry"),
        new("frmKitExecution", "KIT", "/kit/kit-execution", "Kit Execution"),
        new("frmKitGenealogy", "KIT", "/kit/kit-genealogy", "Genealogy Enquiry"),
        new("frmKitMaint", "KIT", "/kit/kit-maint", "Kit Maintenance"),
        new("frmKitTrial", "KIT", "/kit/kit-trial", "Trial Kitting"),
        new("frmLogin", "PLATFORM", "/platform/login", "STERP - Sign On"),
        new("frmLookup", "PLATFORM", "/platform/lookup", "Lookup"),
        new("frmMaintLauncher", "PLATFORM", "/platform/maint-launcher", "File Maintenance", 520, 420),
        new("frmPaymentRun", "AP", "/ap/payment-run", "Payment Run"),
        new("frmPeriodClose", "FIN", "/fin/period-close", "Period Close Checklist"),
        new("frmPurchaseOrder", "PO", "/po/purchase-order", "Purchase Order Entry and Approval"),
        new("frmReceiptEntry", "AR", "/ar/receipt-entry", "Receipt Entry and Allocation - Sales Ledger"),
        new("frmReportLauncher", "PLATFORM", "/platform/report-launcher/{reportArgument}", "Run Report", 880, 580),
        new("frmRequisition", "PO", "/po/requisition", "Purchase Requisition"),
        new("frmSalesInvoiceRun", "SO", "/so/sales-invoice-run", "Sales Invoice Run"),
        new("frmSalesOrderEntry", "SO", "/so/sales-order-entry", "Sales Order Entry"),
        new("frmSpoolViewer", "PLATFORM", "/platform/spool-viewer", "Spool File Viewer", 860, 560),
        new("frmStockCount", "ITM", "/itm/stock-count", "Stock Count and Adjustment"),
        new("frmStockEnquiry", "ITM", "/itm/stock-enquiry", "Stock Enquiry"),
        new("frmStockMovement", "ITM", "/itm/stock-movement", "Stock Movement Entry", 960, 470),
        new("frmSupplierVerify", "AP", "/ap/supplier-verify", "Supplier Verification"),
        new("frmVatReturn", "FIN", "/fin/vat-return", "VAT Return - Nine Boxes"),
        new(OperatorConsole, "PLATFORM", "/operator-console", "STERP Operator Console", 700, 470),
    ];

    private static readonly Dictionary<string, GuiRoute> ByClass = All.ToDictionary(r => r.FormClass, StringComparer.OrdinalIgnoreCase);

    public static GuiRoute? Find(string formClass) => ByClass.TryGetValue(formClass.Trim(), out var r) ? r : null;

    /// <summary>Maps a browser path (deep link) to the form class and argument it opens; null when the path opens no window.</summary>
    public static (GuiRoute Route, string Argument)? Match(string path, string? query)
    {
        var p = (path ?? "/").TrimEnd('/');
        if (p.Length == 0) return null;
        var arg = QueryValue(query, "arg") ?? QueryValue(query, "path") ?? "";
        foreach (var r in All)
        {
            var brace = r.Route.IndexOf('{');
            if (brace < 0)
            {
                if (string.Equals(p, r.Route, StringComparison.OrdinalIgnoreCase)) return (r, arg);
                continue;
            }
            var prefix = r.Route[..(brace - 1)];
            if (string.Equals(p, prefix, StringComparison.OrdinalIgnoreCase)) return (r, arg);
            if (p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return (r, Uri.UnescapeDataString(p[(prefix.Length + 1)..]));
            }
        }
        return null;
    }

    public static string? QueryValue(string? query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            }
        }
        return null;
    }
}

/// <summary>A page component registered for a legacy form class by a slice page library.</summary>
public sealed record PageRegistration(string FormClass, Type Component, string Slice, GuiRoute? Route);

/// <summary>
/// Extension point of the shell (STERP-WEB section 6): one page library per slice (<c>src/Web/STERP.Web.Pages.&lt;Slice&gt;</c>)
/// registers the components that replace its legacy forms. See docs/DEVELOPER-GUIDE.md section 10.
/// </summary>
public interface IPageModule
{
    /// <summary>Target application id of the slice ("STERP-ITM"); its pages open only while the API hosts deliver it.</summary>
    string Slice { get; }

    void Register(PageRegistryBuilder pages);
}

public sealed class PageRegistryBuilder
{
    private readonly List<PageRegistration> _pages = [];
    private string _slice = "";

    internal void Begin(string slice) => _slice = slice;

    /// <summary>Registers <typeparamref name="TPage"/> for the legacy form class (must be a row of STERP-WEB 7.2).</summary>
    public PageRegistryBuilder Add<TPage>(string formClass) where TPage : IComponent => Add(formClass, typeof(TPage));

    public PageRegistryBuilder Add(string formClass, Type component)
    {
        var route = GuiRoutes.Find(formClass);
        _pages.Add(new PageRegistration(formClass, component, _slice, route));
        return this;
    }

    internal IReadOnlyList<PageRegistration> Build() => _pages;
}

/// <summary>The composed page registry: legacy FormClass -> page component (frmMain.OpenForm's reflection lookup).</summary>
public sealed class PageRegistry
{
    private readonly Dictionary<string, PageRegistration> _pages;

    public PageRegistry(IEnumerable<IPageModule> modules)
    {
        var builder = new PageRegistryBuilder();
        foreach (var m in modules)
        {
            builder.Begin(m.Slice);
            m.Register(builder);
        }
        _pages = builder.Build().ToDictionary(p => p.FormClass, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<PageRegistration> Pages => _pages.Values;

    public PageRegistration? Find(string formClass) => _pages.TryGetValue(formClass.Trim(), out var p) ? p : null;
}
