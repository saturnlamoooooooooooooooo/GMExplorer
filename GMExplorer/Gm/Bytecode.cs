using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GMExplorer.Gm;

public enum Op
{
    Conv = 0x07, Mul = 0x08, Div = 0x09, Rem = 0x0A, Mod = 0x0B, Add = 0x0C, Sub = 0x0D,
    And = 0x0E, Or = 0x0F, Xor = 0x10, Neg = 0x11, Not = 0x12, Shl = 0x13, Shr = 0x14,
    Cmp = 0x15, Pop = 0x45, PushI = 0x84, Dup = 0x86, CallV = 0x99, Ret = 0x9C, Exit = 0x9D,
    Popz = 0x9E, B = 0xB6, Bt = 0xB7, Bf = 0xB8, PushEnv = 0xBA, PopEnv = 0xBB,
    Push = 0xC0, PushLoc = 0xC1, PushGlb = 0xC2, PushBltn = 0xC3, Call = 0xD9, Break = 0xFF
}

public enum DT
{
    Double = 0, Float = 1, Int32 = 2, Int64 = 3, Bool = 4, Var = 5, Str = 6,
    Inst = 7, Delete = 8, Undefined = 9, UInt32 = 10, Int16 = 15
}

public enum VarKind { Array = 0x00, StackTop = 0x80, Normal = 0xA0, Instance = 0xE0, ArrayPushRef = 0x10, ArrayPopRef = 0x90, Unknown = 0xFF }

public sealed class Instr
{
    public int Addr;
    public int Off;
    public int Size = 4;
    public Op Op;
    public byte Raw;
    public DT T1, T2;
    public byte Extra;
    public int Cmp;
    public int Jump;
    public bool EnvExit;
    public int Target => Off + Jump;
    public short InstType;
    public VarKind VarKind = VarKind.Unknown;
    public string Name = "";
    public int ArgCount;
    public object? Value;
    public int BreakCode;
    public bool IsBranch => Op is Op.B or Op.Bt or Op.Bf or Op.PushEnv or Op.PopEnv;
    public bool IsConditional => Op is Op.Bt or Op.Bf;
}

public static class Bytecode
{
    public static string InstanceName(GmData g, int t)
    {
        switch (t)
        {
            case -1: return "self";
            case -2: return "other";
            case -3: return "all";
            case -4: return "noone";
            case -5: return "global";
            case -6: return "builtin";
            case -7: return "local";
            case -9: return "stacktop";
            case -11: return "arg";
            case -15: return "arg";
            case -16: return "static";
        }
        if (t >= 0 && t < g.Objects.Count) return g.Objects[t].Name;
        return t.ToString(CultureInfo.InvariantCulture);
    }

    static readonly string[] CmpNames = { "??", "<", "<=", "==", "!=", ">=", ">" };

    public static string CmpName(int k) => k >= 0 && k < CmpNames.Length ? CmpNames[k] : "?";

    static Op Remap14(byte k)
    {
        switch (k)
        {
            case 0x03: return Op.Conv;
            case 0x04: return Op.Mul;
            case 0x05: return Op.Div;
            case 0x06: return Op.Rem;
            case 0x07: return Op.Mod;
            case 0x08: return Op.Add;
            case 0x09: return Op.Sub;
            case 0x0A: return Op.And;
            case 0x0B: return Op.Or;
            case 0x0C: return Op.Xor;
            case 0x0D: return Op.Neg;
            case 0x0E: return Op.Not;
            case 0x0F: return Op.Shl;
            case 0x10: return Op.Shr;
            case 0x11: case 0x12: case 0x13: case 0x14: case 0x15: case 0x16: return Op.Cmp;
            case 0x41: return Op.Pop;
            case 0x82: return Op.Dup;
            case 0x99: return Op.Ret;
            case 0x9A: return Op.Exit;
            case 0x9B: return Op.Popz;
            case 0xB7: return Op.B;
            case 0xB8: return Op.Bt;
            case 0xB9: return Op.Bf;
            case 0xBB: return Op.PushEnv;
            case 0xBC: return Op.PopEnv;
            case 0xDA: return Op.Call;
            default: return (Op)k;
        }
    }

    public static int OwnLength(GmData g, GmCode code)
    {
        if (code.Offset == 0) return Math.Max(0, code.Length);

        int end = code.Length;
        int blobStart = code.Address - code.Offset;
        if (blobStart > 0 && blobStart + 4 <= g.Raw.Length && g.Raw[blobStart + 3] == (byte)Op.B)
        {
            int v = g.Raw[blobStart] | (g.Raw[blobStart + 1] << 8) | (g.Raw[blobStart + 2] << 16);
            if ((v & 0x800000) != 0) v -= 0x1000000;
            int tail = v * 4;
            if (tail > code.Offset && tail < end) end = tail;
        }
        if (code.ParentIndex >= 0)
        {
            foreach (var c in g.Code)
                if (c.ParentIndex == code.ParentIndex && c.Offset > code.Offset && c.Offset < end) end = c.Offset;
        }
        return Math.Max(0, end - code.Offset);
    }

