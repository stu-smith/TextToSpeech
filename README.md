# TTS Studio

A Windows desktop app for managing text-to-speech samples with precise control over how they
are spoken. Each sample is plain text plus **regions**: highlighted ranges that can override
pronunciation and shape inflection (pitch, glides, expressiveness, rate, pauses, loudness).
Regions can overlap freely, and their effects combine. Audio is generated on the **CPU** with
[Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M); no GPU is needed.

The app also includes an **MCP server**, so a coding agent can create and edit projects, look
up pronunciations and render audio.

## Features

- **Designer**: the text is laid out with each region tinted behind its words and drawn as a
  bar in its own lane, so overlapping regions stay distinct. Drag to select (snaps to whole
  words; hold Alt for character precision), right-click or press Ctrl+R to add a region, and
  click a bar or tinted word to select it. Clicking the same spot again cycles through
  overlapping regions. Drag the handles at the ends of a selected bar to resize it.
- **Properties panel**: pronunciation (with a dictionary lookup and a clickable phoneme
  palette), stress, pitch, rising/falling contour, expressiveness, rate, pauses before/after,
  volume and energy. One-click styles cover common cases: Emphasise, Rising, Falling, Slower,
  Softer, Flat and others.
- **Live render**: every change re-renders in the background, typically 5–7× faster than real
  time on a desktop CPU. Each word's phonemes are shown under it. Words the dictionary had to
  guess are underlined in red; these are usually the mispronunciations.
- **Pitch and timing view**: the model's predicted pitch (dashed) against the pitch after your
  edits (solid), with the waveform, word timings and the selected region's span. Click it to
  play from that point.
- **A/B**: F6 plays the sample with all prosody edits switched off, for comparison.
- **Projects** (`.ttsproj`, readable JSON): load, save, undo/redo, and a project-wide
  pronunciation lexicon. If another program (such as an agent) changes the file, the app
  reloads it automatically.
- **Export**: a single sample, or every sample into the project's export folder, as WAV, FLAC,
  Ogg Vorbis or MP3.
- **Markup import**: type annotated text such as `I didn't say *she* stole it.` and get a sample
  with regions (see the reference under the book icon).
- **Settings** (gear icon or Ctrl+,): light, dark or system theme, and links to the settings
  and speech runtime folders.

## Running

Requires Windows 10/11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download)
(or publish self-contained, as below).

```sh
dotnet run --project src/TtsStudio.App
```

**First run.** The speech engine's runtime is downloaded automatically into
`%LOCALAPPDATA%\TtsStudio\runtime`; nothing is installed system-wide.

1. [uv](https://github.com/astral-sh/uv) 0.13.8, a single exe pinned by version and SHA-256.
2. A portable CPython 3.12, downloaded by uv.
3. The engine's packages from the shipped `engine/uv.lock`, with CPU-only PyTorch.
4. The Kokoro model weights (~330 MB) from Hugging Face.

This is about 1.2 GB and takes a few minutes. Progress shows in the engine status pill; click
it for the log, which is also written to `runtime\engine.log`. Set `TTS_STUDIO_RUNTIME` to use a
different folder. Deleting the folder resets everything.

To build a distributable folder:

```sh
dotnet publish src/TtsStudio.App -c Release -r win-x64 --self-contained -o publish
```

`publish/` then contains `TtsStudio.exe`, `TtsStudio.Mcp.exe` and the `engine/` folder.

## Connecting a coding agent (MCP)

`TtsStudio.Mcp.exe` sits next to `TtsStudio.exe` and speaks MCP over stdio. The **MCP** button
in the app shows the exact command for your install. For Claude Code:

```sh
claude mcp add tts-studio -- "C:\path\to\TtsStudio.Mcp.exe"
```

Or in an `.mcp.json`:

```json
{ "mcpServers": { "tts-studio": { "command": "C:\\path\\to\\TtsStudio.Mcp.exe" } } }
```

Tools: `get_guide`, `list_voices`, `create_project`, `get_project`, `update_project_settings`,
`set_lexicon_entry`, `remove_lexicon_entry`, `get_sample`, `add_sample`, `update_sample`,
`delete_sample`, `add_region`, `update_region`, `delete_region`, `phonemize`, `render_sample`,
`export_all`.

- Samples can be referenced by id or name.
- Regions can be located by `match` text rather than offsets.
- `update_sample` with new text keeps each region attached to its words, even when the text
  changed in several places.
- `render_sample` reports each word's phonemes and flags guessed pronunciations, so an agent
  can check and fix them.

## How it works

```
text + regions ─▶ misaki G2P ─▶ phonemes ─▶ durations ─▶ pitch (F0) + energy ─▶ vocoder ─▶ audio
                  (pronunciations          (rate)       (pitch, contour, range,     (volume)
                   become [word](/IPA/))                 energy edits)
```

The engine (`engine/ttsengine`) comes from the earlier Kokoro workbench experiment. It
re-implements Kokoro's forward pass with hooks between the stages.

- Rate scales the predicted durations, so the pitch predictor adapts to the new timing.
- Pitch, contour and range edit the F0 curve, smoothed so edits don't click.
- Energy offsets the model's energy curve.
- Volume is applied to the waveform.

`document.py` compiles a sample's text and overlapping regions directly into the engine's
per-line documents. The app talks to the engine over JSON lines on stdio (`worker.py`).

| Path | Contents |
|---|---|
| `src/TtsStudio.App` | WPF app (designer control, pitch view, view model, theme) |
| `src/TtsStudio.Core` | Project model and file format, region-preserving text edits, markup import, runtime installer, engine client |
| `src/TtsStudio.Mcp` | MCP server |
| `engine/` | Python engine; `uv run pytest` runs its tests |
| `tests/` | .NET unit tests (`dotnet test`) |

## Licence

GPL-3.0-or-later (see [LICENSE](LICENSE)). Kokoro and misaki are Apache-2.0, but misaki falls
back to **espeak-ng (GPL-3.0)** for words that aren't in its dictionary. The engine loads it
in-process, so the project as a whole is GPL.
