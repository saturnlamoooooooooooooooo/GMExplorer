using System;
using System.Buffers.Binary;
using System.Text;

namespace GMExplorer.Gm;

public sealed class Reader
{
    public readonly byte[] D;
    public int Pos;

    public Reader(byte[] data, int pos = 0) { D = data; Pos = pos; }

    public int Length => D.Length;
    public bool Has(int n) => Pos >= 0 && (long)Pos + n <= D.Length;
    public bool InRange(int at, int n = 1) => at >= 0 && (long)at + n <= D.Length;

    public byte U8() => D[Pos++];
    public short I16() { var v = BinaryPrimitives.ReadInt16LittleEndian(D.AsSpan(Pos)); Pos += 2; return v; }
    public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(D.AsSpan(Pos)); Pos += 2; return v; }
    public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(D.AsSpan(Pos)); Pos += 4; return v; }
    public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(Pos)); Pos += 4; return v; }
    public long I64() { var v = BinaryPrimitives.ReadInt64LittleEndian(D.AsSpan(Pos)); Pos += 8; return v; }
    public float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(D.AsSpan(Pos)); Pos += 4; return v; }
    public double F64() { var v = BinaryPrimitives.ReadDoubleLittleEndian(D.AsSpan(Pos)); Pos += 8; return v; }
    public void Skip(int n) => Pos += n;

    public int At(int at) => BinaryPrimitives.ReadInt32LittleEndian(D.AsSpan(at));
    public uint AtU(int at) => BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(at));
    public short At16(int at) => BinaryPrimitives.ReadInt16LittleEndian(D.AsSpan(at));

    public string Fourcc()
    {
        var s = Encoding.ASCII.GetString(D, Pos, 4);
        Pos += 4;
        return s;
    }

    public string StringAt(int charAddr)
    {
        if (charAddr < 4 || charAddr >= D.Length) return "";
        int len = At(charAddr - 4);
        if (len < 0 || charAddr + len > D.Length) return "";
        return Encoding.UTF8.GetString(D, charAddr, len);
    }
}