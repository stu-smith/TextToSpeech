"""Runs the worker as a subprocess over its real stdin/stdout protocol."""

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest
import soundfile as sf

ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture(scope="module")
def worker():
    env = {**os.environ, "PYTHONPATH": str(ROOT), "PYTHONIOENCODING": "utf-8"}
    proc = subprocess.Popen([sys.executable, "-m", "ttsengine.worker"], cwd=ROOT, env=env,
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            text=True, encoding="utf-8")
    ids = iter(range(1, 1000))

    def call(method, **params):
        req_id = next(ids)
        proc.stdin.write(json.dumps({"id": req_id, "method": method, "params": params}) + "\n")
        proc.stdin.flush()
        while True:
            msg = json.loads(proc.stdout.readline())
            if msg.get("id") == req_id:
                return msg

    while json.loads(proc.stdout.readline()).get("event") != "ready":
        pass
    yield call
    call("shutdown")
    proc.wait(timeout=30)


def test_synthesize_overlapping_regions_and_formats(worker, tmp_path):
    text = "Generate an IOSS return,\nthen file it today."
    regions = [
        {"start": 12, "end": 16, "pronunciation": "ˈIɑs"},
        {"start": 0, "end": 16, "pitch": 2},
        {"start": 9, "end": 23, "rate": 0.8, "pauseAfterMs": 300},
        {"start": text.index("today"), "end": text.index("today") + 5, "contour": "rise", "contourSemitones": 5},
    ]
    for ext in ("wav", "flac", "ogg", "mp3"):
        out = tmp_path / f"s.{ext}"
        reply = worker("synthesize", text=text, regions=regions, voice="af_heart", output=str(out),
                       lexicon={"nguyen": "wˈɪn"}, baselineOutput=str(tmp_path / f"b.{ext}") if ext == "wav" else None)
        assert "result" in reply, reply
        assert sf.info(str(out)).duration > 1.5
    r = reply["result"]
    ioss = next(t for t in r["tokens"] if t["text"] == "IOSS")
    assert (ioss["phonemes"], ioss["source"], ioss["charStart"], ioss["charEnd"]) == ("ˈIɑs", "markup", 12, 16)
    today = next(t for t in r["tokens"] if t["text"] == "today")
    assert today["charStart"] == text.index("today")
    assert len(r["f0Final"]) == len(r["envelope"]) or abs(len(r["f0Final"]) - len(r["envelope"])) <= 2


def test_phonemize_and_errors(worker):
    r = worker("phonemize", text="Loughborough station", lexicon={"loughborough": "lˈʌfbəɹə"})["result"]
    assert r["tokens"][0]["phonemes"] == "lˈʌfbəɹə"
    assert worker("nope")["error"]["message"].startswith("Unknown method")
    assert "error" in worker("synthesize", text="   ", output="x.wav")


def test_reference_recording(worker, tmp_path):
    text = "Here are ten surnames that sound nothing like they look."
    # A stand-in recording: another voice reading the same text with a slower, different rhythm.
    ref = tmp_path / "reading.flac"
    made = worker("synthesize", text=text, regions=[{"start": 9, "end": 12, "rate": 0.5}], voice="am_adam", output=str(ref))
    assert "result" in made, made

    plain = worker("synthesize", text=text, voice="bf_emma", output=str(tmp_path / "plain.wav"))["result"]
    led = worker("synthesize", text=text, voice="bf_emma", output=str(tmp_path / "led.wav"),
                 referenceAudio=str(ref), referenceStrength=1.0)["result"]
    assert plain["f0Reference"] == [] and any(v for v in led["f0Reference"])
    assert led["duration"] > plain["duration"] + 0.2  # it took on the slower reading
    assert led["warnings"] == []

    off = worker("synthesize", text=text, voice="bf_emma", output=str(tmp_path / "off.wav"),
                 referenceAudio=str(ref), referenceStrength=0)["result"]
    assert off["f0Reference"] == []

    two_lines = worker("synthesize", text=text + "\nAnd again.", voice="bf_emma", output=str(tmp_path / "two.wav"),
                       referenceAudio=str(ref))["result"]
    assert any("single line" in w for w in two_lines["warnings"])
    missing = worker("synthesize", text=text, voice="bf_emma", output=str(tmp_path / "m.wav"),
                     referenceAudio=str(tmp_path / "nope.wav"))["result"]
    assert any("not found" in w for w in missing["warnings"])
    wrong = worker("synthesize", text="Thanks!", voice="bf_emma", output=str(tmp_path / "w.wav"),
                   referenceAudio=str(ref))["result"]
    assert any("may not be a reading of this text" in w for w in wrong["warnings"])
