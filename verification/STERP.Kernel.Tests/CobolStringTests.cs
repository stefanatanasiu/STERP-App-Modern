using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Kernel.Tests;

/// <summary>STRUTIL as the CobolString helpers (FR-STERP-PLATFORM-023; BR-STRUTIL-001 .. 009).</summary>
public sealed class CobolStringTests
{
    [Fact]
    public void DocRef_strip_removes_the_leading_zeros_of_the_trailing_digit_run()
    {
        // FR-023 AC1
        var a = CobolString.DocRef("SI-000123", "STRIP");
        Assert.Equal("SI123", a.TextOut);
        Assert.Equal(4, a.Header.ReturnCode);
        Assert.Equal("STR0006", a.Header.ReasonCode);
        Assert.Equal("Reference normalised to SI123", a.Header.Message);
        Assert.Equal(0, a.Header.MessageCount);
        Assert.Equal(5, a.LengthOut);
    }

    [Fact]
    public void Money_with_symbol_uses_the_single_byte_pound_sign()
    {
        // FR-023 AC2: X'A3' followed by 1,234.56 (decimals truncated)
        var a = CobolString.Money(1234.567m, "GBP", "Y");
        Assert.Equal("\u00A31,234.56", a.TextOut);
        Assert.Equal(new byte[] { 0xA3 }, CobolText.Ansi.GetBytes(a.TextOut[..1]));
        Assert.Equal("Amount formatted", a.Header.Message);
        Assert.Equal("GBP 1,234.56", CobolString.Money(1234.567m).TextOut);
        Assert.Equal("EUR -5.00", CobolString.Money(-5m, "EUR", "Y").TextOut);
        Assert.Equal("gbp 1.00", CobolString.Money(1m, "gbp", "Y").TextOut);
    }

    [Fact]
    public void Code_reports_the_first_bad_character_and_its_position()
    {
        // FR-023 AC3
        var a = CobolString.Code("ab c");
        Assert.Equal(8, a.Header.ReturnCode);
        Assert.Equal("STR0003", a.Header.ReasonCode);
        Assert.Equal(3, a.BadPosition);
        Assert.Equal(" ", a.BadChar);
        Assert.Equal("N", a.Valid);
        Assert.Equal("Character [ ] at position 3 is not allowed in a code (A-Z 0-9 and - . / _ only)", a.Header.Message);
    }

    [Fact]
    public void Code_validation_paths()
    {
        var ok = CobolString.Code("AB-12./_");
        Assert.Equal((0, "Code accepted", "Y"), (ok.Header.ReturnCode, ok.Header.Message, ok.Valid));
        var folded = CobolString.Code("abc");
        Assert.Equal((4, "STR0004", "Code accepted and stored in upper case", "ABC", "Y"),
            (folded.Header.ReturnCode, folded.Header.ReasonCode, folded.Header.Message, folded.TextOut, folded.Changed));
        var empty = CobolString.Code("   ");
        Assert.Equal(("STR0001", "A code is required"), (empty.Header.ReasonCode, empty.Header.Message));
        var tooLong = CobolString.Code(new string('A', 41));
        Assert.Equal(("STR0002", 40, "Code is longer than 40 characters"), (tooLong.Header.ReasonCode, tooLong.BadPosition, tooLong.Header.Message));
        Assert.Equal("STR0002", CobolString.Code("ABCDEF", 5).Header.ReasonCode);
        Assert.Equal(0, CobolString.Code("ABCDEF", 999).Header.ReturnCode);
        var leading = CobolString.Code(" A");
        Assert.Equal((1, "STR0003"), (leading.BadPosition, leading.Header.ReasonCode));
    }

    [Fact]
    public void DocRef_keep_and_edge_cases()
    {
        Assert.Equal("SI000123", CobolString.DocRef("si 000123").TextOut);
        Assert.Equal("SI000123", CobolString.DocRef("SI/000123", "strip").TextOut);
        Assert.Equal("0", CobolString.DocRef("000", "STRIP").TextOut);
        Assert.Equal("AB12C", CobolString.DocRef("ab-12-c", "STRIP").TextOut);
        Assert.Equal("AAS118220", CobolString.DocRef("aas 118220").TextOut);
        var same = CobolString.DocRef("SI123", "STRIP");
        Assert.Equal((0, "", "N"), (same.Header.ReturnCode, same.Header.ReasonCode, same.Changed));
        var none = CobolString.DocRef("--/--");
        Assert.Equal((8, "STR0005", "The reference has no letters or digits", "N"),
            (none.Header.ReturnCode, none.Header.ReasonCode, none.Header.Message, none.Valid));
    }

    [Fact]
    public void Quantity_has_six_decimals_and_strip_trims_trailing_zeros()
    {
        Assert.Equal("1,234.500000", CobolString.Quantity(1234.5m).TextOut);
        Assert.Equal("12.5", CobolString.Quantity(12.5m, "STRIP").TextOut);
        Assert.Equal("12", CobolString.Quantity(12m, "STRIP").TextOut);
        Assert.Equal("-0.000001", CobolString.Quantity(-0.0000019m, "STRIP").TextOut);
        Assert.Equal("Quantity formatted", CobolString.Quantity(1m).Header.Message);
    }

    [Fact]
    public void Squeeze_and_upper()
    {
        var s = CobolString.Squeeze("  a   b\0\0c  ");
        Assert.Equal(("a b c", 5, "Y", "Spaces squeezed"), (s.TextOut, s.LengthOut, s.Changed, s.Header.Message));
        var u = CobolString.Upper("abc \u00e9");
        Assert.Equal(("ABC \u00e9", "Y", "Folded to upper case"), (u.TextOut, u.Changed, u.Header.Message));
        Assert.Equal("N", CobolString.Upper("ABC").Changed);
    }

    [Fact]
    public void Unknown_function_and_defaults()
    {
        var a = new StrutilArea { TextIn = "x" };
        a.Header.Function = "LOWER";
        CobolString.Execute(a);
        Assert.Equal((8, "STR0009", "STRUTIL does not support function LOWER", 1), (a.Header.ReturnCode, a.Header.ReasonCode, a.Header.Message, a.Header.MessageCount));
        Assert.Equal(("GBP", "KEEP", 40), (a.Currency, a.ZeroPolicy, a.MaxLength));
        Assert.Equal(("STRUTIL", "COM-004"), (a.Header.Program, a.Header.RequestRef));
    }
}
