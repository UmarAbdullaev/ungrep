using System;
using System.Collections;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static void Destroy(Object obj) { }
        public static T Instantiate<T>(T original) where T : Object => original;
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
        public T GetComponent<T>() => default;
        public void SendMessage(string methodName) { }
        public void SendMessageUpwards(string methodName) { }
        public void BroadcastMessage(string methodName) { }
    }

    public class Transform : Component { }

    public class Behaviour : Component
    {
        public bool enabled;
    }

    public class Coroutine { }

    public class MonoBehaviour : Behaviour
    {
        public void Invoke(string methodName, float time) { }
        public void InvokeRepeating(string methodName, float time, float repeatRate) { }
        public void CancelInvoke(string methodName) { }
        public bool IsInvoking(string methodName) => false;
        public Coroutine StartCoroutine(string methodName) => null;
        public Coroutine StartCoroutine(IEnumerator routine) => null;
        public void StopCoroutine(string methodName) { }
    }

    public class ScriptableObject : Object { }

    public sealed class GameObject : Object
    {
        public void SendMessage(string methodName) { }
        public T GetComponent<T>() => default;
    }

    public struct Vector3
    {
        public float x, y, z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => a;
    }

    public struct Color { public float r, g, b, a; }

    public struct LayerMask { public int value; }

    public class AnimationCurve { }

    public static class Debug
    {
        public static void Log(object message) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeReference : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class RequireComponent : Attribute
    {
        public RequireComponent(Type requiredComponent) { }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class ContextMenu : Attribute
    {
        public ContextMenu(string itemName) { }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute { }
}

namespace UnityEngine.Serialization
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true)]
    public sealed class FormerlySerializedAsAttribute : Attribute
    {
        public FormerlySerializedAsAttribute(string oldName) { }
    }
}

namespace UnityEditor
{
    public class Editor : UnityEngine.ScriptableObject
    {
        public virtual void OnInspectorGUI() { }
    }

    public class EditorWindow : UnityEngine.ScriptableObject { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class InitializeOnLoadAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CustomEditor : Attribute
    {
        public CustomEditor(Type inspectedType) { }
    }
}
