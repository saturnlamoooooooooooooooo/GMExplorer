using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GMExplorer.Gm;

public sealed class FmodDecoder : IDisposable
{
    const uint FMOD_LOOP_OFF = 0x00000001;
    const uint FMOD_2D = 0x00000008;
    const uint FMOD_OPENONLY = 0x00002000;
    const uint FMOD_IGNORETAGS = 0x02000000;
    const uint FMOD_TIMEUNIT_PCMBYTES = 0x00000004;
    const int FMOD_OUTPUTTYPE_NOSOUND = 2;

    delegate int SystemCreate2(out IntPtr system, uint headerVersion);
    delegate int SystemCreate1(out IntPtr system);
    delegate int SystemSetOutput(IntPtr system, int output);
    delegate int SystemInit(IntPtr system, int maxChannels, uint flags, IntPtr extra);
    delegate int SystemCreateSound(IntPtr system, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode, IntPtr exInfo, out IntPtr sound);
    delegate int SystemCloseOrRelease(IntPtr system);
    delegate int SoundGetNumSubSounds(IntPtr sound, out int num);
    delegate int SoundGetSubSound(IntPtr sound, int index, out IntPtr sub);
    delegate int SoundGetFormat(IntPtr sound, out int type, out int format, out int channels, out int bits);
    delegate int SoundGetDefaults(IntPtr sound, out float frequency, out int priority);
    delegate int SoundGetLength(IntPtr sound, out uint length, uint timeUnit);
    delegate int SoundGetName(IntPtr sound, IntPtr name, int len);
    delegate int SoundReadData(IntPtr sound, IntPtr buffer, uint length, out uint read);
    delegate int SoundSeekData(IntPtr sound, uint pcm);
    delegate int SoundRelease(IntPtr sound);

    IntPtr lib, system;
    SystemCreateSound? createSound;
    SystemCloseOrRelease? close, release;
    SoundGetNumSubSounds? getNumSubSounds;
    SoundGetSubSound? getSubSound;
    SoundGetFormat? getFormat;
    SoundGetDefaults? getDefaults;
    SoundGetLength? getLength;
    SoundGetName? getName;
    SoundReadData? readData;
    SoundSeekData? seekData;
    SoundRelease? soundRelease;

    public string LibraryPath { get; private set; } = "";
    public string? Error { get; private set; }
    public bool Ready => system != IntPtr.Zero && createSound != null;

