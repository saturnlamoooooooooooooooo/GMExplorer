using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace GMExplorer.Gm;

public static class Dump
{
    public static void Run(string[] args)
    {
        string path = args[1];
        string outPath = args.Length > 2 ? args[2] : "dump.txt";
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var g = GmData.Load(path, s => { });
        sb.AppendLine($"loaded in {sw.ElapsedMilliseconds} ms");
        sb.AppendLine($"file      : {g.FileName} ({g.Raw.Length:N0} bytes)");
        sb.AppendLine($"game      : {g.GameName} / {g.DisplayName}");
        sb.AppendLine($"version   : {g.Version}  bytecode {g.BytecodeVersion}  {g.WindowWidth}x{g.WindowHeight}");
        sb.AppendLine($"built     : {g.Timestamp}");
        sb.AppendLine($"chunks    : {string.Join(" ", g.ChunkList.Select(c => c.Name))}");
        sb.AppendLine($"counts    : sprites={g.Sprites.Count} pages={g.Pages.Count} items={g.TexItems.Count} " + $"bg={g.Backgrounds.Count} fonts={g.Fonts.Count} sounds={g.Sounds.Count} audio={g.Audio.Count} " + $"code={g.Code.Count} scripts={g.Scripts.Count} objects={g.Objects.Count} rooms={g.Rooms.Count} " + $"vars={g.Variables.Count} funcs={g.Functions.Count} strings={g.Strings.Count}");
        foreach (var w in g.Warnings) sb.AppendLine("warn      : " + w);

        sb.AppendLine();
        sb.AppendLine("-- texture pages --");
        foreach (var p in g.Pages.Take(6))
            sb.AppendLine($"  [{p.Index}] {p.FormatName} {p.Width}x{p.Height} at 0x{p.DataOffset:X} len={p.DataLength} {p.Error}");

        sb.AppendLine();
        sb.AppendLine("-- sprites --");
        int noFrames = g.Sprites.Count(s => s.Frames.Count == 0);
        sb.AppendLine($"  {noFrames} of {g.Sprites.Count} sprites have no frames");
        foreach (var s in g.Sprites.Take(6))
            sb.AppendLine($"  [{s.Index}] {s.Name} {s.Width}x{s.Height} origin={s.OriginX},{s.OriginY} frames={s.Frames.Count} {s.Note}");

        sb.AppendLine();
        sb.AppendLine("-- sounds --");
        int noBlob = g.Sounds.Count(s => s.Blob == null);
        sb.AppendLine($"  {noBlob} of {g.Sounds.Count} sounds have no audio data");
        foreach (var s in g.Sounds.Take(6))
            sb.AppendLine($"  [{s.Index}] {s.Name} file={s.File} group={s.GroupId} audio={s.AudioId} " + $"{(s.Blob == null ? "-" : s.Blob.Container + " " + s.Blob.Length + "b from " + s.Blob.Origin)}");

        sb.AppendLine();
        sb.AppendLine("-- objects --");
        foreach (var o in g.Objects.Take(5))
            sb.AppendLine($"  [{o.Index}] {o.Name} sprite={o.SpriteIndex} parent={o.ParentIndex} events={o.Events.Count}");

        sb.AppendLine();
        sb.AppendLine("-- rooms --");
        foreach (var r in g.Rooms.Take(5))
            sb.AppendLine($"  [{r.Index}] {r.Name} {r.Width}x{r.Height} speed={r.Speed} instances={r.Instances.Count} {r.Note}");
        if (g.HasCode)
        {
            sw.Restart();
            int bad = 0, total = 0;
            foreach (var c in g.Code)
            {
                var list = Bytecode.Decode(g, c);
                total += list.Count;
                foreach (var x in list)
                    if (!Enum.IsDefined(typeof(Op), x.Op)) { bad++; break; }
            }
            sb.AppendLine();
            sb.AppendLine($"-- bytecode: {total:N0} instructions, {bad} entries with unknown opcodes, {sw.ElapsedMilliseconds} ms --");

            sw.Restart();
            int stackLeft = 0, errors = 0, gotos = 0;
            var samples = new System.Collections.Generic.List<string>();
            foreach (var c in g.Code)
            {
                string txt = Decompiler.Decompile(g, c);
                if (txt.Contains("left on the stack")) { stackLeft++; if (samples.Count < 8) samples.Add(c.Name); }
                if (txt.Contains("goto label_")) gotos++;
                if (txt.Contains("decompiler gave up") || txt.Contains("bailed out")) errors++;
            }
            sb.AppendLine($"-- decompiled {g.Code.Count} entries in {sw.ElapsedMilliseconds} ms: " + $"{stackLeft} with stack residue, {gotos} with gotos, {errors} failures --");
            foreach (var s in samples) sb.AppendLine("   residue: " + s);
        }
        {
            int badInst = 0, allInst = 0;
            foreach (var r in g.Rooms)
                foreach (var inst in r.Instances)
                {
                    allInst++;
                    if (inst.ObjectIndex < -1 || inst.ObjectIndex >= g.Objects.Count) badInst++;
                }
            sb.AppendLine($"-- room instances: {allInst} total, {badInst} with an out-of-range object --");
        }

        if (Environment.GetEnvironmentVariable("GMX_FIND") is string find && find.Length > 0)
        {
            int shown = 0;
            foreach (var c in g.Code)
            {
                var list = Bytecode.Decode(g, c);
                for (int k = 0; k < list.Count && shown < 6; k++)
                {
                    var x = list[k];
                    bool hit = find switch
                    {
                        "stacktop" => x.VarKind == VarKind.StackTop && x.Op != Op.Pop,
                        "array" => x.VarKind == VarKind.Array,
                        "pushaf" => x.Op == Op.Break && x.BreakCode == -2,
                        "popaf" => x.Op == Op.Break && x.BreakCode == -3, _ => false
                    };
                    if (!hit) continue;
                    shown++;
                    sb.AppendLine();
                    sb.AppendLine("--- " + find + " in " + c.Name + " ---");
                    for (int j = Math.Max(0, k - 7); j < Math.Min(list.Count, k + 3); j++)
                        sb.AppendLine((j == k ? " >" : "  ") + list[j].Off.ToString("D5") + "  " + Bytecode.Mnemonic(list[j]).PadRight(16) + Bytecode.Operand(g, list[j]));
                }
                if (shown >= 6) break;
            }
        }

        foreach (var name in args.Skip(3))
        {
            sb.AppendLine();
            sb.AppendLine("=========== " + name + " ===========");
            var entry = g.Code.FirstOrDefault(c => c.Name == name) ?? g.Code.FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) { sb.AppendLine("not found"); continue; }
            sb.AppendLine(Decompiler.Decompile(g, entry));
            sb.AppendLine("----------- disassembly -----------");
            sb.AppendLine(Bytecode.Disassemble(g, entry));
        }

