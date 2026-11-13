using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// Website-owned offline PAK reader. Migrated on 2026-11-13; maintained independently of developer tooling.
namespace GC.MapExtract.Archives;

public sealed record PakEntry(string Source, string Archive, string Path, long Offset, int StoredBytes,
    int OriginalBytes, uint Compression, string Storage = "pak")
{
    public string Id => Storage == "loose" ? $"{Source}/@loose/{Path}" : $"{Source}/{Archive}/{Path}";
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

// Seek-based PAC1 reader. Format researched against rvost/PakInspector's Apache-2.0 schema;
// no generated parser or alternate extractor is used at runtime.
public static class PakArchive
{
    public const int MaxPayload = 64 * 1024 * 1024;
    public static readonly UTF8Encoding Utf8 = new(false, true);

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 2000 || path.Contains('\\') ||
            path.Split('/').Any(part => string.IsNullOrEmpty(part) || part is "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                IsDevice(part)))
            throw new InvalidDataException("Unsafe archive path: " + path);
    }

    private static bool IsDevice(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            (name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) &&
             (name[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

    public static List<PakEntry> Catalogue(Stream stream, string source, string archive, CancellationToken ct)
    {
        using var reader = new BinaryReader(stream, Utf8, true);
        stream.Position = 0;
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "FORM") throw new InvalidDataException("Not a FORM archive.");
        var formLength = Be(reader);
        if (formLength != stream.Length - 8 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "PAC1")
            throw new InvalidDataException("Unsupported PAC1 envelope or length.");
        long tableStart = -1, tableEnd = -1;
        var dataRanges = new List<(long Start, long End)>();
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (stream.Length - stream.Position < 8) throw new InvalidDataException("Truncated PAK chunk.");
            var kind = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = Be(reader);
            long start = stream.Position, end = start + size;
            if (end > stream.Length) throw new InvalidDataException("PAK chunk exceeds archive.");
            if (kind == "FILE")
            {
                if (tableStart >= 0 || size > 64 * 1024 * 1024) throw new InvalidDataException("Unsupported FILE table.");
                tableStart = start; tableEnd = end;
            }
            if (kind == "DATA") dataRanges.Add((start, end));
            stream.Position = end;
        }
        if (tableStart < 0 || dataRanges.Count == 0) throw new InvalidDataException("PAK lacks FILE or DATA.");
        stream.Position = tableStart;
        var entries = new List<PakEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int nodes = 0;
        ReadNode("", 0, true);
        if (stream.Position != tableEnd) throw new InvalidDataException("Unconsumed FILE metadata.");
        return entries;

        void Need(int bytes)
        {
            if (stream.Position + bytes > tableEnd) throw new InvalidDataException("Truncated FILE table.");
        }
        void ReadNode(string parent, int depth, bool root = false)
        {
            ct.ThrowIfCancellationRequested();
            if (++nodes > 1_000_000 || depth > 64) throw new InvalidDataException("PAK tree exceeds supported bounds.");
            Need(2);
            var type = reader.ReadByte();
            var nameLength = reader.ReadByte();
            Need(nameLength);
            var name = Utf8.GetString(reader.ReadBytes(nameLength));
            // PAC1's root folder may have an empty name; children always have one path segment.
            if (!(root && name.Length == 0))
            {
                ValidatePath(name);
                if (name.Contains('/')) throw new InvalidDataException("PAK node name contains a separator.");
            }
            var path = parent.Length == 0 ? name : parent + "/" + name;
            if (type == 0)
            {
                Need(4);
                var children = reader.ReadUInt32();
                if (children > 1_000_000) throw new InvalidDataException("PAK child count exceeds bounds.");
                for (uint i = 0; i < children; i++) ReadNode(path, depth + 1);
            }
            else if (type == 1)
            {
                ValidatePath(path);
                Need(24);
                long offset = reader.ReadUInt32();
                var stored = reader.ReadUInt32(); var original = reader.ReadUInt32();
                reader.ReadUInt32(); var compression = Be(reader); reader.ReadUInt32();
                if (stored > int.MaxValue || original > int.MaxValue ||
                    !dataRanges.Any(r => offset >= r.Start && offset + stored <= r.End))
                    throw new InvalidDataException("Invalid payload range: " + path);
                if (!paths.Add(path)) throw new InvalidDataException("Ambiguous duplicate path within archive: " + path);
                entries.Add(new(source, archive, path, offset, (int)stored, (int)original, compression));
            }
            else throw new InvalidDataException("Unsupported FILE node type: " + type);
        }
    }

    private static uint Be(BinaryReader reader) => BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));

    public static byte[] Read(Stream stream, PakEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entry.StoredBytes is < 0 or > MaxPayload || entry.OriginalBytes is < 0 or > MaxPayload)
            throw new InvalidDataException($"Payload exceeds {MaxPayload} byte limit: {entry.Id}");
        if (entry.Compression is not (0 or 262)) throw new InvalidDataException("Unsupported PAK compression: " + entry.Compression);
        if (entry.Offset < 0 || entry.Offset + entry.StoredBytes > stream.Length) throw new InvalidDataException("Invalid payload range.");
        stream.Position = entry.Offset;
        var stored = new byte[entry.StoredBytes];
        stream.ReadExactly(stored);
        if (entry.Compression == 0)
        {
            if (stored.Length != entry.OriginalBytes) throw new InvalidDataException("Uncompressed length mismatch.");
            return stored;
        }
        if (stored.Length < 6) throw new InvalidDataException("Truncated zlib payload.");
        using var input = new MemoryStream(stored, false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var decoded = new byte[entry.OriginalBytes];
        zlib.ReadExactly(decoded);
        if (zlib.ReadByte() != -1) throw new InvalidDataException("Decompressed length mismatch.");
        uint a = 1, b = 0;
        foreach (byte value in decoded) { a = (a + value) % 65521; b = (b + a) % 65521; }
        if ((b << 16 | a) != BinaryPrimitives.ReadUInt32BigEndian(stored.AsSpan(stored.Length - 4)))
            throw new InvalidDataException("Zlib checksum mismatch.");
        ct.ThrowIfCancellationRequested();
        return decoded;
    }

    public static string Text(byte[] bytes)
    {
        var text = Utf8.GetString(bytes);
        if (text.Contains('\0')) throw new InvalidDataException("Binary data is not readable UTF-8 text.");
        return text.TrimStart('\uFEFF');
    }
}
