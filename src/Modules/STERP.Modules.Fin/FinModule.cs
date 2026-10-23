using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Modules.Fin.Bank;
using Sterp.Modules.Fin.Journals;
using Sterp.Modules.Fin.Vat;
using Sterp.Platform.Hosting;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Hosting.Pipeline;

namespace Sterp.Modules.Fin;

/// <summary>STERP-FIN (W5): Finance: journals, posting, period close, VAT returns, bank reconciliation, reports and streams. Specification: application-specs/STERP-FIN.md.</summary>
public sealed class FinModule : ISterpModule
{
    public string Slice => "STERP-FIN";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Common.IFinKernel, Common.FinKernel>();
        services.AddScoped<GlValidator>();
        services.AddScoped<BankReconciliation>();
        services.AddScoped<BankMatchProgram>();
        services.AddScoped<VatDetermination>();
        services.AddScoped<VatReturnProgram>();
        services.AddScoped<VatErrorProgram>();
        services.AddScoped<JournalEntryProgram>();
        services.AddScoped<JournalPostingProgram>();
        services.AddScoped<Periods.PeriodCloseProgram>();
        services.AddScoped<Batch.AccrualRelease>();
        services.AddScoped<Batch.FxRevaluation>();
    }

    public void MapOperations(OperationRegistry operations)
    {
        operations.Handle("fin_validateJournalLine", JournalReadEndpoints.ValidateLineAsync);
        operations.Handle("fin_validateJournalDraft", JournalEndpoints.ValidateDraftAsync);
        operations.Handle("fin_saveJournalDraft", JournalEndpoints.SaveDraftAsync);
        operations.Handle("fin_postJournal", JournalEndpoints.PostAsync);
        operations.Handle("fin_listJournals", JournalReadEndpoints.ListJournalsAsync);
        operations.Handle("fin_listJournalLines", JournalReadEndpoints.ListJournalLinesAsync);
        operations.Handle("fin_reverseJournal", JournalEndpoints.ReverseAsync);

        operations.Handle("fin_getPeriodCloseStatus", Periods.PeriodEndpoints.StatusAsync);
        operations.Handle("fin_getCloseChecklist", Periods.PeriodEndpoints.ChecklistAsync);
        operations.Handle("fin_completeCloseStep", Periods.PeriodEndpoints.CompleteStepAsync);
        operations.Handle("fin_softClosePeriod", Periods.PeriodEndpoints.SoftCloseAsync);
        operations.Handle("fin_hardClosePeriod", Periods.PeriodEndpoints.HardCloseAsync);
        operations.Handle("fin_reopenPeriod", Periods.PeriodEndpoints.ReopenAsync);

        operations.Handle("fin_listBankAccounts", BankReadEndpoints.ListAccountsAsync);
        operations.Handle("fin_listBankStatementLines", BankReadEndpoints.ListStatementLinesAsync);
        operations.Handle("fin_listCashBookLines", BankReadEndpoints.ListCashBookLinesAsync);
        operations.Handle("fin_getBankBridge", BankReadEndpoints.GetBridgeAsync);
        operations.Handle("fin_matchBankEntries", BankMatchEndpoints.MatchAsync);
        operations.Handle("fin_unmatchBankGroup", BankMatchEndpoints.UnmatchAsync);

        operations.Handle("fin_listVatPeriods", VatReadEndpoints.ListPeriodsAsync);
        operations.Handle("fin_getVatReturnAuthorities", VatReadEndpoints.GetAuthoritiesAsync);
        operations.Handle("fin_prepareVatReturn", VatEndpoints.PrepareAsync);
        operations.Handle("fin_getSavedVatReturn", VatReadEndpoints.GetSavedReturnAsync);
        operations.Handle("fin_drillVatBox", VatEndpoints.DrillAsync);
        operations.Handle("fin_finaliseVatReturn", VatEndpoints.FinaliseAsync);
        operations.Handle("fin_writeVatLocalFile", VatEndpoints.WriteLocalFileAsync);
        operations.Handle("fin_getVatRate", VatReadEndpoints.GetRateAsync);
        operations.Handle("fin_assessVatError", VatEndpoints.AssessErrorAsync);
    }

    public void MapJobs(JobCatalog jobs)
    {
        foreach (var d in Batch.FinJobs.All) jobs.Add(d);
        foreach (var d in Batch.FinStreams.All) jobs.Add(d);
    }
}
