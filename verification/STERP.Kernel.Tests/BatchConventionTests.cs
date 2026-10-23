using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Kernel.Tests;

/// <summary>Batch conventions (FR-STERP-PLATFORM-028): the STRPTHDR heading and the FIN/XMD suite housekeeping helpers.</summary>
public sealed class BatchConventionTests
{
    private static ReportHeading Heading() => new()
    {
        CompanyCode = "STC",
        CompanyName = "Stellar Components Ltd",
        RegisteredNumber = "07412936",
        ReportId = "TBRPT",
        Title = "Trial balance",
        Subtitle = "All accounts",
        AsAtText = "As at 19/10/2026",
        User = "GLCLERK",
        RunAt = new DateTime(2026, 10, 19, 21, 5, 3),
    };

    [Fact]
    public void Heading_lines_follow_the_STRPTHDR_columns()
    {
        var lines = Report132.HeadingLines(Heading(), 1);
        Assert.Equal(6, lines.Count);
        var l1 = lines[0].PadRight(132);
        Assert.Equal("Stellar Components Ltd", l1[..40].TrimEnd());
        Assert.Equal("TBRPT", l1.Substring(49, 12).TrimEnd());
        Assert.Equal("Page ", l1.Substring(111, 5));
        Assert.Equal("   1", l1.Substring(116, 4));
        var l2 = lines[1].PadRight(132);
        Assert.Equal("Registered number: 07412936", l2[..27]);
        Assert.Equal("Place of registration: ", l2.Substring(31, 23));
        Assert.Equal("England and Wales", l2.Substring(56, 24).TrimEnd());
        var l3 = lines[2].PadRight(132);
        Assert.Equal("Trial balance", l3[..60].TrimEnd());
        Assert.Equal("Run date 19/10/2026", l3.Substring(103, 19));
        var l4 = lines[3].PadRight(132);
        Assert.Equal("All accounts", l4[..60].TrimEnd());
        Assert.Equal("As at 19/10/2026", l4.Substring(69, 30).TrimEnd());
        Assert.Equal("GLCLERK", l4.Substring(103, 20).TrimEnd());
        Assert.Equal("21:05:03", l4.Substring(124, 8));
        Assert.Equal("", lines[4]);
        Assert.Equal(new string('-', 132), lines[5]);
        Assert.Equal(" 123", Report132.HeadingLines(Heading(), 123)[0].PadRight(132).Substring(116, 4));
    }

    [Fact]
    public void Writer_puts_the_heading_on_every_page_with_a_form_feed_between_pages()
    {
        var output = new List<string>();
        var w = new Report132Writer(output.Add, () => output.Add("\f"), Heading(), firstDetail: 7, lastDetail: 9);
        w.Detail("a");
        w.Detail("b");
        w.Detail("c");
        w.Detail("d");
        Assert.Equal(2, w.Page);
        Assert.Equal(6 + 3 + 1 + 6 + 1, output.Count);
        Assert.Equal("a", output[6]);
        Assert.Equal("\f", output[9]);
        Assert.Contains("Page    2", output[10]);
        Assert.Equal("d", output[16]);
    }

    [Fact]
    public void Fin_suite_defaults()
    {
        var now = new DateTime(2026, 10, 20, 9, 0, 0);
        var none = BatchSuite.StandardArgs(BatchSuiteKind.Fin, CommandLineArgs.Parse("TBRPT"), now);
        Assert.Equal(("STC", "GLCLERK", 20261020, ""), (none.Co, none.User, none.AsAt, none.Mode));
        var given = BatchSuite.StandardArgs(BatchSuiteKind.Fin, CommandLineArgs.Parse("TBRPT CO=CAL USER=FINCTRL ASAT=20260930 MODE=commit"), now);
        Assert.Equal(("CAL", "FINCTRL", 20260930, "COMMIT"), (given.Co, given.User, given.AsAt, given.Mode));
        var badDate = BatchSuite.StandardArgs(BatchSuiteKind.Fin, CommandLineArgs.Parse("TBRPT ASAT=2026-09-30"), now);
        // CM-ASAT X(8) holds "2026-09-"; NUMVAL is -202609 (SME-039), stored unsigned into WS-ASAT 9(8); not 0, so no default
        Assert.Equal(202609, badDate.AsAt);
    }

