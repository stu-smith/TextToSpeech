"""Engine tests run the real model on CPU (a few seconds; downloads weights on first run)."""

import numpy as np
import pytest

from ttsengine.engine import F0_RATE, Engine
from ttsengine.prosody import Reference


@pytest.fixture(scope="module")
def engine():
    return Engine()


def _word(result, text):
    return next(t for t in result.tokens if t.text == text)


def _frames(t):
    return slice(int(t.start * F0_RATE), int(t.end * F0_RATE))


def test_pitch_shift_lands_on_the_marked_word(engine):
    r = engine.synthesize("The {quick | p=+4} brown fox jumps.")
    ratio = r.f0_final / r.f0_predicted
    voiced = r.f0_predicted > 60
    quick, fox = _word(r, "quick"), _word(r, "fox")
    q = ratio[_frames(quick)][voiced[_frames(quick)]]
    f = ratio[_frames(fox)][voiced[_frames(fox)]]
    assert np.median(q) == pytest.approx(2 ** (4 / 12), rel=0.05)
    assert np.median(f) == pytest.approx(1.0, abs=0.01)


def test_contour_reaches_full_height_on_voiced_frames(engine):
    r = engine.synthesize("You're leaving {tomorrow | c=rise:6}?")
    voiced = np.flatnonzero(r.f0_predicted > 60)
    last = voiced[voiced < int(_word(r, "tomorrow").end * F0_RATE)][-3:]
    semitones = 12 * np.log2(r.f0_final[last] / r.f0_predicted[last])
    assert semitones.mean() > 4.5


def test_rate_lengthens_only_the_span(engine):
    text = "Please speak {very slowly | r=0.5} now."
    base = engine.synthesize(text, prosody=False)
    slow = engine.synthesize(text)
    span = lambda r: _word(r, "slowly").end - _word(r, "very").start
    assert span(slow) > 1.7 * span(base)
    please = lambda r: _word(r, "Please").end - _word(r, "Please").start
    assert please(slow) == pytest.approx(please(base), abs=0.03)


def test_pause_inserts_silence(engine):
    base = engine.synthesize("One. Two.")
    paused = engine.synthesize("One. {pause 700} Two.")
    assert paused.duration - base.duration == pytest.approx(0.7, abs=0.05)
    gap = _word(paused, "Two").start - _word(paused, "One").end
    assert gap > 0.7


def test_pause_is_spliced_in_without_a_hard_cut(engine):
    r = engine.synthesize("This village has nothing to do with mice... {pause 150} or holes.", voice="bf_emma")
    silent = np.flatnonzero(r.audio == 0)
    start = silent[np.searchsorted(silent, int(_word(r, "mice").start * 24000))]
    end = start + int(0.15 * 24000)
    assert np.all(r.audio[start:end] == 0)
    # The waveform fades to and from the silence instead of stopping and starting mid-sound.
    assert np.abs(r.audio[start - 3:start]).max() < 0.01
    assert np.abs(r.audio[end:end + 3]).max() < 0.01


def test_reference_recording_sets_the_timing(engine):
    text = "Here are ten surnames that sound nothing like they look."
    # A stand-in "recording": another voice with a very different rhythm.
    ref = engine.synthesize("Here are {ten | r=0.5} surnames {pause 400} that sound {nothing like | r=0.6} they look.",
                            voice="am_adam")
    plain = engine.synthesize(text, voice="bf_emma")
    led = engine.synthesize(text, voice="bf_emma", reference=Reference(ref.audio))
    starts = lambda r: np.array([t.start for t in r.tokens if t.start is not None and t.source != "punct"])
    assert np.abs(starts(led) - starts(ref)).mean() < 0.08
    assert np.abs(starts(plain) - starts(ref)).mean() > 0.25
    assert led.f0_reference is not None and plain.f0_reference is None


def test_pronunciation_override_and_lexicon(engine):
    r = engine.synthesize("[Nguyen](/wˈɪn/) met Siobhan.", lexicon={"siobhan": "ʃəvˈɔn"})
    assert _word(r, "Nguyen").phonemes == "wˈɪn" and _word(r, "Nguyen").source == "markup"
    assert _word(r, "Siobhan").phonemes == "ʃəvˈɔn" and _word(r, "Siobhan").source == "lexicon"
    assert "wˈɪn" in r.phonemes[0] and "ʃəvˈɔn" in r.phonemes[0]


def test_long_text_is_chunked(engine):
    text = " ".join(["This is sentence number %d, which keeps going for a while." % i for i in range(20)])
    r = engine.synthesize(text)
    assert len(r.phonemes) > 1 and all(len(ps) <= 510 for ps in r.phonemes)
    starts = [t.start for t in r.tokens if t.start is not None]
    assert starts == sorted(starts)
