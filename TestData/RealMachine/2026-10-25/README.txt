2026-10-25 real-machine test dataset

Two sessions: 013252-c5be1e and 021708-603551. No videos included.
Firmware baseline e85d8c9 plus telemetry fixes published as 7fc955a in yu1745/ctw_esp32s3_0909.
Player source is on motor-telemetry-testing-20261025; latest commit d0975b5 includes a later 200ms clock-stop threshold. These recordings preceded that change (150ms threshold). Do not treat the newest source as the exact recording binary.

sessions/*/events.jsonl.gz are lossless copies of the original JSONL logs. Decompress with Python gzip, 7-Zip, or gzip. manifest.json lists raw SHA256 hashes and maps original script paths to portable numbered files in scripts/. Script bytes are unchanged. Original media titles and local paths remain inside logs for correlation; video files are not supplied. Scripts may contain content from third parties; inclusion does not grant additional redistribution rights.

Always apply annotation-deletions.json before counting labels. Raw events are intentionally preserved. An annotation refers to a nearby segment, not an exact physical event instant. Repeated marks and overlapping windows are not independent samples. '正常' means personally satisfactory, not a mandatory normal state. Unmarked or unplayed segments are not negative/normal examples.
Group '臀模震动不适' and '异响' together for the user's analysis. '抖动' means continuous small-amplitude output jitter. '机械感重' is a subjective mechanical/stiff feeling. Human reaction delay is unknown.

Before waveform analysis, align video and command clocks using CLOCK diagnostics (boot/session, mediaEpochUs, sourceSeconds, speed). Split at seek, pause, reconnect, epoch or revision changes; do not use one global shift. Preserve actual motor following lag. Apply per-axis script Offset and global offset according to the source implementation. Media playback coverage is partial, with pauses and seeks.

tracking position/command angles are radians; use validity masks and command/feedback age. motor_health values: speed rad/s, measured Iq A, FET temperature C, motor temperature C, bus V. Validity and ages must be checked; invalid values are not zero. Iq is not calibrated endpoint force. Telemetry is low-rate and does not resolve every current peak or high-frequency vibration. The seventh motor is disabled in these tests; do not treat missing data as failure.

motor_power_observation records observed feedback continuity, not actual uninterrupted power-on time. Missing feedback does not prove power-off. stop.dropped=0 reports the recording queue, not end-to-end packet delivery.

Physical context: effective moving payload estimated around 12kg; no calibrated endpoint force sensor. Coupled L0/L1/L2/R1/R2 use six motors; independent R0 uses motor7. Do not equate a motion axis with a single motor. Output ranges/filter/settings can change; stratify comparisons accordingly.

Known second-session events: R1 segment shorter than 1ms, P4 positioning-state change, and media/device drift166ms causing a 150ms-threshold stop. Early motor2 reports error64; meaning not yet confirmed. Temperature readings reached about88-89C and require sensor/rating verification. These data do not certify safe operating limits.
