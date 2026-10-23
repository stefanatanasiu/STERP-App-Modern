using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Kernel.Tests;

/// <summary>
/// Kernel services on the isolated database ((localdb)\STERPA08_kernel, fresh dataset). Each test runs on its own unit of
/// work; tests that need a unit begin one and it is rolled back when the scope ends, tests of autocommit effects delete
/// what they committed.
/// </summary>
[Collection(LocalDbCollection.Name)]
public sealed class KernelDatabaseTests(LocalDbFixture db) : IAsyncLifetime
{
    private ServiceProvider _root = null!;
    private AsyncServiceScope _scope;

    public ValueTask InitializeAsync()
    {
        _root = db.Services();
        _scope = _root.CreateAsyncScope();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
    }

    private T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    private Task BeginAsync() => Get<IUnitOfWork>().BeginAsync();

    /// <summary>A scalar on the test's own unit of work (sees its uncommitted work).</summary>
    private async Task<T?> UowScalarAsync<T>(string sql, object? param = null) => await Get<ISqlRunner>().ScalarAsync<T>(sql, param);

    private Task<int> UowExecAsync(string sql, object? param = null) => Get<ISqlRunner>().ExecuteAsync(sql, param);

    // ---------------------------------------------------------------- PERMCHK (FR-STERP-PLATFORM-012)

    [Fact]
    public async Task Permchk_refusal_is_a_hold_with_the_procedure_reason()
    {
        var a = await Get<IPermissionService>().CheckAsync(Callers.For("STC", "ENGINEER"), "bom.release");
        Assert.Equal((6, "PRM0006", "N", 1), (a.Header.ReturnCode, a.Header.ReasonCode, a.Allowed, a.Header.MessageCount));
        Assert.StartsWith("STERP-E-0001: User ENGINEER does not hold permission BOM.RELEASE for company STC", a.Header.Message);
        Assert.Equal(a.Header.Message, a.ReasonText);
        Assert.Equal("BOM.RELEASE", a.Permission);
        Assert.Equal(("PERMCHK", "COM-002"), (a.Header.Program, a.Header.RequestRef));
    }

    [Fact]
    public async Task Permchk_allowed_answers_holds()
    {
        var a = await Get<IPermissionService>().CheckAsync(Callers.For("STC", "SYSADM"), "SYS.ADMIN");
        Assert.Equal((0, "", "Y", "SYSADM holds SYS.ADMIN in STC", "Permitted", 0),
            (a.Header.ReturnCode, a.Header.ReasonCode, a.Allowed, a.Header.Message, a.ReasonText, a.Header.MessageCount));
    }

    [Fact]
    public async Task Permchk_validation_needs_no_database()
    {
        var svc = new PermissionService(new NoSqlRunner());
        var co = await svc.CheckAsync(Callers.For("", "SYSADM"), "SYS.ADMIN");
        Assert.Equal((8, "PRM0003", "Authority is always scoped to a company", "N"), (co.Header.ReturnCode, co.Header.ReasonCode, co.Header.Message, co.Allowed));
        Assert.Equal("PRM0001", (await svc.CheckAsync(Callers.For("STC", ""), "SYS.ADMIN")).Header.ReasonCode);
        Assert.Equal("PRM0002", (await svc.CheckAsync(Callers.For("STC", "SYSADM"), "  ")).Header.ReasonCode);
        var list = new PermchkArea { Permission = "SYS.ADMIN" };
        list.Header.Prepare(Callers.For("STC", "SYSADM"), "LIST");
        await svc.ExecuteAsync(list);
        Assert.Equal((8, "PRM0009", "PERMCHK does not support function LIST"), (list.Header.ReturnCode, list.Header.ReasonCode, list.Header.Message));
    }

    [Fact]
    public async Task Permchk_checks_another_user_and_defaults_the_site()
    {
        var a = await Get<IPermissionService>().CheckAsync(Callers.For("STC", "SYSADM", site: "BHM"), "FIN.PERIOD.SOFTPOST", checkUser: "glclerk");
        Assert.Equal((6, "glclerk", "BHM"), (a.Header.ReturnCode, a.CheckUser, a.CheckSite));
        Assert.Contains(" site BHM.", a.ReasonText);
    }

    // ---------------------------------------------------------------- PERIODCK (FR-STERP-PLATFORM-013)

    [Fact]
    public async Task Periodck_hard_closed_period_is_refused()
    {
        var a = await Get<IPeriodService>().CheckAsync(Callers.For("STC", "GLCLERK"), 20260515);
        Assert.Equal((8, "PER0003", "N", "HARD", "202602", 2026, 2),
            (a.Header.ReturnCode, a.Header.ReasonCode, a.Allowed, a.PeriodStatus, a.PeriodKey, a.FiscalYear, a.PeriodNo));
        Assert.Equal("Period 202602 is hard closed - post the correction in the current open period or ask the financial controller to reopen it", a.Header.Message);
        Assert.Equal(a.Header.Message, a.ReasonText);
        Assert.Equal("FIN.PERIOD.SOFTPOST", a.OverridePermission);
    }

    [Fact]
    public async Task Periodck_open_period_is_allowed()
    {
        var a = await Get<IPeriodService>().CheckAsync(Callers.For("STC", "GLCLERK"), 20261019, action: "reverse");
        Assert.Equal((0, "Y", 2026, 7, "OPEN", "REVERSE"), (a.Header.ReturnCode, a.Allowed, a.FiscalYear, a.PeriodNo, a.PeriodStatus, a.Action));
        Assert.Equal("Posting date 20261019 is in period 202607 (OPEN)", a.Header.Message);
    }

    [Fact]
    public async Task Periodck_soft_closed_period_needs_the_close_adjustment_permission()
    {
        var clerk = await Get<IPeriodService>().CheckAsync(Callers.For("STC", "GLCLERK"), 20260915);
        Assert.Equal((6, "PER0006", "N", "FIN.PERIOD.SOFTPOST", 1), (clerk.Header.ReturnCode, clerk.Header.ReasonCode, clerk.Allowed, clerk.OverridePermission, clerk.Header.MessageCount));
        Assert.Equal("Period 202606 is soft closed - only an authorised close adjustment may be posted into it", clerk.Header.Message);
        var controller = await Get<IPeriodService>().CheckAsync(Callers.For("STC", "FINCTRL"), 20260915, function: "DATES");
        Assert.Equal((0, "Y"), (controller.Header.ReturnCode, controller.Allowed));
        Assert.Equal("Posting date 20260915 is in period 202606 (SOFT) - posted as a close adjustment", controller.Header.Message);
        var future = await Get<IPeriodService>().CheckAsync(Callers.For("STC", "FINCTRL"), 20261215);
        Assert.Equal(("PER0004", "Period 202609 is not yet open for posting"), (future.Header.ReasonCode, future.Header.Message));
    }

    [Fact]
    public async Task Periodck_zero_posting_date_takes_the_business_date()
    {
        var a = await Get<IPeriodService>().CheckAsync(Callers.For("STC", "GLCLERK", businessDate: 20261007), 0);
        Assert.Equal((0, 20261007, 7), (a.Header.ReturnCode, a.PostingDate, a.PeriodNo));
        var svc = new PeriodService(new NoSqlRunner());
        var noDate = await svc.CheckAsync(Callers.For("STC", "GLCLERK", businessDate: 0), 0);
        Assert.Equal(("PER0002", "The posting date is not a valid calendar date"), (noDate.Header.ReasonCode, noDate.Header.Message));
        Assert.Equal("PER0003", (await svc.CheckAsync(Callers.For("STC", "GLCLERK"), 20261019, documentDate: 20261399)).Header.ReasonCode);
        Assert.Equal("PER0001", (await svc.CheckAsync(Callers.For("", "GLCLERK"), 20261019)).Header.ReasonCode);
        Assert.Equal("PER0009", (await svc.CheckAsync(Callers.For("STC", "GLCLERK"), 20261019, function: "STATUS")).Header.ReasonCode);
        var none = await Get<IPeriodService>().CheckAsync(Callers.For("STC", "GLCLERK"), 20300101);
        Assert.Equal(("PER0005", "No financial period covers that posting date"), (none.Header.ReasonCode, none.Header.Message));
    }

