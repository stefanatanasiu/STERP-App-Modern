using System.Text.Json.Nodes;
using Sterp.Modules.Bom.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Bom.Application;

/// <summary>
/// BomService.Validate (dotnet/STERP.Business/Bom/BomService.vb:311-340) and BomBulkService.Preview
/// (dotnet/STERP.Business/Bom/BomBulkService.vb:16-63): the COMMAREA filled as the facade did, the LINK with the gateway's
/// header reset, and the facade's reading of the outcome.
/// </summary>
internal sealed class BomChangeFacade(BillValidator validator, BulkChangeEngine bulk)
{
    /// <summary>
    /// BomService.Validate with record = false as the editor calls it: BV-BOM-ID is never set (FR-STERP-BOM-016). The
    /// facade copies outcome, counts, release flag and cycle path, then returns before reading findings when RC &gt; 4.
    /// </summary>
    public async Task<(LegacyResult Outcome, JsonObject? Data)> ValidateAsync(EngineHeader h, string bomRef, string revision,
        int effectiveDate, CancellationToken ct)
    {
        var ca = new ValidationArea
        {
            Function = "CHECK",
            Company = h.Company,
            Site = h.Site,
            User = h.User,
            BusinessDate = h.BusinessDate,
            BomRef = CobolText.Trimmed(bomRef, 30),
            BomRev = CobolText.Trimmed(revision, 4),
            EffDate = effectiveDate,
            FindMax = 50,
        };
        await validator.CallAsync(ca, ct);
        var r = new LegacyResult(ca.ReturnCode, ca.Reason, ca.Message, ca.MessageCount);
        var findings = new JsonArray();
        var data = new JsonObject
        {
            ["outcome"] = ca.Outcome,
            ["errorCount"] = ca.ErrorCount,
            ["warningCount"] = ca.WarningCount,
            ["releaseAllowed"] = ca.ReleaseOk == "Y",
            ["cyclePath"] = ca.CyclePath,
            ["findings"] = findings,
        };
        if (!r.Succeeded) return (r, r.ReturnCode == 8 ? data : null);
        foreach (var f in ca.Findings)
        {
            findings.Add(new JsonObject
            {
                ["severity"] = f.Severity,
                ["reason"] = f.Reason,
                ["lineNum"] = f.LineNum,
                ["item"] = f.Item,
                ["path"] = f.Path,
                ["message"] = f.Message,
            });
        }
        return (r, data);
    }

    /// <summary>
    /// BomBulkService.Preview: action REPLACE, from-line 1, window 25, function PREVIEW; counts, may-apply, summary and
    /// refusal are read, then every outcome line through GETLINE pages even when the preview is refused. The outcome is
    /// the PREVIEW call's unless a GETLINE page fails.
    /// </summary>
    public async Task<(LegacyResult Outcome, JsonObject Data)> PreviewBulkChangeAsync(EngineHeader h, string changeRef, string oldItem,
        string newItem, int effectiveDate, string scope, string scopeItem, bool includeDraft, CancellationToken ct)
    {
        var ca = new BulkArea
        {
            ChangeRef = CobolText.Trimmed(changeRef, 30),
            Action = "REPLACE",
            Scope = CobolText.Trimmed(scope, 10),
            ScopeItem = CobolText.Trimmed(scopeItem, 40),
            OldItem = CobolText.Trimmed(oldItem, 40),
            NewItem = CobolText.Trimmed(newItem, 40),
            EffDate = effectiveDate,
            IncludeDraft = includeDraft ? "Y" : "N",
            FromLine = 1,
            WinMax = 25,
        };
        var result = await LinkBulkAsync(h, ca, "PREVIEW", ct);
        var lines = new JsonArray();
        var data = new JsonObject
        {
            ["targetCount"] = ca.TargetCount,
            ["failedCount"] = ca.FailedCount,
            ["appliedCount"] = ca.AppliedCount,
            ["mayApply"] = ca.MayApply == "Y",
            ["summary"] = ca.Summary,
            ["refusal"] = ca.Refusal,
            ["lines"] = lines,
        };
        // a refused proposal still carries all per-bill outcomes
        var guard = 0;
        while (guard++ < 100)
        {
            for (var i = 0; i < ca.WinCnt; i++)
            {
                var l = ca.Lines[i];
                lines.Add(new JsonObject
                {
                    ["bill"] = l.BomRef,
                    ["revision"] = l.BomRev,
                    ["proposedRevision"] = l.NewRev,
                    ["parent"] = l.Parent,
                    ["treatment"] = l.Treatment,
                    ["outcome"] = l.Outcome,
                    ["reason"] = l.Reason,
                    ["detail"] = l.Detail,
                    ["line"] = l.LineNum,
                    ["beforeQty"] = CobolNumeric.WithScale(l.OldQty, 6),
                    ["afterQty"] = CobolNumeric.WithScale(l.NewQty, 6),
                });
            }
            if (ca.MoreLines != "Y") break;
            var page = await LinkBulkAsync(h, ca, "GETLINE", ct);
            if (!page.Succeeded) return (page, data);
        }
        return (result, data);
    }

    private async Task<LegacyResult> LinkBulkAsync(EngineHeader h, BulkArea ca, string fn, CancellationToken ct)
    {
        ca.Function = fn;
        ca.Company = h.Company;
        ca.Site = h.Site;
        ca.User = h.User;
        ca.BusinessDate = h.BusinessDate;
        ca.CorrelationId = h.CorrelationId;
        ca.ReturnCode = 0;
        ca.Reason = "";
        ca.Message = "";
        ca.MessageCount = 0;
        await bulk.CallAsync(ca, ct);
        return new LegacyResult(ca.ReturnCode, ca.Reason, ca.Message, ca.MessageCount);
    }
}
