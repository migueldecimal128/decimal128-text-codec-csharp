// SPDX-License-Identifier: MIT
//
// Everything reachable from outside is the methods on the class below.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

using Words = (ulong Hi, ulong Lo);

namespace Decimal128.TextCodec;

/// <summary>
/// Thin codec between the two IEEE 754-2019 decimal128 interchange encodings —
/// BID (Binary Integer Decimal) and DPD (Densely Packed Decimal) — and decimal
/// character sequences.
/// </summary>
/// <remarks>
/// <para>
/// A decimal128 encoding is 128 bits: a sign bit, a combination field and a
/// trailing significand field, laid out per IEEE 754-2019 §3.5.2. It crosses
/// this API as a <c>UInt128</c>. That is the entire boundary; there is no
/// value type, no context, no status flags.
/// </para>
///
/// <para>
/// This codec provides no arithmetic, no comparison and no field extraction.
/// For the full IEEE 754-2019 decimal128 implementation — arithmetic,
/// comparison, every rounding-direction attribute, IEEE status flags — see
/// https://decimal128.com.
/// </para>
///
/// <para>
/// <b>Rounding.</b> Rounding, where the text → bits direction requires it, is
/// the IEEE 754-2019 <c>roundTiesToEven</c> attribute and is not configurable.
/// IEEE 754-2019 §5.4.2 places <c>convertFromDecimalCharacter</c> under the
/// applicable rounding-direction attribute; this codec deliberately implements
/// only the ties-to-even case and is therefore a conforming subset of that
/// operation. Callers needing another rounding direction want the full
/// library.
/// </para>
///
/// <para>
/// Output form is the GDAS <c>to-scientific-string</c> form and is not
/// configurable. Cohort information is preserved: <c>"1.00"</c> and
/// <c>"1.0"</c> are distinct outputs from distinct encodings of the same
/// numerical value.
/// </para>
///
/// <para>
/// <b>Round-trip behavior.</b> The renderer's output round-trips exactly:
/// parsing rendered text and rendering it again reproduces that text, and
/// rendering a canonical encoding and parsing the result reproduces the
/// original bits. Three classes of input do not round-trip:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>More than 34 significant digits</b> are accepted and rounded to 34 with
/// <c>roundTiesToEven</c>. The rounded value is encoded; the input text is not
/// preserved.
/// </description></item>
/// <item><description>
/// <b>Magnitudes outside the representable range</b> overflow to
/// <c>Infinity</c> or underflow to a subnormal or zero, per IEEE 754-2019.
/// </description></item>
/// <item><description>
/// <b>Non-canonical bit patterns</b> render as something the encoder would
/// never have emitted, so re-parsing yields the canonical pattern instead.
/// </description></item>
/// </list>
///
/// <para>
/// <b>Non-canonical bit patterns.</b> Not every 128-bit pattern is canonical.
/// Each encoding has its own non-canonical forms — patterns the encoder would
/// never emit — and what makes a pattern non-canonical is a property of that
/// encoding, documented where that encoding is decoded.
/// </para>
///
/// <para>
/// Neither renderer can fail on one: every 128-bit pattern renders as the
/// value IEEE 754-2019 assigns it. Decoding is one-way for such a pattern,
/// because the parsers emit only canonical encodings, so re-parsing the
/// rendered text yields bits that differ from the original. Non-canonical
/// patterns never arise from this codec's own parsers; they appear only when
/// encodings come from outside, such as network or file data.
/// </para>
///
/// <para>
/// <b>Thread safety.</b> All functions are pure and safe for concurrent use.
/// </para>
///
/// <para>
/// <b>This implementation.</b> The renderers return a <c>string</c>; their
/// <c>Bid128ToAscii</c> and <c>Dpd128ToAscii</c> peers write into a
/// caller-owned <c>byte[]</c> and return a length, for callers who want no
/// allocation. The parsers write through an <c>out</c> encoding and return
/// <c>null</c> on success, or a message on failure. The implementation is one
/// file with no dependency beyond the base class library.
/// </para>
/// </remarks>
public static class Decimal128TextCodec
{

    // ------------------------------------------------------------------
    // Internal-state assertions
    //
    // Two mechanisms: one is a debugging aid, the other guards a write into a
    // caller's buffer.
    //
    //   verify   internal-consistency claims -- unreachable for ANY input, so a
    //            failure is a codec bug rather than bad data. [Conditional]
    //            drops the call and its arguments unless VERIFY_ENABLED is
    //            defined; the csproj defines it for Debug only, so a release
    //            consumer never takes a throw inside the library on data they
    //            do not control.
    //
    //   demand   the two claims that must hold in every configuration: that
    //            the caller's buffer is at least MaxLen, checked once on
    //            entry, and that a coefficient handed to the encoder is
    //            inside the precision limit. Live everywhere, because the
    //            alternative is overrunning the buffer or silently encoding
    //            an out-of-range value.
    //
    // Both route through `impossible`, which throws rather than calling
    // Environment.FailFast: a codec is a leaf library a caller embeds, so it
    // raises rather than taking the process down.
    // ------------------------------------------------------------------

    [DoesNotReturn]
    private static void impossible(string message)
    {
        throw new InvalidOperationException("decimal128-text-codec: impossible state: " + message);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void demand(bool condition, string message = "invariant")
    {
        if (!condition)
            impossible(message);
    }

    [Conditional("VERIFY_ENABLED")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void verify(bool condition, string message = "invariant")
    {
        if (!condition)
            impossible(message);
    }

    // ------------------------------------------------------------------
    // Public constants
    // ------------------------------------------------------------------

    /// <summary>
    /// The exact bound on a rendered decimal128, in ASCII character bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The widest scientific form <c>-9.999...E-6143</c> is 1 sign, 1 digit, 1
    /// radix point, 33 digits, 1 <c>E</c>, 1 sign and 4 exponent digits; the
    /// widest plain form <c>-0.00000999...</c> is 1 sign, 1 zero, 1 radix
    /// point, 5 leading zeros and 34 digits. Each form reaches exactly 42.
    /// </para>
    ///
    /// <para>
    /// The plain form's five leading zeros are pinned by the GDAS rule that
    /// selects it only when the adjusted exponent is at least -6, which puts
    /// five zeros between the radix point and the first significant digit. The
    /// exponent field cannot exceed four digits inside decimal128's range, so
    /// neither number can drift without the format itself changing.
    /// </para>
    ///
    /// <para>
    /// The renderers are checked against this bound once, on entry, rather
    /// than at every write: the buffer is confirmed to be at least this long
    /// and the forms above are what guarantee nothing longer is produced.
    /// Sizing it exactly rather than generously is what keeps the two halves
    /// of that argument in contact; with slack, an error in the arithmetic
    /// above would quietly consume the slack instead of showing up.
    /// </para>
    ///
    /// <para>
    /// A buffer passed to <c>Bid128ToAscii</c> must be at least this long. No
    /// NUL is written, so no room is reserved for one.
    /// </para>
    /// </remarks>
    public const int MaxLen = 42;

    private const ulong QNAN_PATTERN_HI = 0x7C00_0000_0000_0000;

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Render a BID128 encoding as a decimal character sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output is the GDAS <c>to-scientific-string</c> form: integer
    /// notation when the quantum exponent is zero (e.g. <c>"123"</c>), plain
    /// notation when the quantum exponent is negative and the adjusted
    /// exponent is at least -6 (e.g. <c>"123.45"</c>), normalized scientific
    /// notation otherwise (e.g. <c>"1.234E+5"</c>). The exponent sign is
    /// always written, as in <c>"6.02E+23"</c>. Cohort information is
    /// preserved: <c>"1.00"</c> and <c>"1.0"</c> are distinct outputs from
    /// distinct encodings of the same numerical value. </para>
    ///
    /// <para>
    /// Special values render as <c>"Infinity"</c>, <c>"-Infinity"</c>,
    /// <c>"NaN"</c> and <c>"sNaN"</c>, each with an optional sign and an
    /// optional payload suffix such as <c>"NaN123"</c>.
    /// </para>
    ///
    /// <para>
    /// The two interchange encodings differ only in how the coefficient is
    /// stored: BID128 as a binary integer, DPD128 as a leading digit plus
    /// eleven 10-bit declets. The sign and the 14-bit biased exponent are the
    /// same in both, and peer encodings of the same value render to identical
    /// text.
    /// </para>
    ///
    /// <para>
    /// This cannot fail. Every 128-bit pattern renders: a non-canonical BID128
    /// coefficient (≥ 10^34) renders as zero at the encoded exponent, and a
    /// NaN payload ≥ 10^33 renders as no payload, both per IEEE 754-2019
    /// §3.5.2.
    /// </para>
    /// </remarks>
    /// <param name="bits">the BID128 encoding.</param>
    /// <returns>the decimal character sequence for <paramref
    /// name="bits"/>.</returns>
    public static string Bid128ToString(UInt128 bits)
    {
        Span<byte> ascii = stackalloc byte[MaxLen];
        int n = decodeBid128toAscii(ascii, hiWord(bits), loWord(bits));
        return Encoding.Latin1.GetString(ascii[..n]);
    }

    /// <summary>
    /// Render a DPD128 encoding as a decimal character sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output is the GDAS <c>to-scientific-string</c> form: integer
    /// notation when the quantum exponent is zero (e.g. <c>"123"</c>), plain
    /// notation when the quantum exponent is negative and the adjusted
    /// exponent is at least -6 (e.g. <c>"123.45"</c>), normalized scientific
    /// notation otherwise (e.g. <c>"1.234E+5"</c>). The exponent sign is
    /// always written, as in <c>"6.02E+23"</c>. Cohort information is
    /// preserved: <c>"1.00"</c> and <c>"1.0"</c> are distinct outputs from
    /// distinct encodings of the same numerical value. </para>
    ///
    /// <para>
    /// Special values render as <c>"Infinity"</c>, <c>"-Infinity"</c>,
    /// <c>"NaN"</c> and <c>"sNaN"</c>, each with an optional sign and an
    /// optional payload suffix such as <c>"NaN123"</c>.
    /// </para>
    ///
    /// <para>
    /// The two interchange encodings differ only in how the coefficient is
    /// stored: BID128 as a binary integer, DPD128 as a leading digit plus
    /// eleven 10-bit declets. The sign and the 14-bit biased exponent are the
    /// same in both, and peer encodings of the same value render to identical
    /// text.
    /// </para>
    ///
    /// <para>
    /// This cannot fail. Every 128-bit pattern renders: a DPD128 coefficient
    /// is always in range, since a leading digit of at most 9 followed by
    /// eleven declets cannot reach 10^34; a NaN payload, carried in those same
    /// declets, is likewise always in range; and non-canonical declets decode
    /// to their value, per IEEE 754-2019 §3.5.2.
    /// </para>
    /// </remarks>
    /// <param name="bits">the DPD128 encoding.</param>
    /// <returns>the decimal character sequence for <paramref
    /// name="bits"/>.</returns>
    public static string Dpd128ToString(UInt128 bits)
    {
        Span<byte> ascii = stackalloc byte[MaxLen];
        int n = decodeDpd128toAscii(ascii, hiWord(bits), loWord(bits));
        return Encoding.Latin1.GetString(ascii[..n]);
    }

    /// <summary>
    /// Render a BID128 encoding into a caller-supplied buffer, allocating
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writes the ASCII text into <c>destination</c> starting at index 0 and
    /// returns its length; the text is <c>destination[..n]</c>. No terminator
    /// is written. <c>destination</c> must have at least <c>MaxLen</c>
    /// elements; <c>null</c> throws <c>ArgumentNullException</c>, and a shorter
    /// one throws <c>InvalidOperationException</c> on entry rather than
    /// corrupting anything, so an undersized buffer is a caller bug that
    /// surfaces immediately -- on every call, not only on the values that
    /// would have overflowed it.
    /// </para>
    ///
    /// <para>
    /// The text is identical to what <c>Bid128ToString</c> returns, and this
    /// form is the one to reach for when the destination is bytes — a stream,
    /// a socket, a CSV or JSON writer — because it leaves nothing for the
    /// collector.
    /// </para>
    ///
    /// <para>
    /// The two interchange encodings differ only in how the coefficient is
    /// stored: BID128 as a binary integer, DPD128 as a leading digit plus
    /// eleven 10-bit declets. The sign and the 14-bit biased exponent are the
    /// same in both, and peer encodings of the same value render to identical
    /// text.
    /// </para>
    /// </remarks>
    /// <param name="destination">the destination, at least <see
    /// cref="MaxLen"/> bytes.</param>
    /// <param name="bits">the BID128 encoding.</param>
    /// <returns>the number of bytes written.</returns>
    public static int Bid128ToAscii(byte[] destination, UInt128 bits)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return decodeBid128toAscii(destination, hiWord(bits), loWord(bits));
    }

    /// <summary>
    /// Render a DPD128 encoding into a caller-supplied buffer, allocating
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writes the ASCII text into <c>destination</c> starting at index 0 and
    /// returns its length; the text is <c>destination[..n]</c>. No terminator
    /// is written. <c>destination</c> must have at least <c>MaxLen</c>
    /// elements; <c>null</c> throws <c>ArgumentNullException</c>, and a shorter
    /// one throws <c>InvalidOperationException</c> on entry rather than
    /// corrupting anything, so an undersized buffer is a caller bug that
    /// surfaces immediately -- on every call, not only on the values that
    /// would have overflowed it.
    /// </para>
    ///
    /// <para>
    /// The text is identical to what <c>Dpd128ToString</c> returns, and this
    /// form is the one to reach for when the destination is bytes — a stream,
    /// a socket, a CSV or JSON writer — because it leaves nothing for the
    /// collector.
    /// </para>
    ///
    /// <para>
    /// The two interchange encodings differ only in how the coefficient is
    /// stored: BID128 as a binary integer, DPD128 as a leading digit plus
    /// eleven 10-bit declets. The sign and the 14-bit biased exponent are the
    /// same in both, and peer encodings of the same value render to identical
    /// text.
    /// </para>
    /// </remarks>
    /// <param name="destination">the destination, at least <see
    /// cref="MaxLen"/> bytes.</param>
    /// <param name="bits">the DPD128 encoding.</param>
    /// <returns>the number of bytes written.</returns>
    public static int Dpd128ToAscii(byte[] destination, UInt128 bits)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return decodeDpd128toAscii(destination, hiWord(bits), loWord(bits));
    }