    // ---------------------------------------------------------------- NUMGEN (FR-STERP-PLATFORM-014)

    [Fact]
    public async Task Numgen_next_outside_a_unit_draws_and_commits_at_once()
    {
        var before = await db.ScalarAsync<long>("SELECT NextNumber FROM core.NumberSeries WHERE CompanyCode='STC' AND SeriesCode='PREQ'");
        string? number = null;
        try
        {
            var a = await Get<IDocumentNumberService>().NextAsync(Callers.For("STC", "BUYER"), "preq");
            number = a.DocumentNumber;
            Assert.Equal((0, "ISSUED", "PREQ", before), (a.Header.ReturnCode, a.Status, a.SeriesCode, a.Sequence));
            Assert.Equal("PRS" + before.ToString("D8", System.Globalization.CultureInfo.InvariantCulture), a.DocumentNumber);
            Assert.Equal($"Reference {a.DocumentNumber} issued from series PREQ", a.Header.Message);
            // committed by the procedure's own transaction: visible on another connection
            Assert.Equal(before + 1, await db.ScalarAsync<long>("SELECT NextNumber FROM core.NumberSeries WHERE CompanyCode='STC' AND SeriesCode='PREQ'"));
            Assert.Equal("BUYER", await db.ScalarAsync<string>("SELECT IssuedBy FROM core.DocumentNumber WHERE CompanyCode='STC' AND SeriesCode='PREQ' AND DocumentNumber=@n", ("@n", number)));
            Assert.Null(await db.ScalarAsync<string>("SELECT CorrelationId FROM core.DocumentNumber WHERE CompanyCode='STC' AND SeriesCode='PREQ' AND DocumentNumber=@n", ("@n", number)));
        }
        finally
        {
            await db.ExecuteAsync("DELETE core.DocumentNumber WHERE CompanyCode='STC' AND SeriesCode='PREQ' AND SequenceNo >= @b", ("@b", before));
            await db.ExecuteAsync("UPDATE core.NumberSeries SET NextNumber=@b, ModifiedBy=NULL, ModifiedAt=NULL WHERE CompanyCode='STC' AND SeriesCode='PREQ'", ("@b", before));
        }
    }

    [Fact]
    public async Task Numgen_replays_the_number_already_drawn_for_the_correlation_id()
    {
        await BeginAsync();
        var caller = Callers.For("STC", "BUYER", correlation: Guid.NewGuid().ToString());
        var first = await Get<IDocumentNumberService>().NextAsync(caller, "PREQ");
        var next = await UowScalarAsync<long>("SELECT NextNumber FROM core.NumberSeries WHERE CompanyCode='STC' AND SeriesCode='PREQ'");
        var again = await Get<IDocumentNumberService>().NextAsync(caller, "PREQ");
        Assert.Equal((4, "NUM0007", first.DocumentNumber, first.Sequence, "ISSUED", 1),
            (again.Header.ReturnCode, again.Header.ReasonCode, again.DocumentNumber, again.Sequence, again.Status, again.Header.MessageCount));
        Assert.Equal($"Reference {first.DocumentNumber} was already issued for this instruction", again.Header.Message);
        Assert.Equal(next, await UowScalarAsync<long>("SELECT NextNumber FROM core.NumberSeries WHERE CompanyCode='STC' AND SeriesCode='PREQ'"));
    }

    [Fact]
    public async Task Numgen_exhausted_series_keeps_the_prefixed_driver_text()
    {
        // FR-010 AC4 / FR-014 AC3: STERP-E-0004 behind the ODBC prefix is SQL0001 RC 12; the THROW (XACT_ABORT) ends the unit
        await BeginAsync();
        await UowExecAsync("UPDATE core.NumberSeries SET NextNumber = MaxNumber + 1 WHERE CompanyCode='STC' AND SeriesCode='SDES'");
        var a = await Get<IDocumentNumberService>().NextAsync(Callers.For("STC", "SALESREP"), "SDES");
        Assert.Equal((12, "SQL0001", 1), (a.Header.ReturnCode, a.Header.ReasonCode, a.Header.MessageCount));
        Assert.Equal("[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]STERP-E-0004: The number series is exhausted. Extend its upper limit before confirming more documents.", a.Header.Message);
        // the unit was rolled back by the server; the engine connection carries on in manual-commit mode
        Assert.Equal(0, Get<IUnitOfWork>().TransactionState);
        Assert.True(await UowScalarAsync<long>("SELECT NextNumber FROM core.NumberSeries WHERE CompanyCode='STC' AND SeriesCode='SDES'") <= 99999999);
        await Get<IUnitOfWork>().RollbackAsync();
    }

    [Fact]
    public async Task Numgen_with_an_empty_driver_prefix_classifies_the_refusal_as_NUM0008()
    {
        // FR-010 AC5: the characterization configuration Sterp:Kernel:LegacyDriverMessagePrefix = ""
        await using var root = db.Services(driverPrefix: "");
        await using var scope = root.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync();
        await scope.ServiceProvider.GetRequiredService<ISqlRunner>().ExecuteAsync(
            "UPDATE core.NumberSeries SET NextNumber = MaxNumber + 1 WHERE CompanyCode='STC' AND SeriesCode='SDES'");
        var a = await scope.ServiceProvider.GetRequiredService<IDocumentNumberService>().NextAsync(Callers.For("STC", "SALESREP"), "SDES");
        Assert.Equal((8, "NUM0008"), (a.Header.ReturnCode, a.Header.ReasonCode));
        Assert.StartsWith("STERP-E-0004: The number series is exhausted.", a.Header.Message);
    }

    [Fact]
    public async Task Numgen_void_requires_number_and_reason_and_voids_through_the_procedure()
    {
        var svc = new DocumentNumberService(new NoSqlRunner());
        var noReason = await svc.VoidAsync(Callers.For("STC", "BUYER"), "PREQ", "PRS00000001", " ");
        Assert.Equal((8, "NUM0004", "A reason is required to void a reference"), (noReason.Header.ReturnCode, noReason.Header.ReasonCode, noReason.Header.Message));
        Assert.Equal("NUM0003", (await svc.VoidAsync(Callers.For("STC", "BUYER"), "PREQ", "", "x")).Header.ReasonCode);
        Assert.Equal("NUM0001", (await svc.NextAsync(Callers.For("", "BUYER"), "PREQ")).Header.ReasonCode);
        Assert.Equal("NUM0002", (await svc.NextAsync(Callers.For("STC", "BUYER"), "")).Header.ReasonCode);

        await BeginAsync();
        var drawn = await Get<IDocumentNumberService>().NextAsync(Callers.For("STC", "BUYER"), "PREQ");
        var v = await Get<IDocumentNumberService>().VoidAsync(Callers.For("STC", "BUYER"), "PREQ", drawn.DocumentNumber, "Keyed twice");
        Assert.Equal((0, "VOID"), (v.Header.ReturnCode, v.Status));
        Assert.Equal($"Reference {drawn.DocumentNumber} voided and retained - it will not be reissued", v.Header.Message);
    }

    // ---------------------------------------------------------------- IDEMPOT (FR-STERP-PLATFORM-015)

