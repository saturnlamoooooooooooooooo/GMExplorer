using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GMExplorer.Gm;

public enum FileKind { Data, AudioGroup, Bank, Library, Executable, Image, Text, Video, Audio, Embedded, Other }

public sealed class PackageFile
{
    public string Name = "";
    public string RelPath = "";
    public string FullPath = "";
    public long Size;
    public FileKind Kind;
    public string Detail = "";

    public byte[]? Bytes;
    public int SourceOffset;

    public bool IsEmbedded => Bytes != null;

    public byte[] Read()
    {
        if (Bytes != null) return Bytes;
        return File.ReadAllBytes(FullPath);
    }

    public string KindName => Kind switch
    {
        FileKind.Data => "game data",
        FileKind.AudioGroup => "audio group",
        FileKind.Bank => "FMOD bank",
        FileKind.Library => "library",
        FileKind.Executable => "executable",
        FileKind.Image => "image",
        FileKind.Text => "text",
        FileKind.Video => "video",
        FileKind.Audio => "audio",
        FileKind.Embedded => "embedded", _ => "file"
    };
}

public sealed class GamePackage
{
    public string Root = "";
    public string? ExePath;
    public string? DataPath;
    public byte[]? EmbeddedData;
    public PeFile? Pe;
    public List<PackageFile> Files = new();
    public List<string> Notes = new();

    public string Title => ExePath != null ? Path.GetFileNameWithoutExtension(ExePath) : Path.GetFileName(Root);

    public static GamePackage Discover(string droppedPath, Action<string>? progress = null)
    {
        var pkg = new GamePackage();
        string full = Path.GetFullPath(droppedPath);
        pkg.Root = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? ".";

        bool isExe = !Directory.Exists(full) && LooksExecutable(full);
        if (isExe)
        {
            pkg.ExePath = full;
            progress?.Invoke("Reading " + Path.GetFileName(full) + "...");
            pkg.Pe = PeFile.Read(full);
            pkg.DataPath = FindDataFile(pkg.Root, full);
            if (pkg.DataPath == null && pkg.Pe != null)
            {
                var embedded = pkg.Pe.FindEmbeddedData();
                if (embedded != null)
                {
                    var (off, size) = embedded.Value;
                    pkg.EmbeddedData = new byte[size];
                    Buffer.BlockCopy(pkg.Pe.Raw, off, pkg.EmbeddedData, 0, size);
                    pkg.Notes.Add($"The game archive is embedded in {Path.GetFileName(full)} at offset 0x{off:X} ({size:N0} bytes).");
                }
            }
        }
        else if (!Directory.Exists(full))
        {
            pkg.DataPath = full;
            pkg.ExePath = NativeCode.FindExecutable(full);
            if (pkg.ExePath != null) pkg.Pe = PeFile.Read(pkg.ExePath);
        }
        else
        {
            pkg.DataPath = FindDataFile(pkg.Root, null);
            pkg.ExePath = Directory.GetFiles(pkg.Root, "*.exe").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (pkg.ExePath != null) pkg.Pe = PeFile.Read(pkg.ExePath);
        }

        progress?.Invoke("Scanning the game folder...");
        pkg.ScanFolder();
        pkg.ScanEmbedded();
        return pkg;
    }

    static bool LooksExecutable(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".exe" || ext == ".dll") return true;
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    public static string? FindDataFile(string dir, string? exePath)
    {
        string[] names = { "data.win", "game.unx", "data.unx", "game.ios", "game.droid", "data.ios" };
        foreach (var n in names)
        {
            string p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }

        if (exePath != null)
        {
            string guess = Path.Combine(dir, Path.GetFileNameWithoutExtension(exePath) + ".win");
            if (File.Exists(guess)) return guess;
        }
        var any = Directory.GetFiles(dir, "*.win");
        return any.Length > 0 ? any[0] : null;
    }

    void ScanFolder()
    {
        if (!Directory.Exists(Root)) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int budget = 4000;

        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (budget-- <= 0) { Notes.Add("Only the first 4000 files in the folder are listed."); break; }
            if (!seen.Add(path)) continue;
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }

