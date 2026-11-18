"""Masters the exported video's soundtrack to YouTube loudness (-14 LUFS, -1.5 dBTP).

Usage: python master_audio.py
Reads output/oddly-pronounced-names.mp4 and writes output/oddly-pronounced-names-youtube.mp4,
copying the video stream untouched. Needs ffmpeg: on the PATH, or `pip install imageio-ffmpeg`.
"""
import json
import pathlib
import re
import shutil
import subprocess

HERE = pathlib.Path(__file__).parent
SOURCE = HERE / "output" / "oddly-pronounced-names.mp4"
TARGET = HERE / "output" / "oddly-pronounced-names-youtube.mp4"
LOUDNORM = "loudnorm=I=-14:TP=-1.5:LRA=11"


def ffmpeg():
    found = shutil.which("ffmpeg")
    if found:
        return found
    import imageio_ffmpeg
    return imageio_ffmpeg.get_ffmpeg_exe()


def main():
    exe = ffmpeg()
    # Pass 1: measure.
    probe = subprocess.run([exe, "-hide_banner", "-i", str(SOURCE), "-af", LOUDNORM + ":print_format=json",
                            "-f", "null", "-"], capture_output=True, text=True, check=True)
    stats = json.loads(re.findall(r"\{[^{}]*\}", probe.stderr)[-1])
    # Pass 2: apply, with the measured values for an accurate linear gain.
    measured = (f":measured_I={stats['input_i']}:measured_TP={stats['input_tp']}"
                f":measured_LRA={stats['input_lra']}:measured_thresh={stats['input_thresh']}"
                f":offset={stats['target_offset']}:linear=true")
    subprocess.run([exe, "-hide_banner", "-y", "-i", str(SOURCE), "-c:v", "copy", "-af", LOUDNORM + measured,
                    "-ar", "48000", "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart", str(TARGET)],
                   check=True, capture_output=True)
    print(f"{SOURCE.name}: {stats['input_i']} LUFS, {stats['input_tp']} dBTP -> {TARGET.name}")


if __name__ == "__main__":
    main()
