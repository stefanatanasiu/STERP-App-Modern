using System.Globalization;
using Sterp.Modules.Kit.Common;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Kit.Jobs;

/// <summary>
/// The KIT batch programs (STERP-KIT.md 7.3): KITRDY and KITDOCP, submitted through POST /api/kit/v1/jobs/{code}/runs.
/// Registration (contracts 8.3, the operation descriptions): the platform registers the run before the 202 - at a slice
/// submission the job-code row of D-PLAT-09 (no <see cref="JobDefinition.SelfRegistration"/>), at a catalogue launch the
/// ReportService launcher row - and the program additionally registers itself with JOBLOG START inside its run (0200-JOB-START);
/// its JOBLOG END carries run id 0 (INITIALIZE Ws-Joblog) and is refused with JOB0002, so that row stays RUNNING (SME-009,
/// SME-010). The platform row of a slice submission ends with the ReportService.JobStatus mapping (spec 7.3) and the spool
/// the program opened, so the listing is served by GET /api/jobs/v1/runs/{id}/spool although the program writes no
/// STERP-I-SPOOL line (SME-1154).
/// </summary>
internal static class KitJobs
{
    public const string Slice = "STERP-KIT";

    public static IReadOnlyList<JobDefinition> All { get; } =
    [
        new JobDefinition
        {
            JobCode = "KITRDY",
            Slice = Slice,
            SubmitOperationId = "kit_submitKitReadinessRun",
            ParameterOrder = ["CO", "SITE", "STATUS", "ASAT", "MODE", "USER"],
            Execute = KitrdyProgram.RunAsync,
            EndValues = PlatformRowEnd,
        },
        new JobDefinition
        {
            JobCode = "KITDOCP",
            Slice = Slice,
            SubmitOperationId = "kit_submitKitPickingDocumentRun",
            ParameterOrder = ["CO", "SITE", "USER", "KIT", "MODE", "COPY", "REASON"],
            Execute = KitdocpProgram.RunAsync,
            EndValues = PlatformRowEnd,
        },
    ];

    /// <summary>END of the platform row: ReportService.JobStatus (&lt;= 0 OK, &lt;= 4 WARNING, &lt; 16 FAILED, else ABENDED), the console as log, the opened spool.</summary>
    private static JobEnd PlatformRowEnd(JobRunContext ctx, int exit) =>
        new(JobService.LauncherStatus(exit), exit, null, null, JobService.Blank(ctx.SpoolFile));
}

/// <summary>Housekeeping shared by KITRDY and KITDOCP (CMDARGS, JOBLOG, RUNPATH, STRPTWS headings, SQLIO).</summary>
internal sealed class KitBatch(JobRunContext ctx, string jobName)
{
    private static readonly int[] Wide = [.. Enumerable.Repeat(100, 20)];

    public JobRunContext Ctx { get; } = ctx;

    public ISqlRunner Sql { get; } = ctx.Get<ISqlRunner>();

    public CancellationToken Ct => Ctx.Cancellation;

    /// <summary>Ws-Spool-Name (spaces until RUNPATH built the path).</summary>
    public string SpoolName { get; private set; } = "";

    /// <summary>RPT-HEAD-WORK (STRPTWS: company name and registration number spaces, place "England and Wales").</summary>
    public ReportHeading Heading { get; } = new();

    public SpoolWriter? Spool { get; private set; }

    public void Display(string line) => Ctx.Display(line);

    /// <summary>0200-JOB-START: JOBLOG START with CA-COMPANY = Ws-Co, CA-USER = Ws-User (no site), JL-ARGUMENTS = CM-COMMAND-LINE.</summary>
    public async Task JobStartAsync(string co, string user)
    {
        var joblog = Ctx.Get<IJobRunRegistry>();
        await joblog.StartAsync(new KernelCaller(co, "", user, 0, ""), jobName, Ctx.Args.Text, Ct);
    }

