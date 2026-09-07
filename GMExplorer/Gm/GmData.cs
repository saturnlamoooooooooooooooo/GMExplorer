using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GMExplorer.Gm;

public sealed class GmData
{
    public string Path = "";
    public string FileName = "";
    public byte[] Raw = Array.Empty<byte>();
    public Reader R = null!;

    public List<GmChunk> ChunkList = new();
    public Dictionary<string, GmChunk> Chunks = new(StringComparer.Ordinal);

    public int BytecodeVersion;
    public string GameName = "";
    public string DisplayName = "";
    public string Version = "";
    public int WindowWidth, WindowHeight;
    public bool IsDebug;
    public DateTime Timestamp;

    public List<string> Strings = new();
    public Dictionary<int, string> StringByAddr = new();

    public List<TexturePage> Pages = new();
    public List<TexItem> TexItems = new();
    public Dictionary<int, int> TexItemByAddr = new();
    public List<GmSprite> Sprites = new();
    public List<GmBackground> Backgrounds = new();
    public List<GmFont> Fonts = new();
    public List<GmSound> Sounds = new();
    public List<GmAudioBlob> Audio = new();
    public List<GmCode> Code = new();
    public Dictionary<string, GmCode> CodeByName = new(StringComparer.Ordinal);
    public List<GmObject> Objects = new();
    public List<GmRoom> Rooms = new();
    public List<GmSimple> Scripts = new();
    public List<GmSimple> Shaders = new();
    public List<GmSimple> Paths = new();
    public List<GmSimple> Timelines = new();
    public List<GmSimple> Extensions = new();
    public List<GmSimple> AudioGroups = new();

    public List<GmVariable> Variables = new();
    public List<GmFunction> Functions = new();

    public Dictionary<int, string> RefNames = new();
    public HashSet<int> FuncRefs = new();

    public List<string> Warnings = new();

    public bool HasCode => Code.Count > 0;

    void Warn(string s) { if (Warnings.Count < 200) Warnings.Add(s); }

    public static GmData Load(string path, Action<string>? progress = null)
    {
        progress?.Invoke("Reading file...");
        return LoadFrom(File.ReadAllBytes(path), path, progress);
    }

