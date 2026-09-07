using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GMExplorer.Gm;

public sealed class BankSample
{
    public string Name = "";
    public int Channels;
    public int Frequency;
    public long Samples;
    public int Offset;
    public int Size;
    public double Seconds => Frequency > 0 ? (double)Samples / Frequency : 0;
}

public sealed class BankSection
{
    public int Offset;
    public int Version;
    public int SampleCount;
    public int HeaderSize, NameSize, DataSize;
    public int Codec;
    public int TotalSize;
    public List<BankSample> Samples = new();
    public string CodecName => Codec switch
    {
        0 => "none",
        1 => "PCM 8-bit",
        2 => "PCM 16-bit",
        3 => "PCM 24-bit",
        4 => "PCM 32-bit",
        5 => "PCM float",
        6 => "GameCube ADPCM",
        7 => "IMA ADPCM",
        8 => "VAG",
        9 => "HEVAG",
        10 => "XMA",
        11 => "MPEG",
        12 => "CELT",
        13 => "ATRAC9",
        14 => "xWMA",
        15 => "Vorbis",
        16 => "FADPCM",
        17 => "Opus", _ => "codec " + Codec
    };
}

public sealed class Bank
{
    public string Path = "";
    public long Length;
    public string FormType = "";
    public List<(string Id, int Offset, int Size)> Chunks = new();
    public List<BankSection> Sections = new();
    public List<string> Strings = new();
    public string? Error;

    public int SampleCount
    {
        get
        {
            int n = 0;
            foreach (var s in Sections) n += s.SampleCount;
            return n;
        }
    }

    public static Bank Read(string path)
    {
        var bank = new Bank { Path = path };
        try
        {
            var d = File.ReadAllBytes(path);
            bank.Length = d.Length;
            bank.Parse(d);
        }
        catch (Exception e) { bank.Error = e.Message; }
        return bank;
    }

    void Parse(byte[] d)
    {
        if (d.Length < 12 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF")
        {
            Error = "not a RIFF bank";
            return;
        }
        FormType = Encoding.ASCII.GetString(d, 8, 4).Trim();

        int pos = 12;
        while (pos + 8 <= d.Length)
        {
            string id = Encoding.ASCII.GetString(d, pos, 4).Trim();
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 4));
            if (size < 0 || pos + 8L + size > d.Length) break;
            Chunks.Add((id, pos + 8, size));
            if (id == "SND") ReadFsb(d, pos + 8, size);
            if (id == "LIST") ReadStrings(d, pos + 8, size);
            pos += 8 + size + (size & 1);
            if (size == 0) break;
        }

