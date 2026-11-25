using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Longhand.Services;

/// <summary>
/// The score as sound: a WAV file made here, so both apps play it the same way with what they already have (an audio element, a
/// media player) and nothing to install. Each note is a few harmonics that fade like a struck string, so it sounds like a soft
/// piano rather than a beep.
/// </summary>
public static class ScoreAudio
{
    public const int Rate = 22050;
    /// <summary>The longest it plays, so a very long score doesn't make a huge file.</summary>
    public const double MaxSeconds = 600;

    public static byte[] Wav(IReadOnlyList<PlayedNote> notes, int tempo)
    {
        double beat = 60.0 / Math.Clamp(tempo, 20, 300);
        double end = notes.Count == 0 ? 0 : notes.Max(n => n.Start + n.Length) * beat;
        int length = (int)(Math.Min(MaxSeconds, end + 0.8) * Rate);
        var mix = new float[length];
        foreach (var note in notes)
        {
            int start = (int)(note.Start * beat * Rate);
            if (start >= length) continue;
            double freq = 440 * Math.Pow(2, (note.Midi - 69) / 12.0);
            double held = note.Length * beat;
            int count = Math.Min(length - start, (int)((held + 0.35) * Rate));
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / Rate;
                // A quick attack, a decay while held, then a short release once the note ends.
                double env = Math.Min(1, t / 0.008) * Math.Exp(-2.2 * t);
                if (t > held) env *= Math.Exp(-(t - held) / 0.07);
                double w = 2 * Math.PI * freq * t;
                double v = Math.Sin(w) + 0.45 * Math.Sin(2 * w) * Math.Exp(-3 * t) + 0.2 * Math.Sin(3 * w) * Math.Exp(-5 * t) + 0.08 * Math.Sin(4 * w) * Math.Exp(-7 * t);
                mix[start + i] += (float)(0.18 * env * v);
            }
        }
        // Loud chords are squashed gently instead of clipping.
        using var ms = new MemoryStream(44 + length * 2);
        using var w2 = new BinaryWriter(ms);
        w2.Write("RIFF"u8);
        w2.Write(36 + length * 2);
        w2.Write("WAVE"u8);
        w2.Write("fmt "u8);
        w2.Write(16);
        w2.Write((short)1); // PCM
        w2.Write((short)1); // mono
        w2.Write(Rate);
        w2.Write(Rate * 2);
        w2.Write((short)2);
        w2.Write((short)16);
        w2.Write("data"u8);
        w2.Write(length * 2);
        foreach (var v in mix) w2.Write((short)Math.Round(Math.Tanh(v) * 32000));
        w2.Flush();
        return ms.ToArray();
    }
}