    public static GmData LoadFrom(byte[] raw, string path, Action<string>? progress = null)
    {
        var g = new GmData
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
        };
        g.Raw = raw;
        g.R = new Reader(g.Raw);
        g.ReadChunks();
        progress?.Invoke("Strings...");
        g.ReadStrings();
        g.ReadGen8();
        progress?.Invoke("Textures...");
        g.ReadTextures();
        g.ReadTexItems();
        progress?.Invoke("Sprites...");
        g.ReadSprites();
        g.ReadBackgrounds();
        g.ReadFonts();
        progress?.Invoke("Audio...");
        g.ReadAudio();
        progress?.Invoke("Code...");
        g.ReadCode();
        g.ReadRefs();
        progress?.Invoke("Assets...");
        g.ReadSimpleLists();
        g.ReadObjects();
        g.ReadRooms();
        return g;
    }

    void ReadChunks()
    {
        var r = R;
        if (r.Length < 16 || r.Fourcc() != "FORM")
            throw new InvalidDataException("Not a GameMaker data file (no FORM header).");
        int total = r.I32();
        int end = (int)Math.Min(8L + total, r.Length);
        int pos = 8;
        while (pos + 8 <= end)
        {
            string name = Encoding.ASCII.GetString(Raw, pos, 4);
            int len = r.At(pos + 4);
            if (len < 0 || pos + 8L + len > r.Length)
            {
                Warn("Chunk " + name + " has a bad length; stopping chunk scan.");
                break;
            }
            var c = new GmChunk { Name = name, Offset = pos + 8, Length = len };
            ChunkList.Add(c);
            Chunks[name] = c;
            pos += 8 + len;
        }
    }

    GmChunk? Chunk(string name) => Chunks.TryGetValue(name, out var c) ? c : null;

    int[] Pointers(GmChunk c)
    {
        if (c.Length < 4) return Array.Empty<int>();
        int n = R.At(c.Offset);
        if (n < 0 || 4L + (long)n * 4 > c.Length)
        {
            Warn(c.Name + ": implausible entry count " + n + ".");
            return Array.Empty<int>();
        }
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = R.At(c.Offset + 4 + i * 4);
        return a;
    }

    static int EntryEnd(int[] ptrs, int i, GmChunk c) => i + 1 < ptrs.Length ? ptrs[i + 1] : c.Offset + c.Length;

    void ReadStrings()
    {
        var c = Chunk("STRG");
        if (c == null) { Warn("No STRG chunk - names will be missing."); return; }
        var ptrs = Pointers(c);
        Strings.Capacity = ptrs.Length;
        foreach (int p in ptrs)
        {
            if (!R.InRange(p, 4)) { Strings.Add(""); continue; }
            int len = R.At(p);
            int start = p + 4;
            if (len < 0 || !R.InRange(start, len)) { Strings.Add(""); continue; }
            string s = Encoding.UTF8.GetString(Raw, start, len);
            Strings.Add(s);
            StringByAddr[start] = s;
        }
    }

    public string Str(int charAddr)
    {
        if (charAddr <= 0) return "";
        if (StringByAddr.TryGetValue(charAddr, out var s)) return s;
        return R.StringAt(charAddr);
    }

    public string StrIndex(int i) => i >= 0 && i < Strings.Count ? Strings[i] : "<string " + i + ">";

    void ReadGen8()
    {
        var c = Chunk("GEN8");
        if (c == null) { Warn("No GEN8 chunk."); return; }
        try
        {
            var r = new Reader(Raw, c.Offset);
            IsDebug = r.U8() != 0;
            BytecodeVersion = r.U8();
            r.U16();
            Str(r.I32());
            r.I32();
            r.I32(); r.I32();
            r.I32();
            r.Skip(16);
            GameName = Str(r.I32());
            int major = r.I32(), minor = r.I32(), release = r.I32(), build = r.I32();
            Version = major + "." + minor + "." + release + "." + build;
            WindowWidth = r.I32();
            WindowHeight = r.I32();
            r.I32();
            r.Skip(16);
            r.I32();
            long ts = r.I64();
            try { Timestamp = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime; } catch { }
            DisplayName = Str(r.I32());
        }
        catch (Exception e) { Warn("GEN8: " + e.Message); }
    }

    void ReadTextures()
    {
        var c = Chunk("TXTR");
        if (c == null) return;
        var ptrs = Pointers(c);
        int stride = ptrs.Length > 1 ? ptrs[1] - ptrs[0] : 0;
        for (int i = 0; i < ptrs.Length; i++)
        {
            var page = new TexturePage { Index = i };
            try
            {
                int entry = ptrs[i];
                int size = stride > 0 ? stride : Math.Min(28, EntryEnd(ptrs, i, c) - entry);
                page.Scaled = R.At(entry) != 0;

                int dataPtr = size >= 8 ? R.At(entry + size - 4) : 0;
                if (!IsBlobStart(dataPtr))
                {
                    for (int off = 4; off + 4 <= size; off += 4)
                    {
                        int cand = R.At(entry + off);
                        if (IsBlobStart(cand)) { dataPtr = cand; break; }
                    }
                }
                page.DataOffset = dataPtr;
                page.Format = FormatAt(dataPtr);
                if (page.Format == TexFormat.Bz2Qoi || page.Format == TexFormat.Qoi)
                {
                    page.Width = (ushort)R.At16(dataPtr + 4);
                    page.Height = (ushort)R.At16(dataPtr + 6);
                }
                page.DataLength = BlobLength(dataPtr, page.Format);
                if (page.Format == TexFormat.Unknown) page.Error = "unrecognised texture format";
            }
            catch (Exception e) { page.Error = e.Message; }
            Pages.Add(page);
        }
    }

    bool IsBlobStart(int p) => R.InRange(p, 12) && FormatAt(p) != TexFormat.Unknown;

    TexFormat FormatAt(int p)
    {
        if (!R.InRange(p, 12)) return TexFormat.Unknown;
        var d = Raw;
        if (d[p] == 0x89 && d[p + 1] == (byte)'P' && d[p + 2] == (byte)'N' && d[p + 3] == (byte)'G') return TexFormat.Png;
        if (d[p] == (byte)'f' && d[p + 1] == (byte)'i' && d[p + 2] == (byte)'o' && d[p + 3] == (byte)'q') return TexFormat.Qoi;
        if (d[p] == (byte)'2' && d[p + 1] == (byte)'z' && d[p + 2] == (byte)'o' && d[p + 3] == (byte)'q') return TexFormat.Bz2Qoi;
        return TexFormat.Unknown;
    }

    int BlobLength(int p, TexFormat f)
    {
        try
        {
            switch (f)
            {
                case TexFormat.Png:
                    {
                        int q = p + 8;
                        while (R.InRange(q, 8))
                        {
                            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(Raw.AsSpan(q));
                            string type = Encoding.ASCII.GetString(Raw, q + 4, 4);
                            if (len < 0) break;
                            q += 12 + len;
                            if (type == "IEND") return q - p;
                        }
                        break;
                    }
                case TexFormat.Qoi:
                    return 12 + R.At(p + 8);
                case TexFormat.Bz2Qoi:
                    return 0;
            }
        }
        catch { }
        return 0;
    }

    void ReadTexItems()
    {
        var c = Chunk("TPAG");
        if (c == null) return;
        var ptrs = Pointers(c);
        for (int i = 0; i < ptrs.Length; i++)
        {
            int p = ptrs[i];
            if (!R.InRange(p, 22)) continue;
            var r = new Reader(Raw, p);
            var it = new TexItem
            {
                Index = i,
                SourceX = r.U16(),
                SourceY = r.U16(),
                SourceW = r.U16(),
                SourceH = r.U16(),
                TargetX = r.U16(),
                TargetY = r.U16(),
                TargetW = r.U16(),
                TargetH = r.U16(),
                BoundW = r.U16(),
                BoundH = r.U16(),
                Page = r.U16()
            };
            TexItems.Add(it);
            TexItemByAddr[p] = i;
        }
    }

    int ItemAt(int addr) => TexItemByAddr.TryGetValue(addr, out int i) ? i : -1;

    void ScanItems(int from, int to, List<int> into)
    {
        for (int p = from; p + 4 <= to; p += 4)
        {
            int i = ItemAt(R.At(p));
            if (i >= 0) into.Add(i);
        }
    }

    void ReadSprites()
    {
        var c = Chunk("SPRT");
        if (c == null) return;
        var ptrs = Pointers(c);
        for (int i = 0; i < ptrs.Length; i++)
        {
            var s = new GmSprite { Index = i };
            int start = ptrs[i], end = EntryEnd(ptrs, i, c);
            try
            {
                var r = new Reader(Raw, start);
                s.Name = Str(r.I32());
                s.Width = r.I32(); s.Height = r.I32();
                s.MarginLeft = r.I32(); s.MarginRight = r.I32();
                s.MarginBottom = r.I32(); s.MarginTop = r.I32();
                s.Transparent = r.I32() != 0; s.Smooth = r.I32() != 0; s.Preload = r.I32() != 0;
                s.BBoxMode = r.I32(); s.SepMasks = r.I32();
                s.OriginX = r.I32(); s.OriginY = r.I32();

                int next = r.I32();
                if (next == -1)
                {
                    int sversion = r.I32();
                    s.SpriteType = r.I32();
                    s.PlaybackSpeed = r.F32();
                    r.I32();
                    if (sversion >= 2) r.I32();
                    if (sversion >= 3) r.I32();
                    if (s.SpriteType == 0) ReadFrameList(r, s, end);
                    else s.Note = s.SpriteType == 1
                        ? "SWF sprite - vector data is not previewable"
                        : "Spine sprite - skeleton data is not previewable";
                }
                else
                {
                    r.Pos -= 4;
                    ReadFrameList(r, s, end);
                }
                if (s.Frames.Count == 0 && s.Note == null) ScanItems(start, end, s.Frames);
            }
            catch (Exception e)
            {
                s.Note = "parse error: " + e.Message;
                s.Frames.Clear();
                try { ScanItems(start, end, s.Frames); } catch { }
            }
            Sprites.Add(s);
        }
    }

    void ReadFrameList(Reader r, GmSprite s, int end)
    {
        int count = r.I32();
        if (count < 0 || count > 8192 || r.Pos + count * 4 > end) return;
        for (int k = 0; k < count; k++)
        {
            int idx = ItemAt(r.I32());
            if (idx >= 0) s.Frames.Add(idx);
        }
    }

    void ReadBackgrounds()
    {
        var c = Chunk("BGND");
        if (c == null) return;
        var ptrs = Pointers(c);
        for (int i = 0; i < ptrs.Length; i++)
        {
            var b = new GmBackground { Index = i };
            int start = ptrs[i], end = EntryEnd(ptrs, i, c);
            try
            {
                b.Name = Str(R.At(start));
                var found = new List<int>();
                ScanItems(start + 4, end, found);
                if (found.Count > 0) b.Item = found[0];
            }
            catch { }
            Backgrounds.Add(b);
        }
    }

    void ReadFonts()
    {
        var c = Chunk("FONT");
        if (c == null) return;
        var ptrs = Pointers(c);
        for (int i = 0; i < ptrs.Length; i++)
        {
            var f = new GmFont { Index = i };
            int start = ptrs[i], end = EntryEnd(ptrs, i, c);
            try
            {
                var r = new Reader(Raw, start);
                f.Name = Str(r.I32());
                f.DisplayName = Str(r.I32());
                var found = new List<int>();
                ScanItems(start + 8, end, found);
                if (found.Count > 0) f.Item = found[0];
            }
            catch { }
            Fonts.Add(f);
        }
    }

    void ReadAudio()
    {
        var agrp = Chunk("AGRP");
        if (agrp != null)
        {
            var gp = Pointers(agrp);
            for (int i = 0; i < gp.Length; i++)
                AudioGroups.Add(new GmSimple { Index = i, Name = Str(R.At(gp[i])) });
        }

        var c = Chunk("AUDO");
        if (c != null)
        {
            var ptrs = Pointers(c);
            for (int i = 0; i < ptrs.Length; i++)
            {
                int p = ptrs[i];
                if (!R.InRange(p, 4)) continue;
                int len = R.At(p);
                if (len < 0 || !R.InRange(p + 4, len)) len = Math.Max(0, EntryEnd(ptrs, i, c) - p - 4);
                Audio.Add(new GmAudioBlob
                {
                    Index = i,
                    Offset = p + 4,
                    Length = len,
                    Container = Container(Raw, p + 4, len)
                });
            }
        }

        var extra = new Dictionary<int, List<GmAudioBlob>>();
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "audiogroup*.dat"))
                {
                    string stem = System.IO.Path.GetFileNameWithoutExtension(file);
                    string digits = new string(stem.Where(char.IsDigit).ToArray());
                    if (!int.TryParse(digits, out int gid)) continue;
                    var list = ReadExternalGroup(File.ReadAllBytes(file), System.IO.Path.GetFileName(file));
                    if (list.Count > 0) extra[gid] = list;
                }
            }
        }
        catch (Exception e) { Warn("audiogroup*.dat: " + e.Message); }

        var sc = Chunk("SOND");
        if (sc == null) return;
        var sptrs = Pointers(sc);
        int stride = sptrs.Length > 1 ? sptrs[1] - sptrs[0] : 36;
        for (int i = 0; i < sptrs.Length; i++)
        {
            var s = new GmSound { Index = i };
            try
            {
                var r = new Reader(Raw, sptrs[i]);
                s.Name = Str(r.I32());
                s.Flags = r.I32();
                s.Type = Str(r.I32());
                s.File = Str(r.I32());
                r.I32();
                s.Volume = r.F32();
                s.Pitch = r.F32();
                if (stride >= 36) { s.GroupId = r.I32(); s.AudioId = r.I32(); }
                else s.AudioId = r.I32();
                s.Embedded = (s.Flags & 0x01) != 0;
                s.Compressed = (s.Flags & 0x02) != 0;

                if (s.GroupId > 0 && extra.TryGetValue(s.GroupId, out var glist))
                {
                    if (s.AudioId >= 0 && s.AudioId < glist.Count) s.Blob = glist[s.AudioId];
                }
                else if (s.AudioId >= 0 && s.AudioId < Audio.Count) s.Blob = Audio[s.AudioId];
            }
            catch (Exception e) { Warn("SOND[" + i + "]: " + e.Message); }
            Sounds.Add(s);
        }
    }

    List<GmAudioBlob> ReadExternalGroup(byte[] bytes, string origin)
    {
        var result = new List<GmAudioBlob>();
        var r = new Reader(bytes);
        if (bytes.Length < 16 || r.Fourcc() != "FORM") return result;
        int total = r.I32();
        int pos = 8, end = (int)Math.Min(8L + total, bytes.Length);
        while (pos + 8 <= end)
        {
            string name = Encoding.ASCII.GetString(bytes, pos, 4);
            int len = r.At(pos + 4);
            if (len < 0 || pos + 8L + len > bytes.Length) break;
            if (name == "AUDO")
            {
                int off = pos + 8;
                int n = r.At(off);
                if (n < 0 || 4L + (long)n * 4 > len) break;
                for (int i = 0; i < n; i++)
                {
                    int p = r.At(off + 4 + i * 4);
                    if (p < 0 || p + 4L > bytes.Length) continue;
                    int l = r.At(p);
                    if (l < 0 || p + 4L + l > bytes.Length) continue;
                    result.Add(new GmAudioBlob
                    {
                        Index = i,
                        Offset = p + 4,
                        Length = l,
                        Source = bytes,
                        Origin = origin,
                        Container = Container(bytes, p + 4, l)
                    });
                }
                break;
            }
            pos += 8 + len;
        }
        return result;
    }

    static string Container(byte[] d, int off, int len)
    {
        if (len < 4 || off + 4 > d.Length) return "?";
        if (d[off] == 'R' && d[off + 1] == 'I' && d[off + 2] == 'F' && d[off + 3] == 'F') return "WAV";
        if (d[off] == 'O' && d[off + 1] == 'g' && d[off + 2] == 'g' && d[off + 3] == 'S') return "OGG";
        if (d[off] == 'I' && d[off + 1] == 'D' && d[off + 2] == '3') return "MP3";
        if (d[off] == 0xFF && (d[off + 1] & 0xE0) == 0xE0) return "MP3";
        return "?";
    }

    void ReadCode()
    {
        var c = Chunk("CODE");
        if (c == null)
        {
            Warn("No CODE chunk - this game was built with the YoYo Compiler, so it ships native code instead of bytecode.");
            return;
        }
        var ptrs = Pointers(c);
        for (int i = 0; i < ptrs.Length; i++)
        {
            var e = new GmCode { Index = i };
            try
            {
                int p = ptrs[i];
                e.Name = Str(R.At(p));
                e.Length = R.At(p + 4);
                if (BytecodeVersion > 14)
                {
                    e.Locals = R.At16(p + 8);
                    e.Args = R.At16(p + 10) & 0x1FFF;
                    int rel = R.At(p + 12);
                    e.Offset = R.At(p + 16);
                    e.Address = p + 12 + rel + e.Offset;
                }
                else
                {
                    e.Address = p + 8;
                }
            }
            catch (Exception ex) { Warn("CODE[" + i + "]: " + ex.Message); }
            Code.Add(e);
            CodeByName[e.Name] = e;
        }

        var byAddr = new Dictionary<int, int>();
        foreach (var e in Code) if (e.Offset == 0) byAddr[e.Address] = e.Index;
        foreach (var e in Code)
            if (e.Offset > 0 && byAddr.TryGetValue(e.Address - e.Offset, out int parent)) e.ParentIndex = parent;
    }

    void ReadRefs()
    {
        var fc = Chunk("FUNC");
        if (fc != null && fc.Length >= 4)
        {
            try
            {
                int n = R.At(fc.Offset);
                if (n >= 0 && 4L + (long)n * 12 <= fc.Length)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int p = fc.Offset + 4 + i * 12;
                        var f = new GmFunction { Name = Str(R.At(p)), Occurrences = R.At(p + 4) };
                        Functions.Add(f);
                        Chain(R.At(p + 8), f.Occurrences, f.Name, true);
                    }
                }
                else Warn("FUNC: unexpected layout.");
            }
            catch (Exception e) { Warn("FUNC: " + e.Message); }
        }

        var vc = Chunk("VARI");
        if (vc != null && vc.Length >= 12)
        {
            try
            {
                bool modern = BytecodeVersion > 14;
                int p = vc.Offset + (modern ? 12 : 0);
                int end = vc.Offset + vc.Length;
                int stride = modern ? 20 : 12;
                while (p + stride <= end)
                {
                    var v = new GmVariable { Name = Str(R.At(p)) };
                    if (modern)
                    {
                        v.InstanceType = R.At(p + 4);
                        v.VarId = R.At(p + 8);
                        v.Occurrences = R.At(p + 12);
                        Chain(R.At(p + 16), v.Occurrences, v.Name, false);
                    }
                    else
                    {
                        v.Occurrences = R.At(p + 4);
                        Chain(R.At(p + 8), v.Occurrences, v.Name, false);
                    }
                    if (v.Name.Length > 0 || v.Occurrences > 0) Variables.Add(v);
                    p += stride;
                }
            }
            catch (Exception e) { Warn("VARI: " + e.Message); }
        }
    }

    void Chain(int first, int count, string name, bool isFunction)
    {
        if (count <= 0 || first <= 0) return;
        int addr = first;
        for (int i = 0; i < count; i++)
        {
            int operand = isFunction ? addr : addr + 4;
            if (!R.InRange(operand, 4)) return;
            RefNames[operand] = name;
            if (isFunction) FuncRefs.Add(operand);
            uint v = R.AtU(operand);
            int next = (int)(v & 0x07FFFFFF);
            if (next == 0) return;
            addr += next;
        }
    }

    void ReadSimpleLists()
    {
        var c = Chunk("SCPT");
        if (c != null)
        {
            var ptrs = Pointers(c);
            for (int i = 0; i < ptrs.Length; i++)
            {
                var s = new GmSimple { Index = i };
                try
                {
                    s.Name = Str(R.At(ptrs[i]));
                    int id = R.At(ptrs[i] + 4);
                    bool ctor = (id & unchecked((int)0x80000000)) != 0;
                    int codeId = id & 0x7FFFFFFF;
                    s.CodeIndex = codeId >= 0 && codeId < Code.Count ? codeId : -1;
                    s.Detail = ctor ? "constructor" : "";
                }
                catch { }
                Scripts.Add(s);
            }
        }
        Simple("SHDR", Shaders);
        Simple("PATH", Paths);
        Simple("TMLN", Timelines);
        Simple("EXTN", Extensions);
    }

    void Simple(string chunk, List<GmSimple> into)
    {
        var c = Chunk(chunk);
        if (c == null) return;
        var ptrs = Pointers(c);
        for (int i = 0; i < ptrs.Length; i++)
        {
            try { into.Add(new GmSimple { Index = i, Name = Str(R.At(ptrs[i])) }); }
            catch { into.Add(new GmSimple { Index = i, Name = "<" + chunk + " " + i + ">" }); }
        }
    }

    static readonly string[] EventNames =
    {
        "Create", "Destroy", "Alarm", "Step", "Collision", "Keyboard", "Mouse",
        "Other", "Draw", "KeyPress", "KeyRelease", "Trigger", "CleanUp", "Gesture",
        "PreCreate", "Async"
    };

    void ReadObjects()
    {
        var c = Chunk("OBJT");
        if (c == null) return;
        var ptrs = Pointers(c);
        for (int i = 0; i < ptrs.Length; i++)
        {
            var o = new GmObject { Index = i };
            try
            {
                int b = ptrs[i] + 4;
                o.Name = Str(R.At(ptrs[i]));
                int m = ObjectLayout(b, ptrs.Length);
                o.SpriteIndex = R.At(b);
                o.Visible = R.At(b + 4) != 0;
                o.Solid = R.At(b + 8 + m * 4) != 0;
                o.Depth = R.At(b + 12 + m * 4);
                o.Persistent = R.At(b + 16 + m * 4) != 0;
                int parent = R.At(b + 20 + m * 4);
                o.ParentIndex = parent < -1 ? -1 : parent;
            }
            catch { }
            Objects.Add(o);
        }

        var byName = new Dictionary<string, GmObject>(StringComparer.Ordinal);
        foreach (var o in Objects) if (o.Name.Length > 0) byName[o.Name] = o;
        var events = new HashSet<string>(EventNames, StringComparer.Ordinal);
        const string prefix = "gml_Object_";
        foreach (var e in Code)
        {
            if (e.IsChild || !e.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rest = e.Name.Substring(prefix.Length);
            int cut = rest.Length;
            while (cut > 0)
            {
                cut = rest.LastIndexOf('_', cut - 1);
                if (cut <= 0) break;
                string objName = rest.Substring(0, cut);
                string after = rest.Substring(cut + 1);
                int dot = after.IndexOf('_');
                string evt = dot < 0 ? after : after.Substring(0, dot);
                if (events.Contains(evt) && byName.TryGetValue(objName, out var obj)) { obj.Events.Add(e); break; }
            }
        }
    }

    int ObjectLayout(int b, int objectCount)
    {
        int best = 0, bestScore = int.MinValue;
        for (int m = 0; m <= 1; m++)
        {
            if (!R.InRange(b + 28 + m * 4, 4)) continue;
            int solid = R.At(b + 8 + m * 4);
            int persistent = R.At(b + 16 + m * 4);
            int parent = R.At(b + 20 + m * 4);
            int mask = R.At(b + 24 + m * 4);
            int physics = R.At(b + 28 + m * 4);
            int score = 0;
            if (parent == -100 || (parent >= -1 && parent < objectCount)) score += 2;
            if (mask >= -1 && mask < Sprites.Count) score += 2;
            if (physics == 0 || physics == 1) score++;
            if (solid == 0 || solid == 1) score++;
            if (persistent == 0 || persistent == 1) score++;
            if (score > bestScore) { bestScore = score; best = m; }
        }
        return best;
    }

    void ReadRooms()
    {
        var c = Chunk("ROOM");
        if (c == null) return;
        var ptrs = Pointers(c);
        for (int i = 0; i < ptrs.Length; i++)
        {
            var room = new GmRoom { Index = i };
            try
            {
                var r = new Reader(Raw, ptrs[i]);
                room.Name = Str(r.I32());
                room.Caption = Str(r.I32());
                room.Width = r.I32();
                room.Height = r.I32();
                room.Speed = r.I32();
                room.Persistent = r.I32() != 0;
                room.BgColor = r.U32();
                r.I32();
                room.CreationCodeId = r.I32();
                r.I32();
                r.I32();
                r.I32();
                ReadRoomInstances(r.I32(), room);
            }
            catch (Exception e) { room.Note = "parse error: " + e.Message; }
            Rooms.Add(room);
        }
    }

    void ReadRoomInstances(int listPtr, GmRoom room)
    {
        if (!R.InRange(listPtr, 4)) return;
        int n = R.At(listPtr);
        if (n < 0 || n > 200000 || !R.InRange(listPtr + 4, n * 4)) return;
        for (int i = 0; i < n; i++)
        {
            int p = R.At(listPtr + 4 + i * 4);
            if (!R.InRange(p, 28)) continue;
            var r = new Reader(Raw, p);
            var inst = new GmRoomInstance
            {
                X = r.I32(),
                Y = r.I32(),
                ObjectIndex = r.I32(),
                Id = r.I32()
            };
            r.I32();
            inst.ScaleX = r.F32();
            inst.ScaleY = r.F32();
            room.Instances.Add(inst);
        }
    }
}