    [Fact]
    public async Task Idempot_claim_commits_under_autocommit_and_survives_a_later_rollback()
    {
        var key = "KT-" + Guid.NewGuid().ToString("N")[..20];
        try
        {
            var a = await Get<IIdempotencyService>().BeginAsync(Callers.For("STC", "ARCLERK", correlation: key), "RCPTALOC");
            Assert.Equal((0, "STARTED", "Instruction claimed - carry on with RCPTALOC"), (a.Header.ReturnCode, a.Outcome, a.Header.Message));
            await BeginAsync();
            await UowExecAsync("UPDATE core.IdempotencyKey SET ResultMessage = 'in unit' WHERE CorrelationId = @key", new { key });
            await Get<IUnitOfWork>().RollbackAsync();
            Assert.Equal("INPROGRESS", await db.ScalarAsync<string>("SELECT Status FROM core.IdempotencyKey WHERE CorrelationId=@k", ("@k", key)));
            var again = await Get<IIdempotencyService>().BeginAsync(Callers.For("STC", "ARCLERK", correlation: key), "RCPTALOC");
            Assert.Equal((6, "IDM0007", "INPROGRESS", 1), (again.Header.ReturnCode, again.Header.ReasonCode, again.Outcome, again.Header.MessageCount));
            Assert.Equal("Another attempt at this instruction is still running - establish its outcome before starting a replacement", again.Header.Message);
        }
        finally
        {
            await db.ExecuteAsync("DELETE core.IdempotencyKey WHERE CorrelationId=@k", ("@k", key));
        }
    }

    [Fact]
    public async Task Idempot_failed_key_is_reset_and_completed_key_replays()
    {
        await BeginAsync();
        var failed = "KT-" + Guid.NewGuid().ToString("N")[..20];
        var done = "KT-" + Guid.NewGuid().ToString("N")[..20];
        await UowExecAsync("INSERT core.IdempotencyKey (CorrelationId, Operation, CompanyCode, UserCode, Status, ResultMessage) VALUES (@failed, 'X', 'STC', 'ARCLERK', 'FAILED', 'boom')", new { failed });
        await UowExecAsync("INSERT core.IdempotencyKey (CorrelationId, Operation, CompanyCode, UserCode, Status, ResultRef) VALUES (@done, 'X', 'STC', 'ARCLERK', 'COMPLETED', 'SINV00000042')", new { done });
        var reset = await Get<IIdempotencyService>().BeginAsync(Callers.For("STC", "ARCLERK", correlation: failed), "X");
        Assert.Equal((0, "STARTED"), (reset.Header.ReturnCode, reset.Outcome));
        var replay = await Get<IIdempotencyService>().BeginAsync(Callers.For("STC", "ARCLERK", correlation: done), "X");
        Assert.Equal((4, "IDM0006", "COMPLETED", "SINV00000042"), (replay.Header.ReturnCode, replay.Header.ReasonCode, replay.Outcome, replay.ResultRef));
        Assert.Equal("This instruction was already completed - the original result is SINV00000042", replay.Header.Message);
        var end = await Get<IIdempotencyService>().EndAsync(Callers.For("STC", "ARCLERK", correlation: failed), "completed", "RCS00000001", "ok");
        Assert.Equal((0, "COMPLETED", "RCS00000001", "Instruction recorded as COMPLETED"), (end.Header.ReturnCode, end.Outcome, end.ResultRef, end.Header.Message));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$", end.StartedAt);
        var svc = new IdempotencyService(new NoSqlRunner());
        Assert.Equal("IDM0001", (await svc.BeginAsync(Callers.For("STC", "ARCLERK"), "X")).Header.ReasonCode);
        Assert.Equal("IDM0002", (await svc.BeginAsync(Callers.For("STC", "ARCLERK", correlation: "k"), " ")).Header.ReasonCode);
        Assert.Equal("IDM0003", (await svc.EndAsync(Callers.For("STC", "ARCLERK", correlation: "k"), "DONE")).Header.ReasonCode);
        var never = await Get<IIdempotencyService>().EndAsync(Callers.For("STC", "ARCLERK", correlation: "KT-never-started"), "FAILED");
        Assert.Equal((12, "SQL0001"), (never.Header.ReturnCode, never.Header.ReasonCode));
        Assert.EndsWith("STERP-E-0016: That correlation id was never started.", never.Header.Message);
    }

    // ---------------------------------------------------------------- AUDITLOG (FR-STERP-PLATFORM-016)

    [Fact]
    public async Task Auditlog_write_inserts_one_row_with_source_cobol()
    {
        await BeginAsync();
        var a = await Get<IAuditWriter>().WriteAsync(Callers.For("STC", "SALESREP", correlation: "KT-AUDIT"),
            new AuditlogArea { EntityType = "so.SalesOrder", EntityKey = "SO00000021", Action = "accept", Sensitive = "x" });
        Assert.Equal((0, "Audited ACCEPT of so.SalesOrder SO00000021", "COBOL", "N"), (a.Header.ReturnCode, a.Header.Message, a.Source, a.Sensitive));
        Assert.True(a.AuditId > 0);
        var row = await Get<ISqlRunner>().QueryFirstOrDefaultAsync<(string Action, string Source, bool IsSensitive, string? FieldName, string? CorrelationId, string CompanyCode)>(
            "SELECT Action, Source, IsSensitive, FieldName, CorrelationId, CompanyCode FROM core.AuditLog WHERE AuditId = @id", new { id = a.AuditId });
        Assert.Equal(("ACCEPT", "COBOL", false, null, "KT-AUDIT", "STC"), row);
        var svc = new AuditWriter(new NoSqlRunner());
        Assert.Equal("AUD0001", (await svc.WriteAsync(Callers.For("STC", ""), new AuditlogArea { EntityType = "t", EntityKey = "k", Action = "a" })).Header.ReasonCode);
        Assert.Equal("AUD0002", (await svc.WriteAsync(Callers.For("STC", "U"), new AuditlogArea { EntityType = "t", Action = "a" })).Header.ReasonCode);
        Assert.Equal("AUD0003", (await svc.WriteAsync(Callers.For("STC", "U"), new AuditlogArea { EntityType = "t", EntityKey = "k" })).Header.ReasonCode);
    }

    // ---------------------------------------------------------------- EXCPRAIS (FR-STERP-PLATFORM-017)

    [Fact]
    public async Task Excprais_error_severity_answers_00_and_refreshes_the_open_item()
    {
        await BeginAsync();
        var body = new ExcpraisArea
        {
            Module = "kit", Severity = "error", Category = "KT-TEST", MessageText = "Kit could not be completed",
            DocumentType = "KIT", DocumentRef = "KT-0001", OwnerRole = "FINCTRL", Priority = 0, AmountGbp = 12.34m,
        };
        var first = await Get<IExceptionRaiser>().RaiseAsync(Callers.For("STC", "OPS"), body);
        Assert.Equal((0, "ERROR raised on worklist: Kit could not be completed", "OPEN", "KIT"),
            (first.Header.ReturnCode, first.Header.Message, first.Status, first.Module));
        Assert.True(first.ExceptionId > 0);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$", first.RaisedAt);
        var stored = await Get<ISqlRunner>().QueryFirstOrDefaultAsync<(byte Priority, string? OwnerUser, decimal? AmountGBP)>(
            "SELECT Priority, OwnerUser, AmountGBP FROM core.ExceptionItem WHERE ExceptionId = @id", new { id = first.ExceptionId });
        Assert.Equal(((byte)5, "FINCTRL", (decimal?)12.34m), (stored.Priority, stored.OwnerUser, stored.AmountGBP));

        var hold = new ExcpraisArea { Module = "KIT", Severity = "HOLD", Category = "KT-TEST", MessageText = "Again", DocumentType = "KIT", DocumentRef = "KT-0001", OwnerUser = "OPS", Priority = 2 };
        var second = await Get<IExceptionRaiser>().RaiseAsync(Callers.For("STC", "OPS"), hold);
        Assert.Equal((6, first.ExceptionId, 2), (second.Header.ReturnCode, second.ExceptionId, second.Priority));
        var svc = new ExceptionRaiser(new NoSqlRunner());
        var sev = await svc.RaiseAsync(Callers.For("STC", "OPS"), new ExcpraisArea { Severity = "info", Category = "c", MessageText = "m", OwnerUser = "u" });
        Assert.Equal(("EXC0002", "Severity must be HOLD, WARNING or ERROR, not INFO"), (sev.Header.ReasonCode, sev.Header.Message));
        Assert.Equal("EXC0005", (await svc.RaiseAsync(Callers.For("STC", "OPS"), new ExcpraisArea { Severity = "HOLD", Category = "c", MessageText = "m" })).Header.ReasonCode);
        Assert.Equal("EXC0006", (await svc.RaiseAsync(Callers.For("STC", "OPS"), new ExcpraisArea { Severity = "HOLD", Category = "c", MessageText = "m", OwnerUser = "u", DueAt = 20261301 })).Header.ReasonCode);
    }

