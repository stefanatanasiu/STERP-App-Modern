using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>JOBLOGCA body (JOBLOGCA.cpy:17-30).</summary>
public sealed class JoblogArea : Commarea
{
    /// <summary>JL-JOB-NAME X(40) - upper-cased by the service.</summary>
    public string JobName { get; set; } = "";

    /// <summary>JL-JOB-RUN-ID S9(9) COMP-5 (core.JobRun.JobRunId is bigint; the target carries int64, BRF-JOBLOG-02).</summary>
    public long JobRunId { get; set; }

    /// <summary>JL-STATUS X(12): RUNNING after START; COMPLETE, WARNING, FAILED or CANCELLED for END (upper-cased).</summary>
    public string Status { get; set; } = "";

    /// <summary>JL-RETURN-CODE S9(4).</summary>
    public int ReturnCode { get; set; }

    /// <summary>JL-ROWS-PROCESSED S9(9) COMP-5.</summary>
    public int RowsProcessed { get; set; }

    /// <summary>JL-ARGUMENTS X(200) (a 256-byte command line moved here keeps its first 200 bytes).</summary>
    public string Arguments { get; set; } = "";

    /// <summary>JL-LOG-FILE X(200) (NULL when blank).</summary>
    public string LogFile { get; set; } = "";

    /// <summary>JL-SPOOL-FILE X(200) (NULL when blank).</summary>
    public string SpoolFile { get; set; } = "";

    /// <summary>JL-MESSAGE-TEXT X(200) (NULL when blank).</summary>
    public string MessageText { get; set; } = "";

    public string StartedAt { get; set; } = "";
    public string EndedAt { get; set; } = "";
}

/// <summary>JOBLOG - core.JobRun registration of a self-registering batch program (FR-STERP-PLATFORM-027, COM-014).</summary>
public interface IJobRunRegistry
{
    /// <summary>CALL "JOBLOG" USING area: functions START and END (other JOB0009).</summary>
    Task<LegacyResult> ExecuteAsync(JoblogArea area, CancellationToken ct = default);

    /// <summary>START: core.usp_JobRunStart(job name, company, user as SubmittedBy, site, arguments; blank values NULL).</summary>
    Task<JoblogArea> StartAsync(KernelCaller caller, string jobName, string? arguments, CancellationToken ct = default);

    /// <summary>
    /// END: run id must be positive (JOB0002 - a caller that re-initialised its area leaves the run RUNNING, SME-009) and the
    /// status COMPLETE, WARNING, FAILED or CANCELLED (stored OK / WARNING / FAILED / ABENDED).
    /// </summary>
    Task<JoblogArea> EndAsync(KernelCaller caller, string jobName, long jobRunId, string status, int returnCode, int rowsProcessed = 0,
        string? logFile = null, string? spoolFile = null, string? message = null, CancellationToken ct = default);
}

