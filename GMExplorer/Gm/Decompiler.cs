using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GMExplorer.Gm;

public sealed class Decompiler
{
    const int PTernary = 1, POr = 2, PAnd = 3, PBitOr = 4, PBitXor = 5, PBitAnd = 6, PCmp = 7, PShift = 8, PAdd = 9, PMul = 10, PUnary = 11, PPrimary = 12;

    struct Ex
    {
        public string T;
        public int P;
        public bool IsCall;
        public string? Func;
        public Ex(string t, int p = PPrimary, bool call = false) { T = t; P = p; IsCall = call; Func = null; }
        public override string ToString() => T;
    }

    readonly GmData g;
    readonly GmCode code;
    readonly List<Instr> ins;
    readonly Dictionary<int, int> idxByOff = new();
    readonly Dictionary<int, int> loopEnd = new();
    readonly HashSet<int> gotoTargets = new();

    List<Ex> stack = new();
    List<string> outp = new();
    int indent;
    int breakIdx = -1, contIdx = -1;
    bool labelPass;
    int guard;

    public static string Decompile(GmData g, GmCode code)
    {
        try
        {
            var d = new Decompiler(g, code, new HashSet<string>(StringComparer.Ordinal) { code.Name }, 0);
            return d.Run();
        }
        catch (Exception e)
        {
            return "// The decompiler gave up on this entry: " + e.Message + "\n// Falling back to the disassembly.\n\n" + Bytecode.Disassemble(g, code);
        }
    }

    readonly HashSet<string> visited;
    readonly int depth;

    Decompiler(GmData g, GmCode code, HashSet<string> visited, int depth)
    {
        this.g = g;
        this.code = code;
        this.visited = visited;
        this.depth = depth;
        ins = Bytecode.Decode(g, code);
        for (int i = 0; i < ins.Count; i++) idxByOff[ins[i].Off] = i;
        if (ins.Count > 0) idxByOff[ins[^1].Off + ins[^1].Size] = ins.Count;
        for (int i = 0; i < ins.Count; i++)
        {
            var b = ins[i];
            if ((b.Op == Op.B || b.Op == Op.Bt || b.Op == Op.Bf) && b.Jump < 0)
            {
                if (idxByOff.TryGetValue(b.Target, out int h) && h <= i)
                    loopEnd[h] = Math.Max(loopEnd.TryGetValue(h, out int prev) ? prev : -1, i);
            }
        }
    }

    string Run()
    {
        if (ins.Count == 0)
            return "// " + code.Name + "\n// (empty)\n";

        TwoPass();

        var sb = new StringBuilder();
        sb.Append("// ").Append(code.Name).Append('\n');
        if (code.Args > 0 || code.Locals > 0)
            sb.Append("// ").Append(code.Args).Append(" argument(s), ").Append(code.Locals).Append(" local(s)\n");
        sb.Append('\n');
        foreach (var l in outp) sb.Append(l).Append('\n');
        if (stack.Count > 0)
            sb.Append("\n// note: ").Append(stack.Count).Append(" value(s) left on the stack - the output above may be incomplete.\n");
        return sb.ToString();
    }

    void TwoPass()
    {
        labelPass = true;
        Reset();
        Body(0, ins.Count);
        labelPass = false;
        Reset();
        Body(0, ins.Count);
    }

    List<string>? InlineBody(GmCode target)
    {
        if (depth >= 3 || visited.Contains(target.Name)) return null;
        var nested = new HashSet<string>(visited, StringComparer.Ordinal) { target.Name };
        var d = new Decompiler(g, target, nested, depth + 1);
        if (d.ins.Count == 0) return null;
        d.TwoPass();
        return d.outp;
    }

    void Reset()
    {
        stack = new List<Ex>();
        outp = new List<string>();
        indent = 0;
        breakIdx = contIdx = -1;
        guard = 0;
    }

    void Line(string s) => outp.Add(new string(' ', indent * 4) + s);

    void Open(string s) { Line(s); Line("{"); indent++; }

    void Close() { indent--; Line("}"); }

    void Push(Ex e) => stack.Add(e);

    void Push(string t, int p = PPrimary, bool call = false) => stack.Add(new Ex(t, p, call));


    Ex Pop()
    {
        if (stack.Count == 0) return new Ex("/* stack underflow */", PPrimary);
        var e = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return e;
    }

    static string Wrap(Ex e, int need) => e.P < need ? "(" + e.T + ")" : e.T;

