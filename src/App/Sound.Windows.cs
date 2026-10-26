using System.Media;

namespace EliteStudio.App;

/// <summary>Plays the sounds Studio makes in memory (the sniffer's chime, Nav scan's ping and click) through Windows' own player.</summary>
internal static class Sound
{
    static SoundPlayer? playing;   // kept, so it is not cleared away while it still sounds
    static readonly Lock guard = new();

    /// <summary>Plays a WAV file held in memory and returns at once. Throws what Windows' player throws when it cannot.</summary>
    public static void Play(byte[] wav)
    {
        lock (guard)
        {
            SoundPlayer? before = playing;
            playing = new SoundPlayer(new MemoryStream(wav));
            playing.Play();
            before?.Dispose();
        }
    }
}
