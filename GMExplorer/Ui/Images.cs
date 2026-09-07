using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GMExplorer.Gm;

namespace GMExplorer.Ui;

public sealed class Images
{
    readonly GmData g;
    readonly Dictionary<int, RawImage?> pages = new();
    readonly Dictionary<int, string> failures = new();

    public Images(GmData data) => g = data;

    public RawImage? Page(int index)
    {
        if (pages.TryGetValue(index, out var cached)) return cached;
        RawImage? img = null;
        try
        {
            if (index >= 0 && index < g.Pages.Count)
            {
                var p = g.Pages[index];
                img = p.Format switch
                {
                    TexFormat.Png => FromPng(Textures.RawBlob(g, p)),
                    TexFormat.Qoi => Textures.DecodeQoi(g.Raw, p.DataOffset, false),
                    TexFormat.Bz2Qoi => Textures.DecodeQoi(g.Raw, p.DataOffset, true),
                    _ => null
                };
                if (img != null) { p.Width = img.Width; p.Height = img.Height; }
            }
        }
        catch (Exception e)
        {
            failures[index] = e.Message;
        }
        pages[index] = img;
        return img;
    }

    public string? PageError(int index) => failures.TryGetValue(index, out var e) ? e : null;

    static RawImage FromPng(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var bmp = new Bitmap(ms);
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        var img = new RawImage { Width = w, Height = h, Bgra = new byte[w * h * 4] };
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(img.Bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), img.Bgra.Length, w * 4); }
        finally { handle.Free(); }
        return img;
    }

    public RawImage? Item(int itemIndex, bool padToBounds = true)
    {
        if (itemIndex < 0 || itemIndex >= g.TexItems.Count) return null;
        var it = g.TexItems[itemIndex];
        var page = Page(it.Page);
        if (page == null) return null;
        var cut = page.Crop(it.SourceX, it.SourceY, it.SourceW, it.SourceH);
        if (!padToBounds || it.BoundW <= 0 || it.BoundH <= 0) return cut;
        if (it.TargetX == 0 && it.TargetY == 0 && it.BoundW == it.SourceW && it.BoundH == it.SourceH) return cut;
        return cut.Pad(it.BoundW, it.BoundH, it.TargetX, it.TargetY);
    }

    public static Bitmap ToBitmap(RawImage img)
    {
        var bmp = new WriteableBitmap(new PixelSize(Math.Max(1, img.Width), Math.Max(1, img.Height)), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = bmp.Lock())
        {
            int rowBytes = Math.Min(fb.RowBytes, img.Width * 4);
            for (int y = 0; y < img.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(img.Bgra, y * img.Width * 4, fb.Address + y * fb.RowBytes, rowBytes);
        }
        return bmp;
    }
}