    void Body(int from, int to)
    {
        int i = from;
        while (i < to)
        {
            if (++guard > 1000000) { Line("// decompiler bailed out (too many steps)"); return; }

            var cur = ins[i];
            if (!labelPass && gotoTargets.Contains(cur.Off))
            {
                int save = indent;
                indent = 0;
                Line("label_" + cur.Off + ":");
                indent = save;
            }

            if (loopEnd.TryGetValue(i, out int back) && back < to && back >= i)
            {
                Loop(i, back, to);
                i = back + 1;
                continue;
            }

            switch (cur.Op)
            {
                case Op.Bt:
                case Op.Bf:
                    i = If(i, to);
                    continue;

                case Op.B:
                    if (cur.EnvExit) { Line("break;"); i++; continue; }
                    i = Jump(i, to);
                    continue;

                case Op.PushEnv:
                    i = With(i, to);
                    continue;

                case Op.Dup:
                    if (TrySwitch(ref i, to)) continue;
                    Step(cur, i, to);
                    i++;
                    continue;

                default:
                    Step(cur, i, to);
                    i++;
                    continue;
            }
        }
    }

    int Idx(int off) => idxByOff.TryGetValue(off, out int i) ? i : -1;

    int If(int i, int to)
    {
        var br = ins[i];
        var cond = Pop();
        int t = Idx(br.Target);
        string taken = br.Op == Op.Bt ? cond.T : Negate(cond);

        if (t >= 0 && t == breakIdx) { Open("if (" + taken + ")"); Line("break;"); Close(); return i + 1; }
        if (t >= 0 && t == contIdx) { Open("if (" + taken + ")"); Line("continue;"); Close(); return i + 1; }
        if (t < 0 || t <= i || t > to)
        {
            if (labelPass && t >= 0) gotoTargets.Add(br.Target);
            Open("if (" + taken + ")");
            Line("goto label_" + br.Target + ";");
            Close();
            return i + 1;
        }

        int thenEnd = t;
        int elseEnd = -1;
        if (t - 1 > i && ins[t - 1].Op == Op.B && ins[t - 1].Jump > 0)
        {
            int e = Idx(ins[t - 1].Target);
            bool isJumpOut = e == breakIdx || e == contIdx;
            if (!isJumpOut && e > t && e <= to) { thenEnd = t - 1; elseEnd = e; }
        }

        var baseStack = new List<Ex>(stack);

        bool probe = elseEnd >= 0 && thenEnd - i <= 40 && elseEnd - t <= 40;
        List<Ex>? afterThen = null, afterElse = null;
        bool thenIsValue = false, elseIsValue = false;
        if (probe)
        {
            var thenLines = Capture(i + 1, thenEnd);
            afterThen = new List<Ex>(stack);
            stack = new List<Ex>(baseStack);
            var elseLines = Capture(t, elseEnd);
            afterElse = new List<Ex>(stack);
            thenIsValue = thenLines.Count == 0 && afterThen.Count == baseStack.Count + 1;
            elseIsValue = elseLines.Count == 0 && afterElse.Count == baseStack.Count + 1;
        }

        if (thenIsValue && elseIsValue)
        {
            var a = afterThen![^1];
            var b = afterElse![^1];
            stack = new List<Ex>(baseStack);
            if (br.Op == Op.Bf)
            {
                if (IsZero(b)) Push(Wrap(cond, PAnd) + " && " + Wrap(a, PAnd), PAnd);
                else Push(Wrap(cond, PTernary + 1) + " ? " + Wrap(a, PTernary + 1) + " : " + Wrap(b, PTernary), PTernary);
            }
            else
            {
                if (IsOne(b)) Push(Wrap(cond, POr) + " || " + Wrap(a, POr), POr);
                else Push(Wrap(cond, PTernary + 1) + " ? " + Wrap(b, PTernary + 1) + " : " + Wrap(a, PTernary), PTernary);
            }
            return elseEnd;
        }

        stack = new List<Ex>(baseStack);
        string condText = br.Op == Op.Bf ? cond.T : Negate(cond);
        Open("if (" + condText + ")");
        Body(i + 1, thenEnd);
        Close();
        if (elseEnd >= 0)
        {
            stack = new List<Ex>(baseStack);
            Open("else");
            Body(t, elseEnd);
            Close();
            stack = new List<Ex>(baseStack);
            return elseEnd;
        }
        stack = new List<Ex>(baseStack);
        return t;
    }

