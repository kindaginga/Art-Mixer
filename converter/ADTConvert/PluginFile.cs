using System.IO.Compression;
using System.Text;

namespace ADTConvert;

/// <summary>A record identity independent of load order: the file that defines the record and its id inside that file.</summary>
public readonly record struct FormKey(string File, uint Local)
{
    public bool Equals(FormKey other) => Local == other.Local && string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() => HashCode.Combine(Local, StringComparer.OrdinalIgnoreCase.GetHashCode(File));
    public override string ToString() => $"{File}:{Local:X6}";
    /// <summary>The form as packs.json names it: the file name in lowercase and the local id in hex.</summary>
    public string Text => $"{File.ToLowerInvariant()}:{Local:x6}";

    /// <summary>The form a packs.json text ("file:hex local id") names, or null when the text is not one.</summary>
    public static FormKey? FromText(string text)
    {
        var colon = text.LastIndexOf(':');
        return colon > 0 && uint.TryParse(text[(colon + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var local) ? new FormKey(text[..colon], local) : null;
    }
}

/// <summary>Reads the parts of a Fallout 4 plugin (.esp, .esm, .esl) the converter needs: the master list, every field of
/// each record with a model (model block MODL, MODC, MODS, name FULL and the rest) and every material swap (MSWP). Record layout per the UESP Fallout 4 mod file format:
/// 24-byte record and group headers, zlib-compressed record data behind flag 0x00040000, 6-byte field headers with XXXX
/// carrying the size of an oversized next field.</summary>
public sealed class PluginFile
{
    private const uint CompressedFlag = 0x00040000;

    // Record types whose MODL field is not the model of a placed object (armor links, race body data, sky effects).
    private static readonly HashSet<string> NotObjectModels = new(StringComparer.Ordinal) { "ARMO", "ARMA", "RACE", "WTHR" };

    /// <summary>One field of a record as stored: its four-letter type and its bytes.</summary>
    public sealed record Field(string Type, byte[] Data);

    /// <summary>One record with a model: its type, identity, editor id, model block, and every field; Source is the plugin it was read from.</summary>
    public sealed record ModelRecord(string Type, FormKey Key, string EditorId, string Model, FormKey? Swap, float? ColorIndex, string Source, List<Field> Fields)
    {
        public byte[]? FieldData(string type) => Fields.FirstOrDefault(f => f.Type == type)?.Data;
    }

    /// <summary>One substitution of a material swap: the material it replaces and its replacement, relative to Materials.</summary>
    public sealed record Substitution(string Original, string Replacement, float? ColorIndex);

    public required string Name { get; init; }
    /// <summary>True when text fields hold ids into the plugin's string tables (TES4 flag 0x80).</summary>
    public bool Localized { get; private set; }
    public List<string> Masters { get; } = [];
    public List<ModelRecord> Models { get; } = [];
    public Dictionary<FormKey, List<Substitution>> MaterialSwaps { get; } = [];
    public Dictionary<FormKey, string[]> TextureSets { get; } = [];  // TX00 to TX07, relative to Textures, empty where unset
    public Dictionary<FormKey, FormKey> LoadScreens { get; } = [];     // loading screen (LSCR) -> the form it shows (NNAM)
    public Dictionary<FormKey, int[]> WorldMaps { get; } = [];         // world space (WRLD) -> its world map data (MNAM): usable width, usable height, NW cell x, y, SE cell x, y
    private bool _worldChildren = true;
    /// <summary>Every record read, as type and identity.</summary>
    public List<(string Type, FormKey Key)> Records { get; } = [];

    /// <summary>Reads a plugin. With a group filter only top-level groups whose record type passes are read; without world
    /// children only the world space records themselves are read, not the cells and references grouped under them.</summary>
    public static PluginFile Read(string name, byte[] data, Func<string, bool>? readGroup = null, bool worldChildren = true)
    {
        var plugin = new PluginFile { Name = name, _worldChildren = worldChildren };
        if (data.Length < 24 || Encoding.ASCII.GetString(data, 0, 4) != "TES4") throw new InvalidDataException("not a plugin file (no TES4 header)");
        var headerSize = BitConverter.ToInt32(data, 4);
        plugin.Localized = (BitConverter.ToUInt32(data, 8) & 0x80) != 0;
        foreach (var (type, field) in Fields(data.AsSpan(24, headerSize).ToArray()))
        {
            if (type == "MAST") plugin.Masters.Add(ZString(field));
        }
        var position = 24 + headerSize;
        while (position + 24 <= data.Length)
        {
            var groupSize = BitConverter.ToInt32(data, position + 4);
            if (Encoding.ASCII.GetString(data, position, 4) != "GRUP" || groupSize < 24 || position + groupSize > data.Length) throw new InvalidDataException($"broken top-level group at {position}");
            var label = Encoding.ASCII.GetString(data, position + 8, 4);
            if (readGroup == null || readGroup(label)) plugin.ReadGroupContents(data, position + 24, position + groupSize);
            position += groupSize;
        }
        return plugin;
    }

    /// <summary>Every texture path (relative to Textures, lowercase) a field of any record of the plugin holds as text.</summary>
    public static HashSet<string> TexturePaths(byte[] data)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data.Length < 24 || Encoding.ASCII.GetString(data, 0, 4) != "TES4") throw new InvalidDataException("not a plugin file (no TES4 header)");
        void Walk(int start, int end)
        {
            var position = start;
            while (position + 24 <= end)
            {
                var size = BitConverter.ToInt32(data, position + 4);
                if (Encoding.ASCII.GetString(data, position, 4) == "GRUP")
                {
                    if (size < 24 || position + size > end) throw new InvalidDataException($"broken group at {position}");
                    Walk(position + 24, position + size);
                    position += size;
                    continue;
                }
                if (size < 0 || position + 24 + size > end) throw new InvalidDataException($"broken record at {position}");
                var body = data.AsSpan(position + 24, size);
                byte[] fields;
                if ((BitConverter.ToUInt32(data, position + 8) & CompressedFlag) != 0)
                {
                    fields = new byte[BitConverter.ToInt32(body[..4])];
                    using var zlib = new ZLibStream(new MemoryStream(body[4..].ToArray()), CompressionMode.Decompress);
                    zlib.ReadExactly(fields);
                }
                else
                {
                    fields = body.ToArray();
                }
                foreach (var (_, field) in Fields(fields))
                {
                    if (field.Length < 6 || field[^1] != 0) continue;
                    var text = ZString(field);
                    if (text.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && text.All(c => c >= ' ' && c < '\x7F')) result.Add(Paths.Normalize(text, "textures"));
                }
                position += 24 + size;
            }
        }
        Walk(24 + BitConverter.ToInt32(data, 4), data.Length);
        return result;
    }

    /// <summary>The identity of a form id as written in this plugin: the high byte indexes the master list, past its end is this plugin.</summary>
    public FormKey Resolve(uint formId)
    {
        var index = (int)(formId >> 24);
        return new FormKey(index < Masters.Count ? Masters[index] : Name, formId & 0xFFFFFF);
    }

    private void ReadGroupContents(byte[] data, int start, int end)
    {
        var position = start;
        while (position + 24 <= end)
        {
            var type = Encoding.ASCII.GetString(data, position, 4);
            var size = BitConverter.ToInt32(data, position + 4);
            if (type == "GRUP")
            {
                if (size < 24 || position + size > end) throw new InvalidDataException($"broken group at {position}");
                // Group type 1 holds a world space's cells.
                if (!_worldChildren && BitConverter.ToInt32(data, position + 12) == 1)
                {
                    position += size;
                    continue;
                }
                ReadGroupContents(data, position + 24, position + size);
                position += size;
                continue;
            }
            if (size < 0 || position + 24 + size > end) throw new InvalidDataException($"broken {type} record at {position}");
            var flags = BitConverter.ToUInt32(data, position + 8);
            var key = Resolve(BitConverter.ToUInt32(data, position + 12));
            Records.Add((type, key));
            ReadRecord(type, key, flags, data.AsSpan(position + 24, size));
            position += 24 + size;
        }
    }

    private void ReadRecord(string type, FormKey key, uint flags, ReadOnlySpan<byte> body)
    {
        byte[] fields;
        if ((flags & CompressedFlag) != 0)
        {
            var expanded = BitConverter.ToInt32(body[..4]);
            fields = new byte[expanded];
            using var zlib = new ZLibStream(new MemoryStream(body[4..].ToArray()), CompressionMode.Decompress);
            zlib.ReadExactly(fields);
        }
        else
        {
            fields = body.ToArray();
        }
        if (type == "MSWP")
        {
            var substitutions = new List<Substitution>();
            string? original = null, replacement = null;
            float? color = null;
            void Flush()
            {
                if (original != null) substitutions.Add(new Substitution(Paths.Normalize(original, "materials"), Paths.Normalize(replacement ?? "", "materials"), color));
                original = null; replacement = null; color = null;
            }
            foreach (var (name, field) in Fields(fields))
            {
                switch (name)
                {
                    case "BNAM": Flush(); original = ZString(field); break;
                    case "SNAM": replacement = ZString(field); break;
                    case "CNAM" when field.Length >= 4: color = BitConverter.ToSingle(field, 0); break;
                }
            }
            Flush();
            MaterialSwaps[key] = substitutions;
            return;
        }
        if (type == "WRLD")
        {
            foreach (var (name, field) in Fields(fields))
            {
                if (name == "MNAM" && field.Length >= 16)
                {
                    WorldMaps[key] = [BitConverter.ToInt32(field, 0), BitConverter.ToInt32(field, 4), BitConverter.ToInt16(field, 8), BitConverter.ToInt16(field, 10), BitConverter.ToInt16(field, 12), BitConverter.ToInt16(field, 14)];
                }
            }
            return;
        }
        if (type == "LSCR")
        {
            foreach (var (name, field) in Fields(fields))
            {
                if (name == "NNAM" && field.Length >= 4) LoadScreens[key] = Resolve(BitConverter.ToUInt32(field, 0));
            }
            return;
        }
        if (type == "TXST")
        {
            var textures = new string[8];
            Array.Fill(textures, "");
            foreach (var (name, field) in Fields(fields))
            {
                if (name.Length == 4 && name.StartsWith("TX0", StringComparison.Ordinal) && name[3] is >= '0' and <= '7') textures[name[3] - '0'] = Paths.Normalize(ZString(field), "textures");
            }
            TextureSets[key] = textures;
            return;
        }
        string editorId = "", model = "";
        FormKey? swap = null;
        float? colorIndex = null;
        var inModel = false;
        var all = new List<Field>();
        foreach (var (name, field) in Fields(fields))
        {
            all.Add(new Field(name, field));
            switch (name)
            {
                case "EDID": editorId = ZString(field); break;
                case "MODL" when model.Length == 0: model = ZString(field); inModel = true; break;
                case "MODT" or "MODF" when inModel: break;
                case "MODC" when inModel && field.Length >= 4: colorIndex = BitConverter.ToSingle(field, 0); break;
                case "MODS" when inModel && field.Length >= 4: swap = Resolve(BitConverter.ToUInt32(field, 0)); break;
                default: inModel = false; break;
            }
        }
        if (model.Length > 0 && !NotObjectModels.Contains(type)) Models.Add(new ModelRecord(type, key, editorId, Paths.Normalize(model, "meshes"), swap, colorIndex, Name, all));
    }

    private static IEnumerable<(string Type, byte[] Data)> Fields(byte[] data)
    {
        var position = 0;
        var oversized = -1;
        while (position + 6 <= data.Length)
        {
            var type = Encoding.ASCII.GetString(data, position, 4);
            var size = (int)BitConverter.ToUInt16(data, position + 4);
            position += 6;
            if (oversized >= 0)
            {
                size = oversized;
                oversized = -1;
            }
            if (position + size > data.Length) throw new InvalidDataException($"field {type} runs past its record");
            if (type == "XXXX")
            {
                oversized = BitConverter.ToInt32(data, position);
                position += size;
                continue;
            }
            yield return (type, data[position..(position + size)]);
            position += size;
        }
    }

    /// <summary>Reads a .STRINGS table (null-terminated entries) or a .DLSTRINGS/.ILSTRINGS table (length-prefixed entries).</summary>
    public static Dictionary<uint, string> ReadStrings(byte[] data, bool lengthPrefixed)
    {
        var table = new Dictionary<uint, string>();
        var count = BitConverter.ToInt32(data, 0);
        var start = 8 + count * 8;
        for (var i = 0; i < count; i++)
        {
            var id = BitConverter.ToUInt32(data, 8 + i * 8);
            var offset = start + BitConverter.ToInt32(data, 12 + i * 8) + (lengthPrefixed ? 4 : 0);
            var end = Array.IndexOf(data, (byte)0, offset);
            table[id] = Encoding.UTF8.GetString(data, offset, (end < 0 ? data.Length : end) - offset);
        }
        return table;
    }

    /// <summary>The text of a string field: from the string tables when the plugin is localized, else the stored text.</summary>
    public static string? Text(byte[]? field, bool localized, Func<uint, string?> strings)
    {
        if (field == null) return null;
        if (localized && field.Length == 4) return BitConverter.ToUInt32(field, 0) == 0 ? "" : strings(BitConverter.ToUInt32(field, 0));
        return ZString(field);
    }

    private static string ZString(byte[] field)
    {
        var end = Array.IndexOf(field, (byte)0);
        return Encoding.Latin1.GetString(field, 0, end < 0 ? field.Length : end);
    }
}
