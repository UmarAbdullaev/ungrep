using Microsoft.CodeAnalysis;

namespace Ungrep.Code;

/// <summary>
/// What Unity makes of a symbol (SPEC-code-index, Node rules and Serialized fields): the kind of
/// Unity object a type is, which methods Unity calls, and which fields it serializes. Types are
/// matched by full name, so the stubs in tests and the real engine assemblies behave the same.
/// </summary>
internal static class UnityRules
{
    // Most derived first: the first one in a type's base chain wins.
    private static readonly Dictionary<string, string> _unityKinds = new(StringComparer.Ordinal)
    {
        ["UnityEditor.Editor"] = "editor",
        ["UnityEditor.EditorWindow"] = "editor_window",
        ["UnityEditor.PropertyDrawer"] = "property_drawer",
        ["UnityEngine.MonoBehaviour"] = "mono_behaviour",
        ["UnityEngine.ScriptableObject"] = "scriptable_object",
        ["UnityEngine.Object"] = "object",
    };

    /// <summary>Methods Unity calls by name on MonoBehaviours, ScriptableObjects and editor classes.</summary>
    internal static readonly IReadOnlySet<string> Messages = new HashSet<string>(StringComparer.Ordinal)
    {
        "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnValidate",
        "Reset", "OnGUI", "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit", "OnBecameVisible",
        "OnBecameInvisible", "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay", "OnTriggerEnter2D", "OnTriggerExit2D",
        "OnTriggerStay2D", "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay", "OnCollisionEnter2D",
        "OnCollisionExit2D", "OnCollisionStay2D", "OnControllerColliderHit", "OnDrawGizmos", "OnDrawGizmosSelected",
        "OnMouseDown", "OnMouseDrag", "OnMouseEnter", "OnMouseExit", "OnMouseOver", "OnMouseUp", "OnMouseUpAsButton",
        "OnAnimatorMove", "OnAnimatorIK", "OnParticleCollision", "OnParticleTrigger", "OnParticleSystemStopped",
        "OnJointBreak", "OnJointBreak2D", "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage",
        "OnRenderObject", "OnWillRenderObject", "OnTransformChildrenChanged", "OnTransformParentChanged",
        "OnRectTransformDimensionsChange", "OnCanvasGroupChanged", "OnDidApplyAnimationProperties",
        "OnSceneGUI", "OnInspectorGUI", "OnFocus", "OnLostFocus", "OnHierarchyChange", "OnProjectChange",
        "OnSelectionChange", "OnInspectorUpdate", "CreateGUI",
    };

    // Attribute names without the `Attribute` suffix.
    private static readonly string[] _entryAttributes =
    [
        "MenuItem", "ContextMenu", "RuntimeInitializeOnLoadMethod", "InitializeOnLoadMethod", "DidReloadScripts",
        "OnOpenAsset", "PostProcessBuild",
    ];

    private static readonly HashSet<string> _unityValueTypes = new(StringComparer.Ordinal)
    {
        "Vector2", "Vector3", "Vector4", "Vector2Int", "Vector3Int", "Quaternion", "Color", "Color32", "Rect", "RectInt",
        "Bounds", "BoundsInt", "Matrix4x4", "LayerMask", "AnimationCurve", "Gradient", "RectOffset", "GUIStyle", "Hash128",
    };

    /// <summary>`namespace.MetadataName`, e.g. `UnityEngine.MonoBehaviour`, `System.Collections.Generic.List`1`.</summary>
    internal static string FullName(INamedTypeSymbol type) =>
        type.ContainingType is { } outer ? FullName(outer) + "+" + type.MetadataName
        : type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.MetadataName
        : type.MetadataName;

    /// <summary>The `unity` prop: the nearest Unity base class in the chain, or null.</summary>
    internal static string? KindOf(INamedTypeSymbol? type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            if (_unityKinds.TryGetValue(FullName(t), out string? kind))
            {
                return kind;
            }
        }

