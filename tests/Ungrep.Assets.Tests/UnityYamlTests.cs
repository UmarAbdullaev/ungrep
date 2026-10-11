using System.Text;

namespace Ungrep.Assets.Tests;

/// <summary>SPEC-asset-index, Reading Unity YAML: one case per construct Unity writes.</summary>
public sealed class UnityYamlTests
{
    private static (string Text, List<string> Problems) Read(string yaml)
    {
        var problems = new List<string>();
        IReadOnlyList<YamlDocument> documents = UnityYaml.Read(new StringReader(yaml.ReplaceLineEndings("\n")), problems);
        var text = new StringBuilder();
        foreach (YamlDocument document in documents)
        {
            text.Append($"{document.ClassId} {document.ClassName} &{document.FileId}{(document.Stripped ? " stripped" : "")} {document.LineStart}-{document.LineEnd}\n");
            foreach (YamlLeaf leaf in document.Leaves)
            {
                text.Append($"  {leaf.Line} {leaf.Path} = {leaf.Value}");
                if (leaf.Ref is { } r)
                {
                    text.Append($" -> {r.FileId}/{r.Guid ?? "-"}/{r.Type}");
                }

                text.Append('\n');
            }
        }

        return (text.ToString(), problems);
    }

    [Fact]
    public void Documents_headers_and_stripped()
    {
        var (text, problems) = Read("""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1 &100
            GameObject:
              m_Name: Turntable
              m_IsActive: 1
            --- !u!114 &-7093541022374150553 stripped
            MonoBehaviour:
              m_CorrespondingSourceObject: {fileID: 6565928212424149828, guid: 90323ddedac07474eb31a0ab2441a061, type: 3}
              m_PrefabInstance: {fileID: 223296611}
            """);

        Assert.Empty(problems);
        Assert.Equal("""
            1 GameObject &100 3-6
              5 m_Name = Turntable
              6 m_IsActive = 1
            114 MonoBehaviour &-7093541022374150553 stripped 7-10
              9 m_CorrespondingSourceObject = {fileID: 6565928212424149828, guid: 90323ddedac07474eb31a0ab2441a061, type: 3} -> 6565928212424149828/90323ddedac07474eb31a0ab2441a061/3
              10 m_PrefabInstance = {fileID: 223296611} -> 223296611/-/0

            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Lists_at_the_key_indent_and_deeper_and_nested_maps()
    {
        var (text, problems) = Read("""
            --- !u!1001 &5
            PrefabInstance:
              m_Modification:
                serializedVersion: 3
                m_Modifications:
                - target: {fileID: 1, guid: aa, type: 3}
                  propertyPath: m_Name
                  value: TopBar
                  objectReference: {fileID: 0}
                - target: {fileID: 2, guid: aa, type: 3}
                  propertyPath: speed
                  value: 3
                m_RemovedComponents: []
              m_Materials:
                - {fileID: 2100000, guid: bb, type: 2}
                -
                  inner: 1
              after: {x: 0, y: 1}
            """);

        Assert.Empty(problems);
        Assert.Equal("""
            1001 PrefabInstance &5 1-18
              4 m_Modification.serializedVersion = 3
              6 m_Modification.m_Modifications[0].target = {fileID: 1, guid: aa, type: 3} -> 1/aa/3
              7 m_Modification.m_Modifications[0].propertyPath = m_Name
              8 m_Modification.m_Modifications[0].value = TopBar
              9 m_Modification.m_Modifications[0].objectReference = {fileID: 0} -> 0/-/0
              10 m_Modification.m_Modifications[1].target = {fileID: 2, guid: aa, type: 3} -> 2/aa/3
              11 m_Modification.m_Modifications[1].propertyPath = speed
              12 m_Modification.m_Modifications[1].value = 3
              13 m_Modification.m_RemovedComponents = []
              15 m_Materials[0] = {fileID: 2100000, guid: bb, type: 2} -> 2100000/bb/2
              17 m_Materials[1].inner = 1
              18 after = {x: 0, y: 1}

            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Quoted_scalars_go_on_over_lines_and_plain_ones_are_cut()
    {
        var (text, problems) = Read("""
            --- !u!114 &1
            MonoBehaviour:
              m_Text: 'It''s a

                long one'
              m_Other: "tab\there A \"q\""
              m_Plain: first line
                second line
              m_Name:
              m_Flow: {fileID: 11400000,
                guid: cc, type: 2}
              m_Empty: ''
            """);

        Assert.Empty(problems);
        Assert.Equal(
            "114 MonoBehaviour &1 1-12\n"
            + "  3 m_Text = It's a long one\n"
            + "  6 m_Other = tab\there A \"q\"\n"
            + "  7 m_Plain = first line\n"
            + "  10 m_Flow = {fileID: 11400000, guid: cc, type: 2} -> 11400000/cc/2\n"
            + "  12 m_Empty = \n",
            text);
    }

    [Theory]
    [InlineData("  m_Text: |\n    block", "line 3: block scalar; rest of the document skipped")]
    [InlineData("  m_Ref: *anchor", "line 3: anchor, alias or tag; rest of the document skipped")]
    [InlineData("\tm_Name: x", "line 3: tab indentation; rest of the document skipped")]
    [InlineData("  just text", "line 3: not a key or a list item; rest of the document skipped")]
    [InlineData("  - - nested", "line 3: nested list item on one line; rest of the document skipped")]
    public void Unsupported_constructs_stop_the_document_not_the_file(string body, string problem)
    {
        var (text, problems) = Read($"--- !u!114 &1\nMonoBehaviour:\n{body}\n  m_After: 1\n--- !u!1 &2\nGameObject:\n  m_Name: Next\n");

        Assert.Equal([problem], problems);
        Assert.DoesNotContain("m_After", text, StringComparison.Ordinal);
        Assert.Contains(" m_Name = Next\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_first_problem_is_reported_and_a_missing_class_line_counts()
    {
        var (text, problems) = Read("--- !u!1 &1\n  m_Name: x\n--- !u!1 &2\n  m_Name: y\n");

        Assert.Equal(["line 2: expected a class name; rest of the document skipped"], problems);
        Assert.Equal("1  &1 1-2\n1  &2 3-4\n", text);
    }

    [Theory]
    [InlineData("{fileID: 0}", 0L, null, 0)]
    [InlineData("{fileID: -12, guid: ab, type: 3}", -12L, "ab", 3)]
    [InlineData("{x: 0, y: 1}", null, null, 0)]
    [InlineData("{}", null, null, 0)]
    [InlineData("{fileID: abc}", null, null, 0)]
    public void References_are_flow_maps_with_a_file_id(string raw, long? fileId, string? guid, int type)
    {
        ObjectRef? reference = UnityYaml.ParseRef(raw);

        Assert.Equal(fileId, reference?.FileId);
        Assert.Equal(guid, reference?.Guid);
        Assert.Equal(type, reference?.Type ?? 0);
    }

    [Fact]
    public void Meta_files_give_guid_folder_and_main_object()
    {
        MetaFile script = MetaFile.Parse(new StringReader("fileFormatVersion: 2\nguid: 13e3a7a23ad7d9b459ed2524ac18624d\nMonoImporter:\n  serializedVersion: 2\n"));
        MetaFile asset = MetaFile.Parse(new StringReader("fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nNativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 11400000\n  userData: \n"));
        MetaFile folder = MetaFile.Parse(new StringReader("fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nfolderAsset: yes\nDefaultImporter:\n"));
        MetaFile bad = MetaFile.Parse(new StringReader("fileFormatVersion: 2\nguid: NOT-A-GUID\n"));
        MetaFile late = MetaFile.Parse(new StringReader("guid: 0123456789abcdef0123456789abcdef\nTextureImporter:\n" + string.Concat(Enumerable.Repeat("  a: 1\n", 10)) + "  mainObjectFileID: 5\n"));

        Assert.Equal(new MetaFile("13e3a7a23ad7d9b459ed2524ac18624d", false, null), script);
        Assert.Equal(new MetaFile("0123456789abcdef0123456789abcdef", false, 11400000), asset);
        Assert.True(folder.Folder);
        Assert.Null(bad.Guid);
        Assert.Null(late.MainObjectFileId);
    }
}
