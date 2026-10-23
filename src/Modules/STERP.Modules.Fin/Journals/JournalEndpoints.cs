using Microsoft.AspNetCore.Http;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Fin.Journals;

public sealed class FinJournalLineInput
{
    public string? Account { get; set; }
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public string? CostCentre { get; set; }
    public string? Project { get; set; }
    public string? VatCode { get; set; }
    public string? Narrative { get; set; }
}

public sealed class FinJournalDraftRequest
{
    public string? JournalRef { get; set; }
    public string? JournalType { get; set; }
    public string? Description { get; set; }
    public string? Currency { get; set; }
    public string? EvidenceRef { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public DateOnly? PostingDate { get; set; }
    public List<FinJournalLineInput>? Lines { get; set; }
}

public sealed class FinJournalPostRequest
{
    public string? Description { get; set; }
    public string? SourceRef { get; set; }
    public string? JournalType { get; set; }
    public string? Currency { get; set; }
    public string? EvidenceRef { get; set; }
    public DateOnly? PostingDate { get; set; }
}

public sealed class FinJournalReverseRequest
{
    public string? Reason { get; set; }
}

/// <summary>Journal preparation, posting and reversal (JournalService.Prepare / Post / Reverse).</summary>
public static class JournalEndpoints
{
    /// <summary>fin_validateJournalDraft: JournalService.Prepare("VALIDAT") -> JNLENTRY.</summary>
    public static Task<IResult> ValidateDraftAsync(OperationContext ctx) => PrepareAsync(ctx, "VALIDAT");

    /// <summary>fin_saveJournalDraft: JournalService.Prepare("SAVE") -> JNLENTRY.</summary>
    public static Task<IResult> SaveDraftAsync(OperationContext ctx) => PrepareAsync(ctx, "SAVE");

    /// <summary>JournalService.Prepare (JournalService.vb:50-81): header defaults of the facade, at most 50 lines.</summary>
    private static async Task<IResult> PrepareAsync(OperationContext ctx, string function)
    {
        var body = await ctx.BodyAsAsync<FinJournalDraftRequest>();
        var s = ctx.Session;
        s.LegacyProgram = "JNLENTRY";
        var je = new JnlEntryArea
        {
            JournalRef = In.X(Facade.S(body.JournalRef), 30),
            JournalType = In.X(Facade.Dflt(body.JournalType, "GL"), 12),
            Description = In.X(Facade.S(body.Description), 200),
            SourceModule = "FIN",
            Currency = In.X(Facade.Dflt(body.Currency, s.CompanyAccess?.BaseCurrency), 3),
            EvidenceRef = In.X(Facade.S(body.EvidenceRef), 120),
            PreparedBy = In.X(s.UserCode, 20),
            DocumentDate = CobolDate.ToYyyymmdd(body.DocumentDate ?? s.BusinessDate),
            PostingDate = CobolDate.ToYyyymmdd(body.PostingDate ?? s.BusinessDate),
        };
        var lines = body.Lines ?? [];
        for (var i = 0; i < Math.Min(lines.Count, 50); i++)
        {
            var l = lines[i];
            je.Lines.Add(new JnlEntryLine
            {
                Num = i + 1,
                Account = In.X(Facade.Str(l.Account), 12),
                Debit = In.N(l.Debit, 11, 2),
                Credit = In.N(l.Credit, 11, 2),
                DimCc = In.X(Facade.Str(l.CostCentre), 20),
                DimProj = In.X(Facade.Str(l.Project), 20),
                VatCode = In.X(Facade.Str(l.VatCode), 4),
                Narrative = In.X(Facade.Str(l.Narrative), 80),
            });
        }
        var ca = await ctx.Get<JournalEntryProgram>().RunAsync(CaRequest.From(s, function), je, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), je.ToJson());
    }

    /// <summary>fin_postJournal: JournalService.Post (JournalService.vb:84-94) -> JNLPOST POST (no document date).</summary>
    public static async Task<IResult> PostAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinJournalPostRequest>();
        var s = ctx.Session;
        s.LegacyProgram = "JNLPOST";
        var jp = new JnlPostArea
        {
            Description = In.X(Facade.S(body.Description), 200),
            SourceRef = In.X(Facade.S(body.SourceRef), 40),
            JournalType = In.X(Facade.Dflt(body.JournalType, "GL"), 12),
            Currency = In.X(Facade.Dflt(body.Currency, s.CompanyAccess?.BaseCurrency), 3),
            EvidenceRef = In.X(Facade.S(body.EvidenceRef), 120),
            PostingDate = CobolDate.ToYyyymmdd(body.PostingDate ?? s.BusinessDate),
        };
        var ca = await ctx.Get<JournalPostingProgram>().RunAsync(CaRequest.From(s, "POST"), jp, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), jp.PostJson());
    }

    /// <summary>fin_reverseJournal: JournalService.Reverse(number, 100, reason) (JournalService.vb:97-103) -> JNLPOST REVERSE.</summary>
    public static async Task<IResult> ReverseAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinJournalReverseRequest>();
        var s = ctx.Session;
        s.LegacyProgram = "JNLPOST";
        var jp = new JnlPostArea
        {
            JournalNumber = In.X(ctx.Route("journalNumber"), 30),
            ReversePct = In.N(100m, 5, 4),
            Reason = In.X(body.Reason, 200),
        };
        var ca = await ctx.Get<JournalPostingProgram>().RunAsync(CaRequest.From(s, "REVERSE"), jp, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), jp.ReverseJson());
    }
}

/// <summary>Small helpers of the VB facades (JournalService.S/Dflt, Fmt.Str).</summary>
public static class Facade
{
    /// <summary>JournalService.S: missing -> "", otherwise trimmed on both sides.</summary>
    public static string S(string? v) => (v ?? "").Trim();

    /// <summary>JournalService.Dflt: the trimmed value, or the spare when blank.</summary>
    public static string Dflt(string? v, string? spare) => S(v).Length == 0 ? spare ?? "" : S(v);

    /// <summary>Fmt.Str: null -> "", trailing spaces trimmed.</summary>
    public static string Str(string? v) => (v ?? "").TrimEnd();
}