    [Fact]
    public void Xmd_suite_defaults()
    {
        var now = new DateTime(2026, 10, 20, 9, 0, 0);
        var none = BatchSuite.StandardArgs(BatchSuiteKind.Xmd, CommandLineArgs.Parse("RECONCTL"), now);
        Assert.Equal(("STC", "FINCTRL", "PREVIEW", 20261020, false), (none.Co, none.User, none.Mode, none.AsAt, none.IsCommit));
        var given = BatchSuite.StandardArgs(BatchSuiteKind.Xmd, CommandLineArgs.Parse("RECONCTL CO=STC SITE=BHM MODE=commit FROM=20261001 TO=20261031"), now);
        Assert.Equal(("BHM", "COMMIT", true, 20261001, 20261031), (given.Site, given.Mode, given.IsCommit, given.From, given.To));
    }

    [Fact]
    public void Suite_job_values_and_messages()
    {
        Assert.Equal("COMPLETE", BatchSuite.EndStatus(0));
        Assert.Equal("WARNING", BatchSuite.EndStatus(4));
        Assert.Equal("FAILED", BatchSuite.EndStatus(6));
        Assert.Equal("FAILED", BatchSuite.EndStatus(-1));
        Assert.Equal("OK", BatchSuite.StoredEndStatus(0));
        Assert.Equal(200, BatchSuite.JobArguments(new string('A', 300)).Length);
        Assert.Equal("STERP-E-9202 cannot open C:\\run\\spool\\x.lst", BatchSuite.SpoolOpenFailure(" C:\\run\\spool\\x.lst "));
        Assert.Equal("STERP-I-SPOOL C:\\run\\spool\\x.lst", BatchSuite.SpoolAnnouncement("C:\\run\\spool\\x.lst"));
        Assert.Equal("STERP-W-9701 exception refused EXC0005  INV-1", BatchSuite.ExceptionRefused("EXC0005", "INV-1"));
        Assert.Equal("19/10/2026", BatchSuite.FormatDate(20261019));
        Assert.Equal("00/00/0000", BatchSuite.FormatDate(0));
        Assert.Equal(20261019, BatchSuite.IsoToNum("2026-10-19"));
        Assert.Equal(20261019, BatchSuite.IsoToNum("20261019"));
        Assert.Equal(0, BatchSuite.IsoToNum("19/10/2026"));
    }

    [Fact]
    public void Command_line_arguments_follow_CMDARGS()
    {
        // FR-025 AC1-AC4 (the kernel parser used by the suites)
        var a = CommandLineArgs.Parse("TBRPT CO=STC YEAR=2026 MODE=preview");
        Assert.Equal(("STC", "PREVIEW", 3, "3 argument(s) read"), (a.Co, a.Mode, a.Pairs.Count, a.Result.Message));
        var b = CommandLineArgs.Parse("X CO=A CO=B");
        Assert.Equal(("B", "A"), (b.Co, b.Get("CO")));
        var c = CommandLineArgs.Parse("X BAD CO=STC");
        Assert.Equal((8, "CMD0001", "1 argument(s) read"), (c.Result.ReturnCode, c.Result.ReasonCode, c.Result.Message));
        var d = CommandLineArgs.Parse("X " + string.Join(' ', Enumerable.Range(1, 21).Select(i => $"K{i}=V")));
        Assert.Equal((4, "CMD0004", "1 argument(s) ignored - only 20 are held", 20), (d.Result.ReturnCode, d.Result.ReasonCode, d.Result.Message, d.Pairs.Count));
    }

    [Fact]
    public void Executables_receive_the_command_line_rebuilt_from_argv()
    {
        // C runtime argv split (quotes consumed) joined by GnuCOBOL ACCEPT FROM COMMAND-LINE; legacy KITDOCP printed only the
        // first word of a quoted REASON (KIT work-stream oracle run)
        Assert.Equal("KITDOCP REASON=Lost in transit CO=STC", CommandLineArgs.AsSeenByProgram("KITDOCP REASON=\"Lost in transit\" CO=STC"));
        var a = CommandLineArgs.Parse(CommandLineArgs.AsSeenByProgram("KITDOCP REASON=\"A B\" CO=STC"));
        Assert.Equal(("A", "STC", 8, "CMD0001"), (a.Get("REASON"), a.Co, a.Result.ReturnCode, a.Result.ReasonCode));
        Assert.Equal(@"CO=STC OUT=C:\run\x.lst", CommandLineArgs.AsSeenByProgram("  CO=STC\tOUT=C:\\run\\x.lst  "));
        Assert.Equal("A=say \"hi\" B=\\\\", CommandLineArgs.AsSeenByProgram("A=\"say \\\"hi\\\"\" B=\\\\"));
        Assert.Equal("", CommandLineArgs.AsSeenByProgram(null));
    }
}
