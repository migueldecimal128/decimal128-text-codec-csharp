# decimal128-text-codec-csharp

Thin codec between the two IEEE 754-2019 decimal128 interchange encodings — BID
and DPD — and decimal character sequences, for .NET.

Ported from `decimal128-text-codec-swift`, which is itself a port of
`decimal128-text-codec-kotlin`, the reference implementation for the family.
Swift is the closer base because it answered the same question first: what the
file looks like when the language has a native 128-bit unsigned integer and
unsigned words, rather than the reference's pair of signed `Long`s. One file,
one class, no runtime dependencies.

## API

```csharp
using Decimal128.TextCodec;

// bits -> text
string s = Decimal128TextCodec.Bid128ToString(bits);
string d = Decimal128TextCodec.Dpd128ToString(bits);

// text -> bits; returns null on success, an error message on failure
string? err  = Decimal128TextCodec.Bid128Parse(out UInt128 b, "1.23E+45");
string? errD = Decimal128TextCodec.Dpd128Parse(out UInt128 p, "1.23E+45");
```

An encoding is a `System.UInt128`: the sign bit is the top bit, and the rest is
a combination field and a trailing significand field laid out per IEEE 754-2019
§3.5.2. That is the entire boundary; there is no value type, no context, no
status flags. Both parse methods always write their `out` parameter — the
canonical positive quiet NaN on failure — so a caller who discards the returned
message still observes a legal IEEE 754-2019 datum.

The receiver comes first, as it does in every sibling port, rather than last as
in .NET's own `TryParse`. These are not `TryParse`: they return a message, not a
`bool`, and the value is always written.

## Scope

Rounding, where required, is `roundTiesToEven` and is not configurable. Output
is the GDAS `to-scientific-string` form and is not configurable — the exponent
sign is always written (`6.02E+23`), and cohort information is preserved. No
arithmetic, no field extraction, no value-type wrapper.

Accepted input matches what the full `decimal128-{lang}` ports accept. That
includes the parenthesised NaN payload: **`NaN(123)` as well as `NaN123`**,
matching the spelling C's `strtod` reads. The form is positional and balanced —
`(` immediately after the word, at least one digit, `)` last — so `NaN()`,
`NaN(1`, `NaN(1)2(3)`, `NaN[1]` and `NaN{1}` are all rejected. Rendering never
emits parentheses. The codecs had this before the ports did; the ports took it
on 2026-08-23, closing the family's one grammar divergence.

For the full decimal128 implementation see `decimal128-csharp` at
<https://decimal128.com>.

## Requirements

`net10.0`, matching the full `decimal128-csharp` port. Where this port differs
from its siblings, in each case because .NET already had the answer:

- `UInt128` is the public boundary, as in Swift. The internals still work in
  `dw1`/`dw0` `ulong` pairs, exactly as the reference does — the split happens
  once on entry and the join once on exit.
- `unsignedMulHi` is `Math.BigMul`, which the JIT turns into the machine's
  widening multiply. The reference decomposes it into four 32×32 partial
  products because the JVM has no unsigned 64-bit multiply-high.
- Arithmetic wraps by default, as Kotlin's does and unlike Swift's, so the sites
  Swift spells `&+` / `&*` are plain operators here. They are the same sites,
  each still carrying the note saying why the wrap is the intended arithmetic,
  and `CheckForOverflowUnderflow` is stated in the csproj rather than assumed.
- Integer-to-text goes through `CultureInfo.InvariantCulture` at the two places
  the codec formats an integer with the runtime rather than by hand.
  `int.ToString()` is culture-sensitive in .NET, and a negative-number format
  that spells its sign with U+2212 would produce a numeral this codec's own
  parser rejects. No other port has a culture to pin.

Internal-consistency claims — the ones unreachable for any input — are spelled
`verify` and carry `[Conditional("VERIFY_ENABLED")]`, the same contract the full
`decimal128-csharp` port uses: the call *and its argument expressions* are
dropped by the compiler unless the symbol is defined. The csproj defines it for
Debug only, exactly as the Swift port's `Package.swift` does, so a release
consumer never takes a throw inside a library on data they do not control.
`demand` — buffer sizing and the precision invariant that stands in for the
removed precision-truncation step — is live in every configuration.

## Tests

```
git submodule update --init --recursive
dotnet test Decimal128.TextCodec.slnx
```

Two test classes in one file. The canonical table is its own class but shares a
file with the other tables, so the two table runners they both use stay internal
to it — the same layout the C, Swift, Kotlin and Java ports use.

| file | what it covers |
|---|---|
| `tests/Decimal128.TextCodec.Tests/TestCorpusVectors.cs` | `TestCorpusVectors` — the four conformance tables over the shared corpus, 2,601 rows, both directions, both formats. Also holds the corpus reader and the hex helpers, its only consumer. |
| | `TestCanonicalCorpus` — the full 84,783-row canonical table |

That is the whole suite, and the shape is deliberate: **every test vector in this
repo comes from the shared corpus.** There is no hand-written table, and nothing
reaches past the four public methods.

The default `dotnet test` is a Debug build, so `VERIFY_ENABLED` is on and every
internal-state assertion runs over all 87k rows. CI runs Release as well,
which proves the codec does not depend on those assertions for its answers.

The repo is standalone: it depends on no decimal128 port, at runtime or in
tests. The independent oracle is the shared `text_bid_dpd` corpus, generated by
the decimal128-c engine, which reaches its answers by different algorithms than
this codec does.

The vectors are read from `resources/text_bid_dpd`, where `resources` is the
`decimal128-resources` submodule — the same mount every decimal128 port uses.
Nothing is copied into this repo and nothing is copied onto the test output
path: the loader resolves the directory from the test file's own compile-time
path via `[CallerFilePath]`, which is the same trick the Swift port plays with
`#filePath`. Tests run out of the build directory, but the source path does not
move. An absolute `DECIMAL128_RESOURCES` overrides it.

## License

MIT.
