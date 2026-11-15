"""Kokoro inference with hooks for editing durations, pitch (F0) and energy before vocoding.

Kokoro (StyleTTS2-style) synthesises in explicit stages:

    phonemes -> per-phoneme durations -> pitch curve F0 + energy curve N -> vocoder -> audio

The stock KModel.forward runs all of these in one go. `_forward` below is a copy of
KModel.forward_with_tokens with hooks between the stages, which is what lets markup
change how words are said rather than just what is said.

Units: one duration frame is 600 samples (25 ms at 24 kHz). F0 and N are predicted at
twice that resolution, so one F0 frame is 300 samples (12.5 ms). F0 is in Hz.
"""

from __future__ import annotations

import re
import time
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import torch
from kokoro import KModel, KPipeline

from . import markup
from .markup import Controls, Document

REPO_ID = "hexgrad/Kokoro-82M"
SAMPLE_RATE = 24000
HOP = 600
F0_HOP = HOP // 2
F0_RATE = SAMPLE_RATE / F0_HOP  # 80 F0 frames per second
MAX_PHONEMES = 510
LINE_GAP_S = 0.2
SMOOTH_FRAMES = 7  # ~90 ms of F0 frames; softens the edges of edited regions
LN_SEMITONE = np.log(2) / 12

# English voices only: the markup relies on misaki's English G2P.
VOICES = [
    "af_heart", "af_bella", "af_nicole", "af_aoede", "af_kore", "af_sarah", "af_nova",
    "af_sky", "af_alloy", "af_jessica", "af_river",
    "am_michael", "am_fenrir", "am_puck", "am_echo", "am_eric", "am_liam", "am_onyx",
    "am_adam", "am_santa",
    "bf_emma", "bf_isabella", "bf_alice", "bf_lily",
    "bm_george", "bm_fable", "bm_lewis", "bm_daniel",
]


@dataclass
class TokenInfo:
    text: str
    phonemes: str
    source: str  # markup | lexicon | gold | silver | guessed | punct
    start: float | None = None  # seconds in the final audio
    end: float | None = None
    controls: str = ""
    line: int = 0  # index of the Document (line) the token came from
    vis: tuple[int, int] | None = None  # offsets into that Document's visible text


@dataclass
class SynthResult:
    audio: np.ndarray
    tokens: list[TokenInfo]
    phonemes: list[str]  # one entry per model call (chunk)
    f0_predicted: np.ndarray  # Hz at F0_RATE, NaN in inserted silences
    f0_final: np.ndarray
    g2p_seconds: float
    synth_seconds: float
    warnings: list[str] = field(default_factory=list)

    @property
    def duration(self) -> float:
        return len(self.audio) / SAMPLE_RATE

    @property
    def rtf(self) -> float:
        return self.synth_seconds / self.duration if self.duration else 0.0


@dataclass
class _Edits:
    dur_scale: np.ndarray  # per input id
    spans: list[tuple[int, int, Controls]]  # input-id ranges [a, b)
    global_pitch: float = 0.0
    global_range: float = 1.0


def load_lexicon(path: str | Path) -> dict[str, str]:
    p = Path(path)
    return parse_lexicon(p.read_text(encoding="utf-8")) if p.exists() else {}


def parse_lexicon(text: str) -> dict[str, str]:
    """Lines of `word = phonemes` (slashes optional); `#` starts a comment."""
    lex = {}
    for n, line in enumerate(text.splitlines(), 1):
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        if "=" not in line:
            raise markup.MarkupError(f"Lexicon line {n}: expected 'word = phonemes'")
        word, ps = (s.strip() for s in line.split("=", 1))
        lex[word.lower()] = ps.strip("/")
    return lex


def _source(tk) -> str:
    # misaki stores the rating on tk._ for multi-part words but on tk itself for simple ones.
    # 5 = markup override, 4/3 = gold/silver dictionary, 2 = espeak-ng fallback guess.
    rating = tk._.rating if tk._ and tk._.rating is not None else getattr(tk, "rating", None)
    if not any(c.isalnum() for c in tk.text):
        return "punct"
    # Merged words (contractions etc.) often lose their rating; those are rule-derived, not guesses.
    return {5: "markup", 4: "gold", 3: "silver", 2: "guessed"}.get(rating, "derived")


