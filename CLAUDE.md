# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Monocle Does

Monocle is a shotgun proteomics tool that reads mass spectrometry data files and corrects precursor ion assignments: it identifies the true monoisotopic peak for each MS2 scan and can also detect charge states. Input formats: `.raw` (Thermo), `.mzXML`, `.mzML`, `.mzDB`. Output formats: `mzXML`, `mzML`, `csv`, `mzDB`.

## Project Structure

| Project | Framework | Purpose |
|---|---|---|
| `Monocle/` | net6.0 | Core library: data models, file I/O, algorithm |
| `Monocle.CLI/` | net6.0 | Cross-platform CLI (`dotnet publish`) |
| `Monocle.Tests/` | net6.0 | xUnit tests |
| `Monocle.UI/` | .NET Framework 4.7.1 | Windows Forms GUI (build with MSBuild/Visual Studio) |

## Commands

All commands run from the project subdirectory, not the repo root (no solution file at root).

```bash
# Run all tests
cd Monocle.Tests && dotnet test

# Run a single test
cd Monocle.Tests && dotnet test --filter "FullyQualifiedName~MonoTest.Mono1"

# Debug build (CLI)
cd Monocle.CLI && dotnet build

# Release publish (CLI) — use -r for target runtime
cd Monocle.CLI && dotnet publish -c Release -r linux-x64 --self-contained -o Monocle.CLI -p:PublishTrimmed=true
cd Monocle.CLI && dotnet publish -c Release -r win10-x64
```

Test data files live in `Monocle.Tests/data/` and are copied to output on build.

## Architecture

### Data Flow

```
File on disk
  → IScanReader.Open() / foreach (Scan scan in reader)
  → List<Scan> (all scans loaded into memory)
  → Monocle.Run(ref scans, options)   ← core algorithm
  → IScanWriter: WriteHeader() + WriteScan() per scan
  → Output file
```

`FileProcessor` (used by the UI) wraps this same flow with async progress events. The CLI (`Program.cs`) also runs this flow directly, with an optional streaming path for `--convert-only` that skips loading all scans into memory.

### Core Algorithm (`Monocle/Monocle.cs`)

`Monocle.Run(ref List<Scan> scans, MonocleOptions options)` iterates every scan at `MS_Level` (default MS2) and for each:

1. Resolves the precursor MS1 scan via `scan.PrecursorMasterScanNumber - 1` (scan numbers are 1-based; the list is 0-based).
2. Calls `GetNearbyScans()` to collect ±N MS1 full scans (default ±6, same FAIMS CV if applicable, excluding SIM scans).
3. For each charge in `ChargeRange`, extracts an observed isotope envelope via `PeptideEnvelopeExtractor.Extract()` and compares it to a theoretical envelope from `PeptideEnvelopeCalculator.GetTheoreticalEnvelope()` (binomial distribution of C13 based on estimated carbon count).
4. Scores using a dot product (`Vector.Dot`). Best-scoring charge and monoisotopic m/z index wins (with a 5% left-bias to favor the monoisotopic peak).
5. Sets `precursor.Mz` and `precursor.Charge` on the scan's `Precursor` objects.

For low-res (ITMS) precursor scans, or when `ForceCharges` is set, the algorithm instead expands the precursor into multiple `Precursor` entries covering `ChargeRangeUnknown`.

### File I/O

`ScanReaderFactory.GetReader(path)` selects the reader by extension. All readers implement `IScanReader` (Open, GetHeader, IEnumerable<Scan>, Close). All writers implement `IScanWriter` (Open, WriteHeader, WriteScan, Close). `ScanWriterFactory` mirrors this for writers.

`RawReader` uses the Thermo `ThermoFisher.CommonCore.RawFileReader` NuGet package. It pre-loads a `ScanParents` dictionary mapping child→parent scan numbers and caches peak arrays for adjacent MS1 scans to improve performance.

### Key Data Types

- `Scan`: all per-scan metadata plus `List<Centroid> Centroids` (m/z + intensity, sorted ascending) and `List<Precursor> Precursors`.
- `Precursor`: holds `IsolationMz`, `Mz` (the corrected monoisotopic value), `Charge`, `IsolationWidth`, `IsolationSpecificity`.
- `MonocleOptions`: all algorithm knobs. Supports property-name indexer for UI data binding.

### Important Invariants

- `scans[scan.PrecursorMasterScanNumber - 1]` is the standard pattern for resolving a parent scan — scan numbers are 1-based but the list is 0-based.
- `Centroids` lists are sorted by m/z; `PeakMatcher` uses binary search (`NearestIndex`).
- FAIMS-aware: `GetNearbyScans` only includes MS1 scans whose `FaimsCV` matches the precursor scan's CV when FAIMS is active.