    // ---------------------------------------------------------------- PARMGET (FR-STERP-PLATFORM-018)

    [Fact]
    public async Task Parmget_company_row_wins_over_the_all_company_row()
    {
        await BeginAsync();
        await UowExecAsync("INSERT core.Parameter (ParamCode, CompanyCode, EffectiveFrom, Value, DataType, Description, OwnerRole, DecisionRef) VALUES " +
                           "('KT.LATE.RATE', '*', '2026-01-01', '8.00', 'PCT', 'all', 'FINCTRL', 'D-07'), ('KT.LATE.RATE', 'STC', '2026-07-01', '9.50', 'PCT', 'stc', 'FINCTRL', 'D-07')");
        var a = await Get<IParameterService>().GetAsync(Callers.For("STC", "FINCTRL"), "kt.late.rate", 20261019);
        Assert.Equal((0, "Y", "9.50", 9.5m, "COMPANY", 20260701, "Y", "PCT", "stc", "D-07"),
            (a.Header.ReturnCode, a.Found, a.ValueText, a.ValueNum, a.Scope, a.EffectiveFrom, a.IsNumeric, a.DataType, a.Description, a.DecisionRef));
        Assert.Equal("KT.LATE.RATE = 9.50 (COMPANY)", a.Header.Message);
        var cal = await Get<IParameterService>().GetAsync(Callers.For("CAL", "FINCTRL"), "KT.LATE.RATE", 20261019);
        Assert.Equal(("ALL", "8.00"), (cal.Scope, cal.ValueText));
        var early = await Get<IParameterService>().GetAsync(Callers.For("STC", "FINCTRL"), "KT.LATE.RATE", 20260601);
        Assert.Equal(("ALL", 20260101), (early.Scope, early.EffectiveFrom));
    }

    [Fact]
    public async Task Parmget_default_and_getnum()
    {
        await BeginAsync();
        var d = await Get<IParameterService>().GetAsync(Callers.For("STC", "ARCLERK"), "KT.UNKNOWN.CODE", 20261019, defaultText: "30");
        Assert.Equal((4, "PRM0004", "30", 30m, "DEFAULT", 1, "N"), (d.Header.ReturnCode, d.Header.ReasonCode, d.ValueText, d.ValueNum, d.Scope, d.Header.MessageCount, d.Found));
        Assert.Equal("Parameter KT.UNKNOWN.CODE is not held - the supplied default was used", d.Header.Message);
        var missing = await Get<IParameterService>().GetAsync(Callers.For("STC", "ARCLERK"), "KT.UNKNOWN.CODE", 20261019);
        Assert.Equal((16, "PRM0003", "Parameter KT.UNKNOWN.CODE is not held for this company and date"), (missing.Header.ReturnCode, missing.Header.ReasonCode, missing.Header.Message));
        await UowExecAsync("INSERT core.Parameter (ParamCode, CompanyCode, EffectiveFrom, Value, DataType) VALUES ('KT.TEXT', '*', '2026-01-01', 'ABC', 'TEXT'), ('KT.ODD', '*', '2026-01-01', '1-2', 'TEXT')");
        var text = await Get<IParameterService>().GetAsync(Callers.For("STC", "ARCLERK"), "KT.TEXT", 20261019, numeric: true);
        Assert.Equal((8, "PRM0005", "Parameter KT.TEXT is not numeric - value is [ABC]", 0m), (text.Header.ReturnCode, text.Header.ReasonCode, text.Header.Message, text.ValueNum));
        var odd = await Get<IParameterService>().GetAsync(Callers.For("STC", "ARCLERK"), "KT.ODD", 20261019, numeric: true);
        // GnuCOBOL 3.2 NUMVAL('1-2') is -12 (runtime characterization, SME-039)
        Assert.Equal((0, "Y", -12m), (odd.Header.ReturnCode, odd.IsNumeric, odd.ValueNum));
        var svc = new ParameterService(new NoSqlRunner(), TimeProvider.System);
        Assert.Equal("PRM0001", (await svc.GetAsync(Callers.For("STC", "U"), " ")).Header.ReasonCode);
        Assert.Equal("PRM0002", (await svc.GetAsync(Callers.For("STC", "U"), "X", 20261399)).Header.ReasonCode);
    }

    // ---------------------------------------------------------------- DATECALC (FR-STERP-PLATFORM-019)

    [Fact]
    public async Task Datecalc_reads_holidays_from_the_database_and_bacs_uses_eng()
    {
        var cal = Get<IBusinessCalendar>();
        var xmas = await cal.IsBusinessDayAsync(20261225);
        Assert.Equal((0, "SQL", "Y"), (xmas.ResultNum, xmas.CalSource, xmas.IsHoliday));
        Assert.Equal(8, xmas.CalRows);
        var bacs = await cal.IsBusinessDayAsync(20271227, "bacs");
        Assert.Equal((0, "SQL", "BACS"), (bacs.ResultNum, bacs.CalSource, bacs.Calendar));
        var nir = await cal.IsBusinessDayAsync(20260317, "NIR");
        Assert.Equal((0, "SQL"), (nir.ResultNum, nir.CalSource));
        // no database rows for 2029: the built-in table is consulted and has none either
        var later = await cal.IsBusinessDayAsync(20291225);
        Assert.Equal((1, "BUILTIN", 0), (later.ResultNum, later.CalSource, later.CalRows));
    }

    [Fact]
    public async Task Datecalc_load_failure_falls_back_to_the_builtin_table_for_that_request_only()
    {
        // FR-019 AC5
        var failure = await CaptureFailureAsync();
        var cache = _root.GetRequiredService<ICalendarCache>();
        cache.Clear();
        var failing = new FailingSqlRunner(Get<ISqlRunner>(), sql => sql.Contains("core.CalendarDay", StringComparison.Ordinal), failure);
        var first = new BusinessCalendar(failing, cache);
        var a = await first.IsBusinessDayAsync(20271225);
        Assert.Equal((0, "BUILTIN", 0), (a.ResultNum, a.CalSource, a.Header.ReturnCode));
        Assert.Equal(1, failing.Failures);
        // the failure is not cached: the next request reads the database again
        await using var next = _root.CreateAsyncScope();
        var b = await next.ServiceProvider.GetRequiredService<IBusinessCalendar>().IsBusinessDayAsync(20271225);
        Assert.Equal((0, "SQL"), (b.ResultNum, b.CalSource));
    }

