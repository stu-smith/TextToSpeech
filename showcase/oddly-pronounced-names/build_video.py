"""Builds oddly-pronounced-names.promoproj, the Promo Foundry project for the video.

Timings come from the TTS Studio renders (narration.ttsproj -> audio/*.wav): for each entry,
the time the naive pronunciation starts and the time the real one starts. Re-render the
narration and update ENTRIES if the script changes.

Usage: python build_video.py
Then open the project in Promo Foundry (or via its MCP server) and export the movie.
"""
import json
import pathlib

from PIL import ImageFont

HERE = pathlib.Path(__file__).parent
W, H = 1920, 1080

GOLD, GOLD2 = "#FFE066", "#FF9F1C"
MAGENTA = "#FF2E7E"
CYAN = "#3EE6E0"
NAVY = "#070B1F"
RED = "#FF3B3B"

FONTS = {  # family -> file, for measuring text widths
    ("Impact", 400): "impact.ttf",
    ("Segoe UI", 700): "segoeuib.ttf",
    ("Bodoni MT", 900): "BOD_BLAR.TTF",
}

# name, image, chip, naive respelling, real respelling, wav length, naive start, real start
ENTRIES = [
    (10, "Leominster", "leominster", "MARKET TOWN  ·  HEREFORDSHIRE", "lee-oh-MIN-ster", "LEM-ster", 7.95, 1.225, 6.5),
    (9, "Beauchamp", "beauchamp", "SURNAME  ·  NORMAN ORIGIN", "BOH-champ", "BEE-chum", 7.8, 1.325, 6.425),
    (8, "Mousehole", "mousehole", "FISHING VILLAGE  ·  CORNWALL", "MOUSE-hole", "MOW-zul", 8.3, 1.2, 6.95),
    (7, "Menzies", "menzies", "SCOTTISH CLAN NAME", "MEN-zeez", "MING-iss", 9.98, 1.45, 8.625),
    (6, "Belvoir", "belvoir", "CASTLE & VALE  ·  LEICESTERSHIRE", "bel-VWAH", "BEE-ver", 9.57, 1.35, 8.075),
    (5, "Marjoribanks", "marjoribanks", "SURNAME  ·  SCOTTISH ARISTOCRACY", "MAR-jor-ee-banks", "MARCH-banks", 10.32, 1.4, 8.625),
    (4, "Happisburgh", "happisburgh", "VILLAGE  ·  NORFOLK COAST", "HAP-iz-burg", "HAZE-bruh", 9.43, 1.225, 7.9),
    (3, "Cholmondeley", "cholmondeley", "SURNAME  ·  CHESHIRE", "CHOL-mon-duh-lee", "CHUM-lee", 10.92, 1.225, 9.6),
    (2, "Woolfardisworthy", "woolfardisworthy", "TWO VILLAGES  ·  DEVON", "wool-FAR-dis-wur-thee", "WOOL-zer-ee", 10.05, 1.275, 8.6),
    (1, "Featherstonehaugh", "featherstonehaugh", "SURNAME  ·  17 LETTERS, 2 SYLLABLES", "FEATHER-stone-haw", "FAN-shaw", 8.8, 1.725, 7.225),
]
INTRO_WAV, OUTRO_WAV = 9.35, 17.8

layers = []  # bottom-most first while building; reversed on save (file is top-most first)
counter = 0


def measure(text, family, weight, size):
    font = ImageFont.truetype(f"C:/Windows/Fonts/{FONTS[(family, weight)]}", size)
    left, _, right, _ = font.getbbox(text)
    return right - left


def anim(start, end, entrance=None, exit=None, idle=None, keyframes=None):
    a = {"startTime": round(start, 3), "endTime": round(end, 3), "holdUntilEnd": False}
    if entrance:
        a["entrance"] = entrance
    if exit:
        a["exit"] = exit
    if idle:
        a["idle"] = idle
    if keyframes:
        a["keyframes"] = [dict({"offsetX": 0, "offsetY": 0, "scale": 1, "rotation": 0, "tiltX": 0, "tiltY": 0,
                                "opacity": 1, "easing": "easeInOut"}, **k) for k in keyframes]
    return a


