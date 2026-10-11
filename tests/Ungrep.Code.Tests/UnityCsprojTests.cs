namespace Ungrep.Code.Tests;

public sealed class UnityCsprojTests
{
    private const string Root = "/work/Game";

    [Fact]
    public void Reads_what_a_compilation_needs()
    {
        string xml = """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><LangVersion>9.0</LangVersion><AssemblyName>Game</AssemblyName></PropertyGroup>
              <PropertyGroup><DefineConstants>A;B,C;A</DefineConstants><AllowUnsafeBlocks>True</AllowUnsafeBlocks></PropertyGroup>
              <ItemGroup>
                <Compile Include="Assets\Scripts\A.cs" />
                <Compile Include="Assets\Scripts\A.cs" />
                <Compile Include="Assets\Owner&apos;s.cs" />
                <None Include="Assets\readme.txt" />
              </ItemGroup>
              <ItemGroup>
                <Reference Include="X"><HintPath>Library\X.dll</HintPath></Reference>
                <Reference Include="Y"><HintPath>/abs/Y.dll</HintPath></Reference>
                <Reference Include="NoPath" />
              </ItemGroup>
              <ItemGroup><ProjectReference Include=".\Core.csproj"><Name>Core</Name></ProjectReference></ItemGroup>
            </Project>
            """;

        UnityCsproj project = UnityCsproj.Parse(xml, Root, "Game.csproj");

        Assert.Equal("Game", project.AssemblyName);
        Assert.Equal(["Assets/Scripts/A.cs", "Assets/Owner's.cs"], project.Compile);
        Assert.Equal([Path.GetFullPath(Path.Combine(Root, "Library/X.dll")), Path.GetFullPath("/abs/Y.dll")], project.References);
        Assert.Equal(["Core.csproj"], project.ProjectReferences);
        Assert.Equal(["A", "B", "C"], project.Defines);
        Assert.Equal("9.0", project.LangVersion);
        Assert.True(project.AllowUnsafe);
    }

    [Fact]
    public void Defaults_come_from_the_file_name()
    {
        UnityCsproj project = UnityCsproj.Parse("<Project />", Root, "Assembly-CSharp.csproj");

        Assert.Equal("Assembly-CSharp", project.AssemblyName);
        Assert.Equal("9.0", project.LangVersion);
        Assert.False(project.AllowUnsafe);
        Assert.Empty(project.Defines);
    }

    [Fact]
    public void Bad_xml_is_a_format_error() =>
        Assert.Throws<FormatException>(() => UnityCsproj.Parse("<Project>", Root, "Bad.csproj"));
}