    /// <summary>
    /// Parse a decimal character sequence into a BID128 encoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>null</c> on success, with the encoding written to
    /// <c>value</c>. On failure returns a human-readable message and writes
    /// the canonical positive quiet NaN, so a caller who discards the message
    /// still observes a legal IEEE 754-2019 datum rather than a stale value.
    /// The message is the engine's ParseStatus name for the failure --
    /// EMPTY_STRING, INVALID_FORMAT, UNEXPECTED_CHAR, DOUBLE_DOT,
    /// NO_COEFFICIENT_DIGIT, NO_EXPONENT_DIGIT, INVALID_UNDERSCORE_LOCATION,
    /// FAILURE_PARSING_INFINITY or FAILURE_PARSING_NAN -- and the conformance
    /// suite asserts it against the shared corpus, so it is part of the contract.
    /// </para>
    ///
    /// <para>
    /// Accepted input:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Plain notation: <c>"0"</c>, <c>"-1.5"</c>, <c>"123.456"</c>,
    /// <c>"0.0001"</c>.
    /// </description></item>
    /// <item><description>
    /// Scientific notation: <c>"1.5E10"</c>, <c>"-2.3e-7"</c>,
    /// <c>"6.02E+23"</c>. Both <c>E</c> and <c>e</c> are accepted as the
    /// exponent separator.
    /// </description></item>
    /// <item><description>
    /// Digit separators: <c>_</c> between two digits, in the coefficient or
    /// the exponent (<c>"1_000"</c>, <c>"1.234_567"</c>, <c>"1e1_0"</c>). An
    /// underscore must be both preceded and followed by a digit, so
    /// <c>"1_"</c>, <c>"_1"</c>, <c>"1__2"</c>, <c>"1_.5"</c> and
    /// <c>"1._5"</c> are all rejected.
    /// </description></item>
    /// <item><description>
    /// The case-insensitive words <c>Infinity</c>, <c>Inf</c>, <c>NaN</c>,
    /// <c>qNaN</c> and <c>sNaN</c>, each with an optional sign and an optional
    /// NaN payload of decimal digits (<c>"NaN123"</c>, <c>"-sNaN42"</c>). A
    /// payload may also be parenthesised, as <c>"NaN(123)"</c>.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// The parenthesised payload form is positional and balanced: <c>(</c>
    /// immediately after the word, at least one digit, <c>)</c> last.
    /// <c>"NaN()"</c>, <c>"NaN(1"</c>, <c>"NaN(1)2(3)"</c>, <c>"NaN[1]"</c>
    /// and <c>"NaN{1}"</c> are rejected. Rendering never emits parentheses,
    /// and the full decimal128 ports accept the same spelling.
    /// </para>
    ///
    /// <para>
    /// More than 34 significant digits round to 34, ties-to-even. Magnitudes
    /// above the representable range become <c>Infinity</c>; magnitudes below
    /// it become a subnormal or zero. None of those are failures.
    /// </para>
    ///
    /// <para>
    /// Failures are structural: an empty sequence, unrecognized characters,
    /// missing digits around the radix point, a malformed exponent field.
    /// </para>
    ///
    /// <para>
    /// The two interchange encodings differ only in how the coefficient is
    /// stored: BID128 as a binary integer, DPD128 as a leading digit plus
    /// eleven 10-bit declets. The same text parses to peer encodings of the
    /// same value in either format, and the canonical positive quiet NaN is
    /// the same bit pattern in both.
    /// </para>
    /// </remarks>
    /// <param name="value">receives the BID128 encoding, or the canonical
    /// positive quiet NaN on failure. Always written.</param>
    /// <param name="text">the character sequence to parse.</param>
    /// <returns><c>null</c> on success, or a human-readable message on
    /// failure.</returns>
    public static string? Bid128Parse(out UInt128 value, string text)
    {
        return parseInto(out value, formatIsBid: true, text: text);
    }

    /// <summary>
    /// Parse a decimal character sequence into a DPD128 encoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>null</c> on success, with the encoding written to
    /// <c>value</c>. On failure returns a human-readable message and writes
    /// the canonical positive quiet NaN, so a caller who discards the message
    /// still observes a legal IEEE 754-2019 datum rather than a stale value.
    /// The message is the engine's ParseStatus name for the failure --
    /// EMPTY_STRING, INVALID_FORMAT, UNEXPECTED_CHAR, DOUBLE_DOT,
    /// NO_COEFFICIENT_DIGIT, NO_EXPONENT_DIGIT, INVALID_UNDERSCORE_LOCATION,
    /// FAILURE_PARSING_INFINITY or FAILURE_PARSING_NAN -- and the conformance
    /// suite asserts it against the shared corpus, so it is part of the contract.
    /// </para>
    ///
    /// <para>
    /// Accepted input:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Plain notation: <c>"0"</c>, <c>"-1.5"</c>, <c>"123.456"</c>,
    /// <c>"0.0001"</c>.
    /// </description></item>
    /// <item><description>
    /// Scientific notation: <c>"1.5E10"</c>, <c>"-2.3e-7"</c>,
    /// <c>"6.02E+23"</c>. Both <c>E</c> and <c>e</c> are accepted as the
    /// exponent separator.
    /// </description></item>
    /// <item><description>
    /// Digit separators: <c>_</c> between two digits, in the coefficient or
    /// the exponent (<c>"1_000"</c>, <c>"1.234_567"</c>, <c>"1e1_0"</c>). An
    /// underscore must be both preceded and followed by a digit, so
    /// <c>"1_"</c>, <c>"_1"</c>, <c>"1__2"</c>, <c>"1_.5"</c> and
    /// <c>"1._5"</c> are all rejected.
    /// </description></item>
    /// <item><description>
    /// The case-insensitive words <c>Infinity</c>, <c>Inf</c>, <c>NaN</c>,
    /// <c>qNaN</c> and <c>sNaN</c>, each with an optional sign and an optional
    /// NaN payload of decimal digits (<c>"NaN123"</c>, <c>"-sNaN42"</c>). A
    /// payload may also be parenthesised, as <c>"NaN(123)"</c>.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// The parenthesised payload form is positional and balanced: <c>(</c>
    /// immediately after the word, at least one digit, <c>)</c> last.
    /// <c>"NaN()"</c>, <c>"NaN(1"</c>, <c>"NaN(1)2(3)"</c>, <c>"NaN[1]"</c>
    /// and <c>"NaN{1}"</c> are rejected. Rendering never emits parentheses,
    /// and the full decimal128 ports accept the same spelling.
    /// </para>
    ///
    /// <para>
    /// More than 34 significant digits round to 34, ties-to-even. Magnitudes
    /// above the representable range become <c>Infinity</c>; magnitudes below
    /// it become a subnormal or zero. None of those are failures.
    /// </para>
    ///
    /// <para>
    /// Failures are structural: an empty sequence, unrecognized characters,
    /// missing digits around the radix point, a malformed exponent field.
    /// </para>
    ///
    /// <para>
    /// The two interchange encodings differ only in how the coefficient is
    /// stored: BID128 as a binary integer, DPD128 as a leading digit plus
    /// eleven 10-bit declets. The same text parses to peer encodings of the
    /// same value in either format, and the canonical positive quiet NaN is
    /// the same bit pattern in both.
    /// </para>
    /// </remarks>
    /// <param name="value">receives the DPD128 encoding, or the canonical
    /// positive quiet NaN on failure. Always written.</param>
    /// <param name="text">the character sequence to parse.</param>
    /// <returns><c>null</c> on success, or a human-readable message on
    /// failure.</returns>
    public static string? Dpd128Parse(out UInt128 value, string text)
    {
        return parseInto(out value, formatIsBid: false, text: text);
    }

    /// <summary>
    /// <paramref name="value"/> is written on both paths -- the encoding on
    /// success, the canonical positive quiet NaN on failure -- so the caller
    /// never reads back what was there before the call. That is why a failure
    /// is reported beside the value rather than in place of it: a caller who
    /// discards the message still holds a legal IEEE 754-2019 datum.
    /// </summary>
    private static string? parseInto(out UInt128 value, bool formatIsBid, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Words words = (0, 0);
        string? errorStr = encodeStringOrError(ref words, formatIsBid: formatIsBid, str: text);
        if (errorStr != null)
            words = (QNAN_PATTERN_HI, 0);
        value = joinWords(words);
        return errorStr;
    }

    // ------------------------------------------------------------------
    // Format constants -- IEEE 754-2019 Table 3.6, decimal128
    // ------------------------------------------------------------------

    private const int K_WIDTH = 128; // storage width in bits

    private const int BIAS = 6176;

    private const int Q_MIN = -6176; // the minimum quantum exponent

    private const int Q_MAX = 6111;  // the maximum quantum exponent

    private const int T_WIDTH = 110; // trailing significand field width in bits

    private const int W5 = 17;       // combination field width in bits

    /// <summary>
    /// The trailing-significand mask for the high word.
    /// </summary>
    /// <remarks>
    /// Written directly as <c>(1 shl 110) - 1</c> this would lean on shift
    /// counts being masked to 6 bits, turning it into <c>(1 shl 46) - 1</c>.
    /// C# does mask its shift counts, so that spelling would work here -- but
    /// it would work by accident.
    /// The intended 46-bit mask is spelled out instead, so the code says what
    /// was meant.
    /// </remarks>
    private const ulong COEFF_T_HI_MASK = (1UL << (110 - 64)) - 1;

    // 10^34, one past the largest coefficient; maxx = max eXclusive
    private const ulong MAXX_COEFF_34_HI = 0x0001_ED09_BEAD_87C0;

    private const ulong MAXX_COEFF_34_LO = 0x378D_8E64_0000_0000;

    // 10^33, the smallest 34-digit coefficient
    private const ulong MIN_PRECISION_34_COEFF_HI = 0x0000_314D_C644_8D93;

    private const ulong MIN_PRECISION_34_COEFF_LO = 0x38C1_5B0A_0000_0000;

    // 33 nines -- the largest canonical NaN payload
    private const ulong MAX_PAYLOAD_HI = MIN_PRECISION_34_COEFF_HI;

    private const ulong MAX_PAYLOAD_LO = MIN_PRECISION_34_COEFF_LO - 1;

    private const ulong SIGN_BIT = 0x8000_0000_0000_0000;

    private const ulong INF_PATTERN_HI = 0x7800_0000_0000_0000;

    private const ulong SNAN_PATTERN_HI = 0x7E00_0000_0000_0000;

    private const ulong TEN_POW_15 = 1_000_000_000_000_000;

    private const ulong TEN_POW_18 = 1_000_000_000_000_000_000;

