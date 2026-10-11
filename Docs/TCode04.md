# TCode 0.4 Buffered Update (serial)

In **Serial → update settings → Update type**, select **Buffered Update** while
disconnected, then connect to the CTW serial port. Fixed Update and Polled Update
retain their original behavior. Other output targets do not offer Buffered Update.

The device must implement CTW's `!4` time-buffered protocol and return its CRC32
acknowledgements on the same serial connection. This is the implementation in
`ctw_esp32s3_0909/include/tcode_queue.hpp`, not a generic claim of compatibility
with other protocols called TCode 0.4. Serial baud rate, DTR/RTS and other port
settings remain configurable in the existing Serial settings panel.

Load scripts for any of `L0`, `L1`, `L2`, `R0`, `R1`, `R2`, enable the desired
output axes, and start media playback. The connected media source must support
MFP play/pause commands and report its playback state. MFP pauses it during clock
synchronization and preloading, allows one second for initial positioning, then
resumes it at the agreed device epoch. The default preparation takes about four
seconds plus handshake time. A media source that does not acknowledge pause or
play is rejected before continuing playback.

The settings panel shows the current state, clock uncertainty estimate and
remaining unsealed buffer. A completed trajectory holds its final positions
until pause, disconnection or a new session. Only one Buffered Update connection
may control media playback at a time.

## Trajectory behavior

- Six-axis mapping: `L0/L1/L2/R0/R1/R2` → protocol axes `0/1/2/3/4/5`.
  Missing, script-bypassed or output-disabled axes are not queued. Auxiliary axes
  such as `V0` are not part of this protocol.
- Uses the loaded (including linked) scripts, global/per-axis time offsets,
  script scale/inversion, playback speed and output minimum/maximum settings.
- Uses the CTW planner's 1% turning-point hysteresis and preserves an explicit
  stationary introduction. The P4 performs rest-to-rest seventh-order
  interpolation. MFP's live interpolation choice, motion generators, transitions
  and auto-home are not applied by this mode.
- Configured script speed limits, smart limits or motion-provider blending for
  a queued axis are rejected with an explanation, rather than silently ignored.
- Playback pause, seek, speed/script/range changes stop the old queue. Resuming
  or continuing playback creates a new session and repeats preparation.
- A user Pause during preparation cancels it without an automatic Play.
- Clock drift over 150 ms, restart/session changes, malformed ACKs and device
  faults stop playback. Disconnection attempts STOP with an independent timeout;
  a missing STOP acknowledgement is explicitly reported.

The protocol uses five clock queries, minimum-RTT clock estimation, a three-second
preload lead, one-second positioning, approximately six seconds of lookahead,
200 ms refill checks, and at most five nodes per frame. Retries send the exact
same request bytes. All trajectory timestamps are immutable P4 microseconds;
late data is never repaired by compressing later motion. Clock uncertainty is
estimated from half the best observed round trip; actual synchronization also
depends on the connected media source's reporting and command latency.

## Validation

Build the Windows application with .NET 9:

```sh
dotnet build Source/MultiFunPlayer/MultiFunPlayer.csproj -c Release -p:EnableWindowsTargeting=true
```

The standalone test project runs without the Windows desktop runtime:

```sh
dotnet test Source/MultiFunPlayer.TCode.Tests
```

To include integration tests against the actual CTW queue, compile the host
adapter against your CTW checkout, then provide its executable path:

```sh
g++ -std=c++17 -O2 -I /path/to/ctw_esp32s3_0909/include \
  Source/MultiFunPlayer.TCode.Tests/FirmwareHarness.cpp -o /tmp/mfp-p4-queue
CTW_TCODE_HARNESS=/tmp/mfp-p4-queue dotnet test Source/MultiFunPlayer.TCode.Tests
```

The adapter uses the production firmware parser, CRC, queue and sampling code.
It substitutes a monotonic host clock and ready/settled simulated motors. It does
not test a physical serial driver, C3 wireless transport, Windows UI, media-player
command latency or actual motor feedback. Firmware integration tests are skipped
when `CTW_TCODE_HARNESS` is not configured.