    public static string? FindLibrary(string gameRoot)
    {
        try
        {
            var all = Directory.EnumerateFiles(gameRoot, "fmod*.dll", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).Contains("studio", StringComparison.OrdinalIgnoreCase)).ToList();
            return all.FirstOrDefault(f => Path.GetFileName(f).Equals("fmod.dll", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(f => Path.GetFileName(f).Equals("fmodL.dll", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        }
        catch { return null; }
    }

    public static FmodDecoder? Create(string gameRoot)
    {
        string? path = FindLibrary(gameRoot);
        if (path == null) return null;
        var d = new FmodDecoder();
        d.Open(path);
        return d;
    }

    T? Bind<T>(string name) where T : Delegate
    {
        try
        {
            return NativeLibrary.TryGetExport(lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;
        }
        catch { return null; }
    }

    void Open(string path)
    {
        LibraryPath = path;
        try
        {
            lib = NativeLibrary.Load(path);
        }
        catch (Exception e)
        {
            Error = "could not load " + Path.GetFileName(path) + ": " + e.Message;
            return;
        }

        var create2 = Bind<SystemCreate2>("FMOD_System_Create");
        var setOutput = Bind<SystemSetOutput>("FMOD_System_SetOutput");
        var init = Bind<SystemInit>("FMOD_System_Init");
        createSound = Bind<SystemCreateSound>("FMOD_System_CreateSound");
        close = Bind<SystemCloseOrRelease>("FMOD_System_Close");
        release = Bind<SystemCloseOrRelease>("FMOD_System_Release");
        getNumSubSounds = Bind<SoundGetNumSubSounds>("FMOD_Sound_GetNumSubSounds");
        getSubSound = Bind<SoundGetSubSound>("FMOD_Sound_GetSubSound");
        getFormat = Bind<SoundGetFormat>("FMOD_Sound_GetFormat");
        getDefaults = Bind<SoundGetDefaults>("FMOD_Sound_GetDefaults");
        getLength = Bind<SoundGetLength>("FMOD_Sound_GetLength");
        getName = Bind<SoundGetName>("FMOD_Sound_GetName");
        readData = Bind<SoundReadData>("FMOD_Sound_ReadData");
        seekData = Bind<SoundSeekData>("FMOD_Sound_SeekData");
        soundRelease = Bind<SoundRelease>("FMOD_Sound_Release");

        if (create2 == null || init == null || createSound == null)
        {
            Error = Path.GetFileName(path) + " does not export the FMOD core API.";
            return;
        }

        int r = -1;
        foreach (uint version in new uint[] { 0x00020000, 0x00010000, 0x00020200, 0x00020100 })
        {
            r = create2(out system, version);
            if (r == 0) break;
        }
        if (r != 0)
        {
            var create1 = Bind<SystemCreate1>("FMOD_System_Create");
            if (create1 != null) r = create1(out system);
        }
        if (r != 0 || system == IntPtr.Zero)
        {
            Error = "FMOD_System_Create failed (" + ErrorName(r) + ")";
            system = IntPtr.Zero;
            return;
        }

        setOutput?.Invoke(system, FMOD_OUTPUTTYPE_NOSOUND);
        r = init(system, 64, 0, IntPtr.Zero);
        if (r != 0)
        {
            Error = "FMOD_System_Init failed (" + ErrorName(r) + ")";
            release?.Invoke(system);
            system = IntPtr.Zero;
        }
    }

    static string ErrorName(int code) => code switch
    {
        0 => "ok",
        12 => "file format",
        14 => "file not found",
        20 => "header mismatch",
        30 => "invalid parameter",
        41 => "memory",
        51 => "output init", _ => "error " + code
    };

    public FmodSound? OpenFsb(string fsbPath)
    {
        if (!Ready) return null;
        int r = createSound!(system, fsbPath, FMOD_OPENONLY | FMOD_2D | FMOD_LOOP_OFF | FMOD_IGNORETAGS, IntPtr.Zero, out var sound);
        if (r != 0 || sound == IntPtr.Zero)
        {
            Error = "FMOD could not open the sample bank (" + ErrorName(r) + ")";
            return null;
        }
        int count = 0;
        getNumSubSounds?.Invoke(sound, out count);
        return new FmodSound(this, sound, count);
    }

    internal byte[]? ReadSubSound(IntPtr parent, int index, out int channels, out int rate)
    {
        channels = 0;
        rate = 0;
        if (getSubSound == null || readData == null) return null;

        IntPtr sub = parent;
        bool ownsSub = false;
        if (index >= 0)
        {
            if (getSubSound(parent, index, out sub) != 0 || sub == IntPtr.Zero) return null;
            ownsSub = false;
        }

        try
        {
            int format = 2;
            if (getFormat != null) getFormat(sub, out _, out format, out channels, out _);
            float freq = 44100;
            getDefaults?.Invoke(sub, out freq, out _);
            rate = (int)freq;
            uint pcmBytes = 0;
            if (getLength == null || getLength(sub, out pcmBytes, FMOD_TIMEUNIT_PCMBYTES) != 0 || pcmBytes == 0) return null;
            if (pcmBytes > 400_000_000) return null;

            var pcm = new byte[pcmBytes];
            var handle = GCHandle.Alloc(pcm, GCHandleType.Pinned);
            try
            {
                seekData?.Invoke(sub, 0);
                uint total = 0;
                while (total < pcmBytes)
                {
                    int rr = readData(sub, handle.AddrOfPinnedObject() + (int)total, pcmBytes - total, out uint got);
                    if (got == 0) break;
                    total += got;
                    if (rr != 0) break;
                }
                if (total == 0) return null;
                if (total < pcmBytes) Array.Resize(ref pcm, (int)total);
            }
            finally { handle.Free(); }

            int bitsPerSample = format switch { 1 => 8, 2 => 16, 3 => 24, 4 => 32, 5 => 32, _ => 16 };
            bool isFloat = format == 5;
            return Wav.Wrap(pcm, channels <= 0 ? 1 : channels, rate <= 0 ? 44100 : rate, bitsPerSample, isFloat);
        }
        finally
        {
            if (ownsSub) soundRelease?.Invoke(sub);
        }
    }

    internal void ReleaseSound(IntPtr sound) => soundRelease?.Invoke(sound);

    public void Dispose()
    {
        if (system != IntPtr.Zero)
        {
            close?.Invoke(system);
            release?.Invoke(system);
            system = IntPtr.Zero;
        }
        if (lib != IntPtr.Zero)
        {
            try { NativeLibrary.Free(lib); } catch { }
            lib = IntPtr.Zero;
        }
    }
}

public sealed class FmodSound : IDisposable
{
    readonly FmodDecoder owner;
    IntPtr sound;
    public int SubSoundCount { get; }

    internal FmodSound(FmodDecoder owner, IntPtr sound, int count)
    {
        this.owner = owner;
        this.sound = sound;
        SubSoundCount = count;
    }

    public byte[]? Wav(int index)
    {
        if (sound == IntPtr.Zero) return null;
        return owner.ReadSubSound(sound, SubSoundCount > 0 ? index : -1, out _, out _);
    }

    public void Dispose()
    {
        if (sound != IntPtr.Zero)
        {
            owner.ReleaseSound(sound);
            sound = IntPtr.Zero;
        }
    }
}

public static class Wav
{
    public static byte[] Wrap(byte[] pcm, int channels, int rate, int bitsPerSample, bool isFloat)
    {
        int blockAlign = channels * bitsPerSample / 8;
        int byteRate = rate * blockAlign;
        var ms = new MemoryStream(pcm.Length + 44);
        var w = new BinaryWriter(ms, Encoding.ASCII, true);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)(isFloat ? 3 : 1));
        w.Write((short)channels);
        w.Write(rate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Flush();
        ms.Write(pcm, 0, pcm.Length);
        return ms.ToArray();
    }
}