    /// <summary>
    /// Powers of 10 from <c>10^0</c> through <c>10^34</c> as 128-bit values in
    /// little-endian order: <c>POW10[2*i]</c> is the low 64 bits and
    /// <c>POW10[2*i + 1]</c> the high 64 bits of <c>10^i</c>.
    /// </summary>
    /// <remarks>
    /// Sized to the coefficient bound and indexed directly — no mask, no
    /// padding. Every reachable index is at most 34: the binding case is
    /// <see cref="calcDigitLen128"/>, whose input is always a coefficient or
    /// NaN payload below <c>10^34</c>, so <c>bitLen &lt;= 113</c> and
    /// <c>(bitLen * 1233) &gt;&gt; 12 &lt;= 34</c>. It doubles as the
    /// <c>5^n</c> source for the divide kernel, since <c>10^n &gt;&gt; n</c>
    /// is exactly <c>5^n</c>; there is no POW5 table.
    /// </remarks>
    private static readonly ulong[] POW10 =
    [
        // little-endian order
        1, 0,                     // 1 (10**0)
        0x0000_0000_0000_000A, 0, // 10 (10**1)
        0x0000_0000_0000_0064, 0, // 100 (10**2)
        0x0000_0000_0000_03E8, 0, // 1000 (10**3)
        0x0000_0000_0000_2710, 0, // 10000 (10**4)
        0x0000_0000_0001_86A0, 0, // 100000 (10**5)
        0x0000_0000_000F_4240, 0, // 1000000 (10**6)
        0x0000_0000_0098_9680, 0, // 10000000 (10**7)
        0x0000_0000_05F5_E100, 0, // 100000000 (10**8)
        0x0000_0000_3B9A_CA00, 0, // 1000000000 (10**9)
        0x0000_0002_540B_E400, 0, // 10000000000 (10**10)
        0x0000_0017_4876_E800, 0, // 100000000000 (10**11)
        0x0000_00E8_D4A5_1000, 0, // 1000000000000 (10**12)
        0x0000_0918_4E72_A000, 0, // 10000000000000 (10**13)
        0x0000_5AF3_107A_4000, 0, // 100000000000000 (10**14)
        0x0003_8D7E_A4C6_8000, 0, // 1000000000000000 (10**15)
        0x0023_86F2_6FC1_0000, 0, // 10000000000000000 (10**16)
        0x0163_4578_5D8A_0000, 0, // 100000000000000000 (10**17)
        0x0DE0_B6B3_A764_0000, 0, // 1000000000000000000 (10**18)
        0x8AC7_2304_89E8_0000, 0, // 10000000000000000000 (10**19)
        // minBitCount:64  maxBitCount:128
        0x6BC7_5E2D_6310_0000, 0x0000_0000_0000_0005, // 10**20
        0x35C9_ADC5_DEA0_0000, 0x0000_0000_0000_0036, // 10**21
        0x19E0_C9BA_B240_0000, 0x0000_0000_0000_021E, // 10**22
        0x02C7_E14A_F680_0000, 0x0000_0000_0000_152D, // 10**23
        0x1BCE_CCED_A100_0000, 0x0000_0000_0000_D3C2, // 10**24
        0x1614_0148_4A00_0000, 0x0000_0000_0008_4595, // 10**25
        0xDCC8_0CD2_E400_0000, 0x0000_0000_0052_B7D2, // 10**26
        0x9FD0_803C_E800_0000, 0x0000_0000_033B_2E3C, // 10**27
        0x3E25_0261_1000_0000, 0x0000_0000_204F_CE5E, // 10**28
        0x6D72_17CA_A000_0000, 0x0000_0001_431E_0FAE, // 10**29
        0x4674_EDEA_4000_0000, 0x0000_000C_9F2C_9CD0, // 10**30
        0xC091_4B26_8000_0000, 0x0000_007E_37BE_2022, // 10**31
        0x85AC_EF81_0000_0000, 0x0000_04EE_2D6D_415B, // 10**32
        0x38C1_5B0A_0000_0000, 0x0000_314D_C644_8D93, // 10**33
        0x378D_8E64_0000_0000, 0x0001_ED09_BEAD_87C0, // 10**34
    ];

    /// <summary>
    /// Residue classification for rounding decisions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When discarding low-order digits during truncation to 34 significant
    /// digits, the discarded part falls into one of four classes relative to
    /// half a ulp of the retained value:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>EXACT</c> — discarded part is zero; no rounding needed</item>
    ///   <item><c>LT_HALF</c> — strictly less than half; round down, toward zero</item>
    ///   <item><c>HALF</c> — exactly half; tie-break per the rounding direction</item>
    ///   <item><c>GT_HALF</c> — strictly greater than half; round up, away from zero</item>
    /// </list>
    /// <para>
    /// For ties-to-even, <c>HALF</c> rounds to the nearest even retained digit;
    /// <c>LT_HALF</c> and <c>GT_HALF</c> are unambiguous; <c>EXACT</c> is a
    /// no-op.
    /// </para>
    /// </remarks>
    private const int EXACT = 0;

    private const int LT_HALF = 1;

    private const int HALF = 2;

    private const int GT_HALF = 3;

    /// <summary>
    /// Maps a discarded decimal digit 0..9 to its residue class, two bits per
    /// digit packed low-to-high: 0 to EXACT, 1..4 to LT_HALF, 5 to HALF, 6..9
    /// to GT_HALF.
    /// </summary>
    private const int DIGIT_MAP = 0b11_11_11_11_10_01_01_01_01_00;

    /// <summary>
    /// Reciprocal-multiplication constants for unsigned <c>÷ 10^8</c> on a
    /// 64-bit dividend, exact for every input.
    /// </summary>
    /// <remarks>
    /// Compute <c>n / 10^8</c> as
    /// <c>mulhi64(n, M_U64_DIV_1E8) >> S_U64_DIV_1E8</c>. The shift is
    /// "+ 64 high" because <see cref="mulhi64"/> already returns the
    /// upper 64 bits of the 128-bit product; the named shift combines with that
    /// to give the effective magic-number divide.
    /// </remarks>
    private const ulong M_U64_DIV_1E8 = 0x8000_0000_0000_0000UL | 0x2BCC_7711_8461_CEFDUL;

    private const int S_U64_DIV_1E8 = 26; // + 64 high

    /// <summary>
    /// Reciprocal-multiplication constants for unsigned division by small
    /// powers of 10.
    /// </summary>
    /// <remarks>
    /// <c>M_U32_DIV_1E1</c> / <c>S_U32_DIV_1E1</c> divide by 10 and
    /// <c>M_U32_DIV_1E2</c> / <c>S_U32_DIV_1E2</c> by 100, both valid for
    /// 32-bit dividends. <c>M_U64_DIV_1E4</c> / <c>S_U64_DIV_1E4</c> divide by
    /// 10000 on 64-bit dividends and are used to split an 8-digit value into
    /// two 4-digit halves.
    /// </remarks>
    private const ulong M_U32_DIV_1E1 = 0xCCCC_CCCD;

    private const int S_U32_DIV_1E1 = 35;

    private const ulong M_U32_DIV_1E2 = 0x51EB_851F;

    private const int S_U32_DIV_1E2 = 37;

    private const ulong M_U64_DIV_1E4 = 0x346D_C5D6_3886_594B;

    private const int S_U64_DIV_1E4 = 11; // + 64 high

    private const ulong MASK32 = 0x0000_0000_FFFF_FFFF;

    private static ReadOnlySpan<byte> InfinityText => "Infinity"u8;

    private static ReadOnlySpan<byte> NanText => "NaN"u8;

    /// <summary>
    /// Every plain-form zero is a prefix of this literal. <c>k = -qExp</c> is
    /// the count of zeros after the radix point, so <c>k == 0</c> renders
    /// <c>"0"</c> with no radix point at all and <c>k &gt;= 1</c> renders
    /// <c>"0."</c> followed by <c>k</c> zeros — lengths 1 and <c>k + 2</c>.
    /// </summary>
    private static ReadOnlySpan<byte> PlainZero => "0.000000"u8;

    private static ReadOnlySpan<byte> ZeroExpText => "0E"u8;

    private const byte ascii_minus = 0x2D;

    private const byte ascii_plus = 0x2B;

    private const byte ascii_0 = 0x30;

    private const byte ascii_9 = 0x39;

    private const byte ascii_dot = 0x2E;

    private const byte ascii_E = 0x45;

    private const byte ascii_e = 0x65;

    private const byte ascii_s = 0x73;

    private const byte ascii_underscore = 0x5F;

    private const byte ascii_lparen = 0x28;

    private const byte ascii_rparen = 0x29;

    // ------------------------------------------------------------------
    // The 128-bit boundary
    //
    // Public in, public out. Everything between these and the encoders works in
    // a `dw1` / `dw0` word pair.
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong hiWord(UInt128 v) => (ulong)(v >> 64);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong loWord(UInt128 v) => (ulong)v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 joinWords(Words w) => ((UInt128)w.Hi << 64) | w.Lo;

    // ------------------------------------------------------------------
    // Low-level unsigned arithmetic
    // ------------------------------------------------------------------

    /// <summary>
    /// High 64 bits of the 128-bit product <c>x * y</c>.
    /// </summary>
    /// <remarks>
    /// Where no unsigned 64-bit multiply-high exists this decomposes into four
    /// 32x32 partial products; .NET has one, and the JIT turns it into the
    /// machine's widening multiply.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong mulhi64(ulong x, ulong y)
    {
        return Math.BigMul(x, y, out _);
    }

    /// <summary>
    /// Bit length of a 128-bit value: the position of the highest set bit plus
    /// one, and 0 for the zero value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int calcBitLen128(ulong dw1, ulong dw0)
    {
        if (dw1 != 0)
            return 128 - BitOperations.LeadingZeroCount(dw1);
        if (dw0 != 0)
            return 64 - BitOperations.LeadingZeroCount(dw0);
        return 0;
    }

    /// <summary>
    /// Decimal digit count of a 128-bit value.
    /// </summary>
    /// <remarks>
    /// <c>(bitLen * 1233) >> 12</c> is <c>floor(bitLen * log10(2))</c>, which
    /// is either the exact digit count or one too small; a single unsigned
    /// compare against the next-larger power of ten resolves the off-by-one.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int calcDigitLen128(ulong dw1, ulong dw0)
    {
        int bitLen = calcBitLen128(dw1, dw0);
        int loDigitCount = (bitLen * 1233) >> 12;
        int hiDigitCount = loDigitCount + 1;
        int pow10Offset = loDigitCount << 1;
        ulong p1 = POW10[pow10Offset + 1];
        ulong p0 = POW10[pow10Offset];
        if (dw1 != p1)
            return (dw1 < p1) ? hiDigitCount - 1 : hiDigitCount;
        return (dw0 < p0) ? hiDigitCount - 1 : hiDigitCount;
    }

    /// <summary>
    /// Multiply a 64-bit value by <c>10^pow10</c> and add <paramref name="a"/>,
    /// as a 128-bit result.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Words u128FmaPow10(ulong x, int pow10, ulong a)
    {
        ulong y = POW10[pow10 << 1];
        // the low word of a genuine 128-bit product, so both wrap by design
        ulong prodLo = x * y;
        ulong prodHi = mulhi64(x, y);
        ulong sumLo = prodLo + a;
        ulong sumHi = prodHi + ((sumLo < prodLo) ? 1UL : 0UL);
        return (sumHi, sumLo);
    }

    // ------------------------------------------------------------------
    // The divide kernel
    // ------------------------------------------------------------------

    /// <summary>
    /// Divide a 113-bit value by <c>10^pow10</c> for <paramref name="pow10"/>
    /// in <c>1..9</c>, returning the quotient and the remainder.
    /// </summary>
    /// <remarks>
    /// Table-free: <c>10^pow10</c> factors as <c>2^pow10 * 5^pow10</c>, so the
    /// low <c>pow10</c> bits fall straight out as the low part of the remainder
    /// and the shifted value is divided by <c>5^pow10</c> with two hardware
    /// 64-bit divides.
    /// </remarks>
    private static (ulong qHi, ulong qLo, ulong rem) u113DivModPow10Max9(ulong dwHi, ulong dwLo, int pow10)
    {
        verify(pow10 >= 1 && pow10 <= 9);
        verify(BitOperations.LeadingZeroCount(dwHi) >= 15);

        int sh = pow10;
        ulong mask = (1UL << sh) - 1;
        ulong b = dwLo & mask;
        ulong sHi = dwHi >> sh;
        ulong sLo = (dwLo >> sh) | (dwHi << (64 - sh));

        int k = 50 - pow10;
        ulong maskK = (1UL << k) - 1;
        ulong hi63 = (sHi << (14 + sh)) | (sLo >> k);
        ulong loK = sLo & maskK;
        // two slots per power in POW10, hence the doubled index
        ulong d = POW10[pow10 << 1] >> sh; // = 5^pow10
        ulong q1 = hi63 / d;
        ulong r1 = hi63 % d;
        ulong mid = (r1 << k) | loK;
        ulong q0 = mid / d;
        ulong r0 = mid % d;

        // `q1 << k` deliberately drops the bits that `qHi` picks up; the low k
        // bits it leaves are zero, so the add below is an or and cannot carry.
        return (q1 >> (14 + sh), (q1 << k) + q0, (r0 << sh) | b);
    }

    // ------------------------------------------------------------------
    // Residue -- how much was discarded, relative to half a ulp
    // ------------------------------------------------------------------

    /// <summary>Classify a single discarded decimal digit 0..9.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int residueFromDecimalDigit(int digit)
    {
        return (DIGIT_MAP >> (digit << 1)) & 0x03;
    }

    /// <summary>
    /// Combine a primary <paramref name="roundResidue"/> with a
    /// <paramref name="stickyResidue"/> from less-significant digits, in the
    /// round-bit-and-sticky-bit style. The sticky residue collapses to a single
    /// nonzero bit which is OR'd into the low bit of the round residue.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int residueMerge(int roundResidue, int stickyResidue)
    {
        int s = (stickyResidue & 1) | (stickyResidue >> 1);
        return (roundResidue | s) & 0x03;
    }

    /// <summary>
    /// Classify a 128-bit value relative to half of <c>10^pow10</c>.
    /// </summary>
    /// <remarks>
    /// Used in the underflow boundary case, where the entire coefficient is
    /// discarded by truncation, so the residue is decided by where it falls
    /// relative to half the truncation divisor.
    /// </remarks>
    private static int residueFromValuePow10(ulong dw1, ulong dw0, int pow10)
    {
        if ((dw1 | dw0) == 0)
            return EXACT;
        int pow10Index = pow10 << 1;
        ulong pow10Hi = POW10[pow10Index + 1];
        ulong pow10Lo = POW10[pow10Index];

        ulong halfHi = pow10Hi >> 1;
        ulong halfLo = (pow10Hi << 63) | (pow10Lo >> 1);
        if (dw1 != halfHi)
            return (dw1 < halfHi) ? LT_HALF : GT_HALF;
        if (dw0 != halfLo)
            return (dw0 < halfLo) ? LT_HALF : GT_HALF;
        return HALF;
    }

