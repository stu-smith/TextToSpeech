from ttsengine.document import Region, build


def test_plain_lines_collapse_whitespace_and_map_back():
    text = "Hello   there.\n\n  Second line "
    lines, warnings = build(text, [])
    assert [ln.doc.visible for ln in lines] == ["Hello there.", "Second line"]
    assert not warnings
    second = lines[1]
    assert second.orig_range((0, 6)) == (text.index("Second"), text.index("Second") + 6)


def test_overlapping_prosody_regions_become_overlapping_spans():
    text = "I didn't say she stole the money."
    a = Region(text.index("say"), text.index("stole") + 5, pitch=2)
    b = Region(text.index("she"), text.index("money") + 5, rate=0.8)
    (line,), _ = build(text, [a, b])
    spans = [(line.doc.visible[s.start:s.end], s.controls.pitch, s.controls.rate) for s in line.doc.spans]
    assert spans == [("say she stole", 2, 1.0), ("she stole the money", 0.0, 0.8)]


def test_pronunciation_snaps_to_whole_words_and_becomes_a_link():
    text = "Generate an IOSS return"
    r = Region(text.index("OS"), text.index("OS") + 2, pronunciation="/ˈIɑs/")
    (line,), _ = build(text, [r])
    assert line.doc.g2p_text == "Generate an [IOSS](/ˈIɑs/) return"
    assert line.doc.visible == "Generate an IOSS return"


def test_stress_link_and_overlapping_pronunciations():
    text = "record the record"
    first = Region(0, 6, stress=-1)
    clash = Region(3, 10, pronunciation="ɹɛkəɹd")
    (line,), warnings = build(text, [first, clash])
    assert line.doc.g2p_text == "[record](-1) the record"
    assert len(warnings) == 1


def test_pauses_and_brackets():
    text = "Wait [here] now"
    r = Region(text.index("here"), text.index("here") + 4, pause_before_ms=300, pause_after_ms=500)
    (line,), _ = build(text, [r])
    assert line.doc.visible == "Wait (here) now"
    assert [(p.offset, p.seconds) for p in line.doc.pauses] == [(6, 0.3), (10, 0.5)]


def test_regions_are_clipped_per_line():
    text = "one two\nthree four"
    r = Region(4, 13, volume=3)
    l1, l2 = build(text, [r])[0]
    assert [(s.start, s.end) for s in l1.doc.spans] == [(4, 7)]
    assert [(s.start, s.end) for s in l2.doc.spans] == [(0, 5)]
