# Release process

The current release is **v1.0.0** (2026-09-26), the first release. It was
released with some of the gates below still open; the table records where
each one stands. No release may be called production-ready or
enterprise-ready until every gate has recorded evidence.

## Releasing a version

1. Update `CHANGELOG.md`: a `## vX.Y.Z` section with a one-line summary of
   what was tested, then "What's in it", "Fixed" (when there is something) and
   "Notes".
2. Run `.\test.ps1 -Device` with the wheel connected and `.\test.ps1 -Sanitize
   -Analyze`.
3. Commit, tag `vX.Y.Z`, push the branch and the tag.
4. Create the GitHub release from the changelog section and attach
   `G29Standalone-vX.Y.Z.zip`: `Install-Driver.ps1`, `Uninstall-Driver.ps1`,
   `Trace-Driver.ps1`, `tools\InstallCommon.ps1`, the three binaries in
   `artifacts\bin`, `README.md`, `CHANGELOG.md`, `LICENSE` and
   `THIRD_PARTY_NOTICES.md`, in the same layout as the repository.

## What the repository automates

- `.github/workflows/build-test.yml` on every push and pull request: the raw
  Brainfuck program validated and compiled ahead of time, x64/x86 Release build with `/W4 /WX`, mitigation check
  (ASLR, DEP, CFG, x64 CET and high-entropy ASLR), `/analyze` on x64 and x86,
  the whole C# suite (reference interpreter and compiled program) and again under AddressSanitizer, the x64 and
  x86 DirectInput smoke clients, the install transaction with failure
  injection, the benchmark, a byte-for-byte reproducibility check (`/Brepro`),
  PSScriptAnalyzer (errors fail), and `tools/New-ReleaseManifest.ps1`.
- `tools/New-ReleaseManifest.ps1` writes `artifacts/release/SHA256SUMS` and
  `release-manifest.json`: commit, dirty flag, ABI version, SHA-256 of each
  binary and of the Brainfuck program it was compiled from, toolchain versions, Authenticode status.
  It contains no timestamps, so two builds of one commit can be compared.

## What is manual

Signing. The binaries are **not signed** today. When a code-signing
certificate exists:

1. Build from a clean checkout of the tagged commit; run `.\test.ps1 -Sanitize
   -Analyze`; keep the unsigned `SHA256SUMS`.
2. Sign on a protected machine or a protected CI environment that pull-request
   jobs cannot reach:
   `signtool sign /fd SHA256 /tr <RFC 3161 timestamp URL> /td SHA256 /n <subject> g29ctl.exe g29ffb64.dll g29ffb32.dll`
3. `signtool verify /pa /v` each file; run `New-ReleaseManifest.ps1` again for
   the signed hashes; publish both manifests.

These are user-mode binaries on the inbox HID stack: Authenticode signing is
recommended for distribution; kernel-driver signing does not apply.

Not yet in the pipeline (need decisions or credentials): signing, build
provenance attestation, an SBOM (the only dependencies are the Windows SDK,
the MSVC runtime linked statically and the .NET Framework compiler used at
build time), CodeQL, protected release environment and branch protection.

## Release gates

Required before a release is called production-ready.

| Gate | Evidence | At v1.0.0 |
|---|---|---|
| automated suite green at the tag, including `-Sanitize -Analyze` | CI run | passed: CI run 36217647647 on the tagged commit, and `test.ps1 -Device` locally |
| architecture tests (raw Brainfuck only, no interpreter shipped), reproducible binaries | CI run | passed: same CI run |
| every HIL test in `docs/TESTING.md` passed and recorded | signed-off log per test | open: HIL-1 (PS3 mode), LEDs and a ±20 force observed on 2026-09-26 (`test.ps1 -Device`), not signed off |
| force safety: HIL-6..HIL-12 pass their counts (100 client kills, 100 unplugs, 50 suspends) | log | open |
| install, upgrade, uninstall, reinstall on clean Windows 10 and 11 (HIL-15), including SYSTEM-context deployment | log | partly: `docs/HARDWARE_VALIDATION.md` |
| real-game matrix: several unrelated titles, both bitnesses, observed | `docs/TESTING.md` table | open: Assetto Corsa (64-bit) only |
| 24 h soak (HIL-16) | log | open |
| signed and timestamped binaries, published hashes | manifest | open: not signed |
| threat model reviewed against the shipped build | `docs/THREAT_MODEL.md` | open |
| third-party provenance reviewed | `THIRD_PARTY_NOTICES.md`, `docs/PROTOCOL.md` | open |
