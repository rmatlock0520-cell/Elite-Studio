using EliteStudio.App;

namespace EliteStudio.Nav;

/// <summary>
/// Nav scan's two sounds, made in memory as the market sniffer's chime is (no sound file ships with Studio), so that a
/// pilot with the game in front of them hears what Nav scan found without looking:
/// - <see cref="Click"/>: a quiet tick when a picture has been read;
/// - <see cref="Ping"/>: a sonar ping, one deep note with two soft echoes, when a system nobody has recorded turns up.
///   It is meant to sound nothing like the sniffer's two rising notes (an A and the E above it), so the two are never
///   taken for each other: one lower note, falling away, heard again twice, fainter each time.
/// </summary>
public static class NavScanSounds
{
    /// <summary>The sounds by the names the page and the settings use.</summary>
    public const string Click = "click", Ping = "ping";

    const int Rate = 44100;

    /// <summary>Plays one at a volume of 0 to 100 and returns at once; nothing at 0. A PC without a sound device stays silent.</summary>
    public static void Play(string sound, int volume)
    {
        if (volume <= 0) return;
        try
        {
            Sound.Play(Wave(sound, volume / 100.0).ToArray());
        }
        catch (Exception problem) when (problem is InvalidOperationException or IOException or TimeoutException)
        {
            Log.Error($"Nav scan's {sound} could not be played", problem);
        }
    }

    /// <summary>The sound as a WAV file in memory, 16-bit mono, at a loudness of 0 to 1.</summary>
    /// <exception cref="ArgumentException">Neither <see cref="Click"/> nor <see cref="Ping"/>.</exception>
    internal static MemoryStream Wave(string sound, double loudness) => sound switch
    {
        Click => Wav(ClickSamples(loudness)),
        Ping => Wav(PingSamples(loudness)),
        _ => throw new ArgumentException("Nav scan has a ping and a click.", nameof(sound)),
    };

    /// <summary>A tick: a few milliseconds of a high note that dies at once, at a third of the ping's loudness.</summary>
    static short[] ClickSamples(double loudness)
    {
        short[] samples = new short[(int)(Rate * 0.04)];
        for (int at = 0; at < samples.Length; at++)
        {
            double time = (double)at / Rate;
            double shape = Math.Min(1, time / 0.001) * Math.Exp(-time * 220);
            samples[at] = (short)(Math.Sin(2 * Math.PI * 2200 * time) * shape * loudness * 0.18 * short.MaxValue);
        }
        return samples;
    }

    /// <summary>
    /// The ping: an E (329.6 Hz, an octave and more under the sniffer's A of 880) that rings out, a little of the octave
    /// above for the sonar's edge, and the same again 0.45 and 0.9 seconds later at a third and a ninth, each heard
    /// clearly over the fading note before it.
    /// </summary>
    internal static short[] PingSamples(double loudness)
    {
        const double hz = 329.63, ring = 1.1;
        (double Start, double Gain)[] heard = [(0, 1), (0.45, 1 / 3.0), (0.9, 1 / 9.0)];
        short[] samples = new short[(int)(Rate * (0.9 + ring))];
        foreach ((double start, double gain) in heard)
            for (int at = 0; at < (int)(Rate * ring); at++)
            {
                double time = (double)at / Rate;
                // A quick rise so it does not click, then a fade.
                double shape = Math.Min(1, time / 0.01) * Math.Exp(-time * 5);
                double wave = Math.Sin(2 * Math.PI * hz * time) + 0.25 * Math.Sin(2 * Math.PI * 2 * hz * time) * Math.Exp(-time * 6);
                int index = (int)(Rate * start) + at;
                samples[index] = (short)Math.Clamp(samples[index] + wave * shape * gain * loudness * 0.5 * short.MaxValue, short.MinValue, short.MaxValue);
            }
        return samples;
    }

    static MemoryStream Wav(short[] samples)
    {
        var wave = new MemoryStream();
        using (var writer = new BinaryWriter(wave, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(Rate); writer.Write(Rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(samples.Length * 2);
            foreach (short sample in samples) writer.Write(sample);
        }
        wave.Position = 0;
        return wave;
    }
}
