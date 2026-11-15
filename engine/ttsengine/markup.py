"""Annotation markup for controlling pronunciation and prosody.

Syntax (all of it can be mixed and nested):

    [word](/IPA/)           Pronunciation override (misaki's native syntax, passed through).
    [word](+1) / [word](-1) Raise / lower lexical stress (misaki's native syntax, passed through).
    *word*                  Emphasis: shorthand for {word | p=+2.5 r=0.85 v=+1.5}.
    {some words | p=+3}     Prosody controls applied to the enclosed words.
    {pause 400}             Insert silence (ms by default; "0.4s" also accepted).
    \\* \\{ \\} \\|           Literal characters.

Prosody controls (space or comma separated):

    p, pitch    semitone shift                     p=+3, p=-2
    r, rate     speaking rate, >1 is faster        r=0.8
    c, contour  pitch glide across the span        c=rise, c=fall, c=rise:6
    g, range    pitch range (expressiveness)       g=0 (monotone), g=1.5 (animated)
    v, vol      volume change in dB                v=+3
    e, energy   EXPERIMENTAL: offset to the model's internal energy curve  e=+1
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

EMPHASIS = {"pitch": 2.5, "rate": 0.85, "vol": 1.5}
DEFAULT_CONTOUR_SEMITONES = 4.0

_LINK = re.compile(r"\[([^\]]+)\]\(([^\)]*)\)")
_PAUSE = re.compile(r"\{\s*pause\s+([0-9]*\.?[0-9]+)\s*(ms|s)?\s*\}", re.IGNORECASE)

_ALIASES = {
    "p": "pitch", "pitch": "pitch",
    "r": "rate", "rate": "rate",
    "c": "contour", "contour": "contour",
    "g": "range", "range": "range",
    "v": "vol", "vol": "vol", "volume": "vol",
    "e": "energy", "energy": "energy",
}


class MarkupError(ValueError):
    pass


@dataclass
class Contour:
    shape: str  # "rise" | "fall"
    semitones: float


@dataclass
class Controls:
    pitch: float = 0.0
    rate: float = 1.0
    contour: Contour | None = None
    range: float | None = None
    vol: float = 0.0
    energy: float = 0.0

    def describe(self) -> str:
        parts = []
        if self.pitch:
            parts.append(f"p={self.pitch:+g}")
        if self.rate != 1.0:
            parts.append(f"r={self.rate:g}")
        if self.contour:
            parts.append(f"c={self.contour.shape}:{self.contour.semitones:g}")
        if self.range is not None:
            parts.append(f"g={self.range:g}")
        if self.vol:
            parts.append(f"v={self.vol:+g}")
        if self.energy:
            parts.append(f"e={self.energy:+g}")
        return " ".join(parts)


@dataclass
class Span:
    start: int  # offsets into Document.visible
    end: int
    controls: Controls


@dataclass
class Pause:
    offset: int  # offset into Document.visible
    seconds: float


@dataclass
class Document:
    g2p_text: str  # text for misaki, pronunciation links preserved
    visible: str  # what misaki tokenizes once links are reduced to their labels
    spans: list[Span] = field(default_factory=list)
    pauses: list[Pause] = field(default_factory=list)


def parse_controls(spec: str) -> Controls:
    c = Controls()
    for item in re.split(r"[\s,]+", spec.strip()):
        if not item:
            continue
        if "=" not in item:
            raise MarkupError(f"Control '{item}' should look like key=value (e.g. p=+2)")
        key, value = item.split("=", 1)
        name = _ALIASES.get(key.strip().lower())
        if name is None:
            raise MarkupError(f"Unknown control '{key}'. Use p, r, c, g, v or e.")
        value = value.strip()
        try:
            if name == "contour":
                shape, _, amount = value.partition(":")
                shape = shape.lower()
                if shape not in ("rise", "fall"):
                    raise MarkupError(f"Contour must be rise or fall, got '{shape}'")
                c.contour = Contour(shape, float(amount) if amount else DEFAULT_CONTOUR_SEMITONES)
            elif name == "rate":
                c.rate = float(value)
                if c.rate <= 0:
                    raise MarkupError("Rate must be greater than 0")
            else:
                setattr(c, name, float(value))
        except ValueError as e:
            if isinstance(e, MarkupError):
                raise
            raise MarkupError(f"Bad value for {key}: '{value}'") from None
    return c


class _Parser:
    def __init__(self, text: str):
        self.text = text
        self.i = 0
        self.g2p: list[str] = []
        self.visible_len = 0
        self.visible: list[str] = []
        self.spans: list[Span] = []
        self.pauses: list[Pause] = []

    def emit(self, g2p: str, visible: str) -> None:
        # Collapse whitespace runs (e.g. left behind by a removed {pause}): spaCy turns extra
        # spaces into tokens, which shifts misaki's alignment of [word](/IPA/) overrides.
        if visible.isspace():
            if not self.visible or self.visible[-1][-1:].isspace():
                return
            g2p = visible = " "
        self.g2p.append(g2p)
        self.visible.append(visible)
        self.visible_len += len(visible)

    def error(self, msg: str) -> MarkupError:
        return MarkupError(f"{msg} (at character {self.i + 1})")

    def parse(self, stop: str = "", in_emphasis: bool = False) -> None:
        t = self.text
        while self.i < len(t):
            ch = t[self.i]
            if ch in stop:
                return
            if ch == "\\" and self.i + 1 < len(t):
                self.emit(t[self.i + 1], t[self.i + 1])
                self.i += 2
            elif ch == "[" and (m := _LINK.match(t, self.i)):
                self.emit(m.group(0), m.group(1))
                self.i = m.end()
            elif ch == "{" and (m := _PAUSE.match(t, self.i)):
                amount = float(m.group(1))
                seconds = amount if (m.group(2) or "ms").lower() == "s" else amount / 1000
                self.pauses.append(Pause(self.visible_len, seconds))
                self.i = m.end()
            elif ch == "{":
                self.parse_span()
            elif ch == "*":
                if in_emphasis:
                    return
                self.parse_emphasis()
            elif ch in "}|":
                raise self.error(f"Unexpected '{ch}' (use \\{ch} for a literal)")
            else:
                self.emit(ch, ch)
                self.i += 1

    def parse_span(self) -> None:
        open_at = self.i
        self.i += 1
        while self.i < len(self.text) and self.text[self.i].isspace():
            self.i += 1
        start = self.visible_len
        self.parse(stop="|}")
        # "{word | p=2}": the space before '|' is layout, not text.
        if self.visible_len > start and self.visible[-1] == " ":
            self.g2p.pop()
            self.visible.pop()
            self.visible_len -= 1
        if self.i >= len(self.text) or self.text[self.i] != "|":
            self.i = open_at
            raise self.error("Span '{' needs controls, e.g. {words | p=+2}")
        close = self.text.find("}", self.i)
        if close < 0:
            self.i = open_at
            raise self.error("Unclosed '{'")
        controls = parse_controls(self.text[self.i + 1:close])
        self.i = close + 1
        self.spans.append(Span(start, self.visible_len, controls))

    def parse_emphasis(self) -> None:
        open_at = self.i
        self.i += 1
        start = self.visible_len
        self.parse(stop="", in_emphasis=True)
        if self.i >= len(self.text):
            self.i = open_at
            raise self.error("Unclosed '*'")
        self.i += 1
        self.spans.append(Span(start, self.visible_len, Controls(**EMPHASIS)))


def parse(text: str) -> Document:
    p = _Parser(text.strip())
    p.parse()
    if p.visible and p.visible[-1] == " ":
        p.g2p.pop()
        p.visible.pop()
    return Document("".join(p.g2p), "".join(p.visible), p.spans, p.pauses)


def strip_prosody(text: str) -> str:
    """Same text with prosody markup removed but pronunciation links kept (for A/B baselines)."""
    return parse(text).g2p_text