        if (!g.HasCode)
        {
            sb.AppendLine();
            sb.AppendLine("-- native code --");
            string? exe = NativeCode.FindExecutable(path);
            sb.AppendLine("executable  : " + (exe ?? "none found next to the data file"));
            sb.AppendLine("analyser    : " + (NativeCode.Available ? (NativeCode.LibraryPath ?? "loaded") : "gmnative.dll not found"));
            if (exe != null && NativeCode.Available)
            {
                sw.Restart();
                var hints = g.Scripts.Select(x => x.Name).Concat(g.Objects.Select(x => x.Name)).Concat(g.Code.Select(x => x.Name));
                using var nc = NativeCode.Open(exe, hints);
                if (nc == null) sb.AppendLine("open failed : " + NativeCode.LastError());
                else
                {
                    sb.AppendLine($"analysed in {sw.ElapsedMilliseconds} ms");
                    sb.AppendLine(nc.Summary);
                    foreach (var f in nc.Functions.Where(f => f.Named).Take(10))
                        sb.AppendLine($"   {f.Name}  at 0x{f.Address:X}  {f.Size} bytes  ({f.Kind})");
                    var sample = nc.Functions.FirstOrDefault(f => f.Named && f.Size > 120) ?? nc.Functions.FirstOrDefault(f => f.Named);
                    if (sample != null)
                    {
                        sb.AppendLine();
                        sb.AppendLine("=========== " + sample.Name + " (pseudo) ===========");
                        sb.AppendLine(nc.Text(sample.Index, true));
                        sb.AppendLine("=========== " + sample.Name + " (asm) ===========");
                        sb.AppendLine(nc.Text(sample.Index, false));
                    }
                }
            }
        }

        try
        {
            var page = g.Pages.FirstOrDefault(p => p.Format != TexFormat.Png && p.Width > 64);
            if (page != null)
            {
                var img = Textures.DecodeQoi(g.Raw, page.DataOffset, page.Format == TexFormat.Bz2Qoi);
                string png = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(outPath) ?? ".", "page.png");
                File.WriteAllBytes(png, Png.Encode(img));
                sb.AppendLine($"\nwrote {png} ({img.Width}x{img.Height})");
            }
        }
        catch (Exception e) { sb.AppendLine("texture export failed: " + e); }

        File.WriteAllText(outPath, sb.ToString());
    }
}