def add(kind, name, **props):
    global counter
    counter += 1
    layer = {"type": kind, "id": f"{kind}{counter}", "name": name}
    layer.update(props)
    layers.append(layer)
    return layer


def audio(name, path, start, length, volume=1.0, **extra):
    return add("audio", name, sourcePath=path, volume=volume, animation=anim(start, start + length), **extra)


def text(name, value, x, y, start, end, family="Segoe UI", weight=700, size=48, color="#FFFFFF", **extra):
    props = dict(text=value, fontFamily=family, fontWeight=weight, fontSize=size, x=x, y=y,
                 fill={"kind": "solid", "color": color})
    props.update(extra)
    props.setdefault("animation", anim(start, end))
    return add("text", name, **props)


def shadow(distance=6, blur=14, opacity=0.75):
    return {"isEnabled": True, "color": "#000000", "opacity": opacity, "angle": 315, "distance": distance,
            "blurRadius": blur}


def fade_in(duration=0.4):
    return {"kind": "fade", "duration": duration, "easing": "easeOut"}


def fade_out(duration=0.35):
    return {"kind": "fade", "duration": duration, "easing": "easeIn"}


MIRRORED = {"featherstonehaugh"}  # scenes whose subject would otherwise sit under the text panel


def background(image, start, end, zoom_from=1.0, zoom_to=1.12, drift=-40):
    """Full-bleed scene with a slow Ken Burns push, plus a dark gradient on the left for the text."""
    length = end - start
    add("image", f"{image} background", sourcePath=f"images\\{image}.jpg", x=0, y=0, width=W, height=H,
        stretch="uniformToFill", scaleX=-1 if image in MIRRORED else 1,
        animation=anim(start, end, entrance=fade_in(0.5), keyframes=[
            {"time": 0, "scale": zoom_from, "easing": "linear"},
            {"time": length, "scale": zoom_to, "offsetX": drift, "easing": "linear"}]))
    add("shape", f"{image} shade", shape="rectangle", x=0, y=0, width=W, height=H,
        fill={"kind": "linearGradient", "color": "#F2070B1F", "color2": "#00070B1F", "angle": 0},
        animation=anim(start, end, entrance=fade_in(0.5)))
    add("shape", f"{image} floor", shape="rectangle", x=0, y=H - 260, width=W, height=260,
        fill={"kind": "linearGradient", "color": "#00070B1F", "color2": "#CC070B1F", "angle": 90},
        animation=anim(start, end, entrance=fade_in(0.5)))


def flash(at):
    add("shape", "flash", shape="rectangle", x=0, y=0, width=W, height=H, fill={"kind": "solid", "color": "#FFFFFF"},
        animation=anim(at, at + 0.35, keyframes=[{"time": 0, "opacity": 0.85, "easing": "easeOut"},
                                                 {"time": 0.35, "opacity": 0}]))


# ----------------------------------------------------------------------------------------------
# Intro
# ----------------------------------------------------------------------------------------------
INTRO_VOICE = 0.8
INTRO_END = INTRO_VOICE + INTRO_WAV + 0.45
SLAM = 2.0

background("intro", 0, INTRO_END, zoom_from=1.15, zoom_to=1.0, drift=0)
audio("riser", "sfx\\riser.wav", 0, 2.2, volume=0.7)
audio("intro narration", "audio\\00-intro.wav", INTRO_VOICE, INTRO_WAV)
flash(SLAM)
audio("title impact", "sfx\\impact.wav", SLAM, 1.3)

line1 = "10 BRITISH NAMES"
line2 = "YOU'RE SAYING WRONG"
w1 = measure(line1, "Impact", 400, 150)
w2 = measure(line2, "Impact", 400, 190)
text("title line 1", line1, (W - w1) / 2, 250, SLAM, INTRO_END, family="Impact", weight=400, size=150,
     shadow=shadow(8, 20, 0.8),
     animation=anim(SLAM, INTRO_END, entrance={"kind": "zoomOut", "duration": 0.45, "easing": "backOut"},
                    exit=fade_out()))
