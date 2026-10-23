using System.Text;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Ap.Engine;

/// <summary>
/// APHASH (cobol/online/ap/APHASH.cob; FR-STERP-AP-051, spec 4.13): the approval content hash. Eight 32-bit lanes over the
/// 300-byte space-padded text up to its last non-space byte (minimum 1); ORD(byte) is the byte value + 1; each lane rendered
/// as 8 lowercase hexadecimal digits, most significant first. The lane-1 seed literal 2166136261 exceeds PIC 9(9) and is held
/// as 166136261 (high-order truncation).
/// </summary>
public static class ApHash
{
    private static readonly ulong[] Seeds = [166136261, 5381000, 17, 987654321, 65599, 1000003, 131, 49979687];
    private static readonly ulong[] Multipliers = [16777619, 33, 131, 65599, 1099, 16411, 37, 2166];
    private const ulong Modulus = 4294967296;

    /// <summary>CALL "APHASH" USING text(X(300)) hash(X(64)).</summary>
    public static string Compute(string text)
    {
        var bytes = CobolText.Ansi.GetBytes(CobolText.Fit(text, 300));
        var len = 300;
        while (len >= 1 && bytes[len - 1] == (byte)' ') len--;
        if (len < 1) len = 1;
        var acc = (ulong[])Seeds.Clone();
        for (var i = 1; i <= len; i++)
        {
            ulong ord = (ulong)bytes[i - 1] + 1;
            for (var j = 1; j <= 8; j++)
            {
                var temp = acc[j - 1] * Multipliers[j - 1] + ord * (ulong)(i + j);
                acc[j - 1] = temp % Modulus;
            }
        }
        var sb = new StringBuilder(64);
        foreach (var a in acc) sb.Append(a.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
