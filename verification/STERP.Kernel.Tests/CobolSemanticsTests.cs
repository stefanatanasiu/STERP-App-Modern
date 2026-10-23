using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Kernel.Tests;

/// <summary>STFUNCS functions and COBOL numeric/date helpers (FR-STERP-PLATFORM-024; BR-ROUND-AWAY-001 .. BR-WEEKDAY-OF-001).</summary>
public sealed class CobolSemanticsTests
{
    [Theory]
    [InlineData("2.5", 0, "3")]
    [InlineData("-2.5", 0, "-3")]
    [InlineData("1.005", 2, "1.01")]
    [InlineData("-1.005", 2, "-1.01")]
    [InlineData("1.004999999999", 2, "1.00")]
    [InlineData("0.125", 2, "0.13")]
    [InlineData("1.23456", 12, "1.23456")]
    [InlineData("1.123456789", 12, "1.12345679")]
    [InlineData("1.5", -3, "2")]
    [InlineData("0.0000000000009", 8, "0")]
    public void RoundAway_rounds_exact_halves_away_from_zero_with_places_clamped_0_to_8(string value, int places, string expected)
    {
        // FR-024 AC1 (2.5 -> 3, -2.5 -> -3) and AC2 (places clamped to 8); decimals beyond 12 are truncated on entry
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            CobolNumeric.RoundAway(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), places));
    }

    [Fact]
    public void SignOf_is_three_way()
    {
        Assert.Equal(-1, CobolNumeric.SignOf(-0.01m));
        Assert.Equal(0, CobolNumeric.SignOf(0m));
        Assert.Equal(1, CobolNumeric.SignOf(5m));
    }

    [Fact]
    public void DaysInMonth_is_zero_outside_the_domain()
    {
        // FR-024 AC3
        Assert.Equal(29, CobolDate.DaysInMonth(2028, 2));
        Assert.Equal(0, CobolDate.DaysInMonth(1600, 2));
        Assert.Equal(28, CobolDate.DaysInMonth(2026, 2));
        Assert.Equal(30, CobolDate.DaysInMonth(2026, 4));
        Assert.Equal(0, CobolDate.DaysInMonth(2026, 13));
        Assert.Equal(0, CobolDate.DaysInMonth(10000, 1));
    }

    [Theory]
    [InlineData(20260200, 20260228)]
    [InlineData(20260231, 20260228)]
    [InlineData(20280215, 20280229)]
    [InlineData(20261031, 20261031)]
    [InlineData(16001201, 0)]
    [InlineData(20261300, 0)]
    public void EndOfMonth_replaces_the_day_without_validating_it(int date, int expected)
    {
        // FR-024 AC4
        Assert.Equal(expected, CobolDate.EndOfMonth(date));
    }

    [Theory]
    [InlineData(20261019, 1)]
    [InlineData(16010101, 1)]
    [InlineData(20261024, 6)]
    [InlineData(20261025, 7)]
    [InlineData(20260230, 0)]
    [InlineData(0, 0)]
    public void WeekdayOf_is_1_Monday_to_7_Sunday_and_0_when_invalid(int date, int expected)
    {
        // FR-024 AC5
        Assert.Equal(expected, CobolDate.WeekdayOf(date));
    }

    [Fact]
    public void IsLeap_follows_the_Gregorian_rule_inside_1601_9999_only()
    {
        Assert.Equal(0, CobolDate.IsLeapFunction(1600));
        Assert.Equal(1, CobolDate.IsLeapFunction(2000));
        Assert.Equal(0, CobolDate.IsLeapFunction(1900));
        Assert.Equal(1, CobolDate.IsLeapFunction(2028));
        Assert.Equal(0, CobolDate.IsLeapFunction(2026));
    }

    [Theory]
    [InlineData(20261019, 0)]
    [InlineData(20260229, 3)]
    [InlineData(20280229, 0)]
    [InlineData(20261301, 2)]
    [InlineData(20261000, 3)]
    [InlineData(16001231, 1)]
    [InlineData(0, 1)]
    public void TestDate_reports_the_first_invalid_part(int date, int expected) => Assert.Equal(expected, CobolDate.TestDate(date));

    [Fact]
    public void IntegerOfDate_and_DateOfInteger_round_trip_from_1601()
    {
        Assert.Equal(1, CobolDate.IntegerOfDate(16010101));
        Assert.Equal(CobolDate.MaxIntegerDate, CobolDate.IntegerOfDate(99991231));
        Assert.Equal(20261019, CobolDate.DateOfIntegerYyyymmdd(CobolDate.IntegerOfDate(20261019)));
        Assert.Equal(0, CobolDate.DateOfIntegerYyyymmdd(0));
        Assert.Equal(0, CobolDate.DateOfIntegerYyyymmdd(CobolDate.MaxIntegerDate + 1));
    }

    // Vectors observed on the deployed GnuCOBOL 3.2 runtime (GC32-BDB-SP1; identical under -std default, mf, ibm, acu, rm,
    // mvs): COMPUTE S9(18)V9(9) = FUNCTION NUMVAL(PIC X(40)), Agent 08 probe NUMVPRB (SME-039 characterization).
    [Theory]
    [InlineData("1         0       ", "10")]
    [InlineData("12A", "12")]
    [InlineData("ABC123", "123")]
    [InlineData(" 12.5 ", "12.5")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("-12", "-12")]
    [InlineData("12-", "-12")]
    [InlineData("12 CR", "-12")]
    [InlineData("+ 12", "12")]
    [InlineData("1 2 3", "123")]
    [InlineData("12.3.4", "12.34")]
    [InlineData("2026-10-20", "-20261020")]
    [InlineData("", "0")]
    [InlineData("   ", "0")]
    [InlineData("  -  5", "-5")]
    [InlineData("5DB", "-5")]
    [InlineData("--5", "-5")]
    [InlineData("12+", "12")]
    [InlineData("1E5", "15")]
    [InlineData("cr12", "0")]
    [InlineData("-12-", "-12")]
    [InlineData("X", "0")]
    [InlineData("0000000123.4500", "123.45")]
    [InlineData(".5", "0.5")]
    [InlineData("5.", "5")]
    [InlineData("1.234,56", "1.23456")]
    [InlineData("12cr", "-12")]
    [InlineData("  12  CR  ", "-12")]
    [InlineData("-", "0")]
    [InlineData("1-2", "-12")]
    [InlineData("+12+", "12")]
    [InlineData("12 -", "-12")]
    [InlineData("1.5-", "-1.5")]
    [InlineData("- 1 . 5", "-1.5")]
    [InlineData("12CRX", "-12")]
    [InlineData("DB12", "0")]
    [InlineData("$12", "12")]
    [InlineData("12 34.5 6", "1234.56")]
    [InlineData("1.15000000", "1.15")]
    [InlineData("12.5CR", "-12.5")]
    public void NumVal_matches_the_deployed_GnuCOBOL_runtime(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), CobolNumeric.NumVal(text));

    [Fact]
    public void NumVal_keeps_the_low_order_digits_a_receiving_field_can_hold() =>
        Assert.Equal(345678901234567890m, CobolNumeric.Store(CobolNumeric.NumVal("12345678901234567890"), 18, 9));

    [Fact]
    public void Sqlio_number_and_integer_edits_match_the_SQLIOCA_pictures()
    {
        Assert.Equal("1234.500000", CobolNumeric.SqlioNumber(1234.5m));
        Assert.Equal("-0.500000", CobolNumeric.SqlioNumber(-0.5m));
        Assert.Equal("0.123456", CobolNumeric.SqlioNumber(0.1234567m));
        Assert.Equal("0.000000", CobolNumeric.SqlioNumber(0m));
        Assert.Equal("234567890123.450000", CobolNumeric.SqlioNumber(1234567890123.45m));
        Assert.Equal("20261019", CobolNumeric.SqlioInteger(20261019));
        Assert.Equal("-7", CobolNumeric.SqlioInteger(-7));
    }

    [Fact]
    public void Exact_multiply_and_divide_truncate_or_round_at_the_receiving_field()
    {
        Assert.Equal(0.333333333333m, CobolNumeric.Divide(1m, 3m, 13, 12));
        Assert.Equal(0.666666666667m, CobolNumeric.Divide(2m, 3m, 13, 12, rounded: true));
        Assert.Equal(1.14942528m, CobolNumeric.Divide(1m, 0.87m, 9, 8));
        Assert.Equal(0.999999999999m, CobolNumeric.Multiply(3m, 0.333333333333m, 13, 12));
        // high-order truncation of the integer part (no ON SIZE ERROR)
        Assert.Equal(1234567.5m, CobolNumeric.Multiply(1001234567.5m, 1m, 7, 2));
        Assert.Equal(-1.5m, CobolNumeric.Multiply(-1.5m, 1m, 3, 1));
        Assert.Equal(1.5m, CobolNumeric.Multiply(-1.5m, 1m, 3, 1, signed: false));
        Assert.Equal(25.0m, CobolNumeric.MultiplyDivide(1000m, 2.5m, 100m, 13, 12));
        Assert.Throws<DivideByZeroException>(() => CobolNumeric.Divide(1m, 0m, 13, 12));
    }

    [Fact]
    public void Store_keeps_low_order_digits_and_truncates_decimals()
    {
        Assert.Equal(2345m, CobolNumeric.Store(12345m, 4, 0));
        Assert.Equal(1.23m, CobolNumeric.Store(1.239m, 13, 2));
        Assert.Equal(-1.23m, CobolNumeric.Store(-1.239m, 13, 2));
        Assert.Equal(1.24m, CobolNumeric.Store(1.235m, 13, 2, rounded: true));
    }

    [Theory]
    [InlineData("1234.567", "---,---,---,---,--9.99", "1,234.56")]
    [InlineData("-1234.5", "---,---,---,---,--9.99", "-1,234.50")]
    [InlineData("0", "---,---,---,---,--9.99", "0.00")]
    [InlineData("1234567.891234", "---,---,---,---,--9.9(6)", "1,234,567.891234")]
    [InlineData("0.5", "-(12)9.9(6)", "0.500000")]
    [InlineData("-0.5", "-(12)9.9(6)", "-0.500000")]
    [InlineData("1.15", "-(5)9.9(8)", "1.15000000")]
    [InlineData("123456.5", "-(5)9.9(8)", "23456.50000000")]
    [InlineData("30", "-(4)9", "30")]
    [InlineData("-5", "-(9)9", "-5")]
    [InlineData("0", "-(9)9", "0")]
    [InlineData("-12.5", "Z(9)9.99-", "12.50-")]
    [InlineData("12.5", "Z(9)9.99-", "12.50")]
    [InlineData("100.999", "-(12)9.99", "100.99")]
    public void Edit_pictures_truncate_and_suppress_like_a_MOVE_to_numeric_edited(string value, string picture, string expected) =>
        Assert.Equal(expected, CobolEdit.Format(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), picture));

    [Fact]
    public void Edited_fields_keep_their_width()
    {
        Assert.Equal("   1", CobolEdit.Field(1, "ZZZ9"));
        Assert.Equal("1234", CobolEdit.Field(1234, "ZZZ9"));
        Assert.Equal("  12.50 ", CobolEdit.Field(12.5m, "ZZZ9.99-"));
        Assert.Equal("  12.50-", CobolEdit.Field(-12.5m, "ZZZ9.99-"));
    }

    [Theory]
    [InlineData("0.5", 2, ".50")]
    [InlineData("-0.5", 2, "-.50")]
    [InlineData("12.5", 2, "12.50")]
    [InlineData("0", 2, ".00")]
    [InlineData("0", 0, "0")]
    [InlineData("100", 0, "100")]
    public void Odbc_character_conversion_of_numbers_has_no_leading_zero(string value, int scale, string expected) =>
        Assert.Equal(expected, OdbcText.Number(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), scale));

    [Fact]
    public void Sqlio_markers_are_replaced_outside_literals_identifiers_and_comments()
    {
        Assert.Equal("EXEC core.usp_CheckPermission @p1, @p2, @p3, @p4", Sqlio.Prepare("EXEC core.usp_CheckPermission ?, ?, ?, ?"));
        Assert.Equal("SELECT '?', [a?b], \"c?\", @p1 -- ?\nFROM t WHERE x = @p2 /* ? */",
            Sqlio.Prepare("SELECT '?', [a?b], \"c?\", ? -- ?\nFROM t WHERE x = ? /* ? */"));
        Assert.Equal("SELECT 'it''s ?', @p1", Sqlio.Prepare("SELECT 'it''s ?', ?"));
    }

    [Fact]
    public void Sqlio_parameters_follow_the_legacy_moves()
    {
        Assert.Equal("ABC", SqlioParm.Text("ABC   ").Value);
        Assert.Equal("", SqlioParm.Text("    ").Value);
        Assert.Null(SqlioParm.NullIfSpaces("   ").Value);
        Assert.Equal(new string('x', 256), SqlioParm.Text(new string('x', 300)).Value);
        Assert.Equal("00000000", SqlioParm.Digits(0, 8).Value);
        Assert.Equal("20261019", SqlioParm.Digits(20261019, 8).Value);
        Assert.Equal("0", SqlioParm.Integer(0).Value);
    }

    [Fact]
    public void Upper_case_folds_only_a_to_z()
    {
        Assert.Equal("ABC-\u00e9", CobolText.Upper("abc-\u00e9"));
        Assert.True(CobolText.IsBlank("\0\0\0", 3));
        Assert.True(CobolText.IsBlank("", 3));
        Assert.False(CobolText.IsBlank("\0 ", 2));
    }
}
