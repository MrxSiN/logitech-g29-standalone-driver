# Release process

G29 Standalone is pre-release. No version may be called 1.0, production-ready
or enterprise-ready until every gate below has recorded evidence.

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

## Release gates (all required for 1.0)

| Gate | Evidence |
|---|---|
| automated suite green at the tag, including `-Sanitize -Analyze` | CI run |
| architecture tests (raw Brainfuck only, no interpreter shipped), reproducible binaries | CI run |
| every HIL test in `docs/TESTING.md` passed and recorded | signed-off log per test |
| force safety: HIL-6..HIL-12 pass their counts (100 client kills, 100 unplugs, 50 suspends) | log |
| install, upgrade, uninstall, reinstall on clean Windows 10 and 11 (HIL-15), including SYSTEM-context deployment | log |
| real-game matrix: several unrelated titles, both bitnesses, observed | `docs/TESTING.md` table |
| 24 h soak (HIL-16) | log |
| signed and timestamped binaries, published hashes | manifest |
| threat model reviewed against the shipped build | `docs/THREAT_MODEL.md` |
| third-party provenance reviewed | `THIRD_PARTY_NOTICES.md`, `docs/PROTOCOL.md` |