    [Fact]
    public async Task Datecalc_cache_is_keyed_by_the_database_identity_including_the_recovery_fork()
    {
        var cache = _root.GetRequiredService<ICalendarCache>();
        cache.Clear();
        await Get<IBusinessCalendar>().IsBusinessDayAsync(20261019);
        var key = await db.ScalarAsync<string>(
            "SELECT @@SERVERNAME + '|' + d.name + '|' + CONVERT(varchar(30), d.create_date, 126) + '|' + CONVERT(varchar(36), r.recovery_fork_guid) " +
            "FROM sys.databases d JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.name = 'STERP'");
        Assert.NotNull(key);
        Assert.True(cache.TryGet(key, "ENG", 2026, out var entry));
        Assert.Equal("SQL", entry.Source);
        // RESTORE ... WITH REPLACE keeps create_date but starts a new recovery fork: an earlier copy's entries are not reused
        var otherFork = key[..key.LastIndexOf('|')] + "|" + Guid.NewGuid().ToString("D").ToUpperInvariant();
        Assert.False(cache.TryGet(otherFork, "ENG", 2026, out _));
    }

    [Fact]
    public async Task Datecalc_without_a_visible_recovery_fork_loads_per_request_and_shares_nothing()
    {
        var cache = (CalendarCache)_root.GetRequiredService<ICalendarCache>();
        cache.Clear();
        var hidden = new RewritingSqlRunner(Get<ISqlRunner>(), sql => sql.Contains("sys.database_recovery_status", StringComparison.Ordinal)
            ? sql.Replace("CONVERT(varchar(36), r.recovery_fork_guid)", "CONVERT(varchar(36), NULL)", StringComparison.Ordinal)
            : sql);
        var cal = new BusinessCalendar(hidden, cache);
        var xmas = await cal.IsBusinessDayAsync(20261225);
        var boxing = await cal.IsBusinessDayAsync(20261228);
        Assert.Equal((0, "SQL", 0, "SQL"), (xmas.ResultNum, xmas.CalSource, boxing.ResultNum, boxing.CalSource));
        // loaded once for the request, never shared
        Assert.Single(hidden.Statements, s => s.Contains("core.CalendarDay WHERE", StringComparison.Ordinal));
        Assert.Equal(0, cache.Count);
    }

    // ---------------------------------------------------------------- TERMCALC (FR-STERP-PLATFORM-020)

    [Fact]
    public async Task Termcalc_unknown_terms_code_applies_the_statutory_default()
    {
        var terms = Get<IPaymentTermsCalculator>();
        var a = await terms.CallAsync(Callers.For("STC", "ARCLERK"), "DUEDATE", t => { t.TermsCode = "zzz"; t.InvoiceDate = 20261001; });
        Assert.Equal((4, "TRM0002", "STATUTORY", 20261031, 1), (a.Header.ReturnCode, a.Header.ReasonCode, a.Source, a.DueDate, a.Header.MessageCount));
        Assert.Equal("Payment terms ZZZ not found - the statutory default applies", a.Header.Message);
        var get = await terms.CallAsync(Callers.For("STC", "ARCLERK"), "GETTERMS", t => t.TermsCode = "ZZZ");
        Assert.Equal((16, "TRM0002", "Payment terms ZZZ are not held for this company"), (get.Header.ReturnCode, get.Header.ReasonCode, get.Header.Message));
        var dom = await terms.CallAsync(Callers.For("STC", "ARCLERK"), "GETTERMS", t => t.TermsCode = "dom31");
        Assert.Equal((0, "TERMS", "DAYOFNEXTMONTH", 31, "Day 31 of the following month"), (dom.Header.ReturnCode, dom.Source, dom.BasisType, dom.DayOfMonth, dom.TermsDescription));
        Assert.Equal("Due  - Day 31 of the following month", dom.Header.Message);
        var d2 = await terms.CallAsync(Callers.For("STC", "ARCLERK"), "DUEDATE", t => { t.TermsCode = "D2N30"; t.InvoiceDate = 20261001; t.NetAmount = 500m; });
        Assert.Equal((0, 20261031, 20261011, 10.00m, 2.0000m), (d2.Header.ReturnCode, d2.DueDate, d2.DiscountDate, d2.DiscountAmount, d2.DiscountPct));
        Assert.Equal("Due 31/10/2026 - 30 days from invoice date", d2.Header.Message);
    }

    [Fact]
    public async Task Termcalc_bacs_adjustment_keeps_the_raw_date()
    {
        // FR-020 AC3: due Saturday 2026-10-24 moves to Monday 2026-10-26
        var a = await Get<IPaymentTermsCalculator>().CallAsync(Callers.For("STC", "APCLERK"), "DUEDATE", t =>
        {
            t.BasisType = "INVOICE";
            t.Days = 30;
            t.InvoiceDate = 20260924;
            t.BacsAdjust = "Y";
        });
        Assert.Equal((4, "TRM0006", 20261026, 20261024, 32), (a.Header.ReturnCode, a.Header.ReasonCode, a.DueDate, a.DueDateRaw, a.TermDays));
        Assert.Equal("Moved 2 day(s) to the NEXT business day on calendar BACS", a.Header.Message);
        var lower = await Get<IPaymentTermsCalculator>().CallAsync(Callers.For("STC", "APCLERK"), "DUEDATE", t =>
        {
            t.BasisType = "INVOICE";
            t.Days = 30;
            t.InvoiceDate = 20260924;
            t.BacsAdjust = "y";
        });
        Assert.Equal((0, 20261024), (lower.Header.ReturnCode, lower.DueDate));
    }

    // ---------------------------------------------------------------- FXCONV (FR-STERP-PLATFORM-021)

    [Fact]
    public async Task Fxconv_getrate_reads_the_latest_rate_on_or_before_the_date()
    {
        var fx = Get<IFxConverter>();
        var none = await fx.CallAsync(Callers.For("STC", "FINCTRL"), "GETRATE", f => { f.Currency = "EUR"; f.RateDate = 20251231; });
        Assert.Equal((16, "FX0001", "No SPOT rate is held for EUR on or before 20251231", 1), (none.Header.ReturnCode, none.Header.ReasonCode, none.Header.Message, none.Header.MessageCount));
        var rate = await fx.CallAsync(Callers.For("STC", "FINCTRL"), "GETRATE", f => { f.Currency = "EUR"; f.RateDate = 20261019; });
        var expected = await db.ScalarAsync<decimal>("SELECT TOP 1 UnitsPerGBP FROM core.FxRate WHERE CurrencyCode='EUR' AND RateType='SPOT' AND RateDate <= '20261019' ORDER BY RateDate DESC");
        var found = await db.ScalarAsync<string>("SELECT TOP 1 CONVERT(char(8), RateDate, 112) FROM core.FxRate WHERE CurrencyCode='EUR' AND RateType='SPOT' AND RateDate <= '20261019' ORDER BY RateDate DESC");
        Assert.Equal(expected, rate.Rate);
        Assert.Equal(int.Parse(found!, System.Globalization.CultureInfo.InvariantCulture), rate.RateFoundDate);
        Assert.Equal(rate.RateFoundDate < 20261019 ? 4 : 0, rate.Header.ReturnCode);
        var togbp = await fx.CallAsync(Callers.For("STC", "FINCTRL"), "TOGBP", f => { f.Currency = "EUR"; f.RateDate = 20261019; f.ForeignAmount = 1000m; });
        Assert.Equal(Sterp.Platform.Kernel.Cobol.CobolNumeric.RoundAway(Sterp.Platform.Kernel.Cobol.CobolNumeric.Divide(1000m, expected, 13, 12), 2), togbp.GbpAmount);
    }

    // ---------------------------------------------------------------- JOBLOG (FR-STERP-PLATFORM-027)