        return null;
    }

    internal static bool IsUnityObject(ITypeSymbol? type) => type is INamedTypeSymbol named && KindOf(named) is not null;

    /// <summary>True for a method Unity calls by name: a message on a Unity script class.</summary>
    internal static bool IsMessage(IMethodSymbol method) =>
        method is { MethodKind: MethodKind.Ordinary, IsStatic: false }
        && Messages.Contains(method.Name)
        && KindOf(method.ContainingType) is "mono_behaviour" or "scriptable_object" or "editor" or "editor_window";

    /// <summary>The attribute that makes the editor or runtime call this method, or null.</summary>
    internal static string? EntryOf(IMethodSymbol method)
    {
        foreach (AttributeData attribute in method.GetAttributes())
        {
            string? name = UnityAttributeName(attribute);
            if (name is not null && _entryAttributes.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>`InitializeOnLoad` when a type has it, else null.</summary>
    internal static string? EntryOf(INamedTypeSymbol type) =>
        type.GetAttributes().Any(a => UnityAttributeName(a) == "InitializeOnLoad") ? "InitializeOnLoad" : null;

    internal static bool HasAttribute(ISymbol symbol, string unityName) =>
        symbol.GetAttributes().Any(a => UnityAttributeName(a) == unityName);

    /// <summary>`FormerlySerializedAs` names on a field, sorted, without duplicates.</summary>
    internal static IReadOnlyList<string> FormerNames(ISymbol field) =>
        field.GetAttributes()
            .Where(a => UnityAttributeName(a) == "FormerlySerializedAs")
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    // The attribute class's name without `Attribute`, when it lives in a Unity namespace.
    private static string? UnityAttributeName(AttributeData attribute)
    {
        INamedTypeSymbol? type = attribute.AttributeClass;
        if (type is null || type.ContainingNamespace?.ToDisplayString() is not { } ns || !ns.StartsWith("Unity", StringComparison.Ordinal))
        {
            return null;
        }

        return type.Name.EndsWith("Attribute", StringComparison.Ordinal) ? type.Name[..^"Attribute".Length] : type.Name;
    }

    /// <summary>Unity's serialization rules for a field; <paramref name="reference"/> is set for `[SerializeReference]`.</summary>
    internal static bool IsSerialized(IFieldSymbol field, out bool reference)
    {
        reference = HasAttribute(field, "SerializeReference");
        INamedTypeSymbol owner = field.ContainingType;
        bool explicitly = reference || HasAttribute(field, "SerializeField");
        bool serialized =
            (IsUnityObject(owner) || owner.IsSerializable && owner.TypeKind is TypeKind.Class or TypeKind.Struct)
            && !field.IsStatic && !field.IsConst && !field.IsReadOnly
            && (field.DeclaredAccessibility == Accessibility.Public || explicitly)
            && !field.GetAttributes().Any(a => a.AttributeClass is { } c && FullName(c) == "System.NonSerializedAttribute")
            && IsSerializableType(field.Type, reference, inCollection: false);
        reference &= serialized;
        return serialized;
    }

    private static bool IsSerializableType(ITypeSymbol type, bool reference, bool inCollection)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return array.Rank == 1 && !inCollection && IsSerializableType(array.ElementType, reference, inCollection: true);
            case INamedTypeSymbol named:
                if (named.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_Double and not SpecialType.System_Decimal
                    || named.SpecialType == SpecialType.System_String
                    || named.TypeKind == TypeKind.Enum
                    || IsUnityObject(named))
                {
                    return true;
                }

                string full = FullName(named);
                if (full == "System.Collections.Generic.List`1")
                {
                    return !inCollection && IsSerializableType(named.TypeArguments[0], reference, inCollection: true);
                }

                if (named.ContainingNamespace?.ToDisplayString() == "UnityEngine" && _unityValueTypes.Contains(named.Name))
                {
                    return true;
                }

                // BCL types (Dictionary, object, ...) are not serialized even when marked [Serializable].
                if (full.StartsWith("System.", StringComparison.Ordinal) || named.TypeKind == TypeKind.Delegate)
                {
                    return false;
                }

                if (reference && (named.TypeKind == TypeKind.Interface || named.IsAbstract || named.TypeKind == TypeKind.Class))
                {
                    return true;
                }

                return named.IsSerializable && !named.IsAbstract && named.TypeKind is TypeKind.Class or TypeKind.Struct;
            default:
                return false;
        }
    }
}
