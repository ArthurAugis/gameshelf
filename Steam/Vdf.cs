using System.Globalization;
using System.IO;
using System.Text;

namespace GameShelf.Steam;

/// <summary>
/// A parsed VDF node. Each key maps to either a string or a nested <see cref="Kv"/>.
/// Keys are case-insensitive, like in Steam.
/// </summary>
internal sealed class Kv : Dictionary<string, object>
{
    public Kv() : base(StringComparer.OrdinalIgnoreCase) { }

    /// <summary>Follows <paramref name="keys"/> down the tree. Null if any step is missing.</summary>
    public object? Get(params string[] keys)
    {
        object? current = this;
        foreach (var key in keys)
        {
            if (current is not Kv node || !node.TryGetValue(key, out current)) return null;
        }
        return current;
    }

    public string? Str(params string[] keys) => Get(keys) as string;

    public Kv? Node(params string[] keys) => Get(keys) as Kv;
}

/// <summary>Parsers for Steam's text VDF files and for the binary appinfo.vdf cache.</summary>
internal static class Vdf
{
    // ---- Text VDF (appmanifest_*.acf, libraryfolders.vdf, localconfig.vdf) ----

    public static Kv ParseText(string text)
    {
        int pos = 0;
        return ReadBlock(text, ref pos);
    }

    static Kv ReadBlock(string text, ref int pos)
    {
        var node = new Kv();
        while (true)
        {
            var key = NextToken(text, ref pos);
            if (key is null or "}") return node;
            var value = NextToken(text, ref pos);
            node[key] = value == "{" ? ReadBlock(text, ref pos) : value ?? "";
        }
    }

    /// <summary>Next quoted string, "{" or "}". Null at the end of the text.</summary>
    static string? NextToken(string text, ref int pos)
    {
        // Skip whitespace and // comments.
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos])) pos++;
            else if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
                while (pos < text.Length && text[pos] != '\n') pos++;
            else break;
        }
        if (pos >= text.Length) return null;
        if (text[pos] is '{' or '}') return text[pos++].ToString();
        if (text[pos] != '"') return null;

        var token = new StringBuilder();
        for (pos++; pos < text.Length && text[pos] != '"'; pos++)
        {
            if (text[pos] == '\\' && pos + 1 < text.Length)
            {
                pos++;
                token.Append(text[pos] switch { 'n' => '\n', 't' => '\t', _ => text[pos] });
            }
            else token.Append(text[pos]);
        }
        pos++; // closing quote
        return token.ToString();
    }

    // ---- Binary appinfo.vdf (app names and types) ----

    const uint AppInfoMagicV40 = 0x07564428; // keys are inline null-terminated strings
    const uint AppInfoMagicV41 = 0x07564429; // keys are indexes into a string table at the end of the file

    // Per-entry header after the size field: state, last update, token, sha1, change number, binary sha1.
    const int AppInfoEntryHeaderSize = 60;

    /// <summary>Reads appinfo.vdf. Only the entries whose app id is in <paramref name="wanted"/> are decoded.</summary>
    public static Dictionary<uint, Kv> ParseAppInfo(string path, HashSet<uint> wanted)
    {
        using var reader = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
        uint magic = reader.ReadUInt32();
        reader.ReadUInt32(); // universe

        string[]? stringTable = null;
        if (magic == AppInfoMagicV41)
        {
            long tableOffset = reader.ReadInt64(), entriesStart = reader.BaseStream.Position;
            reader.BaseStream.Position = tableOffset;
            stringTable = new string[reader.ReadInt32()];
            for (int i = 0; i < stringTable.Length; i++) stringTable[i] = ReadCString(reader);
            reader.BaseStream.Position = entriesStart;
        }
        else if (magic != AppInfoMagicV40)
        {
            throw new NotSupportedException($"Unsupported appinfo.vdf version (magic {magic:X}).");
        }

        var apps = new Dictionary<uint, Kv>();
        while (true)
        {
            uint appId = reader.ReadUInt32();
            if (appId == 0) break; // end marker
            uint size = reader.ReadUInt32();
            long entryEnd = reader.BaseStream.Position + size;
            if (wanted.Contains(appId))
            {
                reader.BaseStream.Position += AppInfoEntryHeaderSize;
                var root = ReadBinaryBlock(reader, stringTable);
                apps[appId] = root.Node("appinfo") ?? root;
            }
            reader.BaseStream.Position = entryEnd;
        }
        return apps;
    }

    static Kv ReadBinaryBlock(BinaryReader reader, string[]? stringTable)
    {
        const byte EndOfBlock = 8;
        var node = new Kv();
        while (true)
        {
            byte type = reader.ReadByte();
            if (type == EndOfBlock) return node;
            string key = stringTable is null ? ReadCString(reader) : stringTable[reader.ReadInt32()];
            node[key] = type switch
            {
                0 => ReadBinaryBlock(reader, stringTable),
                1 => ReadCString(reader),
                2 or 4 or 6 => reader.ReadUInt32().ToString(CultureInfo.InvariantCulture),
                3 => reader.ReadSingle().ToString(CultureInfo.InvariantCulture),
                7 or 10 => reader.ReadUInt64().ToString(CultureInfo.InvariantCulture),
                _ => throw new InvalidDataException($"Unknown binary VDF value type {type}."),
            };
        }
    }

    static string ReadCString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        for (byte b; (b = reader.ReadByte()) != 0;) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
