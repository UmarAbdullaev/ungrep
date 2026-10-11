using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ungrep.Code.Tests;

/// <summary>Unity's rules, table-tested against small compilations that use the fixture's engine stubs.</summary>
public sealed class UnityRulesTests
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> _references = new(() =>
    {
        string dir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var bcl = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        var stubs = CSharpCompilation.Create(
            "UnityEngine",
            [CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(CodeFixture.Source, "Library", "Stubs", "UnityEngine.cs")))],
            bcl,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return [.. bcl, stubs.ToMetadataReference()];
    });

    private const string Types = """
        using System;
        using System.Collections.Generic;
        using UnityEngine;
        public enum Mode { A, B }
        public interface IShape { }
        [Serializable] public abstract class Shape : IShape { }
        [Serializable] public class Data { public int x; }
        [Serializable] public struct Point { public int x; }
        [Serializable] public class Box<T> { public T value; }
        public class Plain { public int x; }
        public class Other : MonoBehaviour { }
        public class Asset : ScriptableObject { }
        """;

    [Theory]
    // Rule 1: the owner is a Unity object or [Serializable].
    [InlineData("class C : MonoBehaviour { public int f; }", true)]
    [InlineData("class C : ScriptableObject { public int f; }", true)]
    [InlineData("[Serializable] class C { public int f; }", true)]
    [InlineData("[Serializable] struct C { public int f; }", true)]
    [InlineData("class C { public int f; }", false)]
    // Rule 2: instance fields only.
    [InlineData("class C : MonoBehaviour { public static int f; }", false)]
    [InlineData("class C : MonoBehaviour { public const int f = 1; }", false)]
    [InlineData("class C : MonoBehaviour { public readonly int f; }", false)]
    // Rule 3: public, or [SerializeField] / [SerializeReference].
    [InlineData("class C : MonoBehaviour { int f; }", false)]
    [InlineData("class C : MonoBehaviour { protected int f; }", false)]
    [InlineData("class C : MonoBehaviour { internal int f; }", false)]
    [InlineData("class C : MonoBehaviour { [SerializeField] int f; }", true)]
    [InlineData("class C : MonoBehaviour { [SerializeField] private string f; }", true)]
    // Rule 4: [NonSerialized].
    [InlineData("class C : MonoBehaviour { [NonSerialized] public int f; }", false)]
    // Rule 5: the field's type.
    [InlineData("class C : MonoBehaviour { public bool f; }", true)]
    [InlineData("class C : MonoBehaviour { public double f; }", true)]
    [InlineData("class C : MonoBehaviour { public decimal f; }", false)]
    [InlineData("class C : MonoBehaviour { public Mode f; }", true)]
    [InlineData("class C : MonoBehaviour { public Other f; }", true)]
    [InlineData("class C : MonoBehaviour { public Asset f; }", true)]
    [InlineData("class C : MonoBehaviour { public GameObject f; }", true)]
    [InlineData("class C : MonoBehaviour { public Vector3 f; }", true)]
    [InlineData("class C : MonoBehaviour { public Color f; }", true)]
    [InlineData("class C : MonoBehaviour { public LayerMask f; }", true)]
    [InlineData("class C : MonoBehaviour { public AnimationCurve f; }", true)]
    [InlineData("class C : MonoBehaviour { public Data f; }", true)]
    [InlineData("class C : MonoBehaviour { public Point f; }", true)]
    [InlineData("class C : MonoBehaviour { public Box<int> f; }", true)]
    [InlineData("class C : MonoBehaviour { public Plain f; }", false)]
    [InlineData("class C : MonoBehaviour { public Shape f; }", false)]
    [InlineData("class C : MonoBehaviour { public IShape f; }", false)]
    [InlineData("class C : MonoBehaviour { public object f; }", false)]
    [InlineData("class C : MonoBehaviour { public Action f; }", false)]
    [InlineData("class C : MonoBehaviour { public int[] f; }", true)]
    [InlineData("class C : MonoBehaviour { public List<Data> f; }", true)]
    [InlineData("class C : MonoBehaviour { public List<Other> f; }", true)]
    [InlineData("class C : MonoBehaviour { public int[,] f; }", false)]
    [InlineData("class C : MonoBehaviour { public int[][] f; }", false)]
    [InlineData("class C : MonoBehaviour { public List<int[]> f; }", false)]
    [InlineData("class C : MonoBehaviour { public List<List<int>> f; }", false)]
    [InlineData("class C : MonoBehaviour { public Dictionary<string, int> f; }", false)]
    [InlineData("class C : MonoBehaviour { public HashSet<int> f; }", false)]
    [InlineData("class C<T> : MonoBehaviour { public T f; }", false)]
    // [SerializeReference] allows interfaces and abstract classes.
    [InlineData("class C : MonoBehaviour { [SerializeReference] IShape f; }", true)]
    [InlineData("class C : MonoBehaviour { [SerializeReference] Shape f; }", true)]
    [InlineData("class C : MonoBehaviour { [SerializeReference] List<IShape> f; }", true)]
    public void Serialized_fields_follow_Unitys_rules(string declaration, bool serialized)
    {
        IFieldSymbol field = Compile(declaration).GetTypeByMetadataName(declaration.Contains("C<T>", StringComparison.Ordinal) ? "C`1" : "C")!
            .GetMembers("f").OfType<IFieldSymbol>().Single();

        Assert.Equal(serialized, UnityRules.IsSerialized(field, out bool reference));
        Assert.Equal(serialized && declaration.Contains("SerializeReference", StringComparison.Ordinal), reference);
    }

    [Theory]
    [InlineData("class C : MonoBehaviour { }", "mono_behaviour")]
    [InlineData("class B : MonoBehaviour { } class C : B { }", "mono_behaviour")]
    [InlineData("class C : ScriptableObject { }", "scriptable_object")]
    [InlineData("class C : UnityEditor.Editor { }", "editor")]
    [InlineData("class C : UnityEditor.EditorWindow { }", "editor_window")]
    [InlineData("class C : Component { }", "object")]
    [InlineData("class C { }", null)]
    public void Unity_kind_comes_from_the_base_chain(string declaration, string? kind) =>
        Assert.Equal(kind, UnityRules.KindOf(Compile(declaration).GetTypeByMetadataName("C")));

    [Theory]
    [InlineData("class C : MonoBehaviour { void Update() { } }", true)]
    [InlineData("class C : MonoBehaviour { void OnTriggerEnter2D() { } }", true)]
    [InlineData("class C : ScriptableObject { void OnEnable() { } }", true)]
    [InlineData("class C : UnityEditor.EditorWindow { void OnGUI() { } }", true)]
    [InlineData("class C : MonoBehaviour { static void Update() { } }", false)]
    [InlineData("class C : MonoBehaviour { void Tick() { } }", false)]
    [InlineData("class C { void Update() { } }", false)]
    public void Messages_are_unity_methods_on_unity_classes(string declaration, bool message)
    {
        IMethodSymbol method = Compile(declaration).GetTypeByMetadataName("C")!.GetMembers().OfType<IMethodSymbol>()
            .Single(m => m.MethodKind == MethodKind.Ordinary);

        Assert.Equal(message, UnityRules.IsMessage(method));
    }

    [Fact]
    public void Every_message_name_is_a_valid_identifier() =>
        Assert.All(UnityRules.Messages, m => Assert.True(SyntaxFacts.IsValidIdentifier(m)));

    private static Compilation Compile(string declaration)
    {
        var compilation = CSharpCompilation.Create(
            "Test",
            [CSharpSyntaxTree.ParseText(Types + "\n" + declaration)],
            _references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        return compilation;
    }
}
