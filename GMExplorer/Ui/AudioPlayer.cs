using System;
using System.IO;
using NAudio.Wave;

namespace GMExplorer.Ui;

public sealed class AudioPlayer : IDisposable
{
    WaveOutEvent? device;
    WaveStream? reader;
    IDisposable? extra;

    public event Action? StateChanged;

    public bool IsPlaying => device?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => device?.PlaybackState == PlaybackState.Paused;
    public bool HasTrack => reader != null;
    public string? Error { get; private set; }

    public TimeSpan Position
    {
        get => reader?.CurrentTime ?? TimeSpan.Zero;
        set { if (reader != null && value <= reader.TotalTime) reader.CurrentTime = value; }
    }

    public TimeSpan Duration => reader?.TotalTime ?? TimeSpan.Zero;

    public float Volume
    {
        get => device?.Volume ?? 1f;
        set { if (device != null) device.Volume = Math.Clamp(value, 0f, 1f); }
    }

    public bool Load(byte[] data, string container)
    {
        Stop();
        Error = null;
        try
        {
            var ms = new MemoryStream(data, false);
            extra = ms;
            reader = container switch
            {
                "WAV" => new WaveFileReader(ms),
                "OGG" => new NAudio.Vorbis.VorbisWaveReader(ms),
                "MP3" => new Mp3FileReader(ms),
                _ => Sniff(ms)
            };
            device = new WaveOutEvent { DesiredLatency = 150 };
            device.PlaybackStopped += (_, _) => StateChanged?.Invoke();
            device.Init(reader);
            return true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Cleanup();
            return false;
        }
    }

    static WaveStream Sniff(MemoryStream ms)
    {
        var head = new byte[4];
        ms.Position = 0;
        int n = ms.Read(head, 0, 4);
        ms.Position = 0;
        if (n == 4 && head[0] == 'O' && head[1] == 'g') return new NAudio.Vorbis.VorbisWaveReader(ms);
        if (n == 4 && head[0] == 'R' && head[1] == 'I') return new WaveFileReader(ms);
        return new Mp3FileReader(ms);
    }

    public void Play()
    {
        if (device == null) return;
        if (reader != null && reader.Position >= reader.Length) reader.Position = 0;
        device.Play();
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        device?.Pause();
        StateChanged?.Invoke();
    }

    public void Toggle()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Stop()
    {
        try { device?.Stop(); } catch { }
        Cleanup();
        StateChanged?.Invoke();
    }

    void Cleanup()
    {
        device?.Dispose();
        device = null;
        reader?.Dispose();
        reader = null;
        extra?.Dispose();
        extra = null;
    }

    public void Dispose() => Cleanup();
}