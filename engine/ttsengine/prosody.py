"""Prosody transfer: make Kokoro follow the timing and intonation of a reference recording.

The reference is a person (or another TTS) reading the same text. Kokoro renders the line once
on its own; dynamic time warping over MFCCs maps that render onto the reference, which gives
reference times for Kokoro's word boundaries. From those:

- durations: each word's phonemes are stretched to the reference word's length (keeping Kokoro's
  proportions within the word), and the gaps between words follow the reference's;
- pitch: the reference's F0 contour, in semitones from its median, is re-centred on Kokoro's
  voice and warped onto Kokoro's frames.

Both become the baseline that pronunciation and prosody regions then edit, so regions behave
exactly as they do without a reference. Only numpy is needed.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np
import soundfile as sf

SAMPLE_RATE = 24000
HOP = 600  # Kokoro duration frame (25 ms)
F0_HOP = HOP // 2  # Kokoro F0 frame (12.5 ms)
FEAT_HOP = 240  # 10 ms analysis frames for alignment
F0_MIN, F0_MAX = 60.0, 500.0


@dataclass
class Reference:
    """A recording of the line to imitate, with how strongly to follow it (0..1)."""
    audio: np.ndarray  # mono float32 at SAMPLE_RATE
    timing: float = 1.0
    pitch: float = 1.0
    keep_overrides: bool = True  # words with a pronunciation override keep Kokoro's own pitch

    @classmethod
    def load(cls, path, **kw) -> "Reference":
        audio, sr = sf.read(str(path), dtype="float32", always_2d=True)
        return cls(resample(audio.mean(axis=1), sr, SAMPLE_RATE), **kw)


# ---------------------------------------------------------------------------- signal helpers

def resample(x: np.ndarray, sr: int, target: int) -> np.ndarray:
    """Band-limited FFT resampling (fine for offline use on whole utterances)."""
    if sr == target:
        return x.astype(np.float32)
    n = int(round(len(x) * target / sr))
    spec = np.fft.rfft(x)
    out = np.zeros(n // 2 + 1, dtype=complex)
    k = min(len(spec), len(out))
    out[:k] = spec[:k]
    return (np.fft.irfft(out, n) * (n / len(x))).astype(np.float32)


def _frames(x: np.ndarray, length: int, hop: int) -> np.ndarray:
    n = 1 + max(0, (len(x) - 1) // hop)
    x = np.pad(x, (length // 2, length + hop))
    idx = np.arange(length)[None, :] + hop * np.arange(n)[:, None]
    return x[idx]


def pitch(x: np.ndarray, sr: int = SAMPLE_RATE, hop: int = F0_HOP) -> np.ndarray:
    """F0 in Hz per hop (0 where unvoiced). Two YIN passes: the first finds the speaker's typical
    pitch, the second searches about an octave either side of it, which avoids most octave errors;
    any left are folded back towards the running median rather than dropped."""
    rough = _yin(x, sr, hop, F0_MIN, F0_MAX, 0.2)
    if (rough > 0).sum() < 5:
        return rough
    centre = np.median(rough[rough > 0])
    f0 = _yin(x, sr, hop, max(F0_MIN, centre / 2.2), min(F0_MAX * 1.5, centre * 2.2), 0.25)
    voiced = f0 > 0
    for i in np.flatnonzero(voiced):
        lo, hi = max(0, i - 12), min(len(f0), i + 13)
        nb = f0[lo:hi][voiced[lo:hi]]
        if len(nb) < 4:
            f0[i] = 0  # isolated blip
            continue
        jump = np.log2(f0[i] / np.median(nb))
        if abs(jump) > 0.7:
            f0[i] *= 2.0 ** -np.round(jump)
    return f0


def _yin(x, sr, hop, fmin, fmax, threshold) -> np.ndarray:
    tau_min, tau_max = int(sr / fmax), int(sr / fmin)
    w = tau_max + 80  # integration window, a bit over the longest period
    fr = _frames(x.astype(np.float64), w + tau_max, hop)
    n_fft = 1 << int(np.ceil(np.log2(w + tau_max + w)))
    head = np.zeros_like(fr)
    head[:, :w] = fr[:, :w]
    r = np.fft.irfft(np.conj(np.fft.rfft(head, n_fft)) * np.fft.rfft(fr, n_fft), n_fft)[:, :tau_max + 1]
    sq = np.concatenate([np.zeros((len(fr), 1)), np.cumsum(fr ** 2, axis=1)], axis=1)
    taus = np.arange(tau_max + 1)
    e0 = sq[:, w][:, None]
    et = sq[:, taus + w] - sq[:, taus]
    d = e0 + et - 2 * r
    d[:, 0] = 0
    cmnd = np.ones_like(d)
    cs = np.cumsum(d[:, 1:], axis=1)
    cmnd[:, 1:] = d[:, 1:] * taus[1:] / np.maximum(cs, 1e-12)

    rms = np.sqrt(e0[:, 0] / w)
    gate = rms > rms.max() * 10 ** (-40 / 20)
    f0 = np.zeros(len(fr))
    for i in np.flatnonzero(gate):
        c = cmnd[i]
        below = np.flatnonzero(c[tau_min:tau_max] < threshold)
        if len(below):
            t = tau_min + below[0]
            while t + 1 < tau_max and c[t + 1] < c[t]:
                t += 1
        else:
            t = tau_min + int(np.argmin(c[tau_min:tau_max]))
            if c[t] > 0.35:
                continue
        if 0 < t < tau_max:  # parabolic refinement
            a, b, cc = c[t - 1], c[t], c[t + 1]
            denom = a - 2 * b + cc
            t = t + (0.5 * (a - cc) / denom if denom else 0)
        f0[i] = sr / t
    return f0


def mfcc(x: np.ndarray, sr: int = SAMPLE_RATE, n_mels: int = 40, n_coef: int = 13) -> np.ndarray:
    """MFCCs (without c0) plus deltas, mean/variance normalised over the utterance."""
    n_fft, win = 1024, 600
    fr = _frames(x.astype(np.float64), win, FEAT_HOP) * np.hanning(win)
    power = np.abs(np.fft.rfft(fr, n_fft)) ** 2
    mel = lambda f: 2595 * np.log10(1 + f / 700)
    imel = lambda m: 700 * (10 ** (m / 2595) - 1)
    edges = imel(np.linspace(mel(80), mel(7600), n_mels + 2))
    bins = np.fft.rfftfreq(n_fft, 1 / sr)
    fb = np.zeros((n_mels, len(bins)))
    for m in range(n_mels):
        l, c, r = edges[m:m + 3]
        fb[m] = np.clip(np.minimum((bins - l) / (c - l), (r - bins) / (r - c)), 0, None)
    logmel = np.log(power @ fb.T + 1e-8)
    k = np.arange(n_mels)
    dct = np.cos(np.pi / n_mels * (k[None, :] + 0.5) * np.arange(1, n_coef + 1)[:, None])
    c = logmel @ dct.T
    delta = np.gradient(c, axis=0)
    f = np.concatenate([c, delta], axis=1)
    return (f - f.mean(0)) / (f.std(0) + 1e-8)


def _speech_span(x: np.ndarray, hop: int = FEAT_HOP, floor_db: float = -35) -> tuple[int, int]:
    """First and last analysis frame louder than floor_db below the peak."""
    rms = np.sqrt((_frames(x, hop * 2, hop) ** 2).mean(axis=1))
    loud = np.flatnonzero(rms > rms.max() * 10 ** (floor_db / 20))
    return (int(loud[0]), int(loud[-1]) + 1) if len(loud) else (0, len(rms))


def dtw(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    """Monotonic alignment of feature sequences a (N) and b (M). Returns, for each frame of a,
    the (fractional) matching frame of b."""
    n, m = len(a), len(b)
    cost = np.sqrt(np.maximum((a ** 2).sum(1)[:, None] + (b ** 2).sum(1)[None, :] - 2 * a @ b.T, 0))
    acc = np.full((n, m), np.inf)
    acc[0] = np.cumsum(cost[0])
    for i in range(1, n):
        # acc[i, j] = cost + min(acc[i-1, j], acc[i-1, j-1], acc[i, j-1]), the last term solved
        # for the whole row at once as a running minimum.
        up = acc[i - 1]
        diag = np.concatenate([[np.inf], acc[i - 1, :-1]])
        best = np.minimum(up, diag)
        cs = np.cumsum(cost[i])
        prev = np.concatenate([[0.0], cs[:-1]])
        acc[i] = cs + np.minimum.accumulate(best - prev)
    i, j = n - 1, m - 1
    path = [(i, j)]
    while i > 0 or j > 0:
        if i == 0:
            j -= 1
        elif j == 0:
            i -= 1
        else:
            k = int(np.argmin([acc[i - 1, j - 1], acc[i - 1, j], acc[i, j - 1]]))
            i, j = (i - 1, j - 1) if k == 0 else (i - 1, j) if k == 1 else (i, j - 1)
        path.append((i, j))
    path = np.array(path[::-1])
    out = np.zeros(n)
    for i in range(n):
        out[i] = path[path[:, 0] == i, 1].mean()
    return np.maximum.accumulate(out)


# ---------------------------------------------------------------------------- the transfer

MAX_STRETCH = 3.0  # how far a speech sound may be lengthened or shortened
MAX_STRETCH_SILENT = 20.0  # spaces and punctuation, which carry the pauses


@dataclass
class TransferPlan:
    """What the second Kokoro pass needs: a target duration per input id, and anchor points
    tying Kokoro's id boundaries to reference times, for warping the reference pitch."""
    target: np.ndarray  # frames per input id
    silent: np.ndarray  # True for ids that are pauses, spaces or punctuation
    timing: float
    anchor_ids: np.ndarray  # id boundaries (indices into cum)
    anchor_ref_s: np.ndarray  # their times in the reference, seconds
    ref_logf: np.ndarray  # reference log-F0 relative to its median, gaps filled, per F0 frame
    pitch: float
    keep_ids: list[tuple[int, int]] = field(default_factory=list)  # id ranges keeping Kokoro's pitch
    warning: str | None = None  # e.g. the recording does not seem to be of this text

    def durations(self, predicted):
        """Moves Kokoro's predicted (unrounded) durations, a torch tensor of shape (1, ids),
        towards the targets by the timing strength."""
        import torch
        target = torch.from_numpy(self.target).to(predicted)[None]
        limit = torch.from_numpy(np.where(self.silent, MAX_STRETCH_SILENT, MAX_STRETCH)).to(predicted)[None]
        ratio = (target / predicted.clamp(min=1e-3)).clamp(min=1 / limit).minimum(limit)
        return predicted * ratio ** self.timing

    def baseline_f0(self, f0_pred: np.ndarray, pred_dur: np.ndarray, smooth) -> tuple[np.ndarray, np.ndarray]:
        """Kokoro's predicted F0 (on the new timing) blended towards the reference contour.
        Returns (new baseline, reference contour in Hz with 0 where Kokoro is unvoiced)."""
        n = len(f0_pred)
        cum = np.concatenate([[0], np.cumsum(pred_dur)]) * 2  # id boundary -> F0 frame
        frames = np.arange(n)
        ref_frame = np.interp(frames, cum[self.anchor_ids], self.anchor_ref_s * SAMPLE_RATE / F0_HOP)
        ref = np.interp(ref_frame, np.arange(len(self.ref_logf)), self.ref_logf)

        voiced_w = np.clip((f0_pred - 30) / 30, 0, 1)
        voiced = voiced_w > 0.5
        logf = np.log(np.where(f0_pred > 1, f0_pred, 1.0))
        centre = np.median(logf[voiced]) if voiced.any() else 0.0
        target = centre + ref

        weight = np.full(n, self.pitch)
        for a, b in self.keep_ids:
            weight[int(cum[a]):int(cum[b])] = 0
        weight = np.clip(smooth(weight), 0, 1) * voiced_w
        new = np.where(f0_pred > 1, np.exp(logf + weight * (target - logf)), f0_pred).astype(np.float32)
        return new, np.where(voiced, np.exp(target), 0).astype(np.float32)


