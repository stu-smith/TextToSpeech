# 10 British Names You're Saying Wrong

A YouTube showcase video for **TTS Studio** and **Promo Foundry**: a countdown of ten British
surnames and place names that sound nothing like they look. For each name, the narrator reads it
as spelled, then gives the local pronunciation. The video ends with a section on the tools used to
make it.

| # | Name | As spelled | As said |
|---|------|------------|---------|
| 10 | Leominster | lee-oh-MIN-ster | LEM-ster |
| 9 | Beauchamp | BOH-champ | BEE-chum |
| 8 | Mousehole | MOUSE-hole | MOW-zul |
| 7 | Menzies | MEN-zeez | MING-iss |
| 6 | Belvoir | bel-VWAH | BEE-ver |
| 5 | Marjoribanks | MAR-jor-ee-banks | MARCH-banks |
| 4 | Happisburgh | HAP-iz-burg | HAZE-bruh |
| 3 | Cholmondeley | CHOL-mon-duh-lee | CHUM-lee |
| 2 | Woolfardisworthy | wool-FAR-dis-wur-thee | WOOL-zer-ee |
| 1 | Featherstonehaugh | FEATHER-stone-haw | FAN-shaw |

## Files

| Path | Made with | What it is |
|------|-----------|------------|
| `narration.ttsproj` | TTS Studio | All narration (voice `bf_emma`). Each name appears twice in a sample, with different pronunciation regions: the as-spelled guess (with a rising "question" contour) and the real one. |
| `audio/` | TTS Studio | Narration rendered from the project. |
| `sound-effects.sfxproj` | SoundFX Foundry | Whoosh, impact, game-show buzzer, bell ding, riser and an 8-second music loop. |
| `sfx/` | SoundFX Foundry | Those effects exported as WAV. |
| `workflows/generate_images.py` | ComfyUI | Generates the scene art with Qwen Image 2.1 (`COMFYUI_URL` sets the server). |
| `images/` | ComfyUI | Scene art, plus a TTS Studio screenshot for the outro. |
| `build_video.py` | Python | Writes the Promo Foundry project from the narration timings. |
| `oddly-pronounced-names.promoproj` | Promo Foundry | The video: one 1920×1080, 30 fps movie composition, about 2:25 long. |
| `master_audio.py` | ffmpeg | Normalises the exported soundtrack to YouTube loudness (-14 LUFS). |

Rendered videos go to `output/`, which git ignores. The WAVs in `audio/` and `sfx/` are ignored
too: regenerate them before opening the Promo Foundry project, by exporting all samples from
`narration.ttsproj` and exporting each effect in `sound-effects.sfxproj` to `sfx/<effect name>.wav`.

## Rebuilding

1. Edit and render the narration in TTS Studio (`export_all` writes `audio/`). If any wording
   changes, copy the new word timings (when the as-spelled and real names start) and clip
   lengths into `ENTRIES` in `build_video.py`.
2. `python build_video.py` regenerates `oddly-pronounced-names.promoproj`.
3. Open the project in Promo Foundry and export the movie to `output/oddly-pronounced-names.mp4`.
4. `python master_audio.py` writes `output/oddly-pronounced-names-youtube.mp4`, the file to upload.
   The raw mix sits around -20 LUFS, and YouTube turns loud videos down but doesn't turn quiet
   ones up.
