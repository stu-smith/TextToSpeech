namespace TtsStudio.Core;

/// <summary>Reference text for people and agents: phoneme key, region properties, workflow.</summary>
public static class Guide
{
    public const string PhonemeKey = """
        Kokoro uses misaki's phoneme set: mostly IPA, plus single letters for diphthongs.
          A = eɪ (day)   I = aɪ (bite)   O = oʊ (go, US)   Q = əʊ (go, UK)
          W = aʊ (now)   Y = ɔɪ (boy)    T = flap t (water, US)   ᵊ = weak schwa
        Stress marks go immediately before the stressed vowel: ˈ primary, ˌ secondary.
        Common symbols: æ (cat) ɑ (father) ɔ (thought) ə (about) ɛ (bed) ɜ (bird) ɪ (sit)
          i (see) ʊ (put) u (blue) ʌ (cup) ŋ (sing) θ (thin) ð (this) ʃ (she) ʒ (vision)
          ʧ (church) ʤ (judge) ɹ (red) ɡ (go) j (yes)
        Examples: Nguyen = wˈɪn, Loughborough = lˈʌfbəɹə, IOSS = ˈIɑs, wind (verb) = wˈInd
        Tip: phonemize a similar-sounding word to see how the dictionary spells it.
        """;

    public const string Properties = """
        Region properties (all optional; regions may overlap and prosody effects add up):
          pronunciation     phonemes for the covered word(s); applies to whole words
          stress            -2 remove, -1 lower, +1 raise, +2 force primary (ignored if pronunciation set)
          pitch             semitones, e.g. +2.5 for emphasis, -2 for a lower aside        (-12..12)
          contour           "rise" or "fall": a pitch glide across the region (questions: rise)
          contourSemitones  size of the glide, default 4                                    (0..12)
          pitchRange        expressiveness: 0 monotone, 1 natural, 2 very animated          (0..2)
          rate              speaking rate, >1 faster, e.g. 0.8 to slow a phrase down        (0.3..3)
          volume            dB, e.g. +1.5 for emphasis                                      (-20..12)
          energy            experimental push on the model's energy curve                   (-3..3)
          pauseBeforeMs / pauseAfterMs   silence inserted before / after the region
        Emphasis preset: pitch +2.5, rate 0.85, volume +1.5.
        Sample-wide settings: voice, speed, pitch (semitones), pitchRange.
        Lines are synthesised separately with a short gap; regions are clipped at line breaks.
        Human reading: a sample may have a referenceAudio recording of someone reading its text
        (single-line samples only). The speech then follows the recording's timing and melody,
        by referenceStrength (0 ignores it, 1 follows it fully), and regions apply on top.
        record_reading opens a recorder on the user's screen so they can record that reading.
        """;

    public static string Full => $"""
        TTS Studio projects (.ttsproj) hold text-to-speech samples. Each sample is plain text plus
        regions: character ranges [start, end) with pronunciation and prosody properties. Audio is
        rendered on CPU with the Kokoro-82M model.

        {Properties}

        {PhonemeKey}

        Markup (accepted by add_sample / update_sample as a quick way to create text + regions):
        {Markup.Reference}

        Workflow: add or edit samples, render_sample to check the result (its token list shows the
        phonemes used for each word; source "guessed" marks words not in the dictionary, which are
        the usual mispronunciations), fix with a pronunciation region or a project lexicon entry,
        then export_all.
        """;
}
