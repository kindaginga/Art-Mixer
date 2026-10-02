using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GShufConvert;

/// <summary>Reads Fallout 4 BA2 archives (versions 1, 7 and 8; GNRL and DX10) and returns file contents.</summary>
public sealed class Ba2Archive
{
    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private abstract record Entry(string Path);
    private sealed record GeneralEntry(string Path, long Offset, uint PackedSize, uint UnpackedSize) : Entry(Path);
    private sealed record TextureEntry(string Path, ushort Width, ushort Height, byte Mips, byte Format, ushort Flags, List<Chunk> Chunks) : Entry(Path);
    private sealed record Chunk(long Offset, uint PackedSize, uint UnpackedSize, ushort StartMip, ushort EndMip);

    public string Path => _path;
    public IEnumerable<string> Paths => _entries.Keys;
    public int Count => _entries.Count;

    public Ba2Archive(string path)
    {
        _path = path;
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file, Encoding.Latin1, leaveOpen: true);
        var magic = reader.ReadBytes(4);
        var version = reader.ReadUInt32();
        var kind = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var count = reader.ReadUInt32();
        var nameTableOffset = reader.ReadInt64();
        if (Encoding.ASCII.GetString(magic) != "BTDX") throw new InvalidDataException($"{path}: not a BA2 archive");
        if (version is not (1 or 7 or 8)) throw new InvalidDataException($"{path}: unsupported BA2 version {version}");

        var records = new List<Entry>((int)count);
        if (kind == "GNRL")
        {
            for (var i = 0; i < count; i++)
            {
                reader.ReadUInt32(); // name hash
                reader.ReadBytes(4); // extension
                reader.ReadUInt32(); // directory hash
                reader.ReadUInt32(); // unknown
                var offset = reader.ReadInt64();
                var packed = reader.ReadUInt32();
                var unpacked = reader.ReadUInt32();
                reader.ReadUInt32(); // 0xBAADF00D
                records.Add(new GeneralEntry("", offset, packed, unpacked));
            }
        }
        else if (kind == "DX10")
        {
            for (var i = 0; i < count; i++)
            {
                reader.ReadUInt32(); // name hash
                reader.ReadBytes(4); // extension
                reader.ReadUInt32(); // directory hash
                reader.ReadByte(); // unknown
                var chunkCount = reader.ReadByte();
                reader.ReadUInt16(); // chunk header size (24)
                var height = reader.ReadUInt16();
                var width = reader.ReadUInt16();
                var mips = reader.ReadByte();
                var format = reader.ReadByte();
                var flags = reader.ReadUInt16(); // low byte: cube map, high byte: tile mode
                var chunks = new List<Chunk>(chunkCount);
                for (var c = 0; c < chunkCount; c++)
                {
                    var offset = reader.ReadInt64();
                    var packed = reader.ReadUInt32();
                    var unpacked = reader.ReadUInt32();
                    var startMip = reader.ReadUInt16();
                    var endMip = reader.ReadUInt16();
                    reader.ReadUInt32(); // 0xBAADF00D
                    chunks.Add(new Chunk(offset, packed, unpacked, startMip, endMip));
                }
                records.Add(new TextureEntry("", width, height, mips, format, flags, chunks));
            }
        }
        else
        {
            throw new InvalidDataException($"{path}: unsupported BA2 type {kind}");
        }

