# decimal128-text-codec-csharp

A thin codec between the two IEEE 754-2019 decimal128 interchange encodings —
BID and DPD — and decimal character sequences. Nothing else: no arithmetic, no
comparison, no value type, no context, no status flags.

One C# file, one class, zero runtime dependencies. The point is that a caller
who holds interchange bits or a string, and wants the other one, can depend on
this without pulling in a full IEEE 754-2019 engine.

For the complete decimal128 implementation see `decimal128-csharp` at
<https://decimal128.com>.

## API

```csharp
using Decimal128.TextCodec;

// bits -> text
string s = Decimal128TextCodec.Bid128ToString(bits);   // cannot fail
string d = Decimal128TextCodec.Dpd128ToString(bits);

byte[] buf = new byte[Decimal128TextCodec.MaxLen];
int n = Decimal128TextCodec.Bid128ToAscii(buf, bits);   // the text is buf[..n]

// text -> bits; returns null on success, an error message on failure
string? err  = Decimal128TextCodec.Bid128Parse(out UInt128 b, "1.23E+45");
string? errD = Decimal128TextCodec.Dpd128Parse(out UInt128 p, "1.23E+45");
```

An encoding is a `System.UInt128`: the sign bit is the top bit, and the rest is
a combination field and a trailing significand field laid out per IEEE 754-2019
§3.5.2. That is the entire boundary; there is no value type, no context, no
status flags. Both parse methods **always write** their `out` parameter — the
canonical positive quiet NaN on failure — so a caller who discards the returned
message still observes a legal IEEE 754-2019 datum. The error message is
human-readable; its exact wording is not part of the contract.

The receiver comes first, as it does in every sibling port, rather than last as
in .NET's own `TryParse`. These are not `TryParse`: they return a message, not
a `bool`, and the value is always written.

The `ToString` renderers return an owned `string`; their `ToAscii` peers write
into a caller-owned `byte[]` and return a length, for callers who want no
allocation. `MaxLen` is 42, the exact bound on a rendered decimal128, and the
buffer must be at least that long.

## Behavior

**Rendering cannot fail.** Every 128-bit pattern maps to a valid decimal
character sequence. A non-canonical BID coefficient (≥ 10³⁴) renders as zero at
the encoded exponent, per IEEE 754-2019 §3.5.2, and a NaN payload ≥ 10³³
renders as no payload. Those patterns never arise from this codec's own encode
direction — they arise from network and file data, which is why the codec
handles them.

**Output is the GDAS `to-scientific-string` form, and only that.** Integer form
when the quantum exponent is zero, plain form when the quantum exponent is
negative and the adjusted exponent is at least `-6`, scientific otherwise. The
exponent sign is always written:
`6.02E+23`, never `6.02E23`. Cohort information is preserved — `1.00` and `1.0`
are distinct outputs from distinct encodings of the same numerical value, and
nothing is ever normalized or stripped.

**Accepted input** matches what the full `decimal128-{lang}` ports accept, with
one deliberate exception below, so a caller can move between this codec and the
full library without the grammar shifting under them: plain and scientific
notation with `E` or `e`, a leading `+` or `-`, `_` digit separators *between
two digits*, and the case-insensitive words `Infinity`, `Inf`, `NaN`, `qNaN`,
`sNaN` with an optional sign and an optional payload of decimal digits.

**The exception: a NaN payload may be parenthesised** — `NaN(123)` as well as
`NaN123`. C's own `strtod` reads `NAN(n-char-sequence)`, so the spelling
reaches a caller from the platform itself. The form is positional and balanced:
`(` immediately after the word, at least one digit, `)` last. So `NaN()`,
`NaN(1`, `NaN(1]`, `NaN(1)2(3)`, `NaN (1)`, `NaN[1]` and `NaN{1}` are all
rejected. The resemblance to `strtod` stops at the parentheses — its sequence
is letters, digits and underscores read as an implementation-defined integer,
so `NAN(abc)` and `NAN(0x1c8)` are legal there and rejected here. Rendering
never emits parentheses; `NaN(123)` parses and renders back as `NaN123`.

The full ports accept the same spelling — `decimal128-csharp` took it on
2026-08-23, closing what had been the codecs' one grammar divergence from them.

**Rounding is ties-to-even, hardcoded**, and reachable only from the text →
bits direction: more than 34 significant digits, overflow to Infinity, or
underflow to a subnormal or zero. There is no rounding parameter and no format
selector — the premise is a four-function surface with no configuration. IEEE
754-2019 §5.4.2 places `convertFromDecimalCharacter` under the applicable
rounding-direction attribute; this codec deliberately implements only the
ties-to-even case, and is therefore a conforming **subset** of that operation.
Callers needing another rounding direction want the full library.

## Building

```
dotnet build Decimal128.TextCodec.slnx
dotnet test Decimal128.TextCodec.slnx
dotnet test Decimal128.TextCodec.slnx -c Release
```

`net10.0`, matching the full `decimal128-csharp` port. No runtime dependencies.