text("title line 2", line2, (W - w2) / 2, 420, SLAM + 0.15, INTRO_END, family="Impact", weight=400, size=190,
     rotation=-3,
     fill={"kind": "linearGradient", "color": GOLD, "color2": GOLD2, "angle": 90},
     outline={"isEnabled": True, "color": "#5A1A00", "thickness": 6, "position": "outside"},
     shadow=shadow(10, 24, 0.85), glow={"isEnabled": True, "color": "#FFB000", "opacity": 0.55, "size": 30},
     animation=anim(SLAM + 0.15, INTRO_END, entrance={"kind": "pop", "duration": 0.5, "easing": "elasticOut"},
                    idle={"kind": "pulse", "amplitude": 2.5, "period": 1.5}, exit=fade_out()))
sub = "Surnames and place names that sound NOTHING like they look"
ws = measure(sub, "Segoe UI", 700, 46)
sub_at = INTRO_VOICE + 3.65
add("shape", "subtitle pill", shape="rectangle", x=(W - ws) / 2 - 36, y=690, width=ws + 72, height=84,
    cornerRadius=42, fill={"kind": "solid", "color": MAGENTA}, shadow=shadow(4, 16, 0.6),
    animation=anim(sub_at, INTRO_END, entrance={"kind": "wipeRight", "duration": 0.45, "easing": "easeOut"},
                   exit=fade_out()))
text("subtitle", sub, (W - ws) / 2, 700, sub_at + 0.2, INTRO_END, size=46,
     animation=anim(sub_at + 0.2, INTRO_END, entrance=fade_in(0.3), exit=fade_out()))