    /// <summary>
    /// Divide a 128-bit coefficient by <c>10^truncationNeeded</c>, returning
    /// the quotient and the round-and-sticky residue class of what was
    /// discarded. Truncates in chunks of up to 9 digits via
    /// <see cref="u113DivModPow10Max9"/>.
    /// </summary>
    private static (ulong hi, ulong lo, int residue) u128ScaleDownPow10(ulong dw1, ulong dw0, int truncationNeeded)
    {
        verify((dw1 | dw0) != 0);
        verify(truncationNeeded > 0 && truncationNeeded < calcDigitLen128(dw1, dw0));
        int scaleResidue = EXACT;
        ulong dw1T = dw1;
        ulong dw0T = dw0;
        int digitsRemaining = truncationNeeded;
        do
        {
            int stepPow10 = Math.Min(digitsRemaining, 9);
            var step = u113DivModPow10Max9(dw1T, dw0T, stepPow10);
            dw1T = step.qHi;
            dw0T = step.qLo;
            int stepResidue = residueFromValuePow10(0, step.rem, stepPow10);
            scaleResidue = residueMerge(stepResidue, scaleResidue);
            digitsRemaining -= stepPow10;
        } while (digitsRemaining > 0);
        return (dw1T, dw0T, scaleResidue);
    }

    // ==================================================================
    // DPD declet conversion
    //
    // A declet is 10 bits holding three decimal digits, 0..999. This is the one
    // place BID and DPD genuinely differ: BID stores the 34-digit coefficient as
    // a binary integer, DPD as a leading digit plus eleven declets.
    //
    // Both directions are pure boolean arithmetic -- Cowlishaw's dpd2bcd and
    // bcd2dpd -- rather than lookup tables, measured as the smaller and faster
    // choice.
    // ==================================================================

    /// <summary>
    /// Decode a 10-bit DPD declet into <c>0..999</c>.
    /// </summary>
    /// <remarks>
    /// Mapping: bit9..bit0 as <c>p q r s t u v w x y</c>. Accepts the 24
    /// non-canonical declets as well as the 1000 canonical ones, per
    /// Cowlishaw's dpd2bcd.
    /// </remarks>
    private static ulong dpdDecodeDeclet(ulong declet)
    {
        verify(declet <= 1023);

        if (declet == 0)
            return 0;

        // Unpack to bits p..y
        ulong p = (declet >> 9) & 1;
        ulong q = (declet >> 8) & 1;
        ulong r = (declet >> 7) & 1;
        ulong s = (declet >> 6) & 1;
        ulong t = (declet >> 5) & 1;
        ulong u = (declet >> 4) & 1;
        ulong v = (declet >> 3) & 1;
        ulong w = (declet >> 2) & 1;
        ulong x = (declet >> 1) & 1;
        ulong y = declet & 1;

        ulong ns = s ^ 1;
        ulong nx = x ^ 1;
        ulong nv = v ^ 1;
        ulong nw = w ^ 1;
        ulong nt = t ^ 1;

        ulong a = (v & w) & (ns | t | nx);
        ulong b = p & (nv | nw | (s & ~t & x));
        ulong c = q & (nv | nw | (s & ~t & x));
        ulong d = r;

        ulong e = v & ((nw & x) | (~t & x) | (s & x));
        ulong f = (s & (nv | nx)) | (p & ns & t & v & w & x);
        ulong g = (t & (nv | nx)) | (q & ns & t & w);
        ulong h = u;

        ulong i = v & ((nw & nx) | (w & x & (s | t)));
        ulong j = (nv & w)
            | (s & v & nw & x)
            | (p & w & (nx | (ns & ~t)));
        ulong k = (nv & x) | (t & nw & x) | (q & v & w & (nx | (ns & nt)));
        ulong m = y;

        // Pack BCD digit bits -> three digits
        ulong d2 = (a << 3) | (b << 2) | (c << 1) | d;
        ulong d1 = (e << 3) | (f << 2) | (g << 1) | h;
        ulong d0 = (i << 3) | (j << 2) | (k << 1) | m;

        return d2 * 100 + d1 * 10 + d0;
    }

    /// <summary>
    /// Encode <c>0..999</c> into a 10-bit DPD declet, per Cowlishaw's bcd2dpd.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="dpdDecodeDeclet"/> over canonical declets. A
    /// non-canonical declet decodes to a value that re-encodes to the canonical
    /// declet for that value, so <c>encode(decode(x)) == x</c> only for
    /// canonical <c>x</c>.
    /// </remarks>
    private static ulong dpdEncodeDeclet(ulong n)
    {
        verify(n <= 999);

        if (n == 0)
            return 0;

        // reciprocal-multiply digit extraction (optimized for n <= 999)
        ulong q10 = (n * 205) >> 11; // n / 10
        ulong q100 = (n * 41) >> 12; // n / 100

        ulong d2 = q100;
        ulong d1 = q10 - (q100 * 10);
        ulong d0 = n - (q10 * 10);

        // BCD bits (MSB to LSB: 8, 4, 2, 1)
        ulong a = (d2 >> 3) & 1;
        ulong b = (d2 >> 2) & 1;
        ulong c = (d2 >> 1) & 1;
        ulong d = d2 & 1;

        ulong e = (d1 >> 3) & 1;
        ulong f = (d1 >> 2) & 1;
        ulong g = (d1 >> 1) & 1;
        ulong h = d1 & 1;

        ulong i = (d0 >> 3) & 1;
        ulong j = (d0 >> 2) & 1;
        ulong k = (d0 >> 1) & 1;
        ulong m = d0 & 1;

        ulong na = a ^ 1;
        ulong ne = e ^ 1;
        ulong ni = i ^ 1;

        ulong p = b | ((a & j) | ((a & f) & i));
        ulong q = c | ((a & k) | ((a & g) & i));
        ulong r = d;
        ulong s = (f & (na | ni)) | ((na & (e & j)) | (e & i));
        ulong t = g | ((na & (e & k)) | (a & i));
        ulong u = h;
        ulong v = a | (e | i);
        ulong w = a | ((e & i) | (ne & j));
        ulong x = e | ((a & i) | (na & k));
        ulong y = m;

        return (p << 9) | (q << 8) | (r << 7) | (s << 6) |
               (t << 5) | (u << 4) | (v << 3) | (w << 2) |
               (x << 1) | y;
    }

    /// <summary>
    /// Decode up to seven packed DPD declets into a binary integer.
    /// </summary>
    /// <remarks>
    /// Declets are ten bits each, least significant first. The topmost may be
    /// narrower than ten bits, which is how the coefficient's leading digit is
    /// carried: 0..9 encodes to itself as a declet.
    /// </remarks>
    private static ulong binFromDeclets(ulong declets)
    {
        ulong bin = 0;
        ulong rest = declets;
        ulong multiplier = 1;
        while (rest != 0)
        {
            bin += dpdDecodeDeclet(rest & 0x3FF) * multiplier;
            // The last iteration multiplies past 10^18 and the product is never
            // used, since rest is zero by then, so it is let wrap.
            multiplier *= 1000;
            rest >>= 10;
        }
        return bin;
    }

    /// <summary>
    /// Encode a binary integer of at most 18 digits into six packed declets,
    /// least significant first. The inverse of <see cref="binFromDeclets"/>
    /// over that range.
    /// </summary>
    private static ulong decletsFromBin(ulong bin)
    {
        verify(bin < TEN_POW_18);
        ulong rest = bin;
        ulong declets = 0;
        int shift = 0;
        while (rest != 0)
        {
            // reciprocal-multiply divide by 1000
            ulong q = mulhi64(rest, 0x020C_49BA_5E35_3F7D) >> 3;
            declets |= dpdEncodeDeclet(rest - (q * 1000)) << shift;
            shift += 10;
            rest = q;
        }
        return declets;
    }

    // ------------------------------------------------------------------
    // Rendering integers into ASCII
    // ------------------------------------------------------------------

    /// <summary>
    /// Render exactly 8 decimal digits of <paramref name="dw"/> into
    /// <paramref name="ascii"/>, ending at <paramref name="offMaxx"/>. The
    /// caller must ensure <c>dw &lt; 10^8</c>; fewer significant digits are
    /// padded with leading zeros.
    /// </summary>
    /// <remarks>
    /// A binary cascade: <c>÷ 10^4</c> once to split into two 4-digit halves,
    /// then <c>÷ 10^2</c> twice to split each half into 2-digit pairs, then
    /// <c>÷ 10</c> four times for the individual digits. The eight stores are
    /// independent, and the single hoisted range check lets the JIT drop the
    /// per-store bounds checks.
    /// </remarks>
    private static void render8DigitsBeforeIndex(ulong dw, Span<byte> ascii, int offMaxx)
    {
        ulong abcd = mulhi64(dw, M_U64_DIV_1E4) >> S_U64_DIV_1E4;
        ulong efgh = dw - (abcd * 10000);

        ulong ab = (abcd * M_U32_DIV_1E2) >> S_U32_DIV_1E2;
        ulong cd = abcd - (ab * 100);

        ulong ef = (efgh * M_U32_DIV_1E2) >> S_U32_DIV_1E2;
        ulong gh = efgh - (ef * 100);

        ulong a = (ab * M_U32_DIV_1E1) >> S_U32_DIV_1E1;
        ulong b = ab - (a * 10);

        ulong c = (cd * M_U32_DIV_1E1) >> S_U32_DIV_1E1;
        ulong d = cd - (c * 10);

        ulong e = (ef * M_U32_DIV_1E1) >> S_U32_DIV_1E1;
        ulong f = ef - (e * 10);

        ulong g = (gh * M_U32_DIV_1E1) >> S_U32_DIV_1E1;
        ulong h = gh - (g * 10);

        ascii[offMaxx - 8] = (byte)(ascii_0 + a);
        ascii[offMaxx - 7] = (byte)(ascii_0 + b);
        ascii[offMaxx - 6] = (byte)(ascii_0 + c);
        ascii[offMaxx - 5] = (byte)(ascii_0 + d);
        ascii[offMaxx - 4] = (byte)(ascii_0 + e);
        ascii[offMaxx - 3] = (byte)(ascii_0 + f);
        ascii[offMaxx - 2] = (byte)(ascii_0 + g);
        ascii[offMaxx - 1] = (byte)(ascii_0 + h);
    }

    /// <summary>
    /// Render exactly <paramref name="renderDigitCount"/> decimal digits of
    /// <paramref name="dw"/> into <paramref name="ascii"/>, ending at
    /// <paramref name="offMaxx"/>. Fewer significant digits are padded with
    /// leading zeros.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two phases: peel 4 digits per iteration while at least 4 remain, then
    /// one digit at a time for the remaining 0–3.
    /// </para>
    /// <para>
    /// Kept private here. It is public in the legacy base this family descends
    /// from, which was an accidental leak — nothing outside the codec ever
    /// called it.
    /// </para>
    /// </remarks>
    private static void renderTailDigitsBeforeIndex(int renderDigitCount, ulong dw, Span<byte> ascii, int offMaxx)
    {
        ulong t = dw;
        int remainingDigitCount = renderDigitCount;
        int ib = offMaxx;
        while (remainingDigitCount >= 4)
        {
            ulong t0 = mulhi64(t, M_U64_DIV_1E4) >> S_U64_DIV_1E4;
            ulong abcd = t - (t0 * 10000);
            t = t0;
            ulong ab = (abcd * M_U32_DIV_1E2) >> S_U32_DIV_1E2;
            ulong cd = abcd - (ab * 100);
            ulong a = (ab * M_U32_DIV_1E1) >> S_U32_DIV_1E1;
            ulong b = ab - (a * 10);
            ulong c = (cd * M_U32_DIV_1E1) >> S_U32_DIV_1E1;
            ulong d = cd - (c * 10);
            ascii[ib - 4] = (byte)(ascii_0 + a);
            ascii[ib - 3] = (byte)(ascii_0 + b);
            ascii[ib - 2] = (byte)(ascii_0 + c);
            ascii[ib - 1] = (byte)(ascii_0 + d);
            ib -= 4;
            remainingDigitCount -= 4;
        }
        while (remainingDigitCount > 0)
        {
            ulong divTen = (t * M_U32_DIV_1E1) >> S_U32_DIV_1E1;
            ulong digit = t - (divTen * 10);
            ib -= 1;
            ascii[ib] = (byte)(ascii_0 + digit);
            t = divTen;
            remainingDigitCount -= 1;
        }
        verify(offMaxx - ib == renderDigitCount);
    }