/// <summary>JOBLOG.cob:56-290 on SQLIO slot 8.</summary>
public sealed class JobRunRegistry(ISqlRunner sql) : IJobRunRegistry
{
    public async Task<JoblogArea> StartAsync(KernelCaller caller, string jobName, string? arguments, CancellationToken ct = default)
    {
        var area = new JoblogArea { JobName = jobName, Arguments = arguments ?? "" };
        area.Header.Prepare(caller, "START");
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<JoblogArea> EndAsync(KernelCaller caller, string jobName, long jobRunId, string status, int returnCode, int rowsProcessed = 0,
        string? logFile = null, string? spoolFile = null, string? message = null, CancellationToken ct = default)
    {
        var area = new JoblogArea
        {
            JobName = jobName, JobRunId = jobRunId, Status = status, ReturnCode = returnCode, RowsProcessed = rowsProcessed,
            LogFile = logFile ?? "", SpoolFile = spoolFile ?? "", MessageText = message ?? "",
        };
        area.Header.Prepare(caller, "END");
        await ExecuteAsync(area, ct);
        return area;
    }

    public async Task<LegacyResult> ExecuteAsync(JoblogArea a, CancellationToken ct = default)
    {
        var h = a.Header;
        // 0100-INITIALISE: job name and status upper-cased in the caller's area
        h.Reset("JOBLOG", "COM-014");
        var jobName = CobolText.Upper(K.X(a.JobName, 40));
        a.JobName = jobName.TrimEnd(' ');
        a.Status = CobolText.Upper(K.X(a.Status, 12)).TrimEnd(' ');
        var fn = h.FunctionCode;

        // 0200-VALIDATE
        if (fn == "START")
        {
            if (K.Blank(jobName, 40))
            {
                h.Reject("JOB0001", "A job name is required");
                return h.Result;
            }
        }
        else if (fn == "END")
        {
            if (a.JobRunId <= 0)
            {
                h.Reject("JOB0002", "A job run id is required to close a run");
                return h.Result;
            }
            if (a.Status is not ("COMPLETE" or "WARNING" or "FAILED" or "CANCELLED"))
            {
                h.Reject("JOB0003", "A run ends COMPLETE, WARNING, FAILED or CANCELLED, not " + K.Trim(a.Status));
                return h.Result;
            }
        }

        switch (fn)
        {
            case "START":
                await StartRunAsync(a, jobName, ct);
                break;
            case "END":
                await EndRunAsync(a, jobName, ct);
                break;
            default:
                h.Reject("JOB0009", "JOBLOG does not support function " + K.Trim(h.Function));
                break;
        }
        return h.Result;
    }

    /// <summary>1000-START-RUN.</summary>
    private async Task StartRunAsync(JoblogArea a, string jobName, CancellationToken ct)
    {
        var h = a.Header;
        a.JobRunId = 0;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_JobRunStart ?, ?, ?, ?, ?",
                [SqlioParm.Text(jobName), SqlioParm.NullIfSpaces(K.X(h.Company, 10)), SqlioParm.NullIfSpaces(K.X(h.User, 20)),
                 SqlioParm.NullIfSpaces(K.X(h.Site, 10)), SqlioParm.NullIfSpaces(K.X(a.Arguments, 200))],
                [20, 40, 12, 23], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "JOBLOG", "JOB0008");
            return;
        }
        // a no-row answer reads a fresh row area (BRF-JOBLOG-01 is not reproduced, BC-PLATFORM-07)
        a.JobRunId = (long)CobolNumeric.Store(row.NumVal(0), 18, 0);
        a.Status = row.Text(2);
        a.StartedAt = row.Text(3);
        h.Message = K.Msg("Job ", K.Trim(jobName), " registered as run ", CobolNumeric.SqlioInteger(a.JobRunId));
    }

    /// <summary>2000-END-RUN: COMPLETE -> OK, CANCELLED -> ABENDED on the way in, back again on the way out.</summary>
    private async Task EndRunAsync(JoblogArea a, string jobName, CancellationToken ct)
    {
        var h = a.Header;
        var dbStatus = a.Status switch
        {
            "COMPLETE" => "OK",
            "CANCELLED" => "ABENDED",
            _ => a.Status,
        };
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync("EXEC core.usp_JobRunEnd ?, ?, ?, ?, ?, ?, ?",
                [
                    SqlioParm.Integer(a.JobRunId), SqlioParm.Text(dbStatus),
                    SqlioParm.Integer((long)CobolNumeric.Store(a.ReturnCode, 4, 0)), SqlioParm.Integer(a.RowsProcessed),
                    SqlioParm.NullIfSpaces(K.X(a.LogFile, 200)), SqlioParm.NullIfSpaces(K.X(a.SpoolFile, 200)),
                    SqlioParm.NullIfSpaces(K.X(a.MessageText, 200)),
                ],
                [20, 40, 10, 12, 8, 12, 23, 23, 200, 200], ct);
        }
        catch (SqlioException ex)
        {
            K.SqlFailed(h, ex, "JOBLOG", "JOB0008");
            return;
        }
        var stored = row.Text(3);
        a.Status = stored switch
        {
            "OK" => "COMPLETE",
            "ABENDED" => "CANCELLED",
            _ => stored,
        };
        a.StartedAt = row.Text(6);
        a.EndedAt = row.Text(7);
        h.Message = K.Msg("Job ", K.Trim(jobName), " ended ", K.Trim(a.Status));
    }
}