    public static List<Instr> Decode(GmData g, GmCode code)
    {
        var list = new List<Instr>();
        var r = g.R;
        int start = code.Address;
        int end = start + OwnLength(g, code);
        if (start <= 0 || end > g.Raw.Length) return list;
        bool old = g.BytecodeVersion <= 14;

        int p = start;
        while (p + 4 <= end)
        {
            var ins = new Instr { Addr = p, Off = p - start };
            byte b0 = g.Raw[p], b1 = g.Raw[p + 1], b2 = g.Raw[p + 2], kind = g.Raw[p + 3];
            ins.Raw = kind;
            ins.Op = old ? Remap14(kind) : (Op)kind;
            ins.T1 = (DT)(b2 & 0x0F);
            ins.T2 = (DT)(b2 >> 4);
            ins.Extra = b0;

            switch (ins.Op)
            {
                case Op.B:
                case Op.Bt:
                case Op.Bf:
                case Op.PushEnv:
                case Op.PopEnv:
                    {
                        int v = b0 | (b1 << 8) | (b2 << 16);
                        ins.EnvExit = (v & 0x800000) != 0;
                        int j = v & 0x7FFFFF;
                        if ((j & 0x400000) != 0) j -= 0x800000;
                        ins.Jump = j * 4;
                        break;
                    }

                case Op.Cmp:
                    ins.Cmp = old ? kind - 0x10 : b1;
                    break;

                case Op.Push:
                case Op.PushLoc:
                case Op.PushGlb:
                case Op.PushBltn:
                case Op.PushI:
                    {
                        ins.InstType = (short)(b0 | (b1 << 8));
                        switch (ins.T1)
                        {
                            case DT.Int16: ins.Value = (int)(short)(b0 | (b1 << 8)); break;
                            case DT.Double: ins.Value = BitConverter.ToDouble(g.Raw, p + 4); ins.Size = 12; break;
                            case DT.Float: ins.Value = BitConverter.ToSingle(g.Raw, p + 4); ins.Size = 8; break;
                            case DT.Int64: ins.Value = BitConverter.ToInt64(g.Raw, p + 4); ins.Size = 12; break;
                            case DT.Bool: ins.Value = r.At(p + 4) != 0; ins.Size = 8; break;
                            case DT.Str: ins.Value = g.StrIndex(r.At(p + 4)); ins.Size = 8; break;
                            case DT.Int32:
                                ins.Size = 8;
                                if (g.RefNames.TryGetValue(p + 4, out var fname) && g.FuncRefs.Contains(p + 4))
                                {
                                    ins.Name = fname;
                                    ins.Value = null;
                                }
                                else ins.Value = r.At(p + 4);
                                break;
                            case DT.Var:
                                ins.Size = 8;
                                ReadVarRef(g, ins, p + 4);
                                break;
                            default:
                                ins.Size = 8;
                                ins.Value = r.At(p + 4);
                                break;
                        }
                        break;
                    }

                case Op.Pop:
                    if (ins.T1 == DT.Int16)
                    {
                        ins.Size = 4;
                    }
                    else
                    {
                        ins.InstType = (short)(b0 | (b1 << 8));
                        ins.Size = 8;
                        ReadVarRef(g, ins, p + 4);
                    }
                    break;

                case Op.Call:
                    ins.ArgCount = (short)(b0 | (b1 << 8));
                    ins.Size = 8;
                    g.RefNames.TryGetValue(p + 4, out var cname);
                    ins.Name = cname ?? ("<function " + r.At(p + 4) + ">");
                    break;

                case Op.CallV:
                    ins.ArgCount = b0;
                    break;

                case Op.Break:
                    ins.BreakCode = (short)(b0 | (b1 << 8));
                    if (ins.T1 == DT.Int32) ins.Size = 8;
                    break;
            }

            list.Add(ins);
            p += ins.Size;
        }
        return list;
    }

    static void ReadVarRef(GmData g, Instr ins, int operand)
    {
        uint v = g.R.AtU(operand);
        ins.VarKind = (VarKind)((v >> 24) & 0xF8);
        if (!Enum.IsDefined(typeof(VarKind), ins.VarKind)) ins.VarKind = VarKind.Unknown;
        ins.Name = g.RefNames.TryGetValue(operand, out var n) ? n : "<var>";
    }

