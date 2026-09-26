# Performance

## Benchmark

```powershell
.\test.ps1                                   # builds g29testhost.dll and G29.Tests.exe
artifacts\bin\G29.Tests.exe --benchmark
```

Method (`tests/Benchmark.cs`): each workload is an existing scenario suite.
It runs once on the reference interpreter while every harness records the
exact input bytes the program consumed and the bytes it wrote. Each recorded
session is then replayed on a fresh instance of the compiled program in
`g29testhost.dll` (`bft_replay`: input and output are plain buffers on the
calling thread, no managed callback per byte), and the output must equal the
recording byte for byte. The native time is the median of 7 passes over all
sessions of the workload and includes each session's setup (tape allocation)
and boot. "reference" is one pass of the C# reference interpreter over the
same sessions, for scale only.

| Workload | Sessions | Events | Content |
|---|---|---|---|
| ffb-engine | 30 | 15 379 | effect engine transcripts: create/update/start effects, 12 010 force samples (the DirectInput hot path) |
| directinput | 17 | 225 | role DIRECTINPUT: COM calls, force ticks, heartbeat |
| steering | 12 | 426 | steering motion estimation |
| cli | 324 | 2 952 | every g29ctl command transcript |
| protocol | 3 | 10 952 | every HID report for every legal input |
| monitor+watchdog | 237 | 14 144 | reconnect monitor, service watchdog, device selection |

## Results

Machine: AMD Ryzen 9 5900HS (8 cores), Windows 11 Pro 10.0.26200, MSVC
14.44 x64, `/O2`. Median native milliseconds per workload pass.

| Workload | Retired interpreter (bfvm.c), 2 runs | AOT compiled, 3 runs | Speed-up | AOT µs per event |
|---|---|---|---|---|
| ffb-engine | 1 277 / 1 258 | 133.3 / 132.5 / 131.5 | 9.6× | 8.6 |
| directinput | 19.5 / 16.3 | 1.6 / 1.6 / 1.8 | ≈10× | 7.2 |
| steering | 13.2 / 13.3 | 1.3 / 1.4 / 1.4 | 9.6× | 3.2 |
| cli | 131 / 198 | 17.8 / 17.9 / 18.2 | 7–11× | 6.0 |
| protocol | 45.8 / 45.1 | 7.9 / 8.4 / 8.0 | 5.7× | 0.73 |
| monitor+watchdog | 256 / 290 | 31.1 / 30.4 / 30.8 | 8.8× | 2.2 |

The interpreter baseline was measured with the same benchmark and the same
recorded workloads immediately before the interpreter was deleted (it ran the
identical program text apart from the later message-text change, which does
not affect these paths measurably). The interpreter numbers varied more
between runs on this laptop.

The compiled code is not free of overhead: it still performs every checked
addition whose value is unknown at build time (205 745 sites), tape checks
where the pointer depends on data (17 132), a budget decrement per loop
iteration, and a call per input or output byte. The remaining cost is the
program's own work (much of the engine is multi-precision arithmetic done
with cell loops) plus those checks.

## Build-time figures

From `artifacts/obj/generated/g29_program.stats.txt`:

| Figure | Value |
|---|---|
| Brainfuck commands in `g29-main.bf` | 19 212 941 |
| Operations after run folding | 893 493 |
| Plain loops / clear / multiply-add / idiom loops | 15 134 / 43 372 / 68 300 / 3 307 |
| Loops removed as dead (cell known zero) | 5 116 |
| Constant stores (folded arithmetic) | 100 975 |
| Runtime overflow checks / tape checks | 205 745 / 17 132 |
| Proven faults | 0 |
| Generated C | 15.3 MB in 3 029 functions, 16 parts |
| Clean build of the three shipping binaries | 60 s |

## Binary size

| Binary | Interpreter + embedded program | AOT compiled |
|---|---|---|
| g29ctl.exe | 19 659 776 | 5 470 208 |
| g29ffb64.dll | 19 641 856 | 5 451 264 |
| g29ffb32.dll | 19 613 184 | 6 940 672 |

The interpreted binaries carried the 19.5 MB program text as a resource and
parsed it at start-up; the compiled ones carry only machine code.