    /// <summary>0500-JOB-END: INITIALIZE Ws-Joblog leaves JL-JOB-RUN-ID 0, so JOBLOG refuses the END (JOB0002) and writes nothing.</summary>
    public async Task JobEndAsync(string co, string user, int rows, int rc)
    {
        var joblog = Ctx.Get<IJobRunRegistry>();
        await joblog.EndAsync(new KernelCaller(co, "", user, 0, ""), jobName, 0, "", rc, rows, null, SpoolName, null, Ct);
    }

    /// <summary>RUNPATH BUILD SPOOL &lt;job&gt;-&lt;CO&gt;-&lt;stamp&gt;.lst; false when the path cannot be built.</summary>
    public bool BuildSpoolPath(string co)
    {
        var r = Ctx.Paths.Build("SPOOL", jobName, co, null, null, true, out var path);
        if (r.ReturnCode > 4) return false;
        SpoolName = path;
        return true;
    }

    /// <summary>OPEN OUTPUT RPT-FILE (LINE SEQUENTIAL, Windows-1252, no share lock); false when the file cannot be opened.</summary>
    public bool OpenSpool()
    {
        try
        {
            Spool = new SpoolWriter(SpoolName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        Ctx.SpoolFiles.Add(SpoolName);
        Ctx.SpoolFile = SpoolName;
        return true;
    }

    public void CloseSpool() => Spool?.Dispose();

    /// <summary>0360-READ-COMPANY: only the name is moved (RPT-COMPANY-NAME), and only when the row is read.</summary>
    public async Task ReadCompanyAsync(string co)
    {
        try
        {
            var row = await Sql.SingleAsync("SELECT Name, ISNULL(RegisteredNumber,'') FROM core.Company WHERE CompanyCode = ?",
                [SqlioParm.Text(co)], Wide, Ct);
            if (row.Found) Heading.CompanyName = Pic.X(row.Raw(0), 40);
        }
        catch (SqlioException)
        {
            // not SQLIO-OK: the heading keeps its initial value
        }
    }

    /// <summary>EXEC + FTCH of a listing statement (100-character slots).</summary>
    public Task<IReadOnlyList<SqlioRow>> FetchAsync(string statement, IReadOnlyList<SqlioParm> parms) => Sql.FetchAllAsync(statement, parms, Wide, Ct);

    /// <summary>A single-row statement (SNGL).</summary>
    public Task<SqlioRow> SingleAsync(string statement, IReadOnlyList<SqlioParm> parms) => Sql.SingleAsync(statement, parms, Wide, Ct);

    /// <summary>"STERP-E-KITnnnn " SQLIO-ERRMSG(1:80) (the reference-modified field is displayed at its full length).</summary>
    public static string ErrText(SqlioException ex) => CobolText.Fit(ex.DriverText, 80);

    /// <summary>FUNCTION CURRENT-DATE (1:8) as PIC 9(8).</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateOnly.FromDateTime(DateTime.Now));

    /// <summary>Ws-D-Dd "/" Ws-D-Mm "/" Ws-D-Yyyy of Ws-Date-R (the REDEFINES of a PIC 9(8) date, by position).</summary>
    public static string Dmy(int yyyymmdd)
    {
        var d = Pic.D8(yyyymmdd);
        return d[6..8] + "/" + d[4..6] + "/" + d[..4];
    }

    /// <summary>"IF slot (1:8) NUMERIC": the eight digits as PIC 9(8), else null.</summary>
    public static int? DateDigits(string slot)
    {
        var s = Pic.Pad(slot, 8)[..8];
        return s.All(char.IsAsciiDigit) ? int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A yyyymmdd slot as JSON date text (null when blank or not a date).</summary>
    public static string? JsonDate(string slot) => DateDigits(slot) is { } d ? Pic.Json(d) : null;

    /// <summary>FUNCTION NUMVAL of a CMDARGS value moved to PIC 9(8) (unsigned: the sign is lost, high digits cut).</summary>
    public static int Date8(string? text) => (int)(Math.Abs(decimal.Truncate(CobolNumeric.NumVal(text))) % 100_000_000m);
}
