"""Builds engine Documents from plain text plus annotated regions.

The studio stores a sample as plain text and a list of regions (character ranges with
properties). Regions may overlap freely, which the brace markup in markup.py cannot express,
so this module builds the Documents directly:

  * prosody properties become Spans (the engine sums overlapping spans),
  * pronunciation / stress become misaki links in the G2P text ([word](/IPA/), [word](+1)),
  * pause before / after become Pauses.

Each text line becomes one Document. Whitespace runs collapse to a single space as in
markup.parse, and every Document keeps a map back to offsets in the original text so token
timings can be reported against the text the user edited.
"""

from __future__ import annotations

from bisect import bisect_left
from dataclasses import dataclass, field

from .markup import DEFAULT_CONTOUR_SEMITONES, Contour, Controls, Document, Pause, Span


@dataclass
class Region:
    start: int
    end: int
    pronunciation: str | None = None
    stress: float | None = None
    pitch: float | None = None
    rate: float | None = None
    contour: str | None = None  # "rise" | "fall"
    contour_semitones: float | None = None
    range: float | None = None
    volume: float | None = None
    energy: float | None = None
    pause_before_ms: float | None = None
    pause_after_ms: float | None = None

    @classmethod
    def from_json(cls, d: dict) -> "Region":
        return cls(
            start=int(d["start"]),
            end=int(d["end"]),
            pronunciation=(d.get("pronunciation") or None),
            stress=d.get("stress"),
            pitch=d.get("pitch"),
            rate=d.get("rate"),
            contour=(d.get("contour") or None),
            contour_semitones=d.get("contourSemitones"),
            range=d.get("pitchRange"),
            volume=d.get("volume"),
            energy=d.get("energy"),
            pause_before_ms=d.get("pauseBeforeMs"),
            pause_after_ms=d.get("pauseAfterMs"),
        )

    def controls(self) -> Controls | None:
        if all(v is None for v in (self.pitch, self.rate, self.contour, self.range, self.volume, self.energy)):
            return None
        contour = None
        if self.contour in ("rise", "fall"):
            contour = Contour(self.contour, self.contour_semitones if self.contour_semitones is not None
                              else DEFAULT_CONTOUR_SEMITONES)
        rate = self.rate if self.rate and self.rate > 0 else 1.0
        return Controls(pitch=self.pitch or 0.0, rate=rate, contour=contour, range=self.range,
                        vol=self.volume or 0.0, energy=self.energy or 0.0)


@dataclass
class Line:
    doc: Document
    orig: list[int] = field(default_factory=list)  # original text offset of each visible char

    def vis_offset(self, offset: int) -> int:
        """Visible offset for an original-text offset (number of visible chars before it)."""
        return bisect_left(self.orig, offset)

    def orig_range(self, vis: tuple[int, int]) -> tuple[int, int]:
        a, b = vis
        return self.orig[a], self.orig[b - 1] + 1


def _is_word(ch: str) -> bool:
    return ch.isalnum() or ch in "'’-"


def _link_feature(r: Region) -> str | None:
    if r.pronunciation:
        ps = r.pronunciation.strip().strip("/").replace(")", "").replace("(", "")
        return f"/{ps}/" if ps else None
    if r.stress is not None and r.stress != 0:
        s = float(r.stress)
        return f"{int(s):+d}" if s.is_integer() else f"{s:+g}"
    return None


def build(text: str, regions: list[Region]) -> tuple[list[Line], list[str]]:
    """Split text into lines and apply regions. Returns (lines, warnings)."""
    warnings: list[str] = []
    lines: list[Line] = []
    pos = 0
    for raw in text.split("\n"):
        ls, le = pos, pos + len(raw)
        pos = le + 1
        vis: list[str] = []
        orig: list[int] = []
        for i in range(ls, le):
            ch = text[i]
            if ch.isspace():
                if vis and vis[-1] != " ":
                    vis.append(" ")
                    orig.append(i)
                continue
            # misaki treats [..](..) as link syntax and has no escape, so neutralise brackets.
            vis.append({"[": "(", "]": ")"}.get(ch, ch))
            orig.append(i)
        if vis and vis[-1] == " ":
            vis.pop()
            orig.pop()
        if not vis:
            continue
        line = Line(Document("", "".join(vis)), orig)
        visible = line.doc.visible

        def clip(r: Region) -> tuple[int, int] | None:
            a, b = max(r.start, ls), min(r.end, le)
            if a >= b:
                return None
            va, vb = line.vis_offset(a), line.vis_offset(b)
            return (va, vb) if va < vb else None

        # Pronunciation and stress links: whole words only, no overlaps (first one wins).
        links: list[tuple[int, int, str]] = []
        for r in sorted(regions, key=lambda r: (r.start, -r.end)):
            feature = _link_feature(r)
            rng = clip(r) if feature else None
            if not rng:
                continue
            a, b = rng
            while a > 0 and _is_word(visible[a - 1]) and _is_word(visible[a]):
                a -= 1
            while b < len(visible) and _is_word(visible[b]) and _is_word(visible[b - 1]):
                b += 1
            while a < b and visible[a] == " ":
                a += 1
            while b > a and visible[b - 1] == " ":
                b -= 1
            if a >= b:
                continue
            if links and a < links[-1][1]:
                warnings.append(f"Pronunciation for '{visible[a:b]}' overlaps another pronunciation and was ignored")
                continue
            if r.end > le:
                warnings.append(f"Pronunciation for '{visible[a:b]}' crosses a line break; only this line's part is used")
            links.append((a, b, feature))

        g2p, cursor = [], 0
        for a, b, feature in links:
            g2p.append(visible[cursor:a])
            g2p.append(f"[{visible[a:b]}]({feature})")
            cursor = b
        g2p.append(visible[cursor:])
        line.doc.g2p_text = "".join(g2p)

        for r in regions:
            controls = r.controls()
            rng = clip(r) if controls else None
            if rng:
                line.doc.spans.append(Span(rng[0], rng[1], controls))
            if r.pause_before_ms and ls <= r.start <= le:
                line.doc.pauses.append(Pause(line.vis_offset(r.start), r.pause_before_ms / 1000))
            if r.pause_after_ms and ls <= r.end <= le:
                line.doc.pauses.append(Pause(line.vis_offset(r.end), r.pause_after_ms / 1000))
        line.doc.pauses.sort(key=lambda p: p.offset)
        lines.append(line)
    return lines, warnings