    /// <summary>
    /// Render a 128-bit unsigned integer as <paramref name="digitPrintCount"/>
    /// decimal digits into <paramref name="ascii"/>, ending at offset
    /// <c>off + digitPrintCount</c>.
    /// </summary>
    /// <remarks>
    /// <para>Three phases:</para>
    /// <list type="number">
    ///   <item>While <c>dw1T != 0</c>: peel 8 digits per outer iteration via
    ///     three <c>÷ 10^8</c> divisions on rotating 96-bit chunks, using
    ///     <see cref="mulhi64"/> for the high half of each multi-word
    ///     divide.</item>
    ///   <item>Once the value fits in 64 bits: peel 8 digits at a time via a
    ///     single <c>÷ 10^8</c> per iteration.</item>
    ///   <item>For 1–7 leftover digits: delegate to
    ///     <see cref="renderTailDigitsBeforeIndex"/>.</item>
    /// </list>
    /// <para>
    /// The caller must have allocated at least <c>off + digitPrintCount</c>
    /// bytes and is responsible for any leading-zero padding outside the
    /// rendered range.
    /// </para>
    /// </remarks>
    private static int u128ToAscii(int digitPrintCount, ulong dw1, ulong dw0, Span<byte> ascii, int off)
    {
        ulong dw1T = dw1;
        ulong dw0T = dw0;
        int ich = off + digitPrintCount;
        int remainingDigitCount = digitPrintCount;
        while (dw1T != 0)
        {
            ulong q2 = mulhi64(dw1T, M_U64_DIV_1E8) >> S_U64_DIV_1E8;
            ulong r2 = dw1T - (q2 * 100_000_000);
            ulong s2 = (r2 << 32) | (dw0T >> 32);
            ulong q1 = mulhi64(s2, M_U64_DIV_1E8) >> S_U64_DIV_1E8;
            ulong r1 = s2 - (q1 * 100_000_000);
            ulong s1 = (r1 << 32) | (dw0T & MASK32);
            ulong q0 = mulhi64(s1, M_U64_DIV_1E8) >> S_U64_DIV_1E8;
            ulong r0 = s1 - (q0 * 100_000_000);
            // q1 and q0 are each strictly below 2^32 -- r2 and r1 are remainders
            // mod 10^8 -- so this recombination cannot overflow
            dw0T = (q1 << 32) + q0;
            dw1T = q2;
            render8DigitsBeforeIndex(r0, ascii, ich);
            ich -= 8;
            remainingDigitCount -= 8;
        }
        while (remainingDigitCount >= 8)
        {
            ulong t0 = mulhi64(dw0T, M_U64_DIV_1E8) >> S_U64_DIV_1E8;
            ulong r0 = dw0T - (t0 * 100_000_000);
            dw0T = t0;
            render8DigitsBeforeIndex(r0, ascii, ich);
            ich -= 8;
            remainingDigitCount -= 8;
        }
        if (remainingDigitCount > 0)
            renderTailDigitsBeforeIndex(remainingDigitCount, dw0T, ascii, ich);
        return digitPrintCount;
    }

    /// <summary>
    /// Write a signed decimal exponent at <paramref name="off"/>, returning its
    /// length. The magnitude never exceeds 6176, so it is at most four digits.
    /// </summary>
    private static int appendInt(Span<byte> ascii, int off, int v)
    {
        int signLen = 0;
        int magnitude = v;
        if (v < 0)
        {
            ascii[off] = ascii_minus;
            signLen = 1;
            magnitude = -v;
        }
        int digitLen;
        if (magnitude >= 1000)
            digitLen = 4;
        else if (magnitude >= 100)
            digitLen = 3;
        else if (magnitude >= 10)
            digitLen = 2;
        else
            digitLen = 1;
        renderTailDigitsBeforeIndex(digitLen, (ulong)magnitude, ascii, off + signLen + digitLen);
        return signLen + digitLen;
    }

    // ------------------------------------------------------------------
    // Rendering a decoded value
    // ------------------------------------------------------------------

    private static int infAscii(Span<byte> ascii, bool sign)
    {
        int signLen = (sign) ? 1 : 0;
        ascii[0] = ascii_minus; // overwritten by the 'I' when unsigned
        InfinityText.CopyTo(ascii[signLen..]);
        return signLen + InfinityText.Length;
    }

    private static int nanAscii(Span<byte> ascii, bool sign, bool isSignaling, ulong payloadHi, ulong payloadLo)
    {
        int signLen = (sign) ? 1 : 0;
        int sLen = (isSignaling) ? 1 : 0;
        int wordOff = signLen + sLen;
        int baseLen = wordOff + NanText.Length;
        ascii[0] = ascii_minus;   // overwritten by the 's' or the 'N' when unsigned
        ascii[signLen] = ascii_s; // overwritten by the 'N' when quiet
        NanText.CopyTo(ascii[wordOff..]);
        if ((payloadHi | payloadLo) == 0 ||
            // non canonical IEEE 754-2019 3.5.2 c) 2)
            MAX_PAYLOAD_HI < payloadHi ||
            (payloadHi == MAX_PAYLOAD_HI && MAX_PAYLOAD_LO < payloadLo))
        {
            return baseLen;
        }
        int digitLen = calcDigitLen128(payloadHi, payloadLo);
        u128ToAscii(digitLen, payloadHi, payloadLo, ascii, baseLen);
        return baseLen + digitLen;
    }

    private static int zerAscii(Span<byte> ascii, bool sign, int qExp)
    {
        int signLen = (sign) ? 1 : 0;
        ascii[0] = ascii_minus; // overwritten by the '0' when unsigned
        if (qExp <= 0 && qExp >= -6)
        {
            int k = -qExp;
            int zeroLen = (k == 0) ? 1 : k + 2;
            PlainZero[..zeroLen].CopyTo(ascii[signLen..]);
            return signLen + zeroLen;
        }
        // A positive exponent needs an explicit '+'; a negative one prints its
        // own '-' from appendInt. Write the '+' unconditionally and let
        // appendInt land on top of it when qExp < 0 — the same discipline
        // as the leading sign, pointed the other way. It is correct only
        // because appendInt emits a '-' for a negative value and emits
        // nothing for a positive one, and because the plain form above has
        // already taken every qExp in [-6, 0], so this branch never sees zero.
        int plusLen = (qExp > 0) ? 1 : 0;
        int baseLen = signLen + ZeroExpText.Length + plusLen;
        ZeroExpText.CopyTo(ascii[signLen..]);
        ascii[signLen + ZeroExpText.Length] = ascii_plus; // overwritten by the '-' when qExp < 0
        return baseLen + appendInt(ascii, baseLen, qExp);
    }

    /// <summary>
    /// Format a finite non-zero value with <c>qExp == 0</c> as a plain integer
    /// sequence: no radix point, no exponent.
    /// </summary>
    private static int fnzToIntegerAscii(Span<byte> ascii, bool sign, int digitLen, ulong dw1, ulong dw0)
    {
        verify(digitLen > 0);
        int signLen = (sign) ? 1 : 0;
        // written unconditionally: when there is no sign the first digit
        // overwrites it, which is cheaper than branching
        ascii[0] = ascii_minus;
        u128ToAscii(digitLen, dw1, dw0, ascii, signLen);
        return signLen + digitLen;
    }

    /// <summary>
    /// Format a finite non-zero value in fixed-point notation, with an explicit
    /// radix point and the appropriate number of leading zeros —
    /// <c>"0.00012345"</c>.
    /// </summary>
    /// <remarks>
    /// Renders the coefficient digits, then shifts the trailing digits right by
    /// one position to open a slot for the radix point. Leading zeros are
    /// written directly into the buffer before the digits.
    /// </remarks>
    private static int fnzToDecimalPointAscii(Span<byte> ascii, bool sign, int qExp, int digitLen, ulong dw1, ulong dw0)
    {
        int digitsRightOfDecimal = -qExp;
        int leadingZeroCount = Math.Max(1 + digitsRightOfDecimal - digitLen, 0);
        int signLen = (sign) ? 1 : 0;
        int decimalPointLen = 1;
        int totalLen = signLen + leadingZeroCount + decimalPointLen + digitLen;
        ascii[0] = ascii_minus;
        for (int i = signLen; i <= leadingZeroCount; ++i) // there is one extra here
            ascii[i] = ascii_0;
        u128ToAscii(digitLen, dw1, dw0, ascii, signLen + leadingZeroCount);
        for (int i = totalLen - 1; i >= totalLen - digitsRightOfDecimal; --i)
            ascii[i] = ascii[i - 1];
        ascii[totalLen - digitsRightOfDecimal - 1] = ascii_dot;
        return totalLen;
    }

    /// <summary>
    /// Format a finite non-zero value in normalized scientific notation —
    /// <c>"1.234E+5"</c>, <c>"-9.99E-200"</c>.
    /// </summary>
    /// <remarks>
    /// Renders the coefficient digits, inserts a radix point after the leading
    /// digit when there is more than one, then appends <c>E</c>, the exponent
    /// sign and the absolute exponent. The sign is always written: <c>+</c> for
    /// non-negative exponents, <c>-</c> for negative, per the GDAS
    /// <c>to-scientific-string</c> form.
    /// </remarks>
    private static int fnzToNormalizedScientificAscii(Span<byte> ascii, bool sign, int qExp, int digitLen, ulong dw1, ulong dw0)
    {
        verify(digitLen > 0);
        int eExp = qExp + (digitLen - 1);
        // this can be computed branchlessly off a 32-bit sign shift; the
        // exponent is bounded well inside an int, so say it directly
        int eExpAbs = (eExp < 0) ? -eExp : eExp;
        int signLen = (sign) ? 1 : 0;
        int decimalPointLen = (digitLen > 1) ? 1 : 0;
        int expELen = 1;
        int expSignLen = 1;
        byte expSignByte = (eExp < 0) ? ascii_minus : ascii_plus;
        int expDigitLen;
        if (eExpAbs >= 1000)
            expDigitLen = 4;
        else if (eExpAbs >= 100)
            expDigitLen = 3;
        else if (eExpAbs >= 10)
            expDigitLen = 2;
        else
            expDigitLen = 1;
        int totalLen = signLen + decimalPointLen + digitLen + expELen + expSignLen + expDigitLen;
        ascii[0] = ascii_minus;
        u128ToAscii(digitLen, dw1, dw0, ascii, signLen + decimalPointLen);
        if (decimalPointLen > 0)
        {
            ascii[signLen] = ascii[signLen + 1];
            ascii[signLen + 1] = ascii_dot;
        }
        int iE = signLen + decimalPointLen + digitLen;
        ascii[iE] = ascii_E;
        ascii[iE + 1] = expSignByte;
        renderTailDigitsBeforeIndex(expDigitLen, (ulong)eExpAbs, ascii, totalLen);
        return totalLen;
    }

    /// <summary>
    /// Finite non-zero: pick the form. Integer when the quantum exponent is
    /// zero, fixed-point when it is negative and the adjusted exponent is at
    /// least <c>-6</c>, scientific otherwise — the GDAS auto-select rule.
    /// </summary>
    private static int fnzAscii(Span<byte> ascii, bool sign, int qExp, ulong dw1, ulong dw0)
    {
        int digitLen = calcDigitLen128(dw1, dw0);
        int eExp = qExp + (digitLen - 1);
        if (qExp == 0)
            return fnzToIntegerAscii(ascii, sign, digitLen, dw1, dw0);
        if (qExp <= 0 && eExp >= -6)
            return fnzToDecimalPointAscii(ascii, sign, qExp, digitLen, dw1, dw0);
        return fnzToNormalizedScientificAscii(ascii, sign, qExp, digitLen, dw1, dw0);
    }

    // ------------------------------------------------------------------
    // bits -> text
    // ------------------------------------------------------------------