def plan(render: np.ndarray, pred_dur: np.ndarray, word_ids: list[tuple[int, int]],
         keep_ids: list[tuple[int, int]], silent: np.ndarray, ref: Reference) -> TransferPlan:
    """Aligns Kokoro's own render of the line (render, with per-id durations pred_dur) to the
    reference, and works out the durations and pitch contour to follow.

    word_ids: [a, b) input-id range of each spoken word, in order.
    keep_ids: ranges whose pitch should stay Kokoro's (pronunciation overrides).
    silent: per id, True for spaces, punctuation and padding."""
    # Align the speech parts only, so differing lead-in/out silence can't skew the warp.
    ka, kb = _speech_span(render)
    ra, rb = _speech_span(ref.audio)
    own_s, ref_s = (kb - ka) * FEAT_HOP / SAMPLE_RATE, (rb - ra) * FEAT_HOP / SAMPLE_RATE
    warning = None
    if not 0.5 <= ref_s / max(own_s, 1e-3) <= 2.0:
        warning = (f"The reference recording has {ref_s:.1f} s of speech but this text takes about {own_s:.1f} s "
                   "to say, so it may not be a reading of this text.")
    fk = mfcc(render)[ka:kb]
    fr = mfcc(ref.audio)[ra:rb]
    warp = dtw(fk, fr)  # kokoro frame (from ka) -> reference frame (from ra)

    def to_ref(t_s: float) -> float:
        f = t_s * SAMPLE_RATE / FEAT_HOP - ka
        if f <= 0:
            return (ra + f) * FEAT_HOP / SAMPLE_RATE  # keep lead-in length
        if f >= len(warp) - 1:
            return (ra + warp[-1] + (f - len(warp) + 1)) * FEAT_HOP / SAMPLE_RATE
        return (ra + np.interp(f, np.arange(len(warp)), warp)) * FEAT_HOP / SAMPLE_RATE

    cum = np.concatenate([[0], np.cumsum(pred_dur)])
    n_ids = len(pred_dur)
    anchors = sorted({0, n_ids, *(a for a, _ in word_ids), *(b for _, b in word_ids)})
    anchor_ref = np.array([to_ref(cum[i] * HOP / SAMPLE_RATE) for i in anchors])
    anchor_ref = np.maximum.accumulate(anchor_ref)

    # Target duration (in Kokoro frames) for every id: each anchor-to-anchor stretch gets the
    # reference's length, shared in Kokoro's own proportions.
    target = pred_dur.astype(np.float64).copy()
    for (p, q), (tp, tq) in zip(zip(anchors, anchors[1:]), zip(anchor_ref, anchor_ref[1:])):
        own = pred_dur[p:q].astype(np.float64)
        frames = max((tq - tp) * SAMPLE_RATE / HOP, 0.5 * (q - p))
        target[p:q] = own / own.sum() * frames

    # Reference pitch in log-Hz relative to its median, gaps bridged so every frame has a value.
    f0 = pitch(ref.audio)
    voiced = f0 > 0
    logf = np.zeros(len(f0))
    if voiced.sum() >= 2:
        lf = np.log(f0[voiced])
        logf = np.interp(np.arange(len(f0)), np.flatnonzero(voiced), lf - np.median(lf))
    return TransferPlan(target.astype(np.float32), np.asarray(silent, bool), ref.timing, np.array(anchors),
                        anchor_ref, logf, ref.pitch, keep_ids, warning)