        file.Position = nameTableOffset;
        for (var i = 0; i < count; i++)
        {
            var length = reader.ReadUInt16();
            var name = Encoding.Latin1.GetString(reader.ReadBytes(length)).Replace('/', '\\');
            _entries[name] = records[i] with { Path = name };
        }
    }

    /// <summary>The file paths of a BA2 read from a forward-only stream, skipping everything before the name table.</summary>
    public static List<string> ReadNames(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var version = reader.ReadUInt32();
        reader.ReadBytes(4); // type
        var count = reader.ReadUInt32();
        var nameTableOffset = reader.ReadInt64();
        if (magic != "BTDX") throw new InvalidDataException("not a BA2 archive");
        if (version is not (1 or 7 or 8)) throw new InvalidDataException($"unsupported BA2 version {version}");
        if (nameTableOffset < 24 || count > (nameTableOffset - 24) / 24) throw new InvalidDataException($"a name table at {nameTableOffset} cannot follow {count} records");
        var skip = nameTableOffset - 24;
        var buffer = new byte[81920];
        while (skip > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, skip));
            if (read == 0) throw new EndOfStreamException("the BA2 ends before its name table");
            skip -= read;
        }
        var names = new List<string>((int)count);
        for (var i = 0; i < count; i++)
        {
            var length = reader.ReadUInt16();
            names.Add(Encoding.Latin1.GetString(reader.ReadBytes(length)).Replace('/', '\\'));
        }
        return names;
    }

    public bool Contains(string path) => _entries.ContainsKey(path);

    /// <summary>Width and height for a DX10 entry or a DDS file in a general archive, or null when the entry is missing or not a DDS.</summary>
    public (int Width, int Height)? Dimensions(string path)
    {
        if (!_entries.TryGetValue(path, out var e)) return null;
        if (e is TextureEntry t) return (t.Width, t.Height);
        var dds = Read(path);
        return dds.Length >= 20 && dds[0] == 'D' && dds[1] == 'D' && dds[2] == 'S' && dds[3] == ' ' ? (BitConverter.ToInt32(dds, 16), BitConverter.ToInt32(dds, 12)) : null;
    }

    /// <summary>Extracts one file; textures are rebuilt as DDS files with a DX10 header.</summary>
    public byte[] Read(string path)
    {
        if (!_entries.TryGetValue(path, out var entry)) throw new FileNotFoundException(path, path);
        using var file = File.OpenRead(_path);
        switch (entry)
        {
            case GeneralEntry g:
            {
                file.Position = g.Offset;
                var raw = new byte[g.PackedSize > 0 ? g.PackedSize : g.UnpackedSize];
                file.ReadExactly(raw);
                return g.PackedSize > 0 ? Inflate(raw, g.UnpackedSize) : raw;
            }
            case TextureEntry t:
            {
                var pixels = new MemoryStream();
                foreach (var chunk in t.Chunks)
                {
                    file.Position = chunk.Offset;
                    var raw = new byte[chunk.PackedSize > 0 ? chunk.PackedSize : chunk.UnpackedSize];
                    file.ReadExactly(raw);
                    var data = chunk.PackedSize > 0 ? Inflate(raw, chunk.UnpackedSize) : raw;
                    pixels.Write(data);
                }
                var dds = Dds.Build(t.Width, t.Height, t.Mips, t.Format, (t.Flags & 0xFF) != 0, pixels.ToArray());
                var expected = Dds.ExpectedDataSize(dds);
                if (expected != dds.Length - 148) throw new InvalidDataException($"{path}: rebuilt DDS holds {dds.Length - 148} bytes, header expects {expected}");
                return dds;
            }
            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>Reads every general entry the filter accepts, in file order, through one open stream.</summary>
    public void ForEachGeneral(Func<string, bool> filter, Action<string, byte[]> action)
    {
        using var file = File.OpenRead(_path);
        foreach (var entry in _entries.Values.OfType<GeneralEntry>().Where(e => filter(e.Path)).OrderBy(e => e.Offset))
        {
            file.Position = entry.Offset;
            var raw = new byte[entry.PackedSize > 0 ? entry.PackedSize : entry.UnpackedSize];
            file.ReadExactly(raw);
            action(entry.Path, entry.PackedSize > 0 ? Inflate(raw, entry.UnpackedSize) : raw);
        }
    }

    private static byte[] Inflate(byte[] packed, uint unpackedSize)
    {
        using var input = new MemoryStream(packed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[unpackedSize];
        var read = 0;
        while (read < output.Length)
        {
            var n = zlib.Read(output, read, output.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return output;
    }
}

/// <summary>DDS header construction and parsing for the formats Fallout 4 ships.</summary>
public static class Dds
{
    public static byte[] Build(int width, int height, int mips, byte dxgiFormat, bool cubeMap, byte[] pixels)
    {
        var header = new byte[148];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0x20534444); // "DDS "
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 124);
        var compressed = IsBlockCompressed(dxgiFormat);
        uint flags = 0x1 | 0x2 | 0x4 | 0x1000 | (mips > 1 ? 0x20000u : 0) | (compressed ? 0x80000u : 0x8u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), compressed ? (uint)MipSize(width, height, dxgiFormat) : (uint)(width * BytesPerPixel(dxgiFormat)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)Math.Max(1, mips));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), 0x4); // DDPF_FOURCC
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84), 0x30315844); // "DX10"
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(108), 0x1000 | (mips > 1 ? 0x400008u : 0) | (cubeMap ? 0x8u : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(112), cubeMap ? 0xFE00u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128), dxgiFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(132), 3); // texture 2D
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(136), cubeMap ? 0x4u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(140), 1);
        var result = new byte[header.Length + pixels.Length];
        header.CopyTo(result, 0);
        pixels.CopyTo(result, header.Length);
        return result;
    }

    /// <summary>Width and height from a DDS header, or null when the data is not a DDS file.</summary>
    public static (int Width, int Height)? ReadDimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x20534444) return null;
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12));
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16));
        return (width, height);
    }

    /// <summary>The part of a texture's top mip level from (x0, y0) to (x1, y1) in pixels, as a DDS with a DX10 header; block
    /// compressed formats are cut on the 4-pixel block edges inside the rectangle, so no neighbouring picture of a texture
    /// sheet bleeds in. Null for a format this cannot cut or a rectangle smaller than a block.</summary>
    public static byte[]? Crop(byte[] data, int x0, int y0, int x1, int y1)
    {
        if (data.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x20534444) return null;
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var formatFlags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
        var fourCc = Encoding.ASCII.GetString(data, 84, 4);
        byte format;
        var offset = 128;
        if ((formatFlags & 0x4) != 0 && fourCc == "DX10")
        {
            if (data.Length < 148) return null;
            format = (byte)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(128));
            offset = 148;
        }
        else if ((formatFlags & 0x4) != 0)
        {
            format = fourCc switch
            {
                "DXT1" => 71,
                "DXT2" or "DXT3" => 74,
                "DXT4" or "DXT5" => 77,
                "ATI1" or "BC4U" => 80,
                "BC4S" => 81,
                "ATI2" or "BC5U" => 83,
                "BC5S" => 84,
                _ => 0,
            };
        }
        else if ((formatFlags & 0x40) != 0 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(88)) == 32)
        {
            // 32-bit RGB(A): red in the low byte is R8G8B8A8, red in the third byte is B8G8R8A8.
            format = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(92)) switch
            {
                0x000000FF => 28,
                0x00FF0000 => 87,
                _ => 0,
            };
        }
        else format = 0;
        if (format == 0 || width <= 0 || height <= 0) return null;
        var compressed = IsBlockCompressed(format);
        var unit = compressed ? 4 : 1;
        x0 = Math.Clamp((x0 + unit - 1) / unit * unit, 0, width);
        y0 = Math.Clamp((y0 + unit - 1) / unit * unit, 0, height);
        // A far edge on the texture's own edge keeps a last partial block whole.
        x1 = x1 >= width ? width : Math.Clamp(x1 / unit * unit, 0, width);
        y1 = y1 >= height ? height : Math.Clamp(y1 / unit * unit, 0, height);
        if (x1 <= x0 || y1 <= y0) return null;
        var unitBytes = compressed ? BlockBytes(format) : BytesPerPixel(format);
        var rowUnits = (width + unit - 1) / unit;
        var rowBytes = rowUnits * unitBytes;
        var cutUnits = (x1 - x0 + unit - 1) / unit;
        var cutRows = (y1 - y0 + unit - 1) / unit;
        if (offset + (long)((y1 + unit - 1) / unit) * rowBytes > data.Length) return null;
        var pixels = new byte[cutUnits * unitBytes * cutRows];
        for (var row = 0; row < cutRows; row++)
        {
            Buffer.BlockCopy(data, offset + (y0 / unit + row) * rowBytes + x0 / unit * unitBytes, pixels, row * cutUnits * unitBytes, cutUnits * unitBytes);
        }
        return Build(x1 - x0, y1 - y0, 1, format, false, pixels);
    }

    /// <summary>A texture's top mip level as RGBA pixels (row by row, 4 bytes each) with its width and height; null for a format
    /// this cannot read.</summary>
    public static (byte[] Rgba, int Width, int Height)? DecodeTop(byte[] data)
    {
        if (data.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x20534444) return null;
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var whole = Crop(data, 0, 0, width, height);
        if (whole == null) return null;
        var format = (byte)BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan(128));
        var pixels = whole.AsSpan(148).ToArray();
        var rgba = new byte[width * height * 4];
        switch (format)
        {
            case 28 or 29:
                pixels.AsSpan(0, rgba.Length).CopyTo(rgba);
                return (rgba, width, height);
            case 87 or 91:
                for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]);
                return (rgba, width, height);
        }
        BCnEncoder.Shared.CompressionFormat? block = format switch
        {
            71 or 72 => BCnEncoder.Shared.CompressionFormat.Bc1WithAlpha,
            74 or 75 => BCnEncoder.Shared.CompressionFormat.Bc2,
            77 or 78 => BCnEncoder.Shared.CompressionFormat.Bc3,
            80 or 81 => BCnEncoder.Shared.CompressionFormat.Bc4,
            83 or 84 => BCnEncoder.Shared.CompressionFormat.Bc5,
            98 or 99 => BCnEncoder.Shared.CompressionFormat.Bc7,
            _ => null,
        };
        if (block == null) return null;
        var decoded = new BCnEncoder.Decoder.BcDecoder().DecodeRaw(pixels, width, height, block.Value);
        for (var i = 0; i < decoded.Length; i++) (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = (decoded[i].r, decoded[i].g, decoded[i].b, decoded[i].a);
        return (rgba, width, height);
    }

    /// <summary>Pixel bytes a DDS with a DX10 header should carry according to its own header.</summary>
    public static int ExpectedDataSize(ReadOnlySpan<byte> dds)
    {
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(dds.Slice(12));
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(dds.Slice(16));
        var mips = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(dds.Slice(28)));
        var format = (byte)BinaryPrimitives.ReadUInt32LittleEndian(dds.Slice(128));
        var faces = (BinaryPrimitives.ReadUInt32LittleEndian(dds.Slice(136)) & 0x4) != 0 ? 6 : 1;
        var total = 0;
        for (int m = 0, w = width, h = height; m < mips; m++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            total += IsBlockCompressed(format) ? MipSize(w, h, format) : w * h * BytesPerPixel(format);
        }
        return total * faces;
    }

    private static bool IsBlockCompressed(byte format) => format is >= 70 and <= 84 or >= 94 and <= 99;

    private static int BlockBytes(byte format) => format is 70 or 71 or 72 or 79 or 80 or 81 ? 8 : 16;

    private static int BytesPerPixel(byte format) => format switch
    {
        61 or 62 or 63 or 64 or 65 => 1,
        49 or 50 or 51 or 54 or 56 or 57 or 58 or 59 or 60 => 2,
        _ => 4,
    };

    private static int MipSize(int width, int height, byte format)
    {
        return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * BlockBytes(format);
    }
}