# ----------------------------------------------------------------------------------------------
# Countdown entries
# ----------------------------------------------------------------------------------------------
LEAD, TAIL = 0.5, 1.3
t = INTRO_END
for number, name, image, chip, naive, real, wav, naive_at, real_at in ENTRIES:
    start = t
    voice = start + LEAD
    end = voice + wav + TAIL
    t_number = voice + 0.3
    t_name = voice + naive_at - 0.15
    t_strike = voice + naive_at + 1.0
    t_real = voice + real_at
    out = {"kind": "fade", "duration": 0.3, "easing": "easeIn"}

    background(image, start, end)
    flash(start)
    if number == 1:
        audio("riser", "sfx\\riser.wav", t_number - 2.0, 2.2, volume=0.7)
    audio("whoosh", "sfx\\whoosh.wav", start - 0.35, 1.2, volume=0.8)
    audio(f"{name} narration", f"audio\\{number:02d}-{image}.wav", voice, wav)
    audio("number impact", "sfx\\impact.wav", t_number, 1.3, volume=0.9)
    audio("wrong buzzer", "sfx\\wrong.wav", t_strike, 0.55, volume=0.45)
    audio("reveal chime", "sfx\\reveal.wav", t_real, 2.2, volume=0.7)

    # Number badge: a tilted magenta slab with a big gold number on it
    label = f"#{number}"
    nw = measure(label, "Impact", 400, 200)
    add("shape", "number slab", shape="rectangle", x=70, y=70, width=nw + 90, height=250, cornerRadius=24,
        rotation=-5, fill={"kind": "linearGradient", "color": MAGENTA, "color2": "#B5179E", "angle": 90},
        shadow=shadow(8, 22, 0.7),
        animation=anim(t_number - 0.05, end, entrance={"kind": "zoomOut", "duration": 0.35, "easing": "backOut"},
                       exit=out))
    text("number", label, 115, 78, t_number, end, family="Impact", weight=400, size=200, rotation=-5,
         fill={"kind": "linearGradient", "color": "#FFFFFF", "color2": GOLD, "angle": 90},
         shadow=shadow(6, 10, 0.6),
         animation=anim(t_number, end, entrance={"kind": "pop", "duration": 0.45, "easing": "elasticOut"},
                        exit=out))

    # The name as written
    size = 150 if len(name) <= 13 else 128
    text("name", name, 90, 360, t_name, end, family="Bodoni MT", weight=900, size=size,
         shadow=shadow(8, 18, 0.85),
         animation=anim(t_name, end, entrance={"kind": "slideRight", "duration": 0.5, "easing": "backOut",
                                               "fade": True, "distance": 160}, exit=out))
    cw = measure(chip, "Segoe UI", 700, 30)
    add("shape", "chip", shape="rectangle", x=96, y=548, width=cw + 48, height=54, cornerRadius=27,
        fill={"kind": "solid", "color": "#B3070B1F"},
        outline={"isEnabled": True, "color": CYAN, "thickness": 3, "position": "inside"},
        animation=anim(t_name + 0.35, end, entrance=fade_in(0.4), exit=out))
    text("chip text", chip, 120, 556, t_name + 0.35, end, size=30, color=CYAN,
         animation=anim(t_name + 0.35, end, entrance=fade_in(0.4), exit=out))

    # What you'd guess, struck through when the buzzer goes
    text("you'd say", "YOU'D SAY", 96, 650, t_name + 0.2, end, size=30, color="#C8CCE0",
         animation=anim(t_name + 0.2, end, entrance=fade_in(0.3), exit=out))
    gw = measure(naive, "Segoe UI", 700, 64)
    text("naive", naive, 92, 688, t_name + 0.2, end, size=64, shadow=shadow(4, 10, 0.7),
         animation=anim(t_name + 0.2, end, entrance={"kind": "wipeRight", "duration": 0.6, "easing": "easeOut"},
                        exit=out, keyframes=[{"time": 0, "opacity": 1},
                                             {"time": t_strike - t_name - 0.2, "opacity": 1},
                                             {"time": t_strike - t_name + 0.1, "opacity": 0.7}]))
    add("shape", "strike", shape="rectangle", x=84, y=742, width=gw + 22, height=12, cornerRadius=6,
        rotation=-2, fill={"kind": "solid", "color": RED}, shadow=shadow(2, 6, 0.5),
        animation=anim(t_strike, end, entrance={"kind": "wipeRight", "duration": 0.22, "easing": "easeOut"},
                       exit=out))

    # What the locals actually say
    text("locals say", "LOCALS SAY", 96, 800, t_real - 0.25, end, size=30, color=GOLD,
         animation=anim(t_real - 0.25, end, entrance=fade_in(0.25), exit=out))
    text("real", real, 88, 834, t_real, end, family="Impact", weight=400, size=150,
         fill={"kind": "linearGradient", "color": GOLD, "color2": GOLD2, "angle": 90},
         outline={"isEnabled": True, "color": "#4A1500", "thickness": 5, "position": "outside"},
         shadow=shadow(8, 18, 0.85), glow={"isEnabled": True, "color": "#FFB000", "opacity": 0.6, "size": 28},
         animation=anim(t_real, end, entrance={"kind": "pop", "duration": 0.55, "easing": "elasticOut"},
                        idle={"kind": "pulse", "amplitude": 2, "period": 1.2}, exit=out))
    t = end

# ----------------------------------------------------------------------------------------------
# Outro: the tools behind the video
# ----------------------------------------------------------------------------------------------
OUTRO = t
OUTRO_VOICE = OUTRO + 0.6
OUTRO_END = OUTRO_VOICE + OUTRO_WAV + 2.2
TTS_AT = OUTRO_VOICE + 4.6       # "...spoken by TTS Studio"
PF_AT = OUTRO_VOICE + 10.3       # "And the whole thing was put together in Promo Foundry"
THANKS_AT = OUTRO_VOICE + 16.1   # "Thanks for watching!"

background("outro", OUTRO, OUTRO_END, zoom_from=1.0, zoom_to=1.08, drift=0)
flash(OUTRO)
audio("whoosh", "sfx\\whoosh.wav", OUTRO - 0.35, 1.2, volume=0.8)
audio("outro narration", "audio\\99-outro.wav", OUTRO_VOICE, OUTRO_WAV)