    [Fact]
    public async Task Joblog_start_and_end_translate_the_status()
    {
        await BeginAsync();
        var jl = Get<IJobRunRegistry>();
        var start = await jl.StartAsync(Callers.For("STC", "GLCLERK"), "tbrpt", "CO=STC YEAR=2026");
        Assert.Equal((0, "TBRPT", "RUNNING"), (start.Header.ReturnCode, start.JobName, start.Status));
        Assert.True(start.JobRunId > 0);
        Assert.Equal($"Job TBRPT registered as run {start.JobRunId}", start.Header.Message);
        var row = await Get<ISqlRunner>().QueryFirstOrDefaultAsync<(string Status, string SubmittedBy, string Arguments, string? SiteCode)>(
            "SELECT Status, SubmittedBy, Arguments, SiteCode FROM core.JobRun WHERE JobRunId = @id", new { id = start.JobRunId });
        Assert.Equal(("RUNNING", "GLCLERK", "CO=STC YEAR=2026", null), row);
        var end = await jl.EndAsync(Callers.For("STC", "GLCLERK"), "TBRPT", start.JobRunId, "complete", 0, 12, spoolFile: @"C:\run\spool\TBRPT.lst");
        Assert.Equal((0, "COMPLETE", "Job TBRPT ended COMPLETE"), (end.Header.ReturnCode, end.Status, end.Header.Message));
        Assert.Equal("OK", await UowScalarAsync<string>("SELECT Status FROM core.JobRun WHERE JobRunId = @id", new { id = start.JobRunId }));
        var cancel = await jl.EndAsync(Callers.For("STC", "GLCLERK"), "TBRPT", start.JobRunId, "CANCELLED", 16);
        Assert.Equal("CANCELLED", cancel.Status);
        Assert.Equal("ABENDED", await UowScalarAsync<string>("SELECT Status FROM core.JobRun WHERE JobRunId = @id", new { id = start.JobRunId }));
        var zero = await new JobRunRegistry(new NoSqlRunner()).EndAsync(Callers.For("STC", "GLCLERK"), "TBRPT", 0, "COMPLETE", 0);
        Assert.Equal((8, "JOB0002", "A job run id is required to close a run"), (zero.Header.ReturnCode, zero.Header.ReasonCode, zero.Header.Message));
        var status = await new JobRunRegistry(new NoSqlRunner()).EndAsync(Callers.For("STC", "GLCLERK"), "TBRPT", 1, "OK", 0);
        Assert.Equal(("JOB0003", "A run ends COMPLETE, WARNING, FAILED or CANCELLED, not OK"), (status.Header.ReasonCode, status.Header.Message));
        var missing = await jl.EndAsync(Callers.For("STC", "GLCLERK"), "TBRPT", 999999999, "FAILED", 8);
        Assert.Equal((12, "SQL0001"), (missing.Header.ReturnCode, missing.Header.ReasonCode));
    }

    // ---------------------------------------------------------------- GL posting gateway (FR-STERP-PLATFORM-029)

    private static GlPostRequest Request(string correlation, int postingDate = 20261019) => new()
    {
        CompanyCode = "STC", CorrelationId = correlation, UserCode = "GLCLERK", Description = "Kernel gateway test",
        PostingDate = postingDate, JournalType = "GL", DocumentDate = postingDate, CurrencyCode = "GBP", SourceModule = "FIN",
        SourceRef = "", EventCode = null, SeriesCode = "JNL", FxRate = 0m, EvidenceRef = null,
    };

    private async Task StageBalancedAsync(IGlPostingGateway gl, string correlation, decimal credit = 100m)
    {
        Assert.Equal(0, (await gl.StageLineAsync(new JournalStageLine(correlation, 1).Text("CompanyCode", "STC").Text("AccountCode", "1000")
            .Amount("DebitAmount", 100m).Amount("CreditAmount", 0m).Text("CurrencyCode", "GBP").NullIfBlank("DimCostCentre", "")
            .NullIfBlank("Narrative", "Debit").Text("CreatedBy", "GLCLERK"))).SqlCode);
        Assert.Equal(0, (await gl.StageLineAsync(new JournalStageLine(correlation, 2).Text("CompanyCode", "STC").Text("AccountCode", "1010")
            .Amount("DebitAmount", 0m).Amount("CreditAmount", credit).Text("CurrencyCode", "GBP").NullIfBlank("DimCostCentre", "")
            .NullIfBlank("Narrative", "Credit").Text("CreatedBy", "GLCLERK"))).SqlCode);
    }

    [Fact]
    public async Task Gl_post_joins_the_callers_unit_and_a_repeat_returns_the_original_journal()
    {
        await BeginAsync();
        var gl = Get<IGlPostingGateway>();
        var correlation = "KT-" + Guid.NewGuid().ToString("N")[..20];
        await StageBalancedAsync(gl, correlation);
        var r = await gl.PostAsync(Request(correlation));
        Assert.False(r.Failed, r.ErrorText);
        Assert.Equal((0, false, "POSTED", 2026, 7, 100m, 100m, "STC"),
            (r.SqlCode, r.WasDuplicate, r.Status, r.FiscalYear, r.PeriodNo, r.TotalDebitGbp, r.TotalCreditGbp, r.CompanyCode));
        Assert.StartsWith("JNS", r.JournalNumber);
        Assert.Equal(0, await UowScalarAsync<int>("SELECT COUNT(*) FROM fin.JournalStage WHERE CorrelationId = @correlation", new { correlation }));
        // the journal joined the open unit: another connection (read committed snapshot) does not see it yet
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM fin.Journal WHERE CorrelationId = @c", ("@c", correlation)));
        var again = await gl.PostAsync(Request(correlation));
        Assert.Equal((0, true, r.JournalNumber, r.JournalId), (again.SqlCode, again.WasDuplicate, again.JournalNumber, again.JournalId));
        Assert.Equal(1, await UowScalarAsync<int>("SELECT COUNT(*) FROM fin.Journal WHERE CorrelationId = @correlation", new { correlation }));
        await Get<IUnitOfWork>().RollbackAsync();
        Assert.Equal(0, await UowScalarAsync<int>("SELECT COUNT(*) FROM fin.Journal WHERE CorrelationId = @correlation", new { correlation }));
    }

    [Fact]
    public async Task Gl_refusals_come_back_as_driver_text_and_nothing_is_posted()
    {
        await BeginAsync();
        var gl = Get<IGlPostingGateway>();
        var unbalanced = "KT-" + Guid.NewGuid().ToString("N")[..20];
        await StageBalancedAsync(gl, unbalanced, credit: 90m);
        var r = await gl.PostAsync(Request(unbalanced));
        Assert.True(r.Failed);
        Assert.Contains("STERP-E-0011: The journal does not balance.", r.ErrorText);
        Assert.StartsWith("[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]", r.ErrorText);
        await Get<IUnitOfWork>().RollbackAsync();

        await BeginAsync();
        var closed = "KT-" + Guid.NewGuid().ToString("N")[..20];
        await StageBalancedAsync(gl, closed);
        var hard = await gl.PostAsync(Request(closed, 20260515));
        Assert.True(hard.Failed);
        Assert.Contains("STERP-E-0009: That accounting period is closed for posting.", hard.ErrorText);
        Assert.Equal(0, await UowScalarAsync<int>("SELECT COUNT(*) FROM fin.Journal WHERE CorrelationId = @closed", new { closed }));
    }

