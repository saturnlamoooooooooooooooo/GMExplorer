using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GMExplorer.Gm;

public sealed class SoundEntry
{
    public string Name = "";
    public string Source = "";
    public string Detail = "";

    public GmSound? Archive;
    public Bank? Bank;
    public BankSection? Section;
    public BankSample? Sample;
    public int SampleIndex;

    public bool FromBank => Bank != null;

    public string Extension => FromBank ? "wav" : Archive?.Blob?.Container.ToLowerInvariant() switch { "ogg" => "ogg", "mp3" => "mp3", "wav" => "wav", _ => "bin" };
}

public sealed class SoundLibrary : IDisposable
{
    public List<SoundEntry> Entries { get; } = new();
    public string? FmodNote;
    public int BankCount { get; private set; }
    public int BankSampleCount { get; private set; }

    GmData? data;
    string root = "";
    FmodDecoder? fmod;
    bool fmodTried;
    readonly Dictionary<string, FmodSound?> opened = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> tempFiles = new();

    public static SoundLibrary Build(GmData data, GamePackage? package, Action<string>? progress = null)
    {
        var lib = new SoundLibrary { data = data, root = package?.Root ?? Path.GetDirectoryName(data.Path) ?? "." };

        foreach (var s in data.Sounds)
            lib.Entries.Add(new SoundEntry
            {
                Name = s.Name,
                Source = s.Blob?.Origin ?? data.FileName,
                Archive = s,
                Detail = s.Blob == null ? "no embedded audio" : s.Blob.Container + "  " + Size(s.Blob.Length)
            });

        if (package != null)
        {
            foreach (var f in package.Files.Where(f => f.Kind == FileKind.Bank && !f.IsEmbedded))
            {
                progress?.Invoke("Reading " + f.Name + "...");
                var bank = Bank.Read(f.FullPath);
                if (bank.Sections.Count == 0) continue;
                lib.BankCount++;
                foreach (var section in bank.Sections)
                {
                    for (int i = 0; i < section.Samples.Count; i++)
                    {
                        var sample = section.Samples[i];
                        lib.Entries.Add(new SoundEntry
                        {
                            Name = sample.Name.Length > 0 ? sample.Name : Path.GetFileNameWithoutExtension(f.Name) + " " + (i + 1),
                            Source = f.Name,
                            Bank = bank,
                            Section = section,
                            Sample = sample,
                            SampleIndex = i,
                            Detail = section.CodecName + "  " + sample.Channels + "ch  " + sample.Frequency + "Hz  " + sample.Seconds.ToString("0.00") + "s"
                        });
                        lib.BankSampleCount++;
                    }
                }
            }
        }
        return lib;
    }

    static string Size(long n)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double v = n;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : "0.#") + " " + u[i];
    }

    FmodDecoder? Fmod()
    {
        if (fmodTried) return fmod;
        fmodTried = true;
        fmod = FmodDecoder.Create(root);
        if (fmod == null)
            FmodNote = "No fmod library was found next to the game, so bank samples cannot be decoded here.";
        else if (!fmod.Ready)
            FmodNote = fmod.Error ?? "The game's fmod library could not be started.";
        else
            FmodNote = "Decoding bank samples with " + Path.GetFileName(fmod.LibraryPath) + ".";
        return fmod;
    }

    public byte[]? Audio(SoundEntry e, out string container)
    {
        container = "?";
        if (!e.FromBank)
        {
            var blob = e.Archive?.Blob;
            if (blob == null) return null;
            container = blob.Container;
            var src = blob.Source ?? data!.Raw;
            int len = Math.Max(0, Math.Min(blob.Length, src.Length - blob.Offset));
            var bytes = new byte[len];
            Buffer.BlockCopy(src, blob.Offset, bytes, 0, len);
            return bytes;
        }

        var decoder = Fmod();
        if (decoder == null || !decoder.Ready) return null;

        var sound = OpenSection(e, decoder);
        if (sound == null) return null;
        var wav = sound.Wav(e.SampleIndex);
        if (wav == null) return null;
        container = "WAV";
        return wav;
    }

    FmodSound? OpenSection(SoundEntry e, FmodDecoder decoder)
    {
        string key = e.Bank!.Path + "#" + e.Section!.Offset;
        if (opened.TryGetValue(key, out var cached)) return cached;

        FmodSound? sound = null;
        try
        {
            string fsb = ExtractFsb(e.Bank, e.Section);
            sound = decoder.OpenFsb(fsb);
            if (sound == null) FmodNote = decoder.Error ?? FmodNote;
        }
        catch (Exception ex) { FmodNote = "Could not extract the sample bank: " + ex.Message; }

        opened[key] = sound;
        return sound;
    }

    string ExtractFsb(Bank bank, BankSection section)
    {
        string dir = Path.Combine(Path.GetTempPath(), "GMExplorer");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, Path.GetFileNameWithoutExtension(bank.Path) + "_" + section.Offset.ToString("X") + ".fsb");

        int size = 60 + section.HeaderSize + section.NameSize + section.DataSize;
        if (!File.Exists(target) || new FileInfo(target).Length != size)
        {
            using var input = File.OpenRead(bank.Path);
            input.Seek(section.Offset, SeekOrigin.Begin);
            using var output = File.Create(target);
            var buffer = new byte[1 << 20];
            long left = size;
            while (left > 0)
            {
                int want = (int)Math.Min(buffer.Length, left);
                int got = input.Read(buffer, 0, want);
                if (got <= 0) break;
                output.Write(buffer, 0, got);
                left -= got;
            }
            tempFiles.Add(target);
        }
        return target;
    }

    public void Dispose()
    {
        foreach (var s in opened.Values) s?.Dispose();
        opened.Clear();
        fmod?.Dispose();
        fmod = null;
        foreach (var f in tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
        tempFiles.Clear();
    }
}