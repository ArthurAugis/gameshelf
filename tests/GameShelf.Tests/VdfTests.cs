using System.IO;
using System.Text;
using GameShelf.Steam;
using Xunit;

namespace GameShelf.Tests;

public sealed class VdfTests
{
    [Fact]
    public void ParseText_ReadsNestedBlocks()
    {
        var kv = Vdf.ParseText("""
            "AppState"
            {
                "appid"  "504230"
                "UserConfig"
                {
                    "language" "french"
                }
            }
            """);

        Assert.Equal("504230", kv.Str("AppState", "appid"));
        Assert.Equal("french", kv.Str("AppState", "UserConfig", "language"));
        Assert.NotNull(kv.Node("AppState", "UserConfig"));
    }

    [Fact]
    public void ParseText_KeysAreCaseInsensitive()
    {
        var kv = Vdf.ParseText("\"AppState\" { \"StateFlags\" \"4\" }");

        Assert.Equal("4", kv.Str("appstate", "stateflags"));
    }

    [Fact]
    public void ParseText_SkipsCommentsAndReadsEscapes()
    {
        var kv = Vdf.ParseText("// a comment\n\"name\" \"line one\\nline two\" // trailing comment\n\"path\" \"C:\\\\Games\"");

        Assert.Equal("line one\nline two", kv.Str("name"));
        Assert.Equal(@"C:\Games", kv.Str("path"));
    }

    [Fact]
    public void ParseText_EmptyOrUnclosedTextDoesNotThrow()
    {
        Assert.Empty(Vdf.ParseText(""));
        Assert.Equal("1", Vdf.ParseText("\"a\" { \"b\" \"1\"").Str("a", "b"));
    }

    [Fact]
    public void Get_ReturnsNullWhenAStepIsMissing()
    {
        var kv = Vdf.ParseText("\"a\" { \"b\" \"1\" }");

        Assert.Null(kv.Get("a", "missing"));
        Assert.Null(kv.Get("a", "b", "too deep"));
        Assert.Null(kv.Str("a")); // a node, not a string
    }

    [Fact]
    public void ParseAppInfo_V40_ReadsOnlyWantedApps()
    {
        var path = WriteAppInfo(version: 40, (504230, "Celeste", "Game"), (730, "Counter-Strike 2", "Game"));

        var apps = Vdf.ParseAppInfo(path, new HashSet<uint> { 504230 });

        var celeste = Assert.Single(apps);
        Assert.Equal(504230u, celeste.Key);
        Assert.Equal("Celeste", celeste.Value.Str("common", "name"));
        Assert.Equal("Game", celeste.Value.Str("common", "type"));
    }

    [Fact]
    public void ParseAppInfo_V41_ResolvesKeysFromTheStringTable()
    {
        var path = WriteAppInfo(version: 41, (504230, "Celeste", "Game"));

        var apps = Vdf.ParseAppInfo(path, new HashSet<uint> { 504230 });

        Assert.Equal("Celeste", apps[504230].Str("common", "name"));
    }

    [Fact]
    public void ParseAppInfo_RejectsAnUnknownVersion()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 });
        try
        {
            Assert.Throws<NotSupportedException>(() => { _ = Vdf.ParseAppInfo(path, new HashSet<uint>()); });
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Writes a minimal appinfo.vdf: for each app, an "appinfo" node with common/name and common/type.</summary>
    static string WriteAppInfo(int version, params (uint AppId, string Name, string Type)[] apps)
    {
        // V41 keeps its keys in a table at the end of the file; V40 writes them inline.
        var keys = new List<string>();
        void Key(BinaryWriter writer, string key)
        {
            if (version == 40)
            {
                writer.Write(Encoding.UTF8.GetBytes(key));
                writer.Write((byte)0);
                return;
            }
            if (!keys.Contains(key)) keys.Add(key);
            writer.Write(keys.IndexOf(key));
        }
        void Text(BinaryWriter writer, string key, string value)
        {
            writer.Write((byte)1);
            Key(writer, key);
            writer.Write(Encoding.UTF8.GetBytes(value));
            writer.Write((byte)0);
        }
        void Node(BinaryWriter writer, string key)
        {
            writer.Write((byte)0);
            Key(writer, key);
        }

        const uint magic40 = 0x07564428, magic41 = 0x07564429;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(version == 40 ? magic40 : magic41);
        writer.Write(1u); // universe
        long tableOffsetPosition = stream.Position;
        if (version == 41) writer.Write(0L); // patched below, once the table position is known

        foreach (var (appId, name, type) in apps)
        {
            using var body = new MemoryStream();
            using (var bodyWriter = new BinaryWriter(body))
            {
                Node(bodyWriter, "appinfo");
                Node(bodyWriter, "common");
                Text(bodyWriter, "name", name);
                Text(bodyWriter, "type", type);
                bodyWriter.Write((byte)8); // end of common
                bodyWriter.Write((byte)8); // end of appinfo
                bodyWriter.Write((byte)8); // end of the root
            }
            var data = body.ToArray(); // the writer closed the stream: ToArray still works, Length would not
            writer.Write(appId);
            writer.Write((uint)(60 + data.Length)); // size: the 60 byte entry header plus the data
            writer.Write(new byte[60]);
            writer.Write(data);
        }
        writer.Write(0u); // end marker

        if (version == 41)
        {
            long tableOffset = stream.Position;
            writer.Write(keys.Count);
            foreach (var key in keys)
            {
                writer.Write(Encoding.UTF8.GetBytes(key));
                writer.Write((byte)0);
            }
            stream.Position = tableOffsetPosition;
            writer.Write(tableOffset);
        }
        writer.Flush();

        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }
}
