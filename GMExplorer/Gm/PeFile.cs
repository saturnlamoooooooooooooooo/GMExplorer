using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GMExplorer.Gm;

public sealed class PeResource
{
    public string Type = "";
    public string Name = "";
    public int Offset;
    public int Size;
    public string Detail = "";
}

public sealed class PeFile
{
    public string Path = "";
    public byte[] Raw = Array.Empty<byte>();
    public bool Is64;
    public bool IsDll;
    public ulong ImageBase;
    public DateTime Timestamp;
    public List<(string Name, uint Rva, uint VSize, uint Raw, uint RSize)> Sections = new();
    public List<PeResource> Resources = new();
    public int OverlayOffset;
    public int OverlaySize;

    public static PeFile? Read(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            return From(raw, path);
        }
        catch { return null; }
    }

    public static PeFile? From(byte[] raw, string path)
    {
        if (raw.Length < 0x40 || raw[0] != 'M' || raw[1] != 'Z') return null;
        var pe = new PeFile { Raw = raw, Path = path };
        try
        {
            int nt = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x3C));
            if (nt < 0 || nt + 0x108 > raw.Length) return null;
            if (raw[nt] != 'P' || raw[nt + 1] != 'E') return null;

            ushort chars = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(nt + 22));
            pe.IsDll = (chars & 0x2000) != 0;
            pe.Timestamp = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(nt + 8))).LocalDateTime;

            ushort nsec = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(nt + 6));
            ushort optSize = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(nt + 20));
            int opt = nt + 24;
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(opt));
            pe.Is64 = magic == 0x20B;
            pe.ImageBase = pe.Is64 ? BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(opt + 24)) : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(opt + 28));

            int sh = opt + optSize;
            int end = 0;
            for (int i = 0; i < nsec; i++)
            {
                int p = sh + i * 40;
                if (p + 40 > raw.Length) break;
                string name = Encoding.ASCII.GetString(raw, p, 8).TrimEnd('\0');
                uint vsize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(p + 8));
                uint rva = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(p + 12));
                uint rsize = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(p + 16));
                uint rptr = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(p + 20));
                pe.Sections.Add((name, rva, vsize, rptr, rsize));
                if (rptr + rsize > end && rptr + rsize <= raw.Length) end = (int)(rptr + rsize);
            }

            if (end > 0 && end < raw.Length)
            {
                pe.OverlayOffset = end;
                pe.OverlaySize = raw.Length - end;
            }

            int dirs = opt + (pe.Is64 ? 112 : 96);
            uint resRva = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(dirs + 2 * 8));
            if (resRva != 0) pe.ReadResources(resRva);
        }
        catch { }
        return pe;
    }

    public int RvaToOffset(uint rva)
    {
        foreach (var s in Sections)
        {
            uint span = Math.Max(s.VSize, s.RSize);
            if (rva >= s.Rva && rva < s.Rva + span)
            {
                uint off = rva - s.Rva;
                if (off >= s.RSize) return -1;
                long file = s.Raw + off;
                return file < Raw.Length ? (int)file : -1;
            }
        }
        return -1;
    }

    static readonly string[] StandardTypes =
    {
        "", "CURSOR", "BITMAP", "ICON", "MENU", "DIALOG", "STRING", "FONTDIR", "FONT",
        "ACCELERATOR", "RCDATA", "MESSAGETABLE", "GROUP_CURSOR", "", "GROUP_ICON", "",
        "VERSION", "DLGINCLUDE", "", "PLUGPLAY", "VXD", "ANICURSOR", "ANIICON", "HTML", "MANIFEST"
    };

    void ReadResources(uint rootRva)
    {
        int root = RvaToOffset(rootRva);
        if (root < 0) return;
        Walk(root, root, rootRva, 0, "", "");
    }

    void Walk(int root, int dir, uint rootRva, int level, string type, string name)
    {
        if (dir < 0 || dir + 16 > Raw.Length || level > 3) return;
        ushort named = BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(dir + 12));
        ushort ided = BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(dir + 14));
        int count = named + ided;
        if (count > 4096) return;

        for (int i = 0; i < count; i++)
        {
            int e = dir + 16 + i * 8;
            if (e + 8 > Raw.Length) return;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(e));
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(e + 4));

            string label;
            if ((id & 0x80000000) != 0)
            {
                int sp = root + (int)(id & 0x7FFFFFFF);
                label = "?";
                if (sp + 2 <= Raw.Length)
                {
                    int len = BinaryPrimitives.ReadUInt16LittleEndian(Raw.AsSpan(sp));
                    if (sp + 2 + len * 2 <= Raw.Length) label = Encoding.Unicode.GetString(Raw, sp + 2, len * 2);
                }
            }
            else label = id.ToString();

            if ((off & 0x80000000) != 0)
            {
                int child = root + (int)(off & 0x7FFFFFFF);
                if (level == 0)
                {
                    string t = !(id < StandardTypes.Length) || (id & 0x80000000) != 0 || StandardTypes[id].Length == 0 ? label : StandardTypes[id];
                    Walk(root, child, rootRva, 1, t, "");
                }
                else Walk(root, child, rootRva, level + 1, type, level == 1 ? label : name);
            }
            else
            {
                int de = root + (int)off;
                if (de + 16 > Raw.Length) continue;
                uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(de));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(de + 4));
                int fileOff = RvaToOffset(dataRva);
                if (fileOff < 0 || size == 0 || fileOff + size > Raw.Length) continue;
                Resources.Add(new PeResource
                {
                    Type = type.Length > 0 ? type : "?",
                    Name = name.Length > 0 ? name : label,
                    Offset = fileOff,
                    Size = (int)size,
                    Detail = Sniff(Raw, fileOff, (int)size)
                });
            }
        }
    }

    public byte[] Bytes(PeResource r)
    {
        var b = new byte[r.Size];
        Buffer.BlockCopy(Raw, r.Offset, b, 0, r.Size);
        return b;
    }

    public static string Sniff(byte[] d, int off, int len)
    {
        if (len < 4 || off + 4 > d.Length) return "";
        string four = Encoding.ASCII.GetString(d, off, 4);
        switch (four)
        {
            case "FORM": return "GameMaker data";
            case "RIFF": return len > 12 && Encoding.ASCII.GetString(d, off + 8, 4) == "FEV " ? "FMOD bank" : "RIFF";
            case "OggS": return "Ogg audio";
            case "FSB5": return "FMOD sample bank";
            case "PK": return "zip archive";
        }
        if (d[off] == 0x89 && d[off + 1] == 'P') return "PNG image";
        if (d[off] == 0xFF && d[off + 1] == 0xD8) return "JPEG image";
        if (d[off] == 'M' && d[off + 1] == 'Z') return "executable";
        if (d[off] == '<' ) return "XML / markup";
        if (d[off] == '{' || d[off] == '[') return "JSON";
        return "";
    }

    public (int Offset, int Size)? FindEmbeddedData()
    {
        if (OverlaySize > 8 && Encoding.ASCII.GetString(Raw, OverlayOffset, 4) == "FORM")
            return (OverlayOffset, OverlaySize);

        foreach (var r in Resources)
            if (r.Size > 1024 && Encoding.ASCII.GetString(Raw, r.Offset, 4) == "FORM")
                return (r.Offset, r.Size);

        for (int i = Math.Max(0, OverlayOffset); i + 12 < Raw.Length; i++)
        {
            if (Raw[i] != 'F' || Raw[i + 1] != 'O' || Raw[i + 2] != 'R' || Raw[i + 3] != 'M') continue;
            int len = BinaryPrimitives.ReadInt32LittleEndian(Raw.AsSpan(i + 4));
            if (len > 0x10000 && i + 8L + len <= Raw.Length &&
                Encoding.ASCII.GetString(Raw, i + 8, 4) == "GEN8")
                return (i, len + 8);
            if (OverlayOffset == 0 && i > 0x400000) break;
        }
        return null;
    }
}