q = "HOW MANY DID YOU GET RIGHT?"
qw = measure(q, "Impact", 400, 130)
text("question", q, (W - qw) / 2, 430, OUTRO + 0.3, TTS_AT - 0.2, family="Impact", weight=400, size=130,
     fill={"kind": "linearGradient", "color": GOLD, "color2": GOLD2, "angle": 90},
     shadow=shadow(8, 20, 0.85),
     animation=anim(OUTRO + 0.3, TTS_AT - 0.2, entrance={"kind": "pop", "duration": 0.5, "easing": "elasticOut"},
                    exit={"kind": "zoomIn", "duration": 0.3, "easing": "easeIn", "fade": True}))
audio("question impact", "sfx\\impact.wav", OUTRO + 0.3, 1.3, volume=0.6)


def tool_card(title, tagline, bullets, at, until, accent):
    """Title, tagline and bullets on the left; the visual is added separately on the right."""
    gone = {"kind": "slideLeft", "duration": 0.35, "easing": "easeIn", "fade": True, "distance": 120}
    text("made with", "MADE WITH", 110, 250, at, until, size=34, color="#C8CCE0",
         animation=anim(at, until, entrance=fade_in(0.3), exit=gone))
    text(f"{title} title", title, 104, 290, at + 0.1, until, family="Impact", weight=400, size=150,
         fill={"kind": "linearGradient", "color": "#FFFFFF", "color2": accent, "angle": 90},
         shadow=shadow(8, 20, 0.85), glow={"isEnabled": True, "color": accent, "opacity": 0.45, "size": 26},
         animation=anim(at + 0.1, until, entrance={"kind": "slideRight", "duration": 0.5, "easing": "backOut",
                                                   "fade": True, "distance": 140}, exit=gone))
    text(f"{title} tagline", tagline, 112, 480, at + 0.35, until, size=40, color=accent,
         animation=anim(at + 0.35, until, entrance=fade_in(0.4), exit=gone))
    for i, bullet in enumerate(bullets):
        appear = at + 0.7 + i * 0.45
        add("shape", "bullet dot", shape="ellipse", x=114, y=578 + i * 70, width=18, height=18,
            fill={"kind": "solid", "color": accent},
            animation=anim(appear, until, entrance={"kind": "pop", "duration": 0.3, "easing": "backOut"}, exit=gone))
        text("bullet", bullet, 150, 562 + i * 70, appear, until, size=36,
             animation=anim(appear, until, entrance={"kind": "slideRight", "duration": 0.35, "easing": "easeOut",
                                                     "fade": True, "distance": 60}, exit=gone))
    audio("card whoosh", "sfx\\whoosh.wav", at - 0.3, 1.2, volume=0.6)
    audio("card chime", "sfx\\reveal.wav", at + 0.1, 2.2, volume=0.45)


# TTS Studio: its own screenshot, tilted towards the viewer
tool_card("TTS Studio", "Text-to-speech you can direct, word by word",
          ["Every word of narration in this video", "Custom pronunciations for every name",
           "Pitch, pauses, emphasis and pace"], TTS_AT, PF_AT - 0.1, CYAN)
add("image", "TTS Studio screenshot", sourcePath="images\\tts-studio.png", x=1010, y=250, width=820, height=520,
    stretch="uniform", cornerRadius=14, tiltY=-14, shadow=shadow(16, 40, 0.8),
    glow={"isEnabled": True, "color": CYAN, "opacity": 0.35, "size": 30},
    animation=anim(TTS_AT + 0.2, PF_AT - 0.1,
                   entrance={"kind": "slideLeft", "duration": 0.6, "easing": "backOut", "fade": True, "distance": 300},
                   idle={"kind": "float", "amplitude": 8, "period": 3},
                   exit={"kind": "slideRight", "duration": 0.35, "easing": "easeIn", "fade": True, "distance": 200}))

# Promo Foundry: a tilted wall of this video's own scenes
tool_card("Promo Foundry", "Layered, animated promo images and videos",
          ["Every title, card and animation here", "Ken Burns pans, pops, wipes and glows",
           "Narration, music and effects mixed in"], PF_AT, THANKS_AT - 0.1, MAGENTA)