    [Fact]
    public async Task Gl_stage_if_not_exists_and_clear()
    {
        await BeginAsync();
        var gl = Get<IGlPostingGateway>();
        var correlation = "KT-" + Guid.NewGuid().ToString("N")[..20];
        JournalStageLine Line() => new(correlation, 7) { IfNotExists = true };
        var first = await gl.StageLineAsync(Line().Text("CompanyCode", "STC").Text("AccountCode", "1000").Amount("DebitAmount", 1.239m)
            .Amount("CreditAmount", 0m).Text("CurrencyCode", "GBP").NullIfBlank("SubledgerType", " ").Text("CreatedBy", "GLCLERK"));
        Assert.Equal((0, 1), (first.SqlCode, first.RowCount));
        var second = await gl.StageLineAsync(Line().Text("CompanyCode", "STC").Text("AccountCode", "1000").Amount("DebitAmount", 1m)
            .Amount("CreditAmount", 0m).Text("CurrencyCode", "GBP").Text("CreatedBy", "GLCLERK"));
        Assert.Equal((100, 0), (second.SqlCode, second.RowCount));
        var staged = await Get<ISqlRunner>().QueryFirstOrDefaultAsync<(decimal DebitAmount, string? SubledgerType)>(
            "SELECT DebitAmount, SubledgerType FROM fin.JournalStage WHERE CorrelationId = @correlation", new { correlation });
        Assert.Equal((1.24m, null), staged);
        Assert.Equal(0, (await gl.ClearStageAsync(correlation)).SqlCode);
        Assert.Equal(100, (await gl.ClearStageAsync(correlation)).SqlCode);
        Assert.Throws<ArgumentException>(() => new JournalStageLine(correlation, 1).Text("Bogus", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => gl.StageStatementAsync("DELETE fin.Journal", []));
    }

    // ---------------------------------------------------------------- PING (FR-STERP-PLATFORM-030) and the batch suites

    [Fact]
    public async Task Ping_probe_reports_the_database()
    {
        var p = await Get<IDatabaseProbe>().ProbeAsync();
        Assert.True(p.Available);
        Assert.Equal(("ENGINE AND DATABASE AVAILABLE", "STERP"), (p.Result.Message, p.DatabaseName));
        // style 121 of SYSDATETIME() (datetime2(7)) carries seven fractional digits
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{7}$", p.ServerTime);
        Assert.StartsWith("Microsoft SQL Server", p.Version);
        Assert.True(p.Version.Length <= 80);
    }

    [Fact]
    public void Connection_factory_turns_pool_blocking_off_and_shortens_the_connect_retry_unless_configured()
    {
        static SqlConnectionStringBuilder Effective(string cs) => new(new SqlConnectionFactory(Microsoft.Extensions.Options.Options.Create(
            new SterpDatabaseOptions { ConnectionString = cs, ApplicationName = "STERP.Kernel.Tests" })).ConnectionString);
        var byDefault = Effective(@"Server=(localdb)\STERPA08_kernel;Database=STERP;Integrated Security=True");
        Assert.Equal((PoolBlockingPeriod.NeverBlock, 1, 1, "STERP.Kernel.Tests"),
            (byDefault.PoolBlockingPeriod, byDefault.ConnectRetryInterval, byDefault.ConnectRetryCount, byDefault.ApplicationName));
        var configured = Effective(@"Server=(localdb)\STERPA08_kernel;Database=STERP;Integrated Security=True;Pool Blocking Period=AlwaysBlock;ConnectRetryInterval=7");
        Assert.Equal((PoolBlockingPeriod.AlwaysBlock, 7), (configured.PoolBlockingPeriod, configured.ConnectRetryInterval));
    }

    [Fact]
    public async Task Ping_failure_is_a_warning_with_SQL0001()
    {
        var factory = new SqlConnectionFactory(Microsoft.Extensions.Options.Options.Create(new SterpDatabaseOptions
        {
            ConnectionString = @"Server=(localdb)\STERPA08_absent;Database=STERP;Integrated Security=True;Connect Timeout=5",
            IsolatedOnly = true,
        }));
        var p = await new DatabaseProbe(factory, Microsoft.Extensions.Options.Options.Create(new SterpDatabaseOptions())).ProbeAsync();
        Assert.Equal((4, "SQL0001", false), (p.Result.ReturnCode, p.Result.ReasonCode, p.Available));
        Assert.StartsWith("[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]", p.Result.Message);
        Assert.True(p.Result.Message.Length <= 200);
    }

    [Fact]
    public async Task Fin_suite_heading_reads_core_company_and_resolves_the_period()
    {
        var args = BatchSuite.StandardArgs(BatchSuiteKind.Fin, Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("TBRPT ASAT=20261019"), new DateTime(2026, 10, 20, 8, 30, 0));
        var heading = await BatchSuite.FillHeadingsAsync(Get<ISqlRunner>(), args, new ReportHeading { ReportId = "TBRPT" }, new DateTime(2026, 10, 20, 8, 30, 15));
        Assert.Equal(("Stellar Components Ltd", "07412936", "England and Wales", "GLCLERK", "20/10/2026", "08:30:15"),
            (heading.CompanyName, heading.RegisteredNumber, heading.RegisteredPlace, heading.User, heading.RunDate, heading.RunTime));
        Assert.Equal((2026, 7, ""), await BatchSuite.ResolvePeriodAsync(Get<ISqlRunner>(), args));
        var xmd = BatchSuite.StandardArgs(BatchSuiteKind.Xmd, Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("RECONCTL CO=ZZZ ASAT=20261019"), DateTime.Now);
        var unknown = await BatchSuite.FillHeadingsAsync(Get<ISqlRunner>(), xmd, new ReportHeading(), DateTime.Now);
        Assert.Equal(("ZZZ", ""), (unknown.CompanyName, unknown.RegisteredNumber));
        Assert.Equal((0, 0, "NONE"), await BatchSuite.ResolvePeriodAsync(Get<ISqlRunner>(), xmd));
        var stc = BatchSuite.StandardArgs(BatchSuiteKind.Xmd, Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("RECONCTL ASAT=20260915"), DateTime.Now);
        Assert.Equal((2026, 6, "SOFT"), await BatchSuite.ResolvePeriodAsync(Get<ISqlRunner>(), stc));
        Assert.Equal(2500.00m, await BatchSuite.GetParmNumAsync(Get<IParameterService>(), stc, "KT.NOT.HELD", 2500m));
        Assert.Equal(1000.00m, await BatchSuite.GetParmNumAsync(Get<IParameterService>(), stc, "FIN.CLOSE.MATERIALITY", 2500m));
    }

    // ---------------------------------------------------------------- SQLIO rendering and the engine unit of work

    [Fact]
    public async Task Sqlio_fetch_renders_odbc_character_text_into_fixed_slots()
    {
        var row = await Get<ISqlRunner>().SingleAsync(
            "SELECT CAST(0.5 AS decimal(18,2)), CAST(1 AS bit), CAST('2026-10-19T21:05:03.1234567' AS datetime2(7)), " +
            "CAST('2026-10-19T21:05:03.123' AS datetime2(3)), CAST('2026-10-19' AS date), NULL, CAST(-12.5 AS decimal(9,4)), ?, CAST(42 AS bigint)",
            [SqlioParm.Text(new string('x', 300))], [6, 1, 23, 23, 10, 5, 12, 256, 3], default);
        Assert.Equal(0, row.SqlCode);
        Assert.Equal(".50   ", row.Raw(0));
        Assert.Equal("1", row.Raw(1));
        Assert.Equal("2026-10-19 21:05:03.123", row.Raw(2));
        Assert.Equal("2026-10-19 21:05:03.123", row.Raw(3));
        Assert.Equal("2026-10-19", row.Raw(4));
        Assert.Equal("     ", row.Raw(5));
        Assert.Equal("-12.5000", row.Text(6));
        Assert.Equal(256, row.Text(7).Length);
        Assert.Equal("42 ", row.Raw(8));
        var none = await Get<ISqlRunner>().SingleAsync("SELECT 1 WHERE 1 = 0", [], [1], default);
        Assert.Equal((100, " "), (none.SqlCode, none.Raw(0)));
        var exec = await Get<ISqlRunner>().ExecAsync("UPDATE core.NumberSeries SET NextNumber = NextNumber WHERE 1 = 0", [], default);
        Assert.Equal(100, exec.SqlCode);
        // no row count reported (SET NOCOUNT ON procedure): SQLCODE 0, as the legacy SQLIO EXEC returned (PO RECEIPT)
        var nocount = await Get<ISqlRunner>().ExecAsync("SET NOCOUNT ON; UPDATE core.NumberSeries SET NextNumber = NextNumber WHERE 1 = 0; SET NOCOUNT OFF", [], default);
        Assert.Equal(0, nocount.SqlCode);
    }

    [Fact]
    public async Task Sqlio_fetch_sets_reads_each_result_set_with_its_own_widths()
    {
        var sets = await Get<ISqlRunner>().FetchSetsAsync(
            "SET NOCOUNT OFF; DECLARE @t TABLE (n int); INSERT @t VALUES (1); SELECT 'header', ?; " +
            "SELECT s FROM (VALUES ('step one'), ('step two')) v(s); SELECT 'ignored'",
            [SqlioParm.Text("2026")], [[6, 4], [5]], default);
        Assert.Equal(2, sets.Count);
        Assert.Equal(["header", "2026"], sets[0].Single().Slots);
        Assert.Equal(["step ", "step "], sets[1].Select(r => r.Raw(0)));
    }

    [Fact]
    public async Task Engine_unit_of_work_commits_and_rolls_back_one_unit()
    {
        var key1 = "KT-" + Guid.NewGuid().ToString("N")[..20];
        var key2 = "KT-" + Guid.NewGuid().ToString("N")[..20];
        var uow = Get<IUnitOfWork>();
        Assert.Equal(1, uow.TransactionState);
        await uow.BeginAsync();
        Assert.Equal(0, uow.TransactionState);
        await UowExecAsync("INSERT core.IdempotencyKey (CorrelationId, Operation, Status) VALUES (@key1, 'KT', 'INPROGRESS'), (@key2, 'KT', 'INPROGRESS')", new { key1, key2 });
        await uow.RollbackAsync();
        Assert.Equal(1, uow.TransactionState);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM core.IdempotencyKey WHERE CorrelationId IN (@a, @b)", ("@a", key1), ("@b", key2)));
        try
        {
            await uow.BeginAsync();
            await UowExecAsync("INSERT core.IdempotencyKey (CorrelationId, Operation, Status) VALUES (@key1, 'KT', 'INPROGRESS'), (@key2, 'KT', 'INPROGRESS')", new { key1, key2 });
            await uow.CommitAsync();
            Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM core.IdempotencyKey WHERE CorrelationId IN (@a, @b)", ("@a", key1), ("@b", key2)));
        }
        finally
        {
            await db.ExecuteAsync("DELETE core.IdempotencyKey WHERE CorrelationId IN (@a, @b)", ("@a", key1), ("@b", key2));
        }
    }

    [Fact]
    public void The_fixture_refuses_the_shared_server()
    {
        Assert.Throws<InvalidOperationException>(() => LocalDbFixture.Guard("."));
        Assert.Throws<InvalidOperationException>(() => LocalDbFixture.Guard("localhost"));
        Assert.Throws<InvalidOperationException>(() => LocalDbFixture.Guard(Environment.MachineName));
        Assert.Throws<InvalidOperationException>(() => LocalDbFixture.Guard(@"(localdb)\MSSQLLocalDB"));
        LocalDbFixture.Guard(@"(localdb)\STERPA08_kernel");
    }

    // ---------------------------------------------------------------- SQLIO login retry (FR-STERP-PLATFORM-011)

    [Fact]
    public async Task Engine_connection_retries_four_times_waiting_2000ms_times_the_attempt()
    {
        // a LocalDB instance that does not exist: every attempt fails before any login
        var missing = new SqlConnectionStringBuilder(db.ConnectionString) { DataSource = @"(localdb)\STERPA08_ktmissing" }.ConnectionString;
        var factory = new CountingConnectionFactory(missing);
        var waits = new List<double>();
        await using var uow = new EngineUnitOfWork(factory) { Delay = (t, _) => { waits.Add(t.TotalMilliseconds); return Task.CompletedTask; } };
        await Assert.ThrowsAsync<SqlException>(() => uow.ConnectionAsync());
        Assert.Equal(4, factory.Created);
        Assert.Equal([2000d, 4000d, 6000d, 8000d], waits);
    }

    [Fact]
    public async Task Engine_connection_does_not_retry_a_refused_login()
    {
        var refused = new SqlConnectionStringBuilder(db.ConnectionString)
        {
            IntegratedSecurity = false, UserID = "sterp_kernel_test_nologin", Password = Guid.NewGuid().ToString("N"), Pooling = false,
        }.ConnectionString;
        var factory = new CountingConnectionFactory(refused);
        var waits = 0;
        await using var uow = new EngineUnitOfWork(factory) { Delay = (_, _) => { waits++; return Task.CompletedTask; } };
        var ex = await Assert.ThrowsAsync<SqlException>(() => uow.ConnectionAsync());
        Assert.Equal((18456, "28000"), (ex.Number, LegacyDriverText.SqlState(ex)));
        Assert.Equal((1, 0), (factory.Created, waits));
        // through SQLIO the failure is SQLCODE -1 with the prefixed driver text
        await using var uow2 = new EngineUnitOfWork(factory) { Delay = (_, _) => { waits++; return Task.CompletedTask; } };
        var runner = new SqlRunner(uow2, Microsoft.Extensions.Options.Options.Create(new SterpDatabaseOptions()));
        var sqlio = await Assert.ThrowsAsync<SqlioException>(() => runner.ScalarAsync<int>("SELECT 1"));
        Assert.StartsWith("[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]Login failed for user 'sterp_kernel_test_nologin'", sqlio.DriverText, StringComparison.Ordinal);
        Assert.Equal(("28000", 2, 0), (sqlio.SqlState, factory.Created, waits));
    }

    [Fact]
    public async Task Engine_connection_does_not_retry_a_database_that_cannot_be_opened()
    {
        // an absent (or offline) database: SQL Server sends 4060 then 18456; ODBC reports the refused login as record 1
        // (SQLSTATE 28000), so ensure_conn gives up at once - the legacy STERPOPS SMOKE with the database offline ends in 0.2 s
        var absent = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = "STERP_KT_ABSENT", Pooling = false }.ConnectionString;
        var factory = new CountingConnectionFactory(absent);
        var waits = 0;
        await using var uow = new EngineUnitOfWork(factory) { Delay = (_, _) => { waits++; return Task.CompletedTask; } };
        var ex = await Assert.ThrowsAsync<SqlException>(() => uow.ConnectionAsync());
        Assert.Contains(ex.Errors.Cast<SqlError>(), e => e.Number == 4060);
        Assert.Equal(("28000", 1, 0), (LegacyDriverText.SqlState(ex), factory.Created, waits));
        Assert.StartsWith("[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]Login failed for user",
            LegacyDriverText.From(ex, new SterpDatabaseOptions().LegacyDriverMessagePrefix), StringComparison.Ordinal);
    }

    /// <summary>A real SQLIO failure (driver text and SqlException) for fault injection.</summary>
    private async Task<SqlioException> CaptureFailureAsync()
    {
        try
        {
            await Get<ISqlRunner>().ExecuteAsync("THROW 50099, 'kernel test failure', 1");
        }
        catch (SqlioException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("THROW did not fail");
    }
}