            string rel = Path.GetRelativePath(Root, path);
            var kind = Classify(path);
            var file = new PackageFile
            {
                Name = Path.GetFileName(path),
                RelPath = rel,
                FullPath = path,
                Size = fi.Length,
                Kind = kind
            };

            if (kind == FileKind.Bank) file.Detail = "FMOD Studio bank";
            else if (kind == FileKind.AudioGroup) file.Detail = "external audio group";
            else if (kind == FileKind.Data) file.Detail = "GameMaker archive";
            else if (kind == FileKind.Library || kind == FileKind.Executable)
            {
                var head = Head(path, 0x400);
                var pe = head.Length > 64 ? PeFile.From(head, path) : null;
                file.Detail = pe == null ? "" : (pe.Is64 ? "x64" : "x86") + (pe.IsDll ? " library" : " program");
            }
            Files.Add(file);
        }

        Files.Sort((a, b) =>
        {
            int ka = Order(a.Kind), kb = Order(b.Kind);
            if (ka != kb) return ka.CompareTo(kb);
            return string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    static byte[] Head(string path, int n)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var b = new byte[Math.Min(n, fs.Length)];
            fs.ReadExactly(b, 0, b.Length);
            return b;
        }
        catch { return Array.Empty<byte>(); }
    }

    static int Order(FileKind k) => k switch
    {
        FileKind.Data => 0,
        FileKind.AudioGroup => 1,
        FileKind.Bank => 2,
        FileKind.Executable => 3,
        FileKind.Library => 4,
        FileKind.Embedded => 5,
        _ => 6
    };

    static FileKind Classify(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".win" || ext == ".unx" || ext == ".ios" || ext == ".droid") return FileKind.Data;
        if (name.StartsWith("audiogroup") && ext == ".dat") return FileKind.AudioGroup;
        if (ext == ".bank") return FileKind.Bank;
        if (ext == ".dll") return FileKind.Library;
        if (ext == ".exe") return FileKind.Executable;
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico") return FileKind.Image;
        if (ext is ".txt" or ".ini" or ".json" or ".xml" or ".yy" or ".yyp" or ".csv" or ".bat" or ".cfg" or ".log" or ".md") return FileKind.Text;
        if (ext is ".mp4" or ".webm" or ".avi" or ".mov") return FileKind.Video;
        if (ext is ".ogg" or ".wav" or ".mp3" or ".flac") return FileKind.Audio;
        return FileKind.Other;
    }

    void ScanEmbedded()
    {
        if (Pe == null) return;
        string exe = Path.GetFileName(ExePath ?? "executable");

        foreach (var r in Pe.Resources)
        {
            if (r.Size < 64) continue;
            Files.Add(new PackageFile
            {
                Name = r.Type + " / " + r.Name,
                RelPath = exe + " : " + r.Type + " / " + r.Name,
                FullPath = ExePath ?? "",
                Size = r.Size,
                Kind = FileKind.Embedded,
                Detail = r.Detail.Length > 0 ? r.Detail : "resource",
                Bytes = Pe.Bytes(r),
                SourceOffset = r.Offset
            });
        }

        if (Pe.OverlaySize > 64)
        {
            var bytes = new byte[Pe.OverlaySize];
            Buffer.BlockCopy(Pe.Raw, Pe.OverlayOffset, bytes, 0, Pe.OverlaySize);
            Files.Add(new PackageFile
            {
                Name = "appended data",
                RelPath = exe + " : overlay",
                FullPath = ExePath ?? "",
                Size = Pe.OverlaySize,
                Kind = FileKind.Embedded,
                Detail = PeFile.Sniff(Pe.Raw, Pe.OverlayOffset, Pe.OverlaySize) is { Length: > 0 } s ? s : "data after the last section",
                Bytes = bytes,
                SourceOffset = Pe.OverlayOffset
            });
        }
    }

    public GmData LoadData(Action<string>? progress = null)
    {
        if (EmbeddedData != null)
            return GmData.LoadFrom(EmbeddedData, ExePath ?? Path.Combine(Root, "data.win"), progress);
        if (DataPath == null)
            throw new FileNotFoundException("No GameMaker data file was found next to " + (ExePath ?? Root) + ".");
        return GmData.Load(DataPath, progress);
    }
}