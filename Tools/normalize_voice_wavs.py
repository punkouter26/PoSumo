#!/usr/bin/env python3
"""Match the loudness of every fighter voice clip, in the WAV files themselves.

WHY THIS EXISTS ALONGSIDE `PoSumo -> Normalize Voice Levels`.
Those are two different things and the project needs both:

  * `NormalizeVoice` (the editor tool) writes `VoiceGains.asset`, a table of
    PLAYBACK multipliers. It cannot make a quiet clip louder, because
    `AudioSource.volume` clamps at 1 and `Systems_FighterVoice` already
    multiplies by 0.9 -- so it can only ever turn things DOWN. That is the
    documented "attenuate-only" rule.
  * This tool rewrites the SAMPLES, so a quiet clip genuinely comes up.

Measured before the first run: 75 clips spanning 22.3 dB, a 13x ratio between
the quietest (Nick_Happy_1, RMS 0.0245) and the loudest (Kim_Happy_5, 0.3181).
That is the difference between inaudible and startling on the same fighter.

RMS, NOT PEAK. Peak normalisation matches the loudest instant, which is not
what a listener hears -- a clip with one sharp transient and a quiet body
normalises to "loud" and still sounds quiet. RMS tracks perceived loudness.

RMS IS TAKEN OVER VOICED SAMPLES ONLY. The regenerated jeers are three short
syllables with deliberate silence between them; averaging that silence in
would score them quiet and then over-boost them. Anything below 8% of the
clip's own peak is treated as silence and excluded.

PEAK CEILING. After scaling, a clip is backed off if it would exceed
PEAK_CEILING, so nothing clips. That is reported per clip rather than applied
silently -- a clip that cannot reach the target without clipping is telling
you its dynamics are unusual.

FORMAT IS PRESERVED EXACTLY. The folder is a mixture -- mono 44.1 kHz
(generated), stereo 48 kHz (Matt, Nick) and mono 24 kHz (Kim) -- and nothing
here resamples or re-channels. Only the sample values change.

Usage:
    python Tools/normalize_voice_wavs.py            # report only, writes nothing
    python Tools/normalize_voice_wavs.py --apply    # rewrite the files
"""

import argparse
import glob
import math
import os
import struct
import sys

VOICE_GLOB = "Assets/Resources/Audio/Voice/*.wav"

# Chosen from the measured distribution: close to the median of the 75 clips, so
# most files move a little rather than a few moving enormously.
TARGET_RMS = 0.14

# Leaves headroom under full scale. VoiceGains still trims at playback, and the
# voice bus is summed with impacts and crowd, so arriving at 1.0 here would
# leave the mix no room at all.
PEAK_CEILING = 0.95

# Below this fraction of a clip's own peak counts as silence for the RMS window.
VOICED_FLOOR = 0.08


def read_wav(path):
    """Parse a RIFF/WAVE file into (header_bytes, fmt, samples).

    Walks the chunk list rather than assuming the data starts at byte 44: that
    holds for the files this project generates but is not guaranteed for a real
    recording, which may carry LIST/fact chunks first.
    """
    with open(path, "rb") as handle:
        raw = handle.read()

    if raw[0:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise ValueError("not a RIFF/WAVE file")

    channels = sample_rate = bits = None
    data_start = data_size = None
    offset = 12
    while offset + 8 <= len(raw):
        chunk_id = raw[offset:offset + 4]
        chunk_size = struct.unpack("<I", raw[offset + 4:offset + 8])[0]
        body = offset + 8
        if chunk_id == b"fmt ":
            channels = struct.unpack("<H", raw[body + 2:body + 4])[0]
            sample_rate = struct.unpack("<I", raw[body + 4:body + 8])[0]
            bits = struct.unpack("<H", raw[body + 14:body + 16])[0]
        elif chunk_id == b"data":
            data_start, data_size = body, chunk_size
        offset = body + chunk_size + (chunk_size & 1)

    if data_start is None or bits != 16:
        raise ValueError(f"unsupported wav (bits={bits})")

    count = data_size // 2
    samples = struct.unpack("<%dh" % count, raw[data_start:data_start + count * 2])
    return raw, (channels, sample_rate, bits), samples, data_start


def measure(samples):
    """Peak and voiced-only RMS, both in 0..1."""
    if not samples:
        return 0.0, 0.0
    peak = max(abs(v) for v in samples) / 32768.0
    if peak <= 0:
        return 0.0, 0.0
    floor = peak * VOICED_FLOOR * 32768.0
    voiced = [v for v in samples if abs(v) > floor]
    if not voiced:
        return peak, 0.0
    rms = math.sqrt(sum((v / 32768.0) ** 2 for v in voiced) / len(voiced))
    return peak, rms


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--apply", action="store_true",
                        help="rewrite the files; without it this only reports")
    parser.add_argument("--target", type=float, default=TARGET_RMS)
    args = parser.parse_args()

    paths = sorted(glob.glob(VOICE_GLOB))
    if not paths:
        print(f"no clips matched {VOICE_GLOB}", file=sys.stderr)
        return 1

    before, after, capped, changed = [], [], [], 0

    for path in paths:
        try:
            raw, fmt, samples, data_start = read_wav(path)
        except ValueError as err:
            print(f"  SKIP {os.path.basename(path)}: {err}")
            continue

        peak, rms = measure(samples)
        if rms <= 0:
            continue
        before.append(rms)

        gain = args.target / rms
        if peak * gain > PEAK_CEILING:
            gain = PEAK_CEILING / peak
            capped.append(os.path.basename(path))

        scaled = [max(-32768, min(32767, int(round(v * gain)))) for v in samples]
        after.append(measure(scaled)[1])
        changed += 1

        if args.apply:
            body = struct.pack("<%dh" % len(scaled), *scaled)
            with open(path, "wb") as handle:
                handle.write(raw[:data_start])
                handle.write(body)
                handle.write(raw[data_start + len(body):])

    def spread(values):
        lo, hi = min(values), max(values)
        return lo, hi, 20 * math.log10(hi / max(1e-9, lo))

    lo0, hi0, db0 = spread(before)
    lo1, hi1, db1 = spread(after)
    mode = "APPLIED" if args.apply else "DRY RUN"
    print(f"VOICE WAV NORMALISE ({mode}): {changed} clips, target RMS {args.target:.3f}")
    print(f"  before: RMS {lo0:.4f}..{hi0:.4f}  spread {db0:.1f} dB")
    print(f"  after : RMS {lo1:.4f}..{hi1:.4f}  spread {db1:.1f} dB")
    if capped:
        print(f"  peak-capped ({len(capped)}), these sit below target to avoid clipping:")
        for name in capped:
            print(f"    {name}")
    if not args.apply:
        print("  nothing written -- re-run with --apply")
    return 0


if __name__ == "__main__":
    sys.exit(main())