    public static string Literal(object? v)
    {
        switch (v)
        {
            case null: return "undefined";
            case string s: return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
            case bool b: return b ? "true" : "false";
            case double d: return FormatNumber(d);
            case float f: return FormatNumber(f);
            default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        }
    }

    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsInfinity(d)) return d > 0 ? "infinity" : "-infinity";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string Disassemble(GmData g, GmCode code)
    {
        var sb = new StringBuilder();
        var instrs = Decode(g, code);
        sb.Append("// ").Append(code.Name).Append('\n');
        sb.Append("// ").Append(instrs.Count).Append(" instructions, ").Append(code.Length).Append(" bytes, ").Append(code.Locals).Append(" locals, ").Append(code.Args).Append(" arguments\n\n");

        var targets = new HashSet<int>();
        foreach (var i in instrs) if (i.IsBranch) targets.Add(i.Target);

        foreach (var ins in instrs)
        {
            if (targets.Contains(ins.Off)) sb.Append("\nlabel_").Append(ins.Off).Append(":\n");
            sb.Append(ins.Off.ToString("D5", CultureInfo.InvariantCulture)).Append("  ");
            sb.Append(Mnemonic(ins).PadRight(18));
            sb.Append(Operand(g, ins));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string TypeSuffix(DT t) => t switch
    {
        DT.Double => "d",
        DT.Float => "f",
        DT.Int32 => "i",
        DT.Int64 => "l",
        DT.Bool => "b",
        DT.Var => "v",
        DT.Str => "s",
        DT.Inst => "e",
        DT.Int16 => "i16",
        DT.Delete => "del",
        DT.Undefined => "undef",
        DT.UInt32 => "u",
        _ => ((int)t).ToString()
    };

    public static string Mnemonic(Instr ins)
    {
        string name = ins.Op switch
        {
            Op.Conv => "conv", Op.Mul => "mul", Op.Div => "div", Op.Rem => "rem", Op.Mod => "mod",
            Op.Add => "add", Op.Sub => "sub", Op.And => "and", Op.Or => "or", Op.Xor => "xor",
            Op.Neg => "neg", Op.Not => "not", Op.Shl => "shl", Op.Shr => "shr", Op.Cmp => "cmp",
            Op.Pop => "pop", Op.PushI => "pushi", Op.Dup => "dup", Op.CallV => "callv", Op.Ret => "ret",
            Op.Exit => "exit", Op.Popz => "popz", Op.B => "b", Op.Bt => "bt", Op.Bf => "bf",
            Op.PushEnv => "pushenv", Op.PopEnv => "popenv", Op.Push => "push", Op.PushLoc => "pushloc",
            Op.PushGlb => "pushglb", Op.PushBltn => "pushbltn", Op.Call => "call", Op.Break => "break",
            _ => "0x" + ins.Raw.ToString("X2")
        };
        switch (ins.Op)
        {
            case Op.Push or Op.PushLoc or Op.PushGlb or Op.PushBltn or Op.PushI:
                return name + "." + TypeSuffix(ins.T1);
            case Op.B or Op.Bt or Op.Bf or Op.PushEnv or Op.PopEnv or Op.Break or Op.CallV:
                return name;
            case Op.Call:
                return name + "." + TypeSuffix(ins.T1);
            default:
                return name + "." + TypeSuffix(ins.T1) + "." + TypeSuffix(ins.T2);
        }
    }

    public static string Operand(GmData g, Instr ins)
    {
        switch (ins.Op)
        {
            case Op.B or Op.Bt or Op.Bf or Op.PushEnv or Op.PopEnv:
                return ins.EnvExit ? "<leave with>" : "label_" + ins.Target;
            case Op.Cmp:
                return CmpName(ins.Cmp);
            case Op.Call:
                return ins.Name + " (" + ins.ArgCount + " args)";
            case Op.CallV:
                return ins.Extra + " args";
            case Op.Dup:
                return ins.Extra.ToString(CultureInfo.InvariantCulture);
            case Op.Break:
                return BreakName(ins.BreakCode);
            case Op.Pop:
                if (ins.T1 == DT.Int16) return "(swap " + ins.Extra + ")";
                return VarText(g, ins);
            case Op.Push or Op.PushLoc or Op.PushGlb or Op.PushBltn or Op.PushI:
                if (ins.T1 == DT.Var) return VarText(g, ins);
                if (ins.T1 == DT.Int32 && ins.Name.Length > 0) return "[function] " + ins.Name;
                return Literal(ins.Value);
            default:
                return "";
        }
    }

    static string VarText(GmData g, Instr ins)
    {
        string prefix = ins.VarKind == VarKind.Normal ? InstanceName(g, ins.InstType) : "";
        string kind = ins.VarKind switch
        {
            VarKind.Array => " [array]",
            VarKind.StackTop => " [stacktop]",
            VarKind.Instance => " [instance]",
            VarKind.ArrayPushRef => " [arrayref]",
            VarKind.ArrayPopRef => " [arrayref]",
            _ => ""
        };
        return (prefix.Length > 0 ? prefix + "." : "") + ins.Name + kind;
    }

    public static string BreakName(int code) => code switch
    {
        -1 => "chkindex",
        -2 => "pushaf",
        -3 => "popaf",
        -4 => "pushac",
        -5 => "setowner",
        -6 => "isstaticok",
        -7 => "setstatic",
        -8 => "savearef",
        -9 => "restorearef",
        -10 => "chknullish",
        -11 => "pushref", _ => code.ToString(CultureInfo.InvariantCulture)
    };
}