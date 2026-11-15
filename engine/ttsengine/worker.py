"""JSON-lines worker driven by TTS Studio over stdin/stdout.

    python -m ttsengine.worker

Requests are one JSON object per line: {"id": 1, "method": "synthesize", "params": {...}}.
Responses: {"id": 1, "result": {...}} or {"id": 1, "error": {"message": "..."}}.
Events (no id): {"event": "log", "message": "..."} and {"event": "ready", ...} once the model
has loaded. The protocol owns the real stdout; anything else that writes to stdout (Python or
native code) is redirected to stderr, which the host treats as a log.
"""

from __future__ import annotations

import json
import math
import os
import sys
import traceback
from pathlib import Path

import numpy as np
import soundfile as sf

from . import document, markup
from .engine import F0_HOP, F0_RATE, SAMPLE_RATE, VOICES, Engine, SynthResult

PROTOCOL_VERSION = 1
FORMATS = {".wav": ("WAV", "PCM_16"), ".flac": ("FLAC", "PCM_16"), ".ogg": ("OGG", "VORBIS"),
           ".mp3": ("MP3", "MPEG_LAYER_III")}


class Worker:
    def __init__(self, out):
        self.out = out
        self.engine: Engine | None = None

    def send(self, msg: dict) -> None:
        self.out.write(json.dumps(msg, ensure_ascii=False, allow_nan=False) + "\n")
        self.out.flush()

    def log(self, message: str) -> None:
        self.send({"event": "log", "message": message})

    # ------------------------------------------------------------------ methods

    def hello(self, _params: dict) -> dict:
        return {"protocol": PROTOCOL_VERSION, "voices": VOICES, "sampleRate": SAMPLE_RATE,
                "formats": sorted(f.lstrip(".") for f in FORMATS)}

    def synthesize(self, p: dict) -> dict:
        text = p.get("text", "")
        regions = [document.Region.from_json(r) for r in p.get("regions", [])]
        lines, warnings = document.build(text, regions)
        if not lines:
            raise ValueError("The sample has no text to speak")
        args = dict(
            voice=p.get("voice") or "af_heart",
            speed=float(p.get("speed") or 1.0),
            lexicon={k.lower(): v.strip("/") for k, v in (p.get("lexicon") or {}).items()},
            global_pitch=float(p.get("pitch") or 0.0),
            global_range=float(p.get("pitchRange", 1.0) if p.get("pitchRange") is not None else 1.0),
        )
        docs = [ln.doc for ln in lines]
        result = self.engine.synthesize_docs(docs, **args)
        result.warnings[:0] = warnings
        out = _write(result.audio, p["output"])
        reply = self._describe(result, lines)
        reply["output"] = out
        if p.get("baselineOutput"):
            base = self.engine.synthesize_docs(docs, prosody=False, **args)
            reply["baseline"] = {"output": _write(base.audio, p["baselineOutput"]), "duration": base.duration}
        return reply

    def phonemize(self, p: dict) -> dict:
        """Dictionary pronunciation of plain text (lexicon applied), word by word."""
        text = p.get("text", "")
        voice = p.get("voice") or "af_heart"
        lexicon = {k.lower(): v.strip("/") for k, v in (p.get("lexicon") or {}).items()}
        pipe = self.engine.pipeline(voice)
        doc = markup.Document(text, text)
        _, mtokens = pipe.g2p(text)
        warnings: list[str] = []
        infos = self.engine._token_infos(mtokens, doc, lexicon, warnings)
        tokens = [{"text": t.text, "phonemes": t.phonemes, "source": t.source} for t in infos]
        ps = "".join(t.phonemes + (" " if tk.whitespace else "") for t, tk in zip(infos, mtokens)).strip()
        return {"phonemes": ps, "tokens": tokens, "warnings": warnings}

    @staticmethod
    def _describe(result: SynthResult, lines: list[document.Line]) -> dict:
        tokens = []
        for t in result.tokens:
            char = lines[t.line].orig_range(t.vis) if t.vis else None
            tokens.append({
                "text": t.text, "phonemes": t.phonemes, "source": t.source,
                "start": _num(t.start), "end": _num(t.end), "controls": t.controls,
                "charStart": char[0] if char else None, "charEnd": char[1] if char else None,
            })
        audio = result.audio
        n = len(audio) // F0_HOP
        envelope = np.abs(audio[: n * F0_HOP]).reshape(n, F0_HOP).max(axis=1) if n else np.zeros(0)
        return {
            "duration": result.duration,
            "sampleRate": SAMPLE_RATE,
            "synthSeconds": result.synth_seconds,
            "phonemes": result.phonemes,
            "warnings": list(dict.fromkeys(result.warnings)),
            "tokens": tokens,
            "f0Rate": F0_RATE,
            "f0Predicted": _curve(result.f0_predicted),
            "f0Final": _curve(result.f0_final),
            "envelope": [round(float(x), 4) for x in envelope],
        }

    # ------------------------------------------------------------------ loop

    def handle(self, line: str) -> None:
        req_id = None
        try:
            req = json.loads(line)
            req_id = req.get("id")
            method = req.get("method", "")
            if method == "shutdown":
                self.send({"id": req_id, "result": {}})
                raise SystemExit(0)
            fn = {"hello": self.hello, "synthesize": self.synthesize, "phonemize": self.phonemize}.get(method)
            if fn is None:
                raise ValueError(f"Unknown method '{method}'")
            self.send({"id": req_id, "result": fn(req.get("params") or {})})
        except SystemExit:
            raise
        except Exception as e:  # report every failure to the host rather than dying
            traceback.print_exc(file=sys.stderr)
            self.send({"id": req_id, "error": {"message": str(e) or type(e).__name__, "type": type(e).__name__}})

    def run(self) -> None:
        self.log("Loading Kokoro model (downloads ~330 MB if not already cached)...")
        self.engine = Engine()
        self.engine.pipeline("af_heart")  # load G2P now rather than on the first request
        self.send({"event": "ready", **self.hello({})})
        for line in sys.stdin:
            if line.strip():
                self.handle(line)


def _num(x):
    return None if x is None else round(float(x), 4)


def _curve(a: np.ndarray) -> list:
    return [None if not math.isfinite(v) or v <= 1 else round(float(v), 1) for v in a]


def _write(audio: np.ndarray, path: str) -> str:
    p = Path(path)
    fmt = FORMATS.get(p.suffix.lower())
    if fmt is None:
        raise ValueError(f"Unsupported audio format '{p.suffix}'. Use one of: {', '.join(FORMATS)}")
    p.parent.mkdir(parents=True, exist_ok=True)
    sf.write(str(p), audio, SAMPLE_RATE, format=fmt[0], subtype=fmt[1])
    return str(p)


def main() -> None:
    # Keep the real stdout for the protocol and point fd 1 at stderr, so stray output from
    # libraries (including native ones such as espeak-ng) cannot corrupt the stream.
    proto = os.fdopen(os.dup(1), "w", encoding="utf-8", newline="\n")
    os.dup2(2, 1)
    sys.stdout = sys.stderr
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    try:
        Worker(proto).run()
    except SystemExit:
        pass


if __name__ == "__main__":
    main()