wall = ["leominster", "mousehole", "belvoir", "happisburgh", "menzies", "woolfardisworthy"]
for i, scene in enumerate(wall):
    col, row = i % 2, i // 2
    appear = PF_AT + 0.3 + i * 0.12
    add("image", f"wall {scene}", sourcePath=f"images\\{scene}.jpg", x=1060 + col * 400, y=200 + row * 235,
        width=380, height=214, stretch="uniformToFill", cornerRadius=12, tiltY=-16, tiltX=6,
        outline={"isEnabled": True, "color": "#FFFFFF", "thickness": 3, "position": "inside"},
        shadow=shadow(10, 26, 0.75),
        animation=anim(appear, THANKS_AT - 0.1,
                       entrance={"kind": "zoomOut", "duration": 0.45, "easing": "backOut", "fade": True},
                       idle={"kind": "float", "amplitude": 6, "period": 2.5 + i * 0.2},
                       exit={"kind": "fade", "duration": 0.3, "easing": "easeIn"}))

# Thanks + credits
thanks = "THANKS FOR WATCHING!"
tw = measure(thanks, "Impact", 400, 140)
text("thanks", thanks, (W - tw) / 2, 330, THANKS_AT, OUTRO_END, family="Impact", weight=400, size=140,
     fill={"kind": "linearGradient", "color": GOLD, "color2": GOLD2, "angle": 90},
     outline={"isEnabled": True, "color": "#4A1500", "thickness": 5, "position": "outside"},
     shadow=shadow(8, 20, 0.85), glow={"isEnabled": True, "color": "#FFB000", "opacity": 0.5, "size": 26},
     animation=anim(THANKS_AT, OUTRO_END, entrance={"kind": "pop", "duration": 0.5, "easing": "elasticOut"},
                    idle={"kind": "pulse", "amplitude": 2, "period": 1.4}))
audio("thanks impact", "sfx\\impact.wav", THANKS_AT, 1.3, volume=0.6)
credits = [
    ("Narration", "TTS Studio  ·  Kokoro-82M"),
    ("Video", "Promo Foundry"),
    ("Artwork", "ComfyUI  ·  Qwen Image 2.1"),
    ("Music & sound effects", "SoundFX Foundry"),
]
for i, (role, tool) in enumerate(credits):
    line = f"{role}:  {tool}"
    lw = measure(line, "Segoe UI", 700, 38)
    appear = THANKS_AT + 0.4 + i * 0.2
    text("credit", line, (W - lw) / 2, 540 + i * 62, appear, OUTRO_END, size=38, color="#E6E9F5",
         shadow=shadow(3, 8, 0.7), animation=anim(appear, OUTRO_END, entrance=fade_in(0.4)))

# ----------------------------------------------------------------------------------------------
# Whole-movie layers
# ----------------------------------------------------------------------------------------------
TOTAL = round(OUTRO_END, 2)
add("audio", "music", sourcePath="sfx\\music-loop.wav", volume=0.16, loop=True, fadeIn=1.0, fadeOut=2.5,
    animation=anim(0, TOTAL))
brand = "10 BRITISH NAMES YOU'RE SAYING WRONG"
bw = measure(brand, "Segoe UI", 700, 22)
add("shape", "brand pill", shape="rectangle", x=W - bw - 84, y=44, width=bw + 44, height=44, cornerRadius=22,
    fill={"kind": "solid", "color": "#99070B1F"},
    animation=anim(INTRO_END, OUTRO, entrance=fade_in(0.5), exit=fade_out()))
text("brand", brand, W - bw - 62, 52, INTRO_END, OUTRO, size=22, color="#E6E9F5",
     animation=anim(INTRO_END, OUTRO, entrance=fade_in(0.5), exit=fade_out()))

project = {
    "name": "Oddly Pronounced British Names",
    "formatVersion": 1,
    "compositions": [{
        "type": "movie", "duration": TOTAL, "frameRate": 30, "id": "oddlypronouncednames",
        "name": "Top Ten Oddly Pronounced Names", "width": W, "height": H, "background": NAVY,
        "layers": list(reversed(layers)),
    }],
}
(HERE / "oddly-pronounced-names.promoproj").write_text(json.dumps(project, indent=2), encoding="utf-8")
print(f"{len(layers)} layers, {TOTAL:.1f} s")
