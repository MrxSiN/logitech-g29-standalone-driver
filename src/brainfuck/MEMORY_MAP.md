# Tape map

Documentation of the tape layout of `g29-main.bf`. The program is the
authority; this file describes it and is not a source from which anything is
generated. The names in backticks are labels for documentation and
discussion only; they do not appear in the program. Update this file whenever
an edit to the program moves or repurposes cells.

The tape has 131072 cells. The program uses the low end so pointer moves stay
short; everything above the highest listed region is reserve.

## Rules

- Every cell is non-negative at rest. `[-]` on a negative non-wrapping cell
  never ends, so helpers never leave a negative value behind. Signed values are
  a sign cell (0 = non-negative, 1 = negative) plus a magnitude cell (and, for
  command-line integers, a flag for the one value, -2147483648, whose magnitude
  does not fit a cell).
- Each region that runs helper code has a nearby scratch pool. Scratch cells
  are zero when a helper starts using them and zero again when it is done.
- Frequently used state lives below cell 700: every access from far away costs
  program size, because Brainfuck has no addressing mode.

## Regions

| Cells | Name | Contents |
|---|---|---|
| 0..15 | current frame | raw header (`fr_magic` .. `fr_len_hi`), `fr_len`, `fr_rem` (payload bytes still unread), `fr_bad`, `fr_short`, `handled` |
| 16..47 | program state | `running`, `booted`, `role`, `test_counter`, `svc` (the continuation the shared services run next) |
| 48..191 | scratch A | frame layer and dispatcher |
| 192..215 | protocol | `pr_arg1..4` (inputs), `pr_flags` (identification), `pr_count` (reports produced), `pr_status`, `test_sel`, `rpt[14]` (two 7-byte reports) |
| 216..319 | scratch B | protocol helpers |
| 320..478 | command line | state machine (`cli_state`, `cli_cur`, `cli_wait`, `cli_have`, `cli_exit`, ...), argument parser (`ap_*`, keyword match flags `ap_m[20]`, one-hot position `ap_oh[16]`), command-argument scan (`ci_*`), integer records `p0`, `o_range`, `o_ac`, `o_ms` (found, valid, negative, magnitude, minimum), error `err[6]` / pending error `pend[6]` (message, name, negative, magnitude, minimum, number), controller (`*_ret` return states, device counts, selected and first-native device: token, native, output length, name length), device being enumerated (`d_*`), clock (`t_*`: two 24-bit limbs), init values `v_*` |
| 320..371 (roles DIRECTINPUT and TEST) | DirectInput | overlays the command-line cells, which only role CLI uses: DIEFFECT request and changed flags (`ap_req`, `di_inv`, `di_chg`, `di_f*`), session (`di_act`, token, revision, lost, last percent sent, heartbeat handle, DeviceID step), the pending call (`di_pend`, `di_sq`, `di_now`), the last system tick, poll time, DownloadEffect state |
| 480..599 | scratch C | command line; DirectInput |
| 600..619 | monitor | `mon_mode` (0 none, 1 watch, 2 service), `mon_stop`, last-seen time, last logged message (`mon_last`, `mon_last_err[6]`), `sink` (1 stdout, 2 stderr, 3 event-log text), path lengths, `cfg_valid`, `mon_first` |
| 620..630 | watchdog | `ev_tid` (timer id of the current EV_TIMER, read before the CLI state machine runs), `wd_on`, `wd_reading`, `wd_pending`, heartbeat handle `wd_h[4]`, system tick of the last check `wd_now[3]` |
| 631..699 | scratch D | monitor |
| 700..739 | registration / doctor | registry request (`rq_*`), step engine (`reg_step`, `rs_*`), lengths of strings being built, ownership markers found, G HUB process scan (`gh_*`), doctor results (`lib64_*`, `cb64_*`, ...) |
| 740..799 | scratch F | registration and doctor |
| 800..889 | effect engine globals | `e_next` (next handle), `e_count`, `e_gain` (device gain), `e_act` (actuators), `e_paused`, `e_paused_at[3]`, command inputs (`e_cmd`, `e_h`, `e_now[3]`, `e_solo`, `e_iter_inf`, `e_iter[3]`, `e_motion[6]`, `e_arg`, `e_shift[3]`), results (`e_status`, `e_result`, `e_res_neg`), walk flag `e_walk`, staged parameters `gp[52]` (laid out like a record) |
| 884, 885 | services | `st_req` (steering request), `tw_op` (role TEST steering operation) |
| 890..933 | steering motion | `st_has`, last sample time, position, velocity, acceleration (signed wide: sign cell + three 16-bit limbs; 1/65536, 1/65536 and 1/4096 units), axis minimum/maximum/range/sum, inputs (`st_now`, `st_raw`, `st_bits`) and the motion answer (`st_o*`) |
| 934..999 | scratch G | engine globals and steering |
| 1000..4095 | text buffers | twelve interleaved lanes of 256 bytes (see below) |
| 4100..4199 | scratch E | next to the buffers |
| 4200..4599 | scratch H | next to the effect records |
| 4608..13055 | effect records | 33 records of 256 cells: 32 effect slots and a terminator (see below) |

## Text buffers

Byte `k` of the buffer in lane `j` is cell `1012 + j + 12k`. Row -1
(1000..1011) is a permanently zero sentinel row and row 256 (4084..4095) a zero
guard row, so a buffer can be printed by scanning. Bytes are stored as
`byte + 1`, newest first. Interleaving keeps copies and
comparisons between buffers lane-local, which keeps the program small.

| Lane | Buffer |
|---|---|
| 0 | `cur_buf`: product name of the device being enumerated, or the argument being parsed |
| 1 | `a0_buf`: the first command-line argument (for "Unknown command") |
| 2 | `off_buf`: the first unknown option |
| 3 | `sel_buf`: product name of the selected wheel |
| 4 | `nat_buf`: product name of the first native wheel |
| 5 | scratch lane (copies) |
| 6 | `cur_path`: interface path (lower case) of the device being enumerated |
| 7 | `sel_path`: path of the selected wheel |
| 8 | `nat_path`: path of the first native wheel |
| 9 | `cfg_path`: path the monitor last configured |
| 10, 11 | scratch lanes (comparison) |

## Effect records

Record `k` (0..32) starts at cell `4608 + 256k`. Record 32 is the terminator
(its `more` cell is 0). Field offsets:

| Offsets | Contents |
|---|---|
| 0 | `more`: 1 in records 0..31 (the walk continues), 0 in the terminator |
| 1..51 | slot: occupied, handle, kind, playing, iterations, start time, duration, gain, delay, direction, envelope, magnitude, ramp, offset, phase, period, condition (wide values use three 16-bit limbs, signed values sign + magnitude) |
| 52..61 | per-visit work cells (elapsed, finished, iteration time, value); always zero between visits |
| 63 | `back`: 1 in records 1..32, marks the way back to record 0 |
| 64..138 | carry block: the operation, its arguments and running results, moved from record to record by the walk |
| 160..255 | the record's scratch pool |

Every engine operation is one walk: the carry block is filled at record 0,
carried forward to the terminator (each record runs the same code on its own
cells), then carried back to record 0 where the results are read. The record
code addresses cells relative to the record it runs in, so the same code
serves every record. The highest cell the program uses is 13055.