    // ==================================================================
    // Rendering into a buffer
    //
    // The renderers proper, and the only ones. Every form is produced here,
    // into a span the caller owns; the two string entry points render into a
    // stackalloc and wrap it, for one allocation -- the string itself.
    //
    // These leaf renderers know nothing about BID or DPD: both decoders
    // reduce an encoding to (sign, qExp, coefficient) and hand it here.
    //
    // A set of string-returning peers used to sit above these to short-circuit
    // three interned constants — "Infinity", a bare NaN and every plain-form
    // zero, all of which cost nothing to return. Keeping them meant a second
    // copy of the field extraction and class dispatch per encoding, kept in
    // step only by the corpus running both forms past the same expected text.
    // One renderer per encoding is worth more than the constants; those three
    // cases now allocate a string like every other.
    //
    // None of the optional leading characters live in the string data. The
    // sign, and the 's' that marks a signaling NaN, are each written
    // unconditionally and then overwritten by whatever follows when they are
    // not wanted. The one exception is a TRAILING '+' in the exponent-form
    // zero, noted where it happens.
    // ==================================================================
    /// <summary>
    /// Render a BID128 encoding into the caller's buffer.
    /// </summary>
    /// <remarks>
    /// Field extraction follows IEEE 754-2019 §3.5.2; the class it lands on
    /// — Infinity, NaN (quiet or signaling, with optional payload), zero
    /// (canonical or non-canonical), or finite non-zero — picks the leaf
    /// renderer. <see cref="Bid128ToString(UInt128)"/> and
    /// <see cref="Bid128ToAscii(byte[], UInt128)"/> both wrap what this writes.
    /// </remarks>
    private static int decodeBid128toAscii(Span<byte> ascii, ulong bid128Hi, ulong bid128Lo)
    {
        // IEEE754-2019 Table 3.6 -- Decimal interchange format parameters, p 23
        const int t = T_WIDTH; // trailing significand field width in bits
        verify(1 + W5 + t == K_WIDTH);

        // The API's contract is a buffer of at least MaxLen, so that is what
        // is checked -- once, on every call, rather than per value. A shorter
        // buffer that happens to fit the value in hand is still a caller bug,
        // and one that would otherwise surface only on the data that overflows
        // it.
        demand(ascii.Length >= MaxLen, "the buffer is at least MaxLen bytes");

        // 1 + 17 + 110 == 128
        // 1 bit for the sign
        bool sign = ((bid128Hi & SIGN_BIT) != 0);
        // w5 bit combination field ... for bid128 0x1FFFF
        int combination = (int)((bid128Hi >> (t - 64)) & 0x1FFFF);
        ulong coeffTHi = bid128Hi & COEFF_T_HI_MASK;

        ulong coeffHi;
        int qExp;
        if ((combination >> (W5 - 2)) != 0b11)
        {
            //  IEEE754-2019 3.5.2 c) 2) i) -- p 21
            //   If G0 and G1 together are one of 00, 01, or 10, then the biased
            //   exponent E is formed from G0 through Gw+1 and the significand
            //   is formed from bits Gw+2 through the end of the encoding (including T).
            //
            // w+5=17 w=12 Gw+1=G13 Gw+2=G14
            // the top 14 bits of the combination field represent the biased exponent ...
            int biasedExponentE = combination >> 3;
            qExp = biasedExponentE - BIAS;
            // ... and the bottom 3 bits are the most significant bits of the significand
            coeffHi = (ulong)(combination & 0x07);
        }
        else if ((combination >> (W5 - 4)) != 0b1111)
        {
            // IEEE754-2019 3.5.2 c) 2) ii) -- p 21
            //  If G0 and G1 together are 11 and G2 and G3 together are one
            //  of 00, 01, or 10, then the biased exponent E is formed from
            //  G2 through Gw+3 and the significand is formed by prefixing
            //  the 4 bits (8 + Gw+4) to T.
            int biasedExponentE = (combination >> 1) & ((1 << (W5 - 5 + 2)) - 1);
            qExp = biasedExponentE - BIAS;
            coeffHi = 8 + (ulong)(combination & 1);
        }
        else if ((combination >> (W5 - 5)) == 0b11110)
        {
            // if the top 5 bits are 0b11110 then Infinity
            return infAscii(ascii, sign);
        }
        else
        {
            // if the top 5 bits are 0b11111 then NaN,
            // with the next bit determining signaling NaN
            verify((combination >> (W5 - 5)) == 0b11111);
            bool isSignalingNaN = (((combination >> (W5 - 6)) & 1) != 0);
            return nanAscii(ascii, sign, isSignalingNaN, coeffTHi, bid128Lo);
        }

        ulong dw1 = (coeffHi << (t - 64)) | coeffTHi;
        ulong dw0 = bid128Lo;
        // IEEE754-2019 3.5.2 p21
        //  If the value exceeds the maximum, the significand c is
        //  non-canonical and the value used for c is zero.
        if ((dw1 | dw0) == 0 ||
            MAXX_COEFF_34_HI < dw1 ||
            (dw1 == MAXX_COEFF_34_HI && (MAXX_COEFF_34_LO - 1) < dw0))
        {
            return zerAscii(ascii, sign, qExp);
        }
        return fnzAscii(ascii, sign, qExp, dw1, dw0);
    }

    /// <summary>
    /// Render a DPD128 encoding into the caller's buffer.
    /// </summary>
    /// <remarks>
    /// The peer of <see cref="decodeBid128toAscii"/>: both reduce an encoding
    /// to <c>(sign, qExp, coefficient)</c> and then dispatch to the same leaf
    /// renderers, which have never seen an interchange format.
    /// </remarks>
    private static int decodeDpd128toAscii(Span<byte> ascii, ulong dpd128Hi, ulong dpd128Lo)
    {
        const int w = 12;      // exponent continuation width
        const int t = T_WIDTH; // trailing significand field width in bits

        // The API's contract is a buffer of at least MaxLen, so that is what
        // is checked -- once, on every call, rather than per value. A shorter
        // buffer that happens to fit the value in hand is still a caller bug,
        // and one that would otherwise surface only on the data that overflows
        // it.
        demand(ascii.Length >= MaxLen, "the buffer is at least MaxLen bytes");

        bool sign = ((dpd128Hi & SIGN_BIT) != 0);
        int combination = (int)((dpd128Hi >> (t - 64)) & 0x1FFFF);
        // in DPD the low w bits of the biased exponent always sit at the bottom of G
        int expContinuation = combination & 0x0FFF;

        ulong d0;
        int qExp;
        bool isNaN = false;
        bool isSignaling = false;

        if ((combination >> (W5 - 2)) != 0b11)
        {
            // G0G1 is 00, 01 or 10: leading digit 0..7, exponent MSBs in G0G1
            int expMSB = combination >> (W5 - 2);
            qExp = ((expMSB << w) | expContinuation) - BIAS;
            d0 = (ulong)((combination >> w) & 0b111);
        }
        else if ((combination >> (W5 - 4)) != 0b1111)
        {
            // G0G1 is 11 and G2G3 is not 11: leading digit 8..9, exponent MSBs in G2G3
            int expMSB = (combination >> (W5 - 4)) & 0b11;
            qExp = ((expMSB << w) | expContinuation) - BIAS;
            d0 = 8 + (ulong)((combination >> w) & 0b1);
        }
        else if ((combination >> (W5 - 5)) == 0b11110)
        {
            return infAscii(ascii, sign);
        }
        else
        {
            verify((combination >> (W5 - 5)) == 0b11111);
            isNaN = true;
            isSignaling = (((combination >> (W5 - 6)) & 1) != 0);
            d0 = 0;
            qExp = 0;
        }

        // Rebuild the binary coefficient from the declets. The leading digit
        // rides along as a sixth declet of the high group: values 0..9 encode
        // to themselves as a declet, so it decodes back unchanged.
        ulong decletsHi = (d0 << 50) | ((dpd128Hi & 0x3FFF_FFFF_FFFF) << 4) | (dpd128Lo >> 60);
        ulong decletsLo = dpd128Lo & 0x0FFF_FFFF_FFFF_FFFF; // low 60 bits, six declets
        ulong binHi = binFromDeclets(decletsHi);
        ulong binLo = binFromDeclets(decletsLo);

        ulong dw1;
        ulong dw0;
        if (binHi == 0)
        {
            dw1 = 0;
            dw0 = binLo;
        }
        else
        {
            // binHi * 10^18 + binLo is a genuine 128-bit product: the low word
            // is the wrapping sum, and the carry out of it feeds the high word.
            // (C# wraps by default in an unchecked context.)
            dw0 = (binHi * TEN_POW_18) + binLo;
            ulong carry = (dw0 < binLo) ? 1UL : 0UL;
            dw1 = mulhi64(binHi, TEN_POW_18) + carry;
        }

        if (isNaN)
            return nanAscii(ascii, sign, isSignaling, dw1, dw0);
        if ((dw1 | dw0) == 0)
            return zerAscii(ascii, sign, qExp);
        return fnzAscii(ascii, sign, qExp, dw1, dw0);
    }

    // ------------------------------------------------------------------
    // Packing a finalized value into an encoding
    // ------------------------------------------------------------------

    /// <summary>
    /// Pack the sign, biased quantum exponent and 113-bit coefficient into the
    /// BID128 encoding.
    /// </summary>
    /// <remarks>
    /// Always the short-form encoding — combination-field top bits not
    /// <c>11</c> — which is correct for any canonical coefficient, since its
    /// top 3 bits necessarily fit in 0..7.
    /// </remarks>
    private static Words bid128SetFinite(bool sign, int qExp, ulong dw1, ulong dw0)
    {
        verify(qExp >= Q_MIN && qExp <= Q_MAX);
        verify(calcDigitLen128(dw1, dw0) <= 34);
        int qBiased = qExp + BIAS;
        ulong s = (sign) ? SIGN_BIT : 0;
        ulong g = (ulong)qBiased << (3 + 46);
        return (s | g | dw1, dw0);
    }

    /// <summary>
    /// Pack a finite value into DPD128. The peer of
    /// <see cref="bid128SetFinite"/>: same inputs, same sign bit, same 14-bit
    /// biased exponent, and a coefficient of the same 34 digits — laid out as a
    /// leading digit carried in the combination field plus eleven declets.
    /// </summary>
    /// <remarks>
    /// The leading digit cannot be packed independently of the exponent: DPD
    /// puts it in the same five-bit prefix as the exponent's top two bits, so
    /// unlike BID there is no shared "pack sign and exponent" step to factor
    /// out.
    /// </remarks>
    private static Words dpd128SetFinite(bool sign, int qExp, ulong dw1, ulong dw0)
    {
        verify(qExp >= Q_MIN && qExp <= Q_MAX);
        verify(calcDigitLen128(dw1, dw0) <= 34);

        // Split the coefficient into its low 18 digits and its high 16. Two
        // divisions by 10^9 rather than one by 10^18, so the existing 128-bit
        // kernel does the work and no wider machinery is needed.
        var step1 = u113DivModPow10Max9(dw1, dw0, 9);
        var step2 = u113DivModPow10Max9(step1.qHi, step1.qLo, 9);
        ulong binLo = (step2.rem * 1_000_000_000) + step1.rem;
        verify(step2.qHi == 0); // the high 16 digits fit in one word
        ulong binHi = step2.qLo;

        // the top digit rides in the combination field; the other 15 are declets
        ulong d0 = binHi / TEN_POW_15;
        ulong declets5Hi = decletsFromBin(binHi - (d0 * TEN_POW_15));
        ulong declets6Lo = decletsFromBin(binLo);
        verify(d0 <= 9);

        int biasedQExp = qExp + BIAS;
        int expMSB = (biasedQExp >> 12) & 0x3;
        int expLSB = biasedQExp & 0x0FFF;
        int gPrefix = (d0 <= 7)
            ? (expMSB << 3) | (int)d0                     // G0G1 = exponent MSBs
            : 0b11000 | (expMSB << 1) | ((int)d0 & 1);    // G0G1 = 11, digit is 8 or 9
        int signCombo = ((sign ? 1 : 0) << 17) | (gPrefix << 12) | expLSB;

        return (((ulong)signCombo << 46) | (declets5Hi >> 4),
                (declets5Hi << 60) | declets6Lo);
    }

    /// <summary>
    /// Pack a finite value into whichever interchange format the caller asked
    /// for.
    /// </summary>
    /// <remarks>
    /// BID and DPD are peers: both carry the same sign, the same 14-bit biased
    /// exponent and the same 34-digit coefficient, and differ only in how that
    /// coefficient is laid out. Everything upstream of this point — rounding,
    /// clamping and the underflow region — is format-neutral.
    /// </remarks>
    private static Words setFinite(bool formatIsBid, bool sign, int qExp, ulong dw1, ulong dw0)
    {
        return (formatIsBid)
            ? bid128SetFinite(sign, qExp, dw1, dw0)
            : dpd128SetFinite(sign, qExp, dw1, dw0);
    }

    private static Words bid128SetNaN(bool sign, bool isSignaling, ulong payloadDw1, ulong payloadDw0)
    {
        ulong withoutPayload =
            ((isSignaling) ? SNAN_PATTERN_HI : QNAN_PATTERN_HI) | ((sign) ? SIGN_BIT : 0);
        return (withoutPayload | payloadDw1, payloadDw0);
    }

    /// <summary>
    /// Pack a NaN into DPD128. The payload occupies the whole 110-bit trailing
    /// field as eleven declets — 33 digits, with no leading digit in the
    /// combination field, since G0..G4 are fixed at 11111 and G5 carries the
    /// signaling flag.
    /// </summary>
    private static Words dpd128SetNaN(bool sign, bool isSignaling, ulong payloadDw1, ulong payloadDw0)
    {
        verify(calcDigitLen128(payloadDw1, payloadDw0) <= 33);

        var step1 = u113DivModPow10Max9(payloadDw1, payloadDw0, 9);
        var step2 = u113DivModPow10Max9(step1.qHi, step1.qLo, 9);
        ulong binLo = (step2.rem * 1_000_000_000) + step1.rem;
        verify(step2.qHi == 0); // a 33-digit payload leaves 15 digits up top
        ulong declets5Hi = decletsFromBin(step2.qLo);
        ulong declets6Lo = decletsFromBin(binLo);

        int gField = (0b11111 << 12) | ((isSignaling) ? 1 << 11 : 0);
        int signCombo = ((sign ? 1 : 0) << 17) | gField;
        return (((ulong)signCombo << 46) | (declets5Hi >> 4),
                (declets5Hi << 60) | declets6Lo);
    }

    /// <summary>
    /// Pack a NaN into whichever interchange format the caller asked for.
    /// </summary>
    /// <remarks>
    /// A NaN payload is a coefficient, so the formats diverge here exactly as
    /// they do for finite values: BID keeps it as a binary integer, DPD as
    /// declets. Infinity by contrast has no coefficient, which is why it is
    /// byte-identical in both and needs no such split.
    /// </remarks>
    private static Words setNaN(bool formatIsBid, bool sign, bool isSignaling,
                                ulong payloadDw1, ulong payloadDw0)
    {
        return (formatIsBid)
            ? bid128SetNaN(sign, isSignaling, payloadDw1, payloadDw0)
            : dpd128SetNaN(sign, isSignaling, payloadDw1, payloadDw0);
    }

