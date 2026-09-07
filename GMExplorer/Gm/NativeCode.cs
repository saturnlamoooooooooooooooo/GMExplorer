using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GMExplorer.Gm;

public sealed class NativeFunc
{
    public int Index;
    public string Name = "";
    public uint Rva;
    public uint Size;
    public ulong Address;
    public bool Named => Name.Length > 0;

    public string ShortName
    {
        get
        {
            foreach (var p in new[] { "gml_Script_", "gml_GlobalScript_", "gml_Object_", "gml_Room_" })
                if (Name.StartsWith(p, StringComparison.Ordinal)) return Name.Substring(p.Length);
            return Name;
        }
    }

    public string Kind
    {
        get
        {
            if (Name.StartsWith("gml_Object_", StringComparison.Ordinal)) return "event";
            if (Name.StartsWith("gml_Script_", StringComparison.Ordinal)) return "script";
            if (Name.StartsWith("gml_GlobalScript_", StringComparison.Ordinal)) return "global script";
            if (Name.StartsWith("gml_Room_", StringComparison.Ordinal)) return "room";
            if (Name.Length == 0) return "unnamed";
            return "runtime";
        }
    }
}

public sealed class NativeCode : IDisposable
{
    const string Lib = "gmnative";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr gmn_open([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void gmn_close(IntPtr img);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr gmn_error();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int gmn_add_hint(IntPtr img, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int gmn_analyze(IntPtr img);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int gmn_is64(IntPtr img);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern ulong gmn_image_base(IntPtr img);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int gmn_func_count(IntPtr img);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern int gmn_named_count(IntPtr img);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern uint gmn_func_rva(IntPtr img, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern uint gmn_func_size(IntPtr img, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr gmn_func_name(IntPtr img, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr gmn_text(IntPtr img, int i, int mode);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr gmn_summary(IntPtr img);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    static extern void gmn_free(IntPtr p);

    static bool resolverInstalled;
    static string? libraryPath;

    static void InstallResolver()
    {
        if (resolverInstalled) return;
        resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(NativeCode).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var candidate in Candidates())
            {
                if (!File.Exists(candidate)) continue;
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    libraryPath = candidate;
                    return handle;
                }
            }
            // Single-file builds carry the analyser inside the executable.
            var unpacked = Unpack();
            if (unpacked != null && NativeLibrary.TryLoad(unpacked, out var embedded))
            {
                libraryPath = unpacked;
                return embedded;
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>Writes the embedded copy of the analyser to a temporary file, once per version.</summary>
    static string? Unpack()
    {
        try
        {
            var asm = typeof(NativeCode).Assembly;
            using var src = asm.GetManifestResourceStream("gmnative.dll");
            if (src == null) return null;

            string dir = Path.Combine(Path.GetTempPath(), "GMExplorer",
                                      asm.GetName().Version?.ToString() ?? "native");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "gmnative.dll");

            if (!File.Exists(target) || new FileInfo(target).Length != src.Length)
            {
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
            return target;
        }
        catch { return null; }        // already loaded by another instance, or no write access
    }

    static IEnumerable<string> Candidates()
    {
        // AppContext.BaseDirectory, not Assembly.Location: the latter is empty in single-file builds.
        string appDir = AppContext.BaseDirectory;
        yield return Path.Combine(appDir, "gmnative.dll");
        var dir = new DirectoryInfo(appDir);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "native", "bin", "x64", "Release", "gmnative.dll");
            yield return Path.Combine(dir.FullName, "native", "bin", "x64", "Debug", "gmnative.dll");
        }
    }

    public static bool Available
    {
        get
        {
            InstallResolver();
            try
            {
                var probe = gmn_error();
                return probe != IntPtr.Zero || libraryPath != null;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
        }
    }

    public static string? LibraryPath => libraryPath;

    IntPtr handle;
    public string ExePath { get; private set; } = "";
    public bool Is64 { get; private set; }
    public ulong ImageBase { get; private set; }
    public List<NativeFunc> Functions { get; } = new();
    public int NamedCount { get; private set; }
    public string Summary { get; private set; } = "";

    static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    public static string LastError() => Utf8(gmn_error());

    public static NativeCode? Open(string exePath, IEnumerable<string>? hints = null, Action<string>? progress = null)
    {
        InstallResolver();
        var img = gmn_open(exePath);
        if (img == IntPtr.Zero) return null;

        var nc = new NativeCode { handle = img, ExePath = exePath };
        if (hints != null)
        {
            int n = 0;
            foreach (var h in hints)
            {
                if (string.IsNullOrEmpty(h)) continue;
                gmn_add_hint(img, h);
                if (++n >= 60000) break;
            }
        }

        progress?.Invoke("Analysing native code...");
        int count = gmn_analyze(img);
        if (count < 0) { nc.Dispose(); return null; }

        nc.Is64 = gmn_is64(img) != 0;
        nc.ImageBase = gmn_image_base(img);
        nc.NamedCount = gmn_named_count(img);
        for (int i = 0; i < count; i++)
        {
            uint rva = gmn_func_rva(img, i);
            nc.Functions.Add(new NativeFunc
            {
                Index = i,
                Rva = rva,
                Size = gmn_func_size(img, i),
                Name = Utf8(gmn_func_name(img, i)),
                Address = nc.ImageBase + rva
            });
        }

        var sum = gmn_summary(img);
        nc.Summary = Utf8(sum);
        gmn_free(sum);
        return nc;
    }

    public string Text(int index, bool pseudo)
    {
        if (handle == IntPtr.Zero) return "";
        var p = gmn_text(handle, index, pseudo ? 1 : 0);
        if (p == IntPtr.Zero) return "// " + LastError();
        string s = Utf8(p);
        gmn_free(p);
        return s;
    }

    public static string? FindExecutable(string dataPath)
    {
        try
        {
            string dir = Path.GetDirectoryName(dataPath) ?? ".";
            var best = new DirectoryInfo(dir).GetFiles("*.exe")
                .OrderByDescending(f => f.Length)
                .FirstOrDefault();
            return best?.FullName;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            gmn_close(handle);
            handle = IntPtr.Zero;
        }
    }
}