Internal-consistency claims — the ones unreachable for any input — are spelled
`verify` and carry `[Conditional("VERIFY_ENABLED")]`, the same contract the
full `decimal128-csharp` port uses: the call *and its argument expressions* are
dropped by the compiler unless the symbol is defined. The csproj defines it for
Debug only, exactly as the Swift port's `Package.swift` does, so a release
consumer never takes a throw inside a library on data they do not control.
`demand` — buffer sizing and the precision invariant that stands in for the
removed precision-truncation step — is live in every configuration.

The default `dotnet test` is a Debug build, so `VERIFY_ENABLED` is on and every
internal-state assertion runs over all 87k rows. CI runs Release as well, which
proves the codec does not depend on those assertions for its answers.

## Tests

Seven tests across two classes in one file. The canonical table is its own
class but shares a file with the other tables, so the two table runners they
both use stay internal to it — the same layout the C, Swift, Kotlin and Java
ports use.

| file | what it covers |
|---|---|
| `tests/Decimal128.TextCodec.Tests/TestCorpusVectors.cs` | `TestCorpusVectors` — the four conformance tables over the shared corpus, 2,601 rows, both directions, both formats. Also holds the corpus reader and the hex helpers, its only consumer. |
| | `TestCanonicalCorpus` — the full 84,783-row canonical table |

That is the whole suite, and the shape is deliberate: **every test vector in
this repo comes from the shared corpus.** Nothing in the suite reaches past the
public surface.

The repo is standalone: it depends on no decimal128 port, at runtime or in
tests. The independent oracle is the shared `text_bid_dpd` corpus, generated by
the `decimal128-c` engine through its public API; that engine reaches its
answers by different algorithms, so agreement is a cross-check rather than a
tautology.

The vectors are read from `resources/text_bid_dpd`, where `resources` is the
`decimal128-resources` submodule — the same mount every decimal128 port uses.
Nothing is copied into this repo and nothing is copied onto the test output
path: the loader resolves the directory from the test file's own compile-time
path via `[CallerFilePath]`, which is the same trick the Swift port plays with
`#filePath`. Tests run out of the build directory, but the source path does not
move. An absolute `DECIMAL128_RESOURCES` overrides it.

Two things worth knowing if you touch the tests:

- **The corpus quotes two of its five files.** `parse` and `error` double-quote
  the text column with `\\` and `\"` escapes; the other three leave it bare. A
  naive comma split silently corrupts four rows and still passes, because a
  malformed input fails to parse either way. The reader honours the quoting,
  the row counts are asserted, and `QuotedColumnRoundTrips` checks those four
  rows by hand.
- **The submodule must be checked out.** With `resources/text_bid_dpd` empty
  the loader fails with a message naming the `git submodule update --init
  --recursive` that fixes it, rather than a suite that quietly tests nothing.
  Clone with `--recurse-submodules`.

## Provenance

Ported from `decimal128-text-codec-swift`, which is itself a port of
`decimal128-text-codec-kotlin`, the reference implementation for the family.
Swift is the closer base because it answered the same question first: what the
file looks like when the language has a native 128-bit unsigned integer and
unsigned words, rather than the reference's pair of signed `Long`s. The design
spec is `TextCodecLayer.md` in the `decimal128-workorder` repository.

Where this port differs from its siblings, in each case because .NET already
had the answer:

- **`UInt128` is the public boundary**, as in Swift. The internals still work
  in `dw1`/`dw0` `ulong` pairs, exactly as the reference does — the split
  happens once on entry and the join once on exit.
- **`mulhi64` is `Math.BigMul`**, which the JIT turns into the machine's
  widening multiply. The reference decomposes it into four 32×32 partial
  products because the JVM has no unsigned 64-bit multiply-high.
- **Arithmetic wraps by default**, as Kotlin's does and unlike Swift's, so the
  sites Swift spells `&+` / `&*` are plain operators here. They are the same
  sites, each still carrying the note saying why the wrap is the intended
  arithmetic, and `CheckForOverflowUnderflow` is stated in the csproj rather
  than assumed.
- **Integer-to-text goes through `CultureInfo.InvariantCulture`** at the two
  places the codec formats an integer with the runtime rather than by hand.
  `int.ToString()` is culture-sensitive in .NET, and a negative-number format
  that spells its sign with U+2212 would produce a numeral this codec's own
  parser rejects. No other port has a culture to pin.

The finalizer's precision-truncation step is removed, in this port and in the
reference alike. It descends from a finalizer built for an arithmetic engine,
whose multiply and FMA hand it a double-width product; a codec's only caller is
a parser that accumulates at most 19 + 15 = 34 significant digits and turns
everything past them into a residue. Measured on the C port: zero executions
across the shared corpus, 400,000 random numerals, and 76,280 finalize calls
from inputs of up to 5,000 digits. A dead path is an unverified path, so it is
replaced by an unconditional `demand`. It stays live in every full port's
`Finalize`, where multiply and FMA do reach it; a caller here that is not a
parser needs it back.

The codec stands on the interchange formats alone. It holds no intermediate
value type: parsing yields loose scalars — sign, quantum exponent, coefficient,
residue — which are finalized and written *toward* an encoding. It does not
pivot through any engine's in-memory representation, and must not be changed
to.

## License

MIT.