    static bool IsZero(Ex e) => e.T == "0" || e.T == "false";
    static bool IsOne(Ex e) => e.T == "1" || e.T == "true";

    string Negate(Ex e)
    {
        string t = e.T;
        if (t.StartsWith("!(", StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal)) return t.Substring(2, t.Length - 3);
        foreach (var (op, inv) in new[] { (" == ", " != "), (" != ", " == "), (" <= ", " > "), (" >= ", " < ") })
        {
            if (e.P == PCmp && t.Contains(op, StringComparison.Ordinal))
                return ReplaceFirst(t, op, inv);
        }
        if (e.P == PCmp)
        {
            if (t.Contains(" < ", StringComparison.Ordinal)) return ReplaceFirst(t, " < ", " >= ");
            if (t.Contains(" > ", StringComparison.Ordinal)) return ReplaceFirst(t, " > ", " <= ");
        }
        return "!" + Wrap(e, PUnary);
    }

    static string ReplaceFirst(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal);
        return i < 0 ? s : s.Substring(0, i) + b + s.Substring(i + a.Length);
    }

    List<string> Capture(int from, int to)
    {
        var savedOut = outp;
        var savedIndent = indent;
        var produced = new List<string>();
        outp = produced;
        indent = 0;
        try { Body(from, to); }
        finally
        {
            outp = savedOut;
            indent = savedIndent;
        }
        return produced;
    }

    int Jump(int i, int to)
    {
        var br = ins[i];
        int t = Idx(br.Target);
        if (t >= 0 && t == breakIdx) { Line("break;"); return i + 1; }
        if (t >= 0 && t == contIdx) { Line("continue;"); return i + 1; }
        if (t > i && t <= to)
        {
            return t;
        }
        if (labelPass) gotoTargets.Add(br.Target);
        Line("goto label_" + br.Target + ";");
        return i + 1;
    }

    void Loop(int head, int back, int to)
    {
        var b = ins[back];
        int afterOff = ins[back].Off + ins[back].Size;
        int exitIdx = back + 1;
        int savedBreak = breakIdx, savedCont = contIdx;

        if (b.Op == Op.B)
        {
            int k = -1;
            for (int j = head; j < back; j++)
                if (ins[j].IsConditional && ins[j].Target == afterOff) { k = j; break; }

            if (k >= 0)
            {
                var before = Capture(head, k);
                var cond = Pop();
                string text = ins[k].Op == Op.Bf ? cond.T : Negate(cond);
                if (before.Count == 0)
                {
                    breakIdx = exitIdx; contIdx = head;
                    Open("while (" + text + ")");
                    Body(k + 1, back);
                    Close();
                    breakIdx = savedBreak; contIdx = savedCont;
                    return;
                }
                breakIdx = exitIdx; contIdx = head;
                Open("while (true)");
                Body(head, k);
                var c2 = Pop();
                Line("if (" + (ins[k].Op == Op.Bf ? Negate(c2) : c2.T) + ")");
                Line("    break;");
                Body(k + 1, back);
                Close();
                breakIdx = savedBreak; contIdx = savedCont;
                return;
            }

            breakIdx = exitIdx; contIdx = head;
            Open("while (true)");
            Body(head, back);
            Close();
            breakIdx = savedBreak; contIdx = savedCont;
            return;
        }

        breakIdx = exitIdx; contIdx = head;
        Open("do");
        Body(head, back);
        var uc = Pop();
        indent--;
        Line("}");
        Line("until (" + (b.Op == Op.Bf ? uc.T : Negate(uc)) + ");");
        breakIdx = savedBreak; contIdx = savedCont;
    }

    int With(int i, int to)
    {
        var target = Pop();
        int t = Idx(ins[i].Target);
        if (t < 0 || t > to) { Line("// unhandled pushenv"); return i + 1; }
        int end = t;
        if (end < ins.Count && ins[end].Op == Op.PopEnv)
        {
            Open("with (" + target.T + ")");
            Body(i + 1, end);
            Close();
            return end + 1;
        }
        Open("with (" + target.T + ")");
        Body(i + 1, Math.Min(end, to));
        Close();
        return Math.Min(end, to);
    }

    bool TrySwitch(ref int i, int to)
    {
        var cases = new List<(string Value, int Target)>();
        int j = i;
        while (j + 3 < to && ins[j].Op == Op.Dup)
        {
            int k = j + 1;
            var probe = new List<Instr>();
            while (k < to && ins[k].Op != Op.Cmp && probe.Count < 6) { probe.Add(ins[k]); k++; }
            if (k >= to || ins[k].Op != Op.Cmp || ins[k].Cmp != 3) break;
            if (k + 1 >= to || ins[k + 1].Op != Op.Bt) break;

            var saved = new List<Ex>(stack);
            Capture(j + 1, k);
            var val = Pop();
            stack = saved;
            int target = Idx(ins[k + 1].Target);
            if (target < 0) break;
            cases.Add((val.T, target));
            j = k + 2;
        }

        if (cases.Count == 0) return false;

        int defaultIdx = -1, endIdx = -1;
        if (j < to && ins[j].Op == Op.B)
        {
            defaultIdx = Idx(ins[j].Target);
            j++;
        }

        int last = cases[^1].Target;
        endIdx = Math.Max(defaultIdx, last);
        for (int k = Math.Min(cases[0].Target, j); k < to && k >= 0; k++)
        {
            if (ins[k].Op == Op.B && ins[k].Jump > 0)
            {
                int e = Idx(ins[k].Target);
                if (e > endIdx && e <= to) endIdx = e;
            }
        }
        if (endIdx <= 0 || endIdx > to) return false;

        var subject = Pop();
        int savedBreak = breakIdx;
        breakIdx = endIdx;

        Open("switch (" + subject.T + ")");
        var starts = new List<(int Start, string? Value)>();
        foreach (var c in cases) starts.Add((c.Target, c.Value));
        if (defaultIdx >= 0 && defaultIdx != endIdx) starts.Add((defaultIdx, null));
        starts.Sort((a, b) => a.Start.CompareTo(b.Start));

        var baseStack = new List<Ex>(stack);
        for (int c = 0; c < starts.Count; c++)
        {
            int start = starts[c].Start;
            int stop = c + 1 < starts.Count ? starts[c + 1].Start : endIdx;
            foreach (var cc in cases)
                if (cc.Target == start) Line("case " + cc.Value + ":");
            if (starts[c].Value == null) Line("default:");
            indent++;
            stack = new List<Ex>(baseStack);
            Body(start, Math.Min(stop, endIdx));
            indent--;
        }
        Close();

        stack = baseStack;
        breakIdx = savedBreak;
        int next = endIdx;
        if (next < ins.Count && ins[next].Op == Op.Popz) next++;
        i = next;
        return true;
    }

    void Step(Instr x, int i, int to)
    {
        switch (x.Op)
        {
            case Op.Conv:
                break;
            case Op.Mul: Bin("*", PMul); break;
            case Op.Div: Bin("/", PMul); break;
            case Op.Rem: Bin("div", PMul); break;
            case Op.Mod: Bin("%", PMul); break;
            case Op.Add: Bin("+", PAdd); break;
            case Op.Sub: Bin("-", PAdd); break;
            case Op.And: Bin(x.T1 == DT.Bool && x.T2 == DT.Bool ? "&&" : "&", x.T1 == DT.Bool ? PAnd : PBitAnd); break;
            case Op.Or: Bin(x.T1 == DT.Bool && x.T2 == DT.Bool ? "||" : "|", x.T1 == DT.Bool ? POr : PBitOr); break;
            case Op.Xor: Bin(x.T1 == DT.Bool && x.T2 == DT.Bool ? "^^" : "^", PBitXor); break;
            case Op.Shl: Bin("<<", PShift); break;
            case Op.Shr: Bin(">>", PShift); break;

            case Op.Neg: { var a = Pop(); Push("-" + Wrap(a, PUnary), PUnary); break; }
            case Op.Not:
                {
                    var a = Pop();
                    Push(x.T1 == DT.Bool ? Negate(a) : "~" + Wrap(a, PUnary), PUnary);
                    break;
                }

            case Op.Cmp:
                {
                    var b = Pop(); var a = Pop();
                    Push(Wrap(a, PCmp + 1) + " " + Bytecode.CmpName(x.Cmp) + " " + Wrap(b, PCmp + 1), PCmp);
                    break;
                }

            case Op.Push:
            case Op.PushLoc:
            case Op.PushGlb:
            case Op.PushBltn:
            case Op.PushI:
                PushValue(x);
                break;

            case Op.Pop:
                PopValue(x);
                break;

            case Op.Dup:
                {
                    int n = x.T1 == DT.Var ? x.Extra + 1 : (stack.Count > 0 && stack[^1].T == "-9" ? 2 : 1);
                    n = Math.Min(n, stack.Count);
                    if (n > 0) stack.AddRange(stack.GetRange(stack.Count - n, n));
                    break;
                }

            case Op.Popz:
                {
                    var e = Pop();
                    if (e.IsCall && e.Func == null) Line(e.T + ";");
                    break;
                }

            case Op.Ret:
                {
                    var e = Pop();
                    Line("return " + e.T + ";");
                    break;
                }

            case Op.Exit:
                if (i < to - 1) Line("return;");
                break;

            case Op.Call:
                {
                    var args = new List<Ex>();
                    for (int k = 0; k < x.ArgCount; k++) args.Add(Pop());
                    var call = new Ex(CallText(x.Name, args.ConvertAll(a => a.T)), PPrimary, true);
                    if (x.Name == "method" && args.Count == 2) call.Func = args[1].Func;
                    Push(call);
                    break;
                }

            case Op.CallV:
                {
                    var fn = Pop();
                    var args = new List<string>();
                    for (int k = 0; k < x.Extra; k++) args.Add(Pop().T);
                    Push(Wrap(fn, PPrimary) + "(" + string.Join(", ", args) + ")", PPrimary, true);
                    break;
                }

            case Op.Break:
                Special(x);
                break;

            case Op.PopEnv:
                break;

            default:
                Line("// unhandled opcode " + Bytecode.Mnemonic(x) + " " + Bytecode.Operand(g, x));
                break;
        }
    }

    void Bin(string op, int prec)
    {
        var b = Pop();
        var a = Pop();
        Push(Wrap(a, prec) + " " + op + " " + Wrap(b, prec + 1), prec);
    }

    string CallText(string name, List<string> args)
    {
        string n = Pretty(name);
        switch (name)
        {
            case "@@This@@": return "self";
            case "@@Other@@": return "other";
            case "@@Global@@": return "global";
            case "@@GetInstance@@": return args.Count > 0 ? args[0] : "self";
            case "@@NewGMLArray@@": return "[" + string.Join(", ", args) + "]";
            case "@@NewGMLObject@@":
                {
                    string ctor = args.Count > 0 ? Pretty(args[0]) : "";
                    return "new " + ctor + "(" + string.Join(", ", args.GetRange(1, Math.Max(0, args.Count - 1))) + ")";
                }
            case "method":
                if (args.Count == 2 && (args[0] == "-1" || args[0] == "undefined" || args[0] == "self"))
                    return Pretty(args[1]);
                break;
        }
        return n + "(" + string.Join(", ", args) + ")";
    }

    static string Pretty(string name)
    {
        if (name.StartsWith("gml_Script_", StringComparison.Ordinal)) return name.Substring("gml_Script_".Length);
        if (name.StartsWith("gml_GlobalScript_", StringComparison.Ordinal)) return name.Substring("gml_GlobalScript_".Length);
        return name;
    }

    void PushValue(Instr x)
    {
        if (x.T1 == DT.Var) { Push(VarExpr(x)); return; }
        if (x.T1 == DT.Int32 && x.Name.Length > 0)
        {
            Push(new Ex(Pretty(x.Name), PPrimary) { Func = x.Name });
            return;
        }
        if (x.T1 == DT.Int16 || x.T1 == DT.Int32 || x.T1 == DT.Int64)
        {
            Push(Bytecode.Literal(x.Value), PPrimary);
            return;
        }
        Push(Bytecode.Literal(x.Value), x.Value is double dd && dd < 0 ? PUnary : PPrimary);
    }

    Ex VarExpr(Instr x)
    {
        string name = x.Name;
        switch (x.VarKind)
        {
            case VarKind.StackTop:
            case VarKind.Instance:
                {
                    var owner = PopOwner();
                    return new Ex(OwnerPrefix(owner) + name, PPrimary);
                }
            case VarKind.Array:
            case VarKind.ArrayPushRef:
            case VarKind.ArrayPopRef:
                {
                    var index = Pop();
                    var owner = PopOwner();
                    return new Ex(OwnerPrefix(owner) + name + "[" + index.T + "]", PPrimary);
                }
            default:
                return new Ex(Scope(x) + name, PPrimary);
        }
    }

    Ex PopOwner()
    {
        var m = Pop();
        return m.T == "-9" ? Pop() : m;
    }

    string OwnerPrefix(Ex owner)
    {
        switch (owner.T)
        {
            case "-1": case "self": case "-6": case "-7": case "-15": return "";
            case "-2": return "other.";
            case "-5": case "global": return "global.";
            case "-16": return "static.";
        }
        return Wrap(owner, PPrimary) + ".";
    }

    string Scope(Instr x)
    {
        switch (x.InstType)
        {
            case -1: return "";
            case -2: return "other.";
            case -5: return "global.";
            case -6: return "";
            case -7: return "";
            case -11: return "";
            case -15: return "";
            case -16: return "static.";
            default:
                if (x.InstType >= 0 && x.InstType < g.Objects.Count) return g.Objects[x.InstType].Name + ".";
                return x.InstType == 0 ? "" : x.InstType.ToString(CultureInfo.InvariantCulture) + ".";
        }
    }

    void PopValue(Instr x)
    {
        if (x.T1 == DT.Int16)
        {
            if (stack.Count >= 2)
            {
                (stack[^1], stack[^2]) = (stack[^2], stack[^1]);
            }
            return;
        }

        string dest;
        switch (x.VarKind)
        {
            case VarKind.StackTop:
            case VarKind.Instance:
                {
                    var (owner, value) = PopOwnerValue(x);
                    dest = OwnerPrefix(owner) + x.Name;
                    Assign(dest, value);
                    return;
                }
            case VarKind.Array:
            case VarKind.ArrayPushRef:
            case VarKind.ArrayPopRef:
                {
                    var index = Pop();
                    var (owner, value) = PopOwnerValue(x);
                    dest = OwnerPrefix(owner) + x.Name + "[" + index.T + "]";
                    Assign(dest, value);
                    return;
                }
            default:
                {
                    var value = Pop();
                    dest = Scope(x) + x.Name;
                    Assign(dest, value);
                    return;
                }
        }
    }

    void Assign(string dest, Ex value)
    {
        if (value.Func != null && g.CodeByName.TryGetValue(value.Func, out var fn))
        {
            var body = InlineBody(fn);
            if (body != null)
            {
                var argNames = new List<string>();
                for (int a = 0; a < fn.Args; a++) argNames.Add("argument" + a);
                Line("function " + dest + "(" + string.Join(", ", argNames) + ")");
                Line("{");
                foreach (var l in body) Line("    " + l);
                Line("}");
                return;
            }
        }
        Line(Compound(dest, value.T));
    }

    static string Compound(string dest, string value)
    {
        foreach (var op in new[] { "+", "-", "*", "/", "|", "&", "^" })
        {
            string prefix = dest + " " + op + " ";
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rest = value.Substring(prefix.Length);
            if (rest.Length == 0) break;
            if (rest == "1" && (op == "+" || op == "-")) return dest + op + op + ";";
            return dest + " " + op + "= " + rest + ";";
        }
        return dest + " = " + value + ";";
    }

    (Ex Owner, Ex Value) PopOwnerValue(Instr x)
    {
        if (x.T1 == DT.Int32)
        {
            var value = Pop();
            var m = Pop();
            return (m.T == "-9" ? Pop() : m, value);
        }
        var marker = Pop();
        if (marker.T == "-9")
        {
            var value = Pop();
            return (Pop(), value);
        }
        return (marker, Pop());
    }

    void Special(Instr x)
    {
        switch (x.BreakCode)
        {
            case -1:
            case -5:
            case -7:
            case -8:
            case -9:
            case -10:
                if (x.BreakCode == -5 && stack.Count > 0) Pop();
                break;

            case -2:
            case -4:
                {
                    var index = Pop();
                    var arr = Pop();
                    Push(Wrap(arr, PPrimary) + "[" + index.T + "]", PPrimary);
                    break;
                }

            case -3:
                {
                    var index = Pop();
                    var arr = Pop();
                    var value = Pop();
                    Line(Wrap(arr, PPrimary) + "[" + index.T + "] = " + value.T + ";");
                    break;
                }

            case -6:
                Push("static_get(self) != undefined", PCmp);
                break;

            case -11:
                Push(x.Value is int v ? "ref_" + v : "ref", PPrimary);
                break;

            default:
                Line("// break " + Bytecode.BreakName(x.BreakCode));
                break;
        }
    }
}