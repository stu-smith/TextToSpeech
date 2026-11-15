import pytest

from ttsengine.markup import MarkupError, parse


def test_plain_text_passes_through():
    d = parse("Hello there.")
    assert d.g2p_text == d.visible == "Hello there."
    assert d.spans == [] and d.pauses == []


def test_pronunciation_link_kept_for_g2p_but_reduced_in_visible():
    d = parse("Ask [Siobhan](/ʃəvˈɔn/) now")
    assert d.g2p_text == "Ask [Siobhan](/ʃəvˈɔn/) now"
    assert d.visible == "Ask Siobhan now"


def test_span_offsets_and_controls():
    d = parse("You're leaving {tomorrow | c=rise:5 p=+1}?")
    assert d.visible == "You're leaving tomorrow?"
    (s,) = d.spans
    assert d.visible[s.start:s.end] == "tomorrow"
    assert s.controls.pitch == 1 and s.controls.contour.shape == "rise" and s.controls.contour.semitones == 5


def test_emphasis_shorthand():
    d = parse("I didn't say *she* did")
    (s,) = d.spans
    assert d.visible[s.start:s.end] == "she"
    assert s.controls.pitch > 0 and s.controls.rate < 1


def test_nested_span_with_link():
    d = parse("{the [Kokoro](/kˈOkəɹO/) *docs* | r=0.8}")
    assert d.visible == "the Kokoro docs"
    outer = next(s for s in d.spans if s.controls.rate == 0.8)
    inner = next(s for s in d.spans if s.controls.rate != 0.8)
    assert d.visible[outer.start:outer.end] == "the Kokoro docs"
    assert d.visible[inner.start:inner.end] == "docs"


def test_pause_units_and_whitespace_collapse():
    d = parse("Wait. {pause 400} Now. {pause 1.5s} Go")
    assert d.visible == "Wait. Now. Go"
    assert [p.seconds for p in d.pauses] == [0.4, 1.5]
    assert d.visible[: d.pauses[0].offset].rstrip() == "Wait."


def test_escapes():
    d = parse(r"5 \* 3 \{x\}")
    assert d.visible == "5 * 3 {x}" and d.spans == []


@pytest.mark.parametrize("bad", ["{oops}", "{a | p=x}", "{a | z=1}", "*open", "a } b", "{a | c=wobble}", "{a | r=0}"])
def test_errors(bad):
    with pytest.raises(MarkupError):
        parse(bad)