def _smooth(x: np.ndarray, n: int = SMOOTH_FRAMES) -> np.ndarray:
    if n <= 1 or len(x) < 2:
        return x
    k = np.ones(n) / n
    return np.convolve(np.pad(x, n // 2, mode="edge"), k, mode="valid")[: len(x)]


class Engine:
    def __init__(self, device: str = "cpu"):
        self.device = device
        self.model = KModel(repo_id=REPO_ID).to(device).eval()
        self._pipelines: dict[str, KPipeline] = {}

    def pipeline(self, voice: str) -> KPipeline:
        lang = voice[0]
        if lang not in "ab":
            raise ValueError(f"Only English voices (a*/b*) are supported, got {voice}")
        if lang not in self._pipelines:
            self._pipelines[lang] = KPipeline(lang_code=lang, repo_id=REPO_ID, model=self.model)
        return self._pipelines[lang]

    def phonemize(self, text: str, voice: str = "af_heart") -> str:
        ps, _ = self.pipeline(voice).g2p(text)
        return ps

    # ------------------------------------------------------------------ synthesis

    def synthesize(
        self,
        text: str,
        voice: str = "af_heart",
        speed: float = 1.0,
        lexicon: dict[str, str] | None = None,
        prosody: bool = True,
        global_pitch: float = 0.0,
        global_range: float = 1.0,
    ) -> SynthResult:
        """Synthesize annotated text. With prosody=False, prosody markup and the global
        pitch/range settings are ignored (pronunciation overrides still apply), which
        gives a fair A/B baseline."""
        docs = []
        for line_no, line in enumerate((ln for ln in text.splitlines() if ln.strip()), 1):
            try:
                docs.append(markup.parse(line))
            except markup.MarkupError as e:
                raise markup.MarkupError(f"Line {line_no}: {e}") from None
        return self.synthesize_docs(docs, voice, speed, lexicon, prosody, global_pitch, global_range)

    def synthesize_docs(
        self,
        docs: list[Document],
        voice: str = "af_heart",
        speed: float = 1.0,
        lexicon: dict[str, str] | None = None,
        prosody: bool = True,
        global_pitch: float = 0.0,
        global_range: float = 1.0,
    ) -> SynthResult:
        """Synthesize already-parsed lines (one Document each), so callers can build
        Documents directly, e.g. with overlapping spans that markup cannot express."""
        pipe = self.pipeline(voice)
        pack = pipe.load_voice(voice).to(self.device)
        lexicon = lexicon or {}
        warnings: list[str] = []
        audio_parts: list[np.ndarray] = []
        f0p_parts: list[np.ndarray] = []
        f0f_parts: list[np.ndarray] = []
        all_tokens: list[TokenInfo] = []
        all_ps: list[str] = []
        t_g2p = t_syn = 0.0
        clock = 0.0  # seconds of audio produced so far

        for line_no, doc in enumerate(docs):
            if not prosody:
                doc = Document(doc.g2p_text, doc.visible)

            t0 = time.perf_counter()
            _, mtokens = pipe.g2p(doc.g2p_text)
            t_g2p += time.perf_counter() - t0

            infos = self._token_infos(mtokens, doc, lexicon, warnings)
            for info in infos:
                info.line = line_no
            chunks = self._chunks(mtokens)
            # Each pause goes to the chunk holding the last token before it.
            chunk_pauses = [[] for _ in chunks]
            for pause in doc.pauses:
                owner = 0
                for ci, chunk in enumerate(chunks):
                    if any(mtokens[i]._.vis and mtokens[i]._.vis[1] <= pause.offset for i in chunk):
                        owner = ci
                chunk_pauses[owner].append(pause)
            for chunk, pauses in zip(chunks, chunk_pauses):
                t0 = time.perf_counter()
                out = self._synth_chunk(
                    [mtokens[i] for i in chunk], [infos[i] for i in chunk], doc, pauses, pack, speed,
                    global_pitch if prosody else 0.0, global_range if prosody else 1.0,
                )
                t_syn += time.perf_counter() - t0
                audio, f0p, f0f, ps = out
                for i in chunk:
                    if infos[i].start is not None:
                        infos[i].start += clock
                        infos[i].end += clock
                audio_parts.append(audio)
                f0p_parts.append(f0p)
                f0f_parts.append(f0f)
                all_ps.append(ps)
                clock += len(audio) / SAMPLE_RATE
            all_tokens.extend(infos)

            if line_no < len(docs) - 1:
                gap = int(LINE_GAP_S * SAMPLE_RATE)
                audio_parts.append(np.zeros(gap, dtype=np.float32))
                nan = np.full(gap // F0_HOP, np.nan, dtype=np.float32)
                f0p_parts.append(nan)
                f0f_parts.append(nan)
                clock += gap / SAMPLE_RATE

        if not audio_parts:
            raise ValueError("Nothing to say")
        return SynthResult(
            audio=np.concatenate(audio_parts),
            tokens=all_tokens,
            phonemes=all_ps,
            f0_predicted=np.concatenate(f0p_parts),
            f0_final=np.concatenate(f0f_parts),
            g2p_seconds=t_g2p,
            synth_seconds=t_syn,
            warnings=warnings,
        )

    def synthesize_phonemes(self, ps: str, voice: str = "af_heart", speed: float = 1.0) -> SynthResult:
        """Raw phoneme input, bypassing G2P entirely (for experimenting with the vocabulary)."""
        pack = self.pipeline(voice).load_voice(voice).to(self.device)
        dropped = sorted({c for c in ps if c not in self.model.vocab})
        ids = [0, *(self.model.vocab[c] for c in ps if c in self.model.vocab), 0]
        if len(ids) - 2 > MAX_PHONEMES:
            raise ValueError(f"Too many phonemes ({len(ids) - 2} > {MAX_PHONEMES})")
        t0 = time.perf_counter()
        audio, _, f0p, f0f = self._forward(ids, pack[min(len(ps), MAX_PHONEMES) - 1], speed, None)
        t_syn = time.perf_counter() - t0
        warnings = [f"Not in Kokoro's phoneme vocabulary, dropped: {' '.join(dropped)}"] if dropped else []
        return SynthResult(audio, [], [ps], f0p, f0f, 0.0, t_syn, warnings)

    # ------------------------------------------------------------------ internals

    def _token_infos(self, mtokens, doc: Document, lexicon, warnings) -> list[TokenInfo]:
        infos = []
        cursor = 0
        for tk in mtokens:
            source = _source(tk)
            key = tk.text.lower()
            if key in lexicon and source != "markup":
                tk.phonemes = lexicon[key]
                source = "lexicon"
            tk.phonemes = tk.phonemes or ""
            # Locate the token in the visible text so spans/pauses can be mapped onto it.
            pos = doc.visible.find(tk.text, cursor)
            if pos < 0:
                tk._.vis = None
            else:
                tk._.vis = (pos, pos + len(tk.text))
                cursor = pos + len(tk.text)
            labels = [s.controls.describe() for s in doc.spans if tk._.vis and s.start < tk._.vis[1] and s.end > tk._.vis[0]]
            infos.append(TokenInfo(tk.text, tk.phonemes, source, controls=" | ".join(l for l in labels if l), vis=tk._.vis))
            if source == "guessed" and tk.phonemes:
                warnings.append(f"'{tk.text}' isn't in the dictionary; its pronunciation /{tk.phonemes}/ was guessed")
            if source not in ("punct", "markup") and not tk.phonemes:
                warnings.append(f"'{tk.text}' produced no phonemes and will be silent")
        return infos

    @staticmethod
    def _token_ps(tk) -> str:
        return tk.phonemes + (" " if tk.whitespace else "")

    def _chunks(self, mtokens) -> list[list[int]]:
        """Split token indices into model calls of <= MAX_PHONEMES, preferring sentence ends."""
        chunks, cur, count = [], [], 0
        for i, tk in enumerate(mtokens):
            n = len(self._token_ps(tk))
            if cur and count + n > MAX_PHONEMES:
                cut = len(cur)
                for marks in ("!.?…", ":;", ",—"):
                    z = next((j for j in range(len(cur) - 1, 0, -1)
                              if mtokens[cur[j]].phonemes and mtokens[cur[j]].phonemes in marks), None)
                    if z is not None:
                        cut = z + 1
                        break
                chunks.append(cur[:cut])
                cur = cur[cut:]
                count = sum(len(self._token_ps(mtokens[j])) for j in cur)
            cur.append(i)
            count += n
        if cur:
            chunks.append(cur)
        return chunks

    def _synth_chunk(self, mtokens, infos, doc: Document, pauses, pack, speed, global_pitch, global_range):
        vocab = self.model.vocab
        # Phoneme string + where each token's phonemes land in it.
        ps, ranges = "", []
        for tk in mtokens:
            start = len(ps)
            ps += tk.phonemes
            ranges.append((start, len(ps)))
            if tk.whitespace and ps and not ps.endswith(" "):
                ps += " "
        ps = ps.rstrip()

        ids, ps2id = [0], []
        for ch in ps:
            if ch in vocab:
                ps2id.append(len(ids))
                ids.append(vocab[ch])
            else:
                ps2id.append(None)
        ids.append(0)

        def id_range(a, b):
            got = [ps2id[k] for k in range(a, min(b, len(ps))) if ps2id[k] is not None]
            return (got[0], got[-1] + 1) if got else None

        tok_ids = [id_range(a, b) for a, b in ranges]

        dur_scale = np.ones(len(ids), dtype=np.float32)
        spans = []
        for span in doc.spans:
            hit = [r for tk, r in zip(mtokens, tok_ids)
                   if r and tk._.vis and span.start < tk._.vis[1] and span.end > tk._.vis[0]]
            if not hit:
                continue
            a, b = hit[0][0], hit[-1][1]
            dur_scale[a:b] /= span.controls.rate
            spans.append((a, b, span.controls))

        edits = _Edits(dur_scale, spans, global_pitch, global_range)
        audio, pred_dur, f0p, f0f = self._forward(ids, pack[len(ps) - 1], speed, edits)
        cum = np.concatenate([[0], np.cumsum(pred_dur)])

        # Pauses: silence after the preceding token, mid-way through its trailing space if it has one.
        inserts = []  # (sample position, n samples)
        for pause in pauses:
            prev = [r for tk, r in zip(mtokens, tok_ids) if r and tk._.vis and tk._.vis[1] <= pause.offset]
            pos = 0
            if prev:
                b = prev[-1][1]
                pos = int(cum[b] + (pred_dur[b] // 2 if b < len(ids) - 1 and ids[b] == vocab[" "] else 0))
            inserts.append((pos * HOP, int(pause.seconds * SAMPLE_RATE)))

        # Token timings (before inserting pauses), then shift by pauses that precede them.
        for info, r in zip(infos, tok_ids):
            if r:
                info.start = cum[r[0]] * HOP / SAMPLE_RATE
                info.end = cum[r[1]] * HOP / SAMPLE_RATE
        for pos, n in sorted(inserts, reverse=True):
            audio = np.concatenate([audio[:pos], np.zeros(n, dtype=np.float32), audio[pos:]])
            fpos, fn = pos // F0_HOP, n // F0_HOP
            nan = np.full(fn, np.nan, dtype=np.float32)
            f0p = np.concatenate([f0p[:fpos], nan, f0p[fpos:]])
            f0f = np.concatenate([f0f[:fpos], nan, f0f[fpos:]])
            for info in infos:
                if info.start is not None and info.start * SAMPLE_RATE >= pos - 1:
                    info.start += n / SAMPLE_RATE
                    info.end += n / SAMPLE_RATE
        return audio, f0p, f0f, ps

    @torch.no_grad()
    def _forward(self, ids: list[int], ref_s: torch.Tensor, speed: float, edits: _Edits | None):
        m = self.model
        input_ids = torch.LongTensor([ids]).to(self.device)
        ref_s = ref_s.to(self.device)
        input_lengths = torch.full((1,), input_ids.shape[-1], device=self.device, dtype=torch.long)
        text_mask = torch.arange(input_lengths.max()).unsqueeze(0).type_as(input_lengths)
        text_mask = torch.gt(text_mask + 1, input_lengths.unsqueeze(1)).to(self.device)
        bert_dur = m.bert(input_ids, attention_mask=(~text_mask).int())
        d_en = m.bert_encoder(bert_dur).transpose(-1, -2)
        s = ref_s[:, 128:]
        d = m.predictor.text_encoder(d_en, s, input_lengths, text_mask)
        x, _ = m.predictor.lstm(d)
        duration = torch.sigmoid(m.predictor.duration_proj(x)).sum(axis=-1) / speed

        # Hook 1: durations (rate control). The F0/energy predictors below run on the
        # stretched sequence, so the model adapts its intonation to the new timing.
        if edits is not None:
            duration = duration * torch.from_numpy(edits.dur_scale).to(self.device)[None]
        pred_dur = torch.round(duration).clamp(min=1).long().squeeze(0)

        indices = torch.repeat_interleave(torch.arange(input_ids.shape[1], device=self.device), pred_dur)
        aln = torch.zeros((input_ids.shape[1], indices.shape[0]), device=self.device)
        aln[indices, torch.arange(indices.shape[0])] = 1
        aln = aln.unsqueeze(0)
        en = d.transpose(-1, -2) @ aln
        F0, N = m.predictor.F0Ntrain(en, s)
        f0_pred = F0[0].cpu().numpy().copy()

        # Hook 2: pitch and energy curves.
        dur_np = pred_dur.cpu().numpy()
        vol_db = None
        if edits is not None:
            f0_new, n_delta, vol_db = self._edit_curves(f0_pred, dur_np, edits)
            F0 = torch.from_numpy(f0_new)[None].to(self.device)
            N = N + torch.from_numpy(n_delta)[None].to(self.device)

        t_en = m.text_encoder(input_ids, input_lengths, text_mask)
        asr = t_en @ aln
        audio = m.decoder(asr, F0, N, ref_s[:, :128]).squeeze().cpu().numpy().astype(np.float32)

        # Hook 3: volume, applied to the waveform.
        if vol_db is not None and np.any(vol_db):
            gain = np.repeat(10 ** (vol_db / 20), F0_HOP)
            gain = np.pad(gain, (0, max(0, len(audio) - len(gain))), mode="edge")[: len(audio)]
            audio = audio * gain.astype(np.float32)
        return audio, dur_np, f0_pred, F0[0].cpu().numpy()

    @staticmethod
    def _edit_curves(f0: np.ndarray, pred_dur: np.ndarray, edits: _Edits):
        n = len(f0)
        cum = np.concatenate([[0], np.cumsum(pred_dur)]) * 2  # id -> F0 frame
        positive = f0 > 1
        logf = np.log(np.where(positive, f0, 1.0))
        # Soft voicing weight so range edits never pull unvoiced frames up into voicing.
        voiced_w = np.clip((f0 - 30) / 30, 0, 1)
        voiced = voiced_w > 0.5

        delta = np.zeros(n)  # log-F0 change
        if edits.global_range != 1.0 and voiced.any():
            mean = logf[voiced].mean()
            delta += (mean + (logf - mean) * edits.global_range - logf) * voiced_w
        delta += edits.global_pitch * LN_SEMITONE

        n_delta = np.zeros(n)
        vol_db = np.zeros(n)
        for a, b, c in edits.spans:
            fa, fb = int(cum[a]), int(min(cum[b], n))
            if fb <= fa:
                continue
            d = np.zeros(n)
            seg = slice(fa, fb)
            if c.range is not None:
                vmask = voiced[seg]
                if vmask.any():
                    cur = logf[seg] + delta[seg]
                    mean = cur[vmask].mean()
                    d[seg] += (mean + (cur - mean) * c.range - cur) * voiced_w[seg]
            d[seg] += c.pitch * LN_SEMITONE
            if c.contour:
                # Ramp across the voiced part of the span only: durations are soft alignments and
                # a word's last frames are often silent, which would leave the glide's peak unheard.
                sign = 1 if c.contour.shape == "rise" else -1
                v_idx = np.flatnonzero(voiced[seg])
                va, vb = (fa + v_idx[0], fa + v_idx[-1] + 1) if len(v_idx) > 1 else (fa, fb)
                ramp = np.zeros(fb - fa)
                ramp[va - fa:vb - fa] = np.linspace(0, 1, vb - va)
                ramp[vb - fa:] = 1
                d[seg] += ramp * sign * c.contour.semitones * LN_SEMITONE
            delta += _smooth(d)
            if c.energy:
                e = np.zeros(n)
                e[seg] = c.energy
                n_delta += _smooth(e)
            if c.vol:
                v = np.zeros(n)
                v[seg] = c.vol
                vol_db += _smooth(v)

        f0_new = np.where(positive, np.exp(logf + delta), f0).astype(np.float32)
        return f0_new, n_delta.astype(np.float32), vol_db


def safe_filename(s: str, limit: int = 40) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "_", s).strip("_")[:limit] or "untitled"
