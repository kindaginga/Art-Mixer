using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace ArtMixerConvert;

/// <summary>Reads single keys out of a LevelDB database folder (Vortex's state.v2) from its files alone, without opening the
/// database: the log files (writes not yet in a table) and the table files (.ldb, blocks compressed with Snappy). The value
/// with the highest sequence number wins, as in the database itself; a deletion with the highest number means no value.</summary>
public static class LevelDb
{
    /// <summary>The key's value as a JSON string, or null when the key has no value or the value is not a JSON string.</summary>
    public static string? ReadJsonString(string folder, string key)
    {
        var value = Read(folder, Encoding.UTF8.GetBytes(key));
        if (value == null) return null;
        try
        {
            return JsonSerializer.Deserialize<string>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The newest value written for the key across the folder's log and table files, or null.</summary>
    public static byte[]? Read(string folder, byte[] key) => Scan(folder, key, true).GetValueOrDefault(Encoding.UTF8.GetString(key)).Value;

    /// <summary>The newest value of every key that starts with the prefix, as UTF-8 key text to value (null for a deleted key).</summary>
    public static Dictionary<string, byte[]?> ReadPrefix(string folder, string prefix) =>
        Scan(folder, Encoding.UTF8.GetBytes(prefix), false).ToDictionary(kv => kv.Key, kv => kv.Value.Value);

    // The newest write of every key equal to the given bytes (exact) or starting with them, with its sequence number.
    private static Dictionary<string, (ulong Sequence, byte[]? Value)> Scan(string folder, byte[] key, bool exact)
    {
        var newest = new Dictionary<string, (ulong, byte[]?)>();
        if (!Directory.Exists(folder)) return newest;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".log" or ".ldb")) continue;
            byte[] data;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                data = new byte[stream.Length];
                stream.ReadExactly(data);
            }
            catch (IOException)
            {
                continue;
            }
            try
            {
                foreach (var (entryKey, sequence, deleted, entryValue) in extension == ".log" ? LogEntries(data, key, exact) : TableEntries(data, key, exact))
                {
                    var text = Encoding.UTF8.GetString(entryKey);
                    if (newest.TryGetValue(text, out var known) && sequence < known.Item1) continue;
                    newest[text] = (sequence, deleted ? null : entryValue);
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException or OverflowException)
            {
                // A file the reader cannot follow (damaged or another format) is skipped.
            }
        }
        return newest;
    }

    // Whether an entry's key is the wanted key (exact) or starts with it.
    private static bool Matches(ReadOnlySpan<byte> entryKey, byte[] key, bool exact) => exact ? entryKey.SequenceEqual(key) : entryKey.StartsWith(key);

    // Every write of the key in a log file: 32 KiB blocks of records (crc, length, type), whole or in fragments, each a batch of
    // a sequence number, a count, and the entries (type, key, value for a put).
    private static List<(byte[] Key, ulong Sequence, bool Deleted, byte[]? Value)> LogEntries(byte[] data, byte[] key, bool exact)
    {
        const int blockSize = 32768;
        var entries = new List<(byte[], ulong, bool, byte[]?)>();
        var at = 0;
        var pending = new List<byte>();
        while (at + 7 <= data.Length)
        {
            var left = blockSize - at % blockSize;
            if (left < 7)
            {
                at += left;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4, 2));
            var type = data[at + 6];
            at += 7;
            if (type == 0 && length == 0) break;
            if (at + length > data.Length) break;
            var fragment = data.AsSpan(at, length).ToArray();
            at += length;
            byte[] record;
            switch (type)
            {
                case 1:
                    record = fragment;
                    break;
                case 2:
                    pending.Clear();
                    pending.AddRange(fragment);
                    continue;
                case 3:
                    pending.AddRange(fragment);
                    continue;
                case 4:
                    pending.AddRange(fragment);
                    record = pending.ToArray();
                    pending.Clear();
                    break;
                default:
                    return entries;
            }
            if (record.Length < 12) continue;
            var sequence = BinaryPrimitives.ReadUInt64LittleEndian(record);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8, 4));
            var p = 12;
            for (var i = 0; i < count && p < record.Length; i++, sequence++)
            {
                var entryType = record[p++];
                var keyLength = Varint(record, ref p);
                var entryKey = Matches(record.AsSpan(p, keyLength), key, exact) ? record.AsSpan(p, keyLength).ToArray() : null;
                p += keyLength;
                byte[]? value = null;
                if (entryType == 1)
                {
                    var valueLength = Varint(record, ref p);
                    value = record.AsSpan(p, valueLength).ToArray();
                    p += valueLength;
                }
                if (entryKey != null) entries.Add((entryKey, sequence, entryType != 1, value));
            }
        }
        return entries;
    }

    // Every write of the key in a table file: the footer names the index block, whose entries name the data blocks.
    private static List<(byte[] Key, ulong Sequence, bool Deleted, byte[]? Value)> TableEntries(byte[] data, byte[] key, bool exact)
    {
        var entries = new List<(byte[], ulong, bool, byte[]?)>();
        if (data.Length < 48 || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(data.Length - 8)) != 0xdb4775248b80fb57) return entries;
        var footer = data.Length - 48;
        Varint(data, ref footer);
        Varint(data, ref footer);
        var indexOffset = Varint(data, ref footer);
        var indexSize = Varint(data, ref footer);
        foreach (var (_, handle) in BlockEntries(Block(data, indexOffset, indexSize)))
        {
            var p = 0;
            var offset = Varint(handle, ref p);
            var size = Varint(handle, ref p);
            foreach (var (internalKey, value) in BlockEntries(Block(data, offset, size)))
            {
                if (internalKey.Length < 8 || !Matches(internalKey.AsSpan(0, internalKey.Length - 8), key, exact)) continue;
                var trailer = BinaryPrimitives.ReadUInt64LittleEndian(internalKey.AsSpan(internalKey.Length - 8));
                entries.Add((internalKey[..^8], trailer >> 8, (trailer & 0xFF) != 1, value));
            }
        }
        return entries;
    }

    // A block's contents: the bytes at the handle, decompressed when the type byte after them says Snappy.
    private static byte[] Block(byte[] data, int offset, int size)
    {
        var raw = data.AsSpan(offset, size);
        return data[offset + size] == 1 ? Snappy(raw) : raw.ToArray();
    }

    // A block's entries: shared key length, unshared key length, value length, the key's unshared tail, the value; the restart
    // array and its count close the block.
    private static List<(byte[] Key, byte[] Value)> BlockEntries(byte[] block)
    {
        var entries = new List<(byte[], byte[])>();
        if (block.Length < 4) return entries;
        var restarts = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(block.Length - 4));
        var end = block.Length - 4 - 4 * restarts;
        var p = 0;
        var key = Array.Empty<byte>();
        while (p < end)
        {
            var shared = Varint(block, ref p);
            var unshared = Varint(block, ref p);
            var valueLength = Varint(block, ref p);
            var next = new byte[shared + unshared];
            Array.Copy(key, next, shared);
            block.AsSpan(p, unshared).CopyTo(next.AsSpan(shared));
            p += unshared;
            key = next;
            var value = block.AsSpan(p, valueLength).ToArray();
            p += valueLength;
            entries.Add((key, value));
        }
        return entries;
    }

    // Snappy decompression: the uncompressed length, then literal runs and back references.
    private static byte[] Snappy(ReadOnlySpan<byte> source)
    {
        var p = 0;
        var length = Varint(source, ref p);
        var output = new byte[length];
        var o = 0;
        while (p < source.Length)
        {
            var tag = source[p];
            int run, back;
            switch (tag & 3)
            {
                case 0:
                    run = tag >> 2;
                    if (run < 60)
                    {
                        run++;
                        p++;
                    }
                    else
                    {
                        var bytes = run - 59;
                        run = 0;
                        for (var i = 0; i < bytes; i++) run |= source[p + 1 + i] << (8 * i);
                        run++;
                        p += 1 + bytes;
                    }
                    source.Slice(p, run).CopyTo(output.AsSpan(o));
                    p += run;
                    o += run;
                    continue;
                case 1:
                    run = ((tag >> 2) & 7) + 4;
                    back = ((tag >> 5) << 8) | source[p + 1];
                    p += 2;
                    break;
                case 2:
                    run = (tag >> 2) + 1;
                    back = source[p + 1] | (source[p + 2] << 8);
                    p += 3;
                    break;
                default:
                    run = (tag >> 2) + 1;
                    back = (int)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(p + 1, 4));
                    p += 5;
                    break;
            }
            if (back <= 0 || back > o) throw new InvalidDataException("bad Snappy back reference");
            for (var i = 0; i < run; i++) output[o + i] = output[o + i - back];
            o += run;
        }
        if (o != length) throw new InvalidDataException("Snappy length mismatch");
        return output;
    }

    // A LEB128 varint at the position, which moves past it.
    private static int Varint(ReadOnlySpan<byte> data, ref int at)
    {
        var value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = data[at++];
            value |= (b & 0x7F) << shift;
            if (b < 0x80) return value;
        }
        throw new InvalidDataException("varint too long");
    }
}
