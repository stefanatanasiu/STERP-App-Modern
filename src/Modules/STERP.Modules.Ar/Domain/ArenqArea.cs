using Sterp.Platform.Kernel.Services;
using Sterp.Modules.Ar.Contracts;

namespace Sterp.Modules.Ar.Domain;

/// <summary>ARENQCA (cobol/copy/ARENQCA.cpy) - ARENQ customer enquiry. JSON form: OpenAPI schema ArArenqCommarea.</summary>
public sealed class ArenqArea : Commarea
{
    [Pic("X(20)")] public string Customer { get; set; } = "";
    [Pic("9(8)")] public int AsAt { get; set; }
    [Pic("9(8)", Date = true)] public int FromDate { get; set; }
    [Pic("9(8)", Date = true)] public int ToDate { get; set; }
    [Pic("X(120)")] public string CustomerName { get; set; } = "";
    [Pic("X(3)")] public string Currency { get; set; } = "";
    [Pic("X(10)")] public string Status { get; set; } = "";
    [Pic("S9(11)V99")] public decimal Balance { get; set; }
    [Pic("S9(11)V99")] public decimal Debits { get; set; }
    [Pic("S9(11)V99")] public decimal Credits { get; set; }
    [Pic("S9(11)V99")] public decimal Deposits { get; set; }
    [Pic("S9(11)V99")] public decimal NotDue { get; set; }
    [Pic("S9(11)V99")] public decimal Bucket1 { get; set; }
    [Pic("S9(11)V99")] public decimal Bucket2 { get; set; }
    [Pic("S9(11)V99")] public decimal Bucket3 { get; set; }
    [Pic("S9(11)V99")] public decimal Bucket4 { get; set; }
    [Pic("S9(11)V99")] public decimal OverdueTotal { get; set; }
    [Pic("S9(11)V99")] public decimal Disputed { get; set; }
    [Pic("S9(9)", Binary = true)] public int OpenItems { get; set; }
    [Pic("9(8)")] public int OldestDue { get; set; }
    [Pic("S9(5)")] public int WorstDays { get; set; }
    [Pic("S9(11)V99")] public decimal CreditLimit { get; set; }
    [Pic("S9(11)V99")] public decimal Exposure { get; set; }
    [Pic("S9(5)V99")] public decimal UtilisedPct { get; set; }
    [Pic("X")] public string HoldFlag { get; set; } = "";
    [Pic("S9(11)V99")] public decimal TurnoverInv { get; set; }
    [Pic("S9(11)V99")] public decimal OrderValue { get; set; }
    [Pic("S9(11)V99")] public decimal CreditSales { get; set; }
    [Pic("S9(11)V99")] public decimal ReceiptsValue { get; set; }
    [Pic("S9(9)", Binary = true)] public int ReceiptCount { get; set; }
    [Pic("S9(9)", Binary = true)] public int DishonourCount { get; set; }
    [Pic("S9(9)", Binary = true)] public int DisputeCount { get; set; }
    [Pic("S9(5)")] public int AvgDaysPaid { get; set; }
    [Pic("S9(5)")] public int PeriodDays { get; set; }
    [Pic("S9(7)V99")] public decimal Dso { get; set; }
    [Pic("X")] public string DsoDefined { get; set; } = "";
    [Pic("X(60)")] public string DsoBasis { get; set; } = "";
}