    // ------------------------------------------------------------------
    // Finalize: rounding, clamping, underflow
    // ------------------------------------------------------------------

    /// <summary>Encode <c>Infinity</c>, or <c>-Infinity</c>.</summary>
    private static Words finalizeOverflow(bool sign)
    {
        return (((sign) ? SIGN_BIT : 0) | INF_PATTERN_HI, 0);
    }

    /// <summary>
    /// Test whether a coefficient/exponent pair fits as a canonical encoding:
    /// exponent in <c>Q_MIN..Q_MAX</c> and coefficient in <c>0..10^34-1</c>.
    /// </summary>
    /// <remarks>
    /// The fast path uses the high word's leading-zero count to cheaply confirm
    /// the coefficient has at most 113 bits, in which case it is necessarily
    /// below 10^34, since 2^113 &gt; 10^34. The slow path compares against
    /// 10^34.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool coeffQExpFit(ulong dw1, ulong dw0, int qExp)
    {
        if (qExp < Q_MIN || qExp > Q_MAX)
            return false;
        if (BitOperations.LeadingZeroCount(dw1) > 128 - 113)
            return true;
        int pow10Offset = 34 << 1;
        ulong maxxHi = POW10[pow10Offset + 1];
        return (dw1 < maxxHi || (dw1 == maxxHi && dw0 < POW10[pow10Offset]));
    }

    /// <summary>
    /// Encode a finite value whose natural quantum exponent exceeds
    /// <c>Q_MAX</c> but whose coefficient has enough leading capacity to absorb
    /// the excess.
    /// </summary>
    /// <remarks>
    /// Multiplies the coefficient by <c>10^qExcess</c>, reducing the exponent
    /// to exactly <c>Q_MAX</c>. This preserves the value while bringing the
    /// encoding into representable range. The caller must have verified there
    /// is room (<c>digitLen + qExcess &lt;= 34</c>).
    /// </remarks>
    private static Words finalizeClamping(bool formatIsBid, bool sign, int qExp, ulong dw1, ulong dw0)
    {
        int qExcess = qExp - Q_MAX;
        verify(qExcess > 0 && qExcess <= 34 - calcDigitLen128(dw1, dw0));
        int pow10Offset = qExcess << 1;
        ulong y1 = POW10[pow10Offset + 1];
        ulong y0 = POW10[pow10Offset];
        // A 128x128 product truncated to 128 bits: each partial product below
        // wraps by construction, and the
        // discarded halves are known zero because the caller checked the result
        // fits in 34 digits.
        ulong pp00Hi = mulhi64(dw0, y0);
        ulong pp00Lo = dw0 * y0;
        ulong pp10Lo = dw1 * y0;
        ulong pp01Lo = dw0 * y1;
        ulong dw0Scaled = pp00Lo;
        ulong dw1Scaled = pp00Hi + pp10Lo + pp01Lo;
        verify(coeffQExpFit(dw1Scaled, dw0Scaled, Q_MAX));
        return setFinite(formatIsBid, sign, Q_MAX, dw1Scaled, dw0Scaled);
    }

    /// <summary>
    /// Handle the underflow boundary case, where the most-significant digit of
    /// the coefficient sits exactly one position right of the representable
    /// range.
    /// </summary>
    /// <remarks>
    /// The coefficient value itself decides the rounding: comparing it against
    /// <c>(10^digitLen) / 2</c> yields the residue class. Combined with any
    /// incoming residue that drives a ties-to-even decision between zero —
    /// always even, and the default outcome — and 1 ulp, the smallest
    /// subnormal, reached only when the total residue is GT_HALF.
    /// </remarks>
    private static Words finalizeUnderflowBoundary(bool formatIsBid, bool sign,
                                                   ulong dw1, ulong dw0, int residueIn)
    {
        int digitLen = calcDigitLen128(dw1, dw0);
        verify(digitLen != 0);
        int scaleResidue = residueFromValuePow10(dw1, dw0, digitLen);
        int totalResidue = residueMerge(scaleResidue, residueIn);
        // ties-to-even: round away from zero only at GT_HALF, since the
        // alternative result is 0, which is even
        return setFinite(formatIsBid, sign, Q_MIN, 0, (totalResidue == GT_HALF) ? 1UL : 0UL);
    }

    private static Words finalizeSubnormal(bool formatIsBid, bool sign, int qExpIn,
                                           ulong dw1In, ulong dw0In, int residueIn)
    {
        int truncationNeeded = Q_MIN - qExpIn;
        verify(truncationNeeded > 0 && truncationNeeded < calcDigitLen128(dw1In, dw0In));

        var scaled = u128ScaleDownPow10(dw1In, dw0In, truncationNeeded);
        int totalResidue = residueMerge(scaled.residue, residueIn);
        ulong dw1T = scaled.hi;
        ulong dw0T = scaled.lo;
        int qExpT = Q_MIN;

        bool roundUp = (totalResidue == GT_HALF || (totalResidue == HALF && (dw0T & 1) != 0));
        if (roundUp)
        {
            // apply rounding -- wrapping, as in step 6.1
            dw0T = dw0T + 1;
            dw1T = dw1T + ((dw0T == 0) ? 1UL : 0UL);
            if (dw1T == MAXX_COEFF_34_HI && dw0T == MAXX_COEFF_34_LO)
            {
                dw1T = MIN_PRECISION_34_COEFF_HI;
                dw0T = MIN_PRECISION_34_COEFF_LO;
                qExpT += 1;
            }
        }
        return setFinite(formatIsBid, sign, qExpT, dw1T, dw0T);
    }

    /// <summary>
    /// Dispatch underflow handling for a value whose exponent is below Emin.
    /// </summary>
    /// <remarks>
    /// Three cases: truncation needed exceeds the digit count, so the result is
    /// zero, the value being below half the smallest subnormal; truncation
    /// equals the digit count, a boundary case where the leading digit of the
    /// coefficient decides the rounding; or truncation is less than the digit
    /// count, representable as a subnormal with some precision preserved and
    /// rounding applied.
    /// </remarks>
    private static Words finalizeUnderflowRegion(bool formatIsBid, bool sign, int qExp,
                                                 ulong dw1, ulong dw0, int residue)
    {
        // IEEE 754-2019 7.5 Underflow -- the subnormal region
        verify(qExp < Q_MIN);
        int truncationNeeded = Q_MIN - qExp;
        int digitLen = calcDigitLen128(dw1, dw0);
        verify(digitLen <= 34);
        if (truncationNeeded > digitLen)
        {
            // Swamped: the result is zero under ties-to-even, and always inexact
            return roundAndFinalizeFinite(formatIsBid, sign, Q_MIN, 0, 0, LT_HALF);
        }
        if (truncationNeeded == digitLen)
            return finalizeUnderflowBoundary(formatIsBid, sign, dw1, dw0, residue);
        return finalizeSubnormal(formatIsBid, sign, qExp, dw1, dw0, residue);
    }

    /// <summary>
    /// Round a parsed coefficient/exponent pair to ties-to-even and finalize it
    /// into an encoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Full handling of the decimal128 boundary conditions: underflow to
    /// subnormal or zero, overflow to infinity, and the clamped-near-Emax
    /// region where a finite value with too-large quantum exponent is still
    /// representable by absorbing the excess into the coefficient.
    /// </para>
    /// <para>
    /// Step 1 (fast path): if the input already fits within precision and range
    /// and no rounding is required — residue at most LT_HALF, or HALF with an
    /// even retained low bit — encode and return immediately. This picks up the
    /// vast majority of cases.
    /// </para>
    /// <para>
    /// Step 2 (zero coefficient): zeros with an out-of-range qExp get clamped
    /// into range.
    /// </para>
    /// <para>
    /// Step 3 (special values): not applicable — specials are handled by the
    /// callers before reaching here.
    /// </para>
    /// <para>
    /// Step 4 (underflow): below Emin, divert to
    /// <see cref="finalizeUnderflowRegion"/>.
    /// </para>
    /// <para>
    /// Step 5 (precision truncation): REMOVED as unreachable from a codec's
    /// callers — see the note in the body, which also states what would have to
    /// change for it to be needed again.
    /// </para>
    /// <para>
    /// Step 6 (rounding): apply ties-to-even. If rounding rolls the coefficient
    /// over to 10^34, normalize to 10^33 and increment the exponent.
    /// </para>
    /// <para>
    /// Step 7 (overflow / clamping): above Emax, either absorb the excess into
    /// the coefficient or overflow to infinity.
    /// </para>
    /// </remarks>
    private static Words roundAndFinalizeFinite(bool formatIsBid, bool sign, int qExpIn,
                                                ulong dw1In, ulong dw0In, int residueIn)
    {
        // Step 1: fast path -- already in valid decimal128 range ...
        //         ... and no ties-to-even rounding to apply
        if (residueIn <= LT_HALF || (residueIn == HALF && (dw0In & 1) == 0))
        {
            if (coeffQExpFit(dw1In, dw0In, qExpIn))
                return setFinite(formatIsBid, sign, qExpIn, dw1In, dw0In);
            // Step 2: zero coefficient with out-of-range qExp ... clamp qExp
            if ((dw1In | dw0In) == 0)
            {
                int qClamped = Math.Max(Math.Min(qExpIn, Q_MAX), Q_MIN);
                return setFinite(formatIsBid, sign, qClamped, 0, 0);
            }
        }

        // Step 3: special values ... not applicable ... only Finite

        // Step 5 -- precision truncation -- IS NOT HERE, and this is the reason.
        //
        // This finalize chain descends from one built for an arithmetic engine,
        // whose callers hand it coefficients wider than the 34-digit precision
        // limit: multiply and FMA produce a double-width product, and the engine
        // truncates it here, accumulating the discarded digits into the residue
        // before rounding. A codec has no such caller.
        //
        // parseFiniteValueText accumulates at most 19 + 15 = 34 significant
        // digits and turns every digit past them into a residue instead, so the
        // coefficient it hands over is never wider than the limit. The only
        // other entry to this function is the swamped branch of
        // finalizeUnderflowRegion, which passes a zero coefficient. So the
        // truncation step is unreachable, and rather than carry a path that has
        // never once executed -- and therefore has never been verified to work
        // -- it is removed and the invariant is enforced.
        //
        // Measured: zero executions across the 87k-row shared corpus,
        // 400,000 random
        // numerals, and 76,280 finalize calls driven by inputs of up to 5,000
        // digits, where the widest coefficient ever seen here was 34.
        //
        // This is `demand`, not `verify`: it is unconditional, because a build
        // without VERIFY_ENABLED would otherwise fall through and encode an
        // out-of-range coefficient silently. It cannot fire on a caller's data
        // -- no input reaches it -- only on a future caller that breaks the
        // invariant, which is exactly when a loud failure is wanted. A caller
        // that is not a parser needs the truncation step back, as a multiply
        // or an FMA would.
        demand(calcDigitLen128(dw1In, dw0In) <= 34, "coefficient wider than precision");

        // Step 4: underflow. With no precision truncation to outrank it, the
        // test is simply whether the exponent is below the minimum.
        if (qExpIn < Q_MIN)
            return finalizeUnderflowRegion(formatIsBid, sign, qExpIn, dw1In, dw0In, residueIn);

        int totalResidue = residueIn;
        ulong dw1 = dw1In;
        ulong dw0 = dw0In;
        int qExp = qExpIn;

        // Step 6: rounding ... in this world only ties-to-even
        bool applyRounding = (totalResidue == GT_HALF || (totalResidue == HALF && (dw0 & 1) != 0));
        if (applyRounding)
        {
            // step 6.1: increment. Wrapping on purpose:
            // a coefficient of 0xFFFF...FFFF in the low word carries into the
            // high word, which is exactly what the two statements compute.
            dw0 = dw0 + 1;
            dw1 = dw1 + ((dw0 == 0) ? 1UL : 0UL);

            // step 6.2: rollover
            if (dw1 == MAXX_COEFF_34_HI && dw0 == MAXX_COEFF_34_LO)
            {
                dw1 = MIN_PRECISION_34_COEFF_HI;
                dw0 = MIN_PRECISION_34_COEFF_LO;
                qExp += 1;
            }
        }

        // Step 7: check final bounds
        verify(qExp >= Q_MIN);
        verify(calcDigitLen128(dw1, dw0) <= 34);
        if (qExp > Q_MAX)
        {
            int qExcess = qExp - Q_MAX;
            int digitLen = calcDigitLen128(dw1, dw0);
            if (digitLen + qExcess <= 34)
                return finalizeClamping(formatIsBid, sign, qExp, dw1, dw0);
            return finalizeOverflow(sign);
        }
        return setFinite(formatIsBid, sign, qExp, dw1, dw0);
    }

    // ------------------------------------------------------------------
    // text -> bits
    // ------------------------------------------------------------------