        if (Sections.Count == 0)
        {
            int i = IndexOf(d, "FSB5", 0);
            if (i >= 0) ReadFsb(d, i, d.Length - i);
        }
    }

    static int IndexOf(byte[] d, string magic, int from)
    {
        for (int i = from; i + magic.Length <= d.Length; i++)
        {
            bool hit = true;
            for (int k = 0; k < magic.Length; k++)
                if (d[i + k] != magic[k]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    void ReadStrings(byte[] d, int off, int size)
    {
        int end = Math.Min(d.Length, off + size);
        int start = -1;
        for (int i = off; i < end; i++)
        {
            byte c = d[i];
            bool printable = c >= 0x20 && c < 0x7F;
            if (printable && start < 0) start = i;
            else if (!printable && start >= 0)
            {
                int len = i - start;
                if (len >= 6)
                {
                    string s = Encoding.ASCII.GetString(d, start, len);
                    if (s.Contains(':') || s.Contains('/')) Strings.Add(s);
                }
                start = -1;
            }
        }
        if (Strings.Count > 4000) Strings.RemoveRange(4000, Strings.Count - 4000);
    }

    static readonly int[] Rates = { 4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };

    void ReadFsb(byte[] d, int off, int size)
    {
        int at = IndexOf(d, "FSB5", off);
        if (at < 0 || at + 60 > d.Length) return;

        var s = new BankSection
        {
            Offset = at,
            Version = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4)),
            SampleCount = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 8)),
            HeaderSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 12)),
            NameSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 16)),
            DataSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 20)),
            Codec = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 24))
        };
        if (s.SampleCount < 0 || s.SampleCount > 100000) return;

        int headerStart = at + 60;
        int nameStart = headerStart + s.HeaderSize;
        int dataStart = nameStart + s.NameSize;
        s.TotalSize = 60 + s.HeaderSize + s.NameSize + s.DataSize;

        int p = headerStart;
        var offsets = new List<int>();
        for (int i = 0; i < s.SampleCount && p + 8 <= d.Length; i++)
        {
            ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p));
            p += 8;
            bool extra = (raw & 1) != 0;
            int rateIndex = (int)((raw >> 1) & 0x0F);
            int channels = (int)((raw >> 5) & 0x03) + 1;
            int dataOffset = (int)(((raw >> 7) & 0x0FFFFFFF) * 16);
            long samples = (long)((raw >> 34) & 0x3FFFFFFF);

            while (extra && p + 4 <= d.Length)
            {
                uint chunk = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
                p += 4;
                extra = (chunk & 1) != 0;
                int clen = (int)((chunk >> 1) & 0xFFFFFF);
                p += clen;
            }

            var sample = new BankSample
            {
                Channels = channels,
                Frequency = rateIndex >= 0 && rateIndex < Rates.Length ? Rates[rateIndex] : 44100,
                Samples = samples,
                Offset = dataStart + dataOffset
            };
            offsets.Add(dataOffset);
            s.Samples.Add(sample);
        }

        for (int i = 0; i < s.Samples.Count; i++)
        {
            int next = i + 1 < offsets.Count ? offsets[i + 1] : s.DataSize;
            s.Samples[i].Size = Math.Max(0, next - offsets[i]);
        }

        if (s.NameSize > 0 && nameStart + s.SampleCount * 4 <= d.Length)
        {
            for (int i = 0; i < s.Samples.Count; i++)
            {
                int rel = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nameStart + i * 4));
                int np = nameStart + rel;
                if (np <= 0 || np >= d.Length) continue;
                int len = 0;
                while (np + len < d.Length && d[np + len] != 0 && len < 200) len++;
                s.Samples[i].Name = Encoding.UTF8.GetString(d, np, len);
            }
        }

        Sections.Add(s);
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("container    ").Append(FormType.Length > 0 ? FormType : "?").Append('\n');
        sb.Append("size         ").Append(Length.ToString("N0")).Append(" bytes\n");
        if (Error != null) sb.Append("problem      ").Append(Error).Append('\n');
        sb.Append("chunks       ");
        foreach (var c in Chunks) sb.Append(c.Id).Append(' ');
        sb.Append('\n');

        foreach (var s in Sections)
        {
            sb.Append("\nFSB5 at 0x").Append(s.Offset.ToString("X")).Append("  version ").Append(s.Version).Append("  codec ").Append(s.CodecName).Append("  ").Append(s.SampleCount).Append(" samples").Append("  ").Append(s.DataSize.ToString("N0")).Append(" bytes of audio\n");
            if (s.Codec == 15 || s.Codec == 12 || s.Codec == 13)
                sb.Append("(FMOD strips the codec headers from these samples, so only FMOD itself can decode them. " + "The whole sample bank can still be exported.)\n");
            sb.Append('\n');
            int shown = 0;
            foreach (var x in s.Samples)
            {
                if (shown++ >= 400) { sb.Append("... and ").Append(s.Samples.Count - 400).Append(" more\n"); break; }
                sb.Append("  ").Append((x.Name.Length > 0 ? x.Name : "sample " + shown).PadRight(44)).Append(x.Channels).Append("ch  ").Append(x.Frequency).Append("Hz  ").Append(x.Seconds.ToString("0.00")).Append("s  ").Append(x.Size.ToString("N0")).Append(" bytes\n");
            }
        }

        if (Strings.Count > 0)
        {
            sb.Append("\nevent paths (").Append(Strings.Count).Append(")\n");
            int n = 0;
            foreach (var s in Strings)
            {
                if (n++ >= 300) { sb.Append("...\n"); break; }
                sb.Append("  ").Append(s).Append('\n');
            }
        }
        return sb.ToString();
    }
}