using System.Collections.Generic;

namespace GMExplorer.Gm;

public sealed class GmChunk
{
    public string Name = "";
    public int Offset;
    public int Length;
}

public enum TexFormat { Png, Qoi, Bz2Qoi, Unknown }

public sealed class TexturePage
{
    public int Index;
    public int DataOffset;
    public int DataLength;
    public TexFormat Format = TexFormat.Unknown;
    public int Width, Height;
    public bool Scaled;
    public string? Error;

    public string FormatName => Format switch
    {
        TexFormat.Png => "PNG",
        TexFormat.Qoi => "QOI",
        TexFormat.Bz2Qoi => "QOI + BZip2", _ => "unknown"
    };
}

public sealed class TexItem
{
    public int Index;
    public int SourceX, SourceY, SourceW, SourceH;
    public int TargetX, TargetY, TargetW, TargetH;
    public int BoundW, BoundH;
    public int Page;
}

public sealed class GmSprite
{
    public int Index;
    public string Name = "";
    public int Width, Height;
    public int MarginLeft, MarginRight, MarginBottom, MarginTop;
    public int OriginX, OriginY;
    public int BBoxMode, SepMasks;
    public bool Transparent, Smooth, Preload;
    public float PlaybackSpeed = 15f;
    public int SpriteType;
    public List<int> Frames = new();
    public string? Note;
}

public sealed class GmBackground
{
    public int Index;
    public string Name = "";
    public int Item = -1;
}

public sealed class GmFont
{
    public int Index;
    public string Name = "";
    public string DisplayName = "";
    public float Size;
    public bool Bold, Italic;
    public int Item = -1;
    public int GlyphCount;
}

public sealed class GmAudioBlob
{
    public int Index;
    public int Offset;
    public int Length;
    public byte[]? Source;
    public string Container = "?";
    public string Origin = "AUDO";
}

public sealed class GmSound
{
    public int Index;
    public string Name = "";
    public string File = "";
    public string Type = "";
    public int Flags;
    public float Volume = 1, Pitch = 1;
    public int GroupId;
    public int AudioId = -1;
    public bool Embedded;
    public bool Compressed;
    public GmAudioBlob? Blob;
}

public sealed class GmCode
{
    public int Index;
    public string Name = "";
    public int Length;
    public int Locals, Args;
    public int Address;
    public int Offset;
    public int ParentIndex = -1;
    public bool IsChild => Offset > 0;
}

public sealed class GmObject
{
    public int Index;
    public string Name = "";
    public int SpriteIndex = -1;
    public int ParentIndex = -1;
    public int Depth;
    public bool Visible, Solid, Persistent;
    public List<GmCode> Events = new();
}

public sealed class GmRoomInstance
{
    public float X, Y;
    public int ObjectIndex = -1;
    public int Id;
    public float ScaleX = 1, ScaleY = 1;
}

public sealed class GmRoom
{
    public int Index;
    public string Name = "";
    public string Caption = "";
    public int Width, Height, Speed;
    public bool Persistent;
    public uint BgColor;
    public int CreationCodeId = -1;
    public List<GmRoomInstance> Instances = new();
    public string? Note;
}

public sealed class GmSimple
{
    public int Index;
    public string Name = "";
    public string Detail = "";
    public int CodeIndex = -1;
}

public sealed class GmVariable
{
    public string Name = "";
    public int InstanceType;
    public int VarId;
    public int Occurrences;
}

public sealed class GmFunction
{
    public string Name = "";
    public int Occurrences;
}