using System;
using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.BZip2;

namespace GMExplorer.Gm;

public sealed class RawImage
{
    public int Width, Height;
    public byte[] Bgra = Array.Empty<byte>();

    public RawImage Crop(int x, int y, int w, int h)
    {
        w = Math.Max(0, Math.Min(w, Width - x));
        h = Math.Max(0, Math.Min(h, Height - y));
        var img = new RawImage { Width = w, Height = h, Bgra = new byte[w * h * 4] };
        if (w == 0 || h == 0) return img;
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Bgra, ((y + row) * Width + x) * 4, img.Bgra, row * w * 4, w * 4);
        return img;
    }

    public RawImage Pad(int canvasW, int canvasH, int offsetX, int offsetY)
    {
        if (canvasW <= 0 || canvasH <= 0) return this;
        var img = new RawImage { Width = canvasW, Height = canvasH, Bgra = new byte[canvasW * canvasH * 4] };
        for (int row = 0; row < Height; row++)
        {
            int ty = offsetY + row;
            if (ty < 0 || ty >= canvasH) continue;
            int copyW = Math.Min(Width, canvasW - offsetX);
            if (offsetX < 0 || copyW <= 0) continue;
            Buffer.BlockCopy(Bgra, row * Width * 4, img.Bgra, (ty * canvasW + offsetX) * 4, copyW * 4);
        }
        return img;
    }
}

public static class Textures
{
    public static bool IsPng(TexturePage p) => p.Format == TexFormat.Png;

    public static byte[] RawBlob(GmData g, TexturePage p)
    {
        int len = p.DataLength > 0 ? p.DataLength : Math.Max(0, g.Raw.Length - p.DataOffset);
        len = Math.Min(len, g.Raw.Length - p.DataOffset);
        var bytes = new byte[len];
        Buffer.BlockCopy(g.Raw, p.DataOffset, bytes, 0, len);
        return bytes;
    }

    public static RawImage DecodeQoi(byte[] src, int offset, bool compressed)
    {
        byte[] qoi;
        int qoiOffset;
        if (compressed)
        {
            bool hasSize = !(src[offset + 8] == 'B' && src[offset + 9] == 'Z' && src[offset + 10] == 'h');
            int start = offset + (hasSize ? 12 : 8);
            using var input = new MemoryStream(src, start, src.Length - start, false);
            using var bz = new BZip2InputStream(input) { IsStreamOwner = false };
            using var outp = new MemoryStream(1 << 20);
            bz.CopyTo(outp);
            qoi = outp.ToArray();
            qoiOffset = 0;
        }
        else
        {
            qoi = src;
            qoiOffset = offset;
        }

        if (qoi.Length < qoiOffset + 12 ||
            qoi[qoiOffset] != 'f' || qoi[qoiOffset + 1] != 'i' || qoi[qoiOffset + 2] != 'o' || qoi[qoiOffset + 3] != 'q')
            throw new InvalidDataException("Not a GameMaker QOI image.");

        int w = qoi[qoiOffset + 4] | (qoi[qoiOffset + 5] << 8);
        int h = qoi[qoiOffset + 6] | (qoi[qoiOffset + 7] << 8);
        if (w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024) throw new InvalidDataException("Bad QOI dimensions.");

        var img = new RawImage { Width = w, Height = h, Bgra = new byte[w * h * 4] };
        var px = img.Bgra;
        var index = new byte[64 * 4];
        byte c0 = 0, c1 = 0, c2 = 0, c3 = 255;
        int p = qoiOffset + 12, o = 0, n = px.Length, len = qoi.Length;
        while (o < n && p < len)
        {
            byte b1 = qoi[p++];
            int run = 0;
            if ((b1 & 0xC0) == 0x00)
            {
                int i = (b1 & 0x3F) * 4;
                c0 = index[i]; c1 = index[i + 1]; c2 = index[i + 2]; c3 = index[i + 3];
            }
            else if ((b1 & 0xE0) == 0x40) run = (b1 & 0x1F) + 1;
            else if ((b1 & 0xE0) == 0x60) { run = (((b1 & 0x1F) << 8) | qoi[p++]) + 33; }
            else if ((b1 & 0xC0) == 0x80)
            {
                c0 = (byte)(c0 + ((b1 >> 4) & 0x03) - 2);
                c1 = (byte)(c1 + ((b1 >> 2) & 0x03) - 2);
                c2 = (byte)(c2 + (b1 & 0x03) - 2);
            }
            else if ((b1 & 0xE0) == 0xC0)
            {
                byte b2 = qoi[p++];
                c0 = (byte)(c0 + (b1 & 0x1F) - 16);
                c1 = (byte)(c1 + (b2 >> 4) - 8);
                c2 = (byte)(c2 + (b2 & 0x0F) - 8);
            }
            else if ((b1 & 0xF0) == 0xE0)
            {
                byte b2 = qoi[p++], b3 = qoi[p++];
                c0 = (byte)(c0 + (((b1 & 0x0F) << 1) | (b2 >> 7)) - 16);
                c1 = (byte)(c1 + ((b2 & 0x7C) >> 2) - 16);
                c2 = (byte)(c2 + (((b2 & 0x03) << 3) | ((b3 & 0xE0) >> 5)) - 16);
                c3 = (byte)(c3 + (b3 & 0x1F) - 16);
            }
            else
            {
                if ((b1 & 8) != 0) c0 = qoi[p++];
                if ((b1 & 4) != 0) c1 = qoi[p++];
                if ((b1 & 2) != 0) c2 = qoi[p++];
                if ((b1 & 1) != 0) c3 = qoi[p++];
            }

            if (run > 0)
            {
                for (int i = 0; i < run && o < n; i++)
                {
                    px[o] = c0; px[o + 1] = c1; px[o + 2] = c2; px[o + 3] = c3;
                    o += 4;
                }
                continue;
            }

            int hi = ((c0 ^ c1 ^ c2 ^ c3) % 64) * 4;
            index[hi] = c0; index[hi + 1] = c1; index[hi + 2] = c2; index[hi + 3] = c3;
            px[o] = c0; px[o + 1] = c1; px[o + 2] = c2; px[o + 3] = c3;
            o += 4;
        }
        return img;
    }
}

public static class Png
{
    public static byte[] Encode(RawImage img)
    {
        int w = img.Width, h = img.Height;
        var raw = new byte[(w * 4 + 1) * h];
        int src = 0, dst = 0;
        for (int y = 0; y < h; y++)
        {
            raw[dst++] = 0;
            for (int x = 0; x < w; x++)
            {
                raw[dst] = img.Bgra[src + 2];
                raw[dst + 1] = img.Bgra[src + 1];
                raw[dst + 2] = img.Bgra[src];
                raw[dst + 3] = img.Bgra[src + 3];
                src += 4; dst += 4;
            }
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w);
        WriteBe(ihdr, 4, h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);

        using (var comp = new MemoryStream())
        {
            using (var z = new ZLibStream(comp, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            Chunk(ms, "IDAT", comp.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void WriteBe(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len);
        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, body, 4, data.Length);
        s.Write(body);
        var crc = new byte[4];
        WriteBe(crc, 0, unchecked((int)Crc32(body)));
        s.Write(crc);
    }

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}