    /// <summary>
    /// The code of the character at <paramref name="index"/>, or -1 past the
    /// end of the string.
    /// </summary>
    /// <remarks>
    /// The parsers walk the input as character codes, so end-of-input is one
    /// more code value rather than a bound test at every read. -1 is not a
    /// character, which keeps NUL an ordinary input character — and an invalid
    /// one — instead of doubling as the terminator. A .NET
    /// <see cref="string"/> is randomly indexable, so the parsers walk it by
    /// index rather than through a cursor. Every character a numeral can
    /// contain is ASCII, so a non-ASCII code unit fails every test below and is
    /// rejected as an unrecognized character.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int codeAt(string str, int index)
    {
        return (index >= 0 && index < str.Length) ? str[index] : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool isDigit(int ch) => (ch >= ascii_0 && ch <= ascii_9);

    /// <summary>
    /// Parse a finite decimal numeral: an optional radix point, an optional
    /// <c>E</c>/<c>e</c> exponent with an optional sign, and any number of
    /// digits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coefficients with more than 34 significant digits are accepted; leading
    /// zeros before a non-zero digit are flushed, and the digits past 34 are
    /// tracked as <c>residue</c> for the ties-to-even rounding that follows.
    /// Excess exponent magnitudes clamp to 9999, which is enough for the
    /// rounding logic to detect overflow or underflow.
    /// </para>
    /// <para>
    /// Sign comes from <paramref name="sign"/>, already consumed by the
    /// dispatcher; <paramref name="chFirst"/> is the first digit or <c>.</c>
    /// and is processed by the main loop.
    /// </para>
    /// </remarks>
    private static string? parseFiniteValueText(ref Words outWords, bool formatIsBid, bool sign,
                                                int chFirst, string str, int startPos)
    {
        int pos = startPos;
        int residue = EXACT;

        bool hasCoefficientDigit = false; // have we seen any digits at all, including zero
        int significantDigitCount = 0;    // does not count leading zeros
        bool hasDot = false;
        bool expSign = false;
        // an underscore is a digit separator: legal only between two digits,
        // so it must follow a digit and the next character must be a digit too
        bool prevWasDigit = false;
        bool afterUnderscore = false;

        verify(chFirst != -1);
        int ch = chFirst;
        int fractionalDigitCount = 0;
        ulong accum19a = 0;
        ulong accum19b = 0;
        int exp = 0;

        while (isDigit(ch) || ch == ascii_dot || ch == ascii_underscore)
        {
            if (isDigit(ch))
            {
                int d = ch - ascii_0;
                hasCoefficientDigit = true;
                // count while flushing leading zeros. This can be done
                // branchlessly off a 32-bit sign shift; said directly here.
                if (significantDigitCount != 0 || d != 0)
                    significantDigitCount += 1;
                if (significantDigitCount <= 19)
                {
                    // at most 19 digits, and 10^19 - 1 fits a ulong
                    accum19a = (accum19a * 10) + (ulong)d;
                }
                else if (significantDigitCount <= 34)
                {
                    // at most 15 more digits, so likewise in range
                    accum19b = (accum19b * 10) + (ulong)d;
                }
                else if (significantDigitCount == 34 + 1)
                {
                    residue = residueFromDecimalDigit(d);
                }
                else
                {
                    residue = residueMerge(residue, residueFromDecimalDigit(d));
                }
                if (hasDot)
                    fractionalDigitCount += 1;
                prevWasDigit = true;
                afterUnderscore = false;
            }
            else if (ch == ascii_underscore)
            {
                if (!prevWasDigit)
                    return "INVALID_UNDERSCORE_LOCATION";
                prevWasDigit = false;
                afterUnderscore = true;
            }
            else // '.'
            {
                if (afterUnderscore)
                    return "INVALID_UNDERSCORE_LOCATION";
                if (hasDot)
                    return "DOUBLE_DOT";
                hasDot = true;
                prevWasDigit = false;
            }
            ch = codeAt(str, pos++);
        }
        if (afterUnderscore)
            return "INVALID_UNDERSCORE_LOCATION";
        if (!hasCoefficientDigit)
            return "NO_COEFFICIENT_DIGIT";
        // this path has at least one digit
        if (ch == ascii_E || ch == ascii_e)
        {
            ch = codeAt(str, pos++);
            if (ch == ascii_plus || ch == ascii_minus)
            {
                expSign = (ch == ascii_minus);
                ch = codeAt(str, pos++);
            }
            bool hasExpDigit = false;
            int expSignificantDigitCount = 0;
            prevWasDigit = false;
            afterUnderscore = false;
            while (isDigit(ch) || ch == ascii_underscore)
            {
                if (ch == ascii_underscore)
                {
                    if (!prevWasDigit)
                        return "INVALID_UNDERSCORE_LOCATION";
                    prevWasDigit = false;
                    afterUnderscore = true;
                }
                else
                {
                    hasExpDigit = true;
                    int eDigit = ch - ascii_0;
                    // count while flushing leading zeros
                    if (expSignificantDigitCount != 0 || eDigit != 0)
                        expSignificantDigitCount += 1;
                    // Wrapping on purpose: the
                    // clamp below discards `exp` entirely once more than 4
                    // significant digits were written, which is strictly before
                    // it could wrap.
                    exp = (exp * 10) + eDigit;
                    prevWasDigit = true;
                    afterUnderscore = false;
                }
                ch = codeAt(str, pos++);
            }
            if (afterUnderscore)
                return "INVALID_UNDERSCORE_LOCATION";
            if (!hasExpDigit)
                return "NO_EXPONENT_DIGIT";
            // clamp exp to 9999 once after the loop
            if (expSignificantDigitCount > 4)
                exp = 9999;
        }
        if (ch != -1)
            return "UNEXPECTED_CHAR";
        // we have at least one digit
        ulong dw0 = accum19a;
        ulong dw1 = 0;
        if (significantDigitCount > 19)
        {
            int pow10 = Math.Min(34, significantDigitCount) - 19;
            Words p = u128FmaPow10(accum19a, pow10, accum19b);
            dw1 = p.Hi;
            dw0 = p.Lo;
        }
        // At this point the coefficient is within precision, but the residue
        // still has to be rounded in, and a rounding rollover could move the
        // exponent. Excess digits to the right of the radix point and excess
        // digits beyond the precision limit pull the quantum exponent in
        // opposite directions, so both corrections land here.
        int signedExp = (expSign) ? -exp : exp;
        int qExp = signedExp - fractionalDigitCount + Math.Max(0, significantDigitCount - 34);
        outWords = roundAndFinalizeFinite(formatIsBid, sign, qExp, dw1, dw0, residue);
        return null;
    }

    /// <summary>
    /// Parse the keyword <c>Infinity</c>, or its 3-letter abbreviation
    /// <c>Inf</c>, case-insensitively.
    /// </summary>
    private static string? parseInfinityText(ref Words outWords, bool sign, int chFirst, string str, int startPos)
    {
        int pos = startPos;
        int ch = chFirst;
        int chPrevCode = 0;
        const string target = "infinity";
        for (int i = 0; i < 8; ++i)
        {
            int targetCode = target[i];
            if ((ch | 0x20) != targetCode)
            {
                if (ch == -1)
                    break;
                else
                    return "FAILURE_PARSING_INFINITY";
            }
            chPrevCode = ch | 0x20;
            ch = codeAt(str, pos++);
        }
        if (ch != -1 || (chPrevCode != 'f' && chPrevCode != 'y'))
            return "FAILURE_PARSING_INFINITY";
        outWords = (INF_PATTERN_HI | (sign ? SIGN_BIT : 0), 0);
        return null;
    }

    /// <summary>
    /// Parse <c>NaN</c>, <c>qNaN</c> or <c>sNaN</c> — case-insensitive — with
    /// an optional decimal payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Payloads of 34+ digits AS WRITTEN are non-canonical and produce a zero
    /// payload per IEEE 754-2019 §3.5.2.
    /// </para>
    /// <para>
    /// The payload may be written bare (<c>NaN123</c>) or enclosed in
    /// parentheses (<c>NaN(123)</c>). The parenthesised form is positional and
    /// balanced: the <c>(</c> must follow the word immediately, at least one
    /// digit must appear, and the <c>)</c> must be the last character of the
    /// input. So <c>NaN(1)2(3)</c>, <c>NaN(1</c>, <c>NaN(1]</c> and
    /// <c>NaN()</c> are all rejected, and <c>NaN[1]</c> and <c>NaN{1}</c> are
    /// not this form at all.
    /// </para>
    /// <para>
    /// The reason this spelling exists at all
    /// is that the platform's own <c>strtod</c> reads
    /// <c>NAN(n-char-sequence)</c>, so the
    /// spelling reaches a caller from the platform itself. The resemblance
    /// stops at the parentheses — strtod's sequence is letters, digits and
    /// underscores read as an implementation-defined integer (<c>NAN(abc)</c>
    /// and <c>NAN(0x1c8)</c> are legal there), while a decimal128 payload is
    /// decimal digits. This is a paren-tolerant spelling of our own payload
    /// syntax, not strtod's.
    /// </para>
    /// <para>
    /// The legacy base this family descends from skipped the six bracket
    /// characters ANYWHERE, so it read <c>NaN(1)2(3)</c> as payload 123. That
    /// is not this, and stays rejected.
    /// </para>
    /// </remarks>
    private static string? parseNanText(ref Words outWords, bool formatIsBid, bool sign,
                                        int chFirst, string str, int startPos)
    {
        int pos = startPos;
        int ch = chFirst;
        bool hasS = ((ch | 0x20) == 's');
        bool hasQ = ((ch | 0x20) == 'q');
        if (hasQ || hasS)
            ch = codeAt(str, pos++);
        if ((ch | 0x20) != 'n' ||
            (codeAt(str, pos++) | 0x20) != 'a' ||
            (codeAt(str, pos++) | 0x20) != 'n')
        {
            return "FAILURE_PARSING_NAN";
        }
        ulong payloadDw0 = 0;
        ulong payloadDw1 = 0;
        ch = codeAt(str, pos++);
        bool hasParen = (ch == ascii_lparen);
        if (hasParen)
            ch = codeAt(str, pos++);
        if (hasParen || ch != -1)
        {
            int accumDigitCount = 0;
            ulong accum19a = 0;
            ulong accum19b = 0;
            while (isDigit(ch))
            {
                ulong d = (ulong)(ch - ascii_0);
                // Count every digit written, leading zeros included. The
                // 33-digit limit below is a limit on the payload as spelled, not
                // on its significant digits, so "NaN" followed by forty zeros
                // and a one is over the limit and reads as zero. Leading zeros
                // contribute nothing to the accumulators, so the value of a
                // payload within the limit is unaffected.
                accumDigitCount += 1;
                if (accumDigitCount <= 19)
                {
                    // at most 19 digits, and 10^19 - 1 fits a ulong
                    accum19a = (accum19a * 10) + d;
                }
                else
                {
                    // Wrapping on purpose.
                    // Within the 33-digit limit this holds at most 14 digits and
                    // cannot wrap; past the limit it can, and the whole payload
                    // is discarded below, so the wrapped value is never read.
                    accum19b = (accum19b * 10) + d;
                }
                ch = codeAt(str, pos++);
            }
            if (accumDigitCount == 0)
                return "FAILURE_PARSING_NAN";
            if (hasParen)
            {
                // the ')' must be present and must end the input
                if (ch != ascii_rparen)
                    return "FAILURE_PARSING_NAN";
                if (codeAt(str, pos++) != -1)
                    return "FAILURE_PARSING_NAN";
            }
            else if (ch != -1)
            {
                return "FAILURE_PARSING_NAN";
            }
            if (accumDigitCount > 33)
            {
                // IEEE754-2019 3.5.2 Encodings
                //  ... as the payload of a NaN). If the value exceeds the maximum,
                //  the significand c is non-canonical and the value used for c is zero.
                payloadDw0 = 0;
                payloadDw1 = 0;
            }
            else
            {
                payloadDw0 = accum19a;
                payloadDw1 = 0;
                if (accumDigitCount > 19)
                {
                    int pow10 = accumDigitCount - 19;
                    Words p = u128FmaPow10(accum19a, pow10, accum19b);
                    payloadDw1 = p.Hi;
                    payloadDw0 = p.Lo;
                }
            }
        }
        outWords = setNaN(formatIsBid, sign, hasS, payloadDw1, payloadDw0);
        return null;
    }

    /// <summary>
    /// Top-level dispatcher. Reads an optional sign, then dispatches by leading
    /// character to the finite-value, infinity or NaN parser.
    /// </summary>
    /// <remarks>
    /// The leading character is passed through to the dispatched parser so it
    /// does not have to re-read what the dispatcher already consumed.
    /// </remarks>
    private static string? encodeStringOrError(ref Words outWords, bool formatIsBid, string str)
    {
        int pos = 0;
        int chFirst = codeAt(str, pos++);
        int ch = chFirst;
        bool sign = (ch == ascii_minus);
        if (ch == ascii_plus || ch == ascii_minus)
            ch = codeAt(str, pos++);
        if (isDigit(ch) || ch == ascii_dot)
            return parseFiniteValueText(ref outWords, formatIsBid, sign, ch, str, pos);
        int chLower = ch | 0x20;
        if (chLower == 'i')
            return parseInfinityText(ref outWords, sign, ch, str, pos);
        if (chLower == 'n' || chLower == 's' || chLower == 'q')
            return parseNanText(ref outWords, formatIsBid, sign, ch, str, pos);
        if (chFirst == -1)
            return "EMPTY_STRING";
        return "INVALID_FORMAT";
    }

}
