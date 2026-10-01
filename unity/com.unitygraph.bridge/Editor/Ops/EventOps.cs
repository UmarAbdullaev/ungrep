using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `/event/list`, `/event/add`, `/event/remove` (SPEC-editor-ops, Endpoints): the persistent calls
    /// of a UnityEvent field (`Button.onClick`), written through the event's SerializedProperty so they
    /// are one undo group and Unity keeps the call list in the format the inspector writes.
    /// </summary>
    internal static class EventOps
    {
        private const string Calls = "m_PersistentCalls.m_Calls";

        // PersistentListenerMode order in the serialized enum.
        private const int EventDefined = 0;
        private const int Void = 1;
        private const int ObjectMode = 2;
        private const int IntMode = 3;
        private const int FloatMode = 4;
        private const int StringMode = 5;
        private const int BoolMode = 6;

        private const int RuntimeOnly = 2;

        public static Reply List(Request request)
        {
            GameObject target = Addresses.Find(request.Get("object"), out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            string componentName = request.Get("component");
            string eventName = request.Get("event");
            List<Component> components;
            if (string.IsNullOrWhiteSpace(componentName))
            {
                components = target.GetComponents<Component>().Where(c => c != null).ToList();
            }
            else
            {
                Component named = Addresses.Component(target, componentName, out error);
                if (named == null)
                {
                    return Answer.Error(error);
                }

                components = new List<Component> { named };
            }

            var lines = new List<string>();
            foreach (Component component in components)
            {
                var serialized = new SerializedObject(component);
                foreach (SerializedProperty field in Events(serialized))
                {
                    if (!string.IsNullOrWhiteSpace(eventName) && !Matches(field, eventName))
                    {
                        continue;
                    }

                    SerializedProperty calls = field.FindPropertyRelative(Calls);
                    if (calls.arraySize == 0 && string.IsNullOrWhiteSpace(eventName))
                    {
                        continue;
                    }

                    lines.Add(Addresses.Of(target) + "  " + Addresses.ComponentName(component) + "." + Values.ShownName(field.name)
                        + (calls.arraySize == 0 ? "  no calls" : ""));
                    for (int i = 0; i < calls.arraySize; i++)
                    {
                        lines.Add("  " + i.ToString(CultureInfo.InvariantCulture) + "  " + Describe(calls.GetArrayElementAtIndex(i)));
                    }
                }
            }

            if (lines.Count == 0)
            {
                return Answer.Error(string.IsNullOrWhiteSpace(eventName)
                    ? "no event has calls on " + Addresses.Of(target)
                    : "no event " + eventName.Trim() + " on " + Addresses.Of(target) + Names(components));
            }

            return Answer.Lines(lines);
        }

        public static Reply Add(Request request) => EditOps.Edit("event add", request.Get("object"), go =>
        {
            string method = request.Get("method");
            if (string.IsNullOrWhiteSpace(method))
            {
                return Answer.Error("no method given");
            }

            if (!Locate(go, request.Get("component"), request.Get("event"), out Component owner, out SerializedObject serialized, out SerializedProperty field, out string error))
            {
                return Answer.Error(error);
            }

            UnityEngine.Object target = Target(go, request.Get("target"), method.Trim(), out error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            string argument = request.Get("arg");
            MethodInfo chosen = Choose(target.GetType(), method.Trim(), EventArguments(field), argument != null, out int mode, out error);
            if (chosen == null)
            {
                return Answer.Error(error);
            }

            SerializedProperty calls = field.FindPropertyRelative(Calls);
            calls.arraySize++;
            SerializedProperty call = calls.GetArrayElementAtIndex(calls.arraySize - 1);
            Reset(call);
            call.FindPropertyRelative("m_Target").objectReferenceValue = target;
            call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = TypeName(target.GetType());
            call.FindPropertyRelative("m_MethodName").stringValue = chosen.Name;
            call.FindPropertyRelative("m_Mode").enumValueIndex = mode;
            call.FindPropertyRelative("m_CallState").enumValueIndex = RuntimeOnly;
            if (mode != EventDefined && mode != Void)
            {
                string problem = SetArgument(call.FindPropertyRelative("m_Arguments"), mode, chosen.GetParameters()[0].ParameterType, argument);
                if (problem != null)
                {
                    return Answer.Error(problem);
                }
            }

            serialized.ApplyModifiedProperties();
            serialized.Update();
            return Answer.Line(Addresses.Of(go) + "  " + Addresses.ComponentName(owner) + "." + Values.ShownName(field.name)
                + "  + " + Describe(field.FindPropertyRelative(Calls).GetArrayElementAtIndex(calls.arraySize - 1)) + Answer.Unsaved(go));
        });

        public static Reply Remove(Request request) => EditOps.Edit("event remove", request.Get("object"), go =>
        {
            if (!Locate(go, request.Get("component"), request.Get("event"), out Component owner, out SerializedObject serialized, out SerializedProperty field, out string error))
            {
                return Answer.Error(error);
            }

            SerializedProperty calls = field.FindPropertyRelative(Calls);
            string where = Addresses.Of(go) + "  " + Addresses.ComponentName(owner) + "." + Values.ShownName(field.name);
            if (calls.arraySize == 0)
            {
                return Answer.Error(where + " has no calls");
            }

            string text = request.Get("index");
            int index = 0;
            bool bad = string.IsNullOrWhiteSpace(text)
                ? calls.arraySize > 1
                : !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index >= calls.arraySize;
            if (bad)
            {
                return Answer.Error(where + " has " + Answer.Count(calls.arraySize, "call", "calls") + ": give an index from 0 to "
                    + (calls.arraySize - 1).ToString(CultureInfo.InvariantCulture) + " (ug event list)");
            }

            string removed = Describe(calls.GetArrayElementAtIndex(index));
            calls.DeleteArrayElementAtIndex(index);
            serialized.ApplyModifiedProperties();
            return Answer.Line(where + "  - " + removed + Answer.Unsaved(go));
        });

        // The event fields (UnityEvent and its generic forms) among the top-level serialized fields.
        private static IEnumerable<SerializedProperty> Events(SerializedObject serialized) =>
            Values.Fields(serialized).Where(p => p.propertyType == SerializedPropertyType.Generic && p.FindPropertyRelative(Calls) != null);

        private static bool Matches(SerializedProperty field, string name) =>
            string.Equals(field.name, name.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(Values.ShownName(field.name), name.Trim(), StringComparison.OrdinalIgnoreCase);

        private static string Names(IEnumerable<Component> components)
        {
            List<string> names = components
                .SelectMany(c => Events(new SerializedObject(c)).Select(p => Addresses.ComponentName(c) + "." + Values.ShownName(p.name)))
                .ToList();
            return names.Count == 0 ? " (no events on its components)" : " (events: " + string.Join(", ", names) + ")";
        }

        // The component (given, or the one holding the event) and its event field.
        private static bool Locate(GameObject go, string componentName, string eventName, out Component owner, out SerializedObject serialized, out SerializedProperty field, out string error)
        {
            owner = null;
            serialized = null;
            field = null;
            error = null;
            if (string.IsNullOrWhiteSpace(eventName))
            {
                error = "no event given";
                return false;
            }

            List<Component> candidates;
            if (string.IsNullOrWhiteSpace(componentName))
            {
                candidates = go.GetComponents<Component>().Where(c => c != null).ToList();
            }
            else
            {
                Component named = Addresses.Component(go, componentName, out error);
                if (named == null)
                {
                    return false;
                }

                candidates = new List<Component> { named };
            }

            var found = new List<KeyValuePair<Component, SerializedObject>>();
            foreach (Component candidate in candidates)
            {
                var candidateSerialized = new SerializedObject(candidate);
                if (Events(candidateSerialized).Any(p => Matches(p, eventName)))
                {
                    found.Add(new KeyValuePair<Component, SerializedObject>(candidate, candidateSerialized));
                }
            }

            if (found.Count == 0)
            {
                error = "no event " + eventName.Trim() + " on " + Addresses.Of(go) + Names(candidates);
                return false;
            }

            if (found.Count > 1)
            {
                error = "ambiguous: " + eventName.Trim() + " is on " + string.Join(", ", found.Select(f => Addresses.ComponentName(f.Key))) + ": give --component";
                return false;
            }

            owner = found[0].Key;
            serialized = found[0].Value;
            field = Events(serialized).First(p => Matches(p, eventName));
            return true;
        }

        // `Canvas:UiHandler` is that component; a game object address is the component of it that has the
        // method, or the game object itself for its own methods (SetActive).
        private static UnityEngine.Object Target(GameObject from, string text, string method, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "no target given";
                return null;
            }

            UnityEngine.Object found = FindIn(from, text, out string notFound);
            if (found == null)
            {
                int colon = text.LastIndexOf(':');
                GameObject owner = colon > 0 ? FindIn(from, text.Substring(0, colon), out _) as GameObject : null;
                found = owner == null ? null : Addresses.Component(owner, text.Substring(colon + 1), out error);
                if (found == null)
                {
                    error = error ?? notFound;
                }

                return found;
            }

            if (!(found is GameObject go))
            {
                return found;
            }

            List<Component> holders = go.GetComponents<Component>().Where(c => c != null && Methods(c.GetType(), method).Any()).ToList();
            if (holders.Count == 1)
            {
                return holders[0];
            }

            if (holders.Count > 1)
            {
                error = "ambiguous: " + method + " is on " + string.Join(", ", holders.Select(Addresses.ComponentName))
                    + ": name the component (" + Addresses.Of(go) + ":" + Addresses.ComponentName(holders[0]) + ")";
                return null;
            }

            if (Methods(typeof(GameObject), method).Any())
            {
                return go;
            }

            error = Addresses.Of(go) + " has no method " + method + " (components: " + string.Join(", ", go.GetComponents<Component>().Select(Addresses.ComponentName)) + ")";
            return null;
        }

        // A prefab asset being edited is a loaded copy: its calls can only point inside that copy.
        private static UnityEngine.Object FindIn(GameObject from, string text, out string error)
        {
            string contents = Addresses.ContentsPath(from);
            if (contents == null)
            {
                return Addresses.FindObject(text, out error);
            }

            if (!Addresses.SplitPrefab(text.Trim(), out string prefab, out string inner) || prefab != contents)
            {
                error = "a prefab asset can only call methods on its own objects: write the target as " + contents + ":<object>:<Component>";
                return null;
            }

            GameObject root = from.transform.root.gameObject;
            error = null;
            return inner.Length == 0 ? root : Addresses.Walk(Addresses.Children(root), inner, contents, out error);
        }

        // Public instance methods returning void, or a property setter when `name` is a property.
        private static IEnumerable<MethodInfo> Methods(Type type, string name) =>
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.ReturnType == typeof(void) && !m.IsGenericMethod && (m.Name == name || m.Name == "set_" + name))
                .OrderBy(m => m.Name == name ? 0 : 1);

        private static Type[] EventArguments(SerializedProperty field)
        {
            for (Type type = Values.FieldOf(field)?.FieldType; type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.Name.StartsWith("UnityEvent`", StringComparison.Ordinal))
                {
                    return type.GetGenericArguments();
                }
            }

            return Type.EmptyTypes;
        }

        // The overload to persist: the event's own arguments passed on, no argument, or one static argument.
        private static MethodInfo Choose(Type type, string name, Type[] eventArguments, bool hasArgument, out int mode, out string error)
        {
            mode = Void;
            error = null;
            List<MethodInfo> methods = Methods(type, name).ToList();
            if (methods.Count == 0)
            {
                IEnumerable<string> own = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.ReturnType == typeof(void) && !m.IsSpecialName)
                    .Select(m => m.Name)
                    .Distinct()
                    .Take(15);
                error = type.Name + " has no public void method " + name + " (its own: " + string.Join(", ", own) + ")";
                return null;
            }

            if (hasArgument)
            {
                foreach (MethodInfo method in methods)
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 1 && StaticMode(parameters[0].ParameterType) >= 0)
                    {
                        mode = StaticMode(parameters[0].ParameterType);
                        return method;
                    }
                }

                error = type.Name + "." + name + " takes no int, float, string, bool or object argument: drop --arg";
                return null;
            }

            if (eventArguments.Length > 0)
            {
                MethodInfo dynamic = methods.FirstOrDefault(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(eventArguments));
                if (dynamic != null)
                {
                    mode = EventDefined;
                    return dynamic;
                }
            }

            MethodInfo plain = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
            if (plain != null)
            {
                return plain;
            }

            MethodInfo withArgument = methods.FirstOrDefault(m => m.GetParameters().Length == 1 && StaticMode(m.GetParameters()[0].ParameterType) >= 0);
            error = withArgument != null
                ? type.Name + "." + name + " takes a " + withArgument.GetParameters()[0].ParameterType.Name + ": give --arg"
                : type.Name + "." + name + " has parameters an event cannot pass (" + string.Join(", ", methods[0].GetParameters().Select(p => p.ParameterType.Name)) + ")";
            return null;
        }

        private static int StaticMode(Type parameter)
        {
            if (parameter == typeof(int))
            {
                return IntMode;
            }

            if (parameter == typeof(float))
            {
                return FloatMode;
            }

            if (parameter == typeof(string))
            {
                return StringMode;
            }

            if (parameter == typeof(bool))
            {
                return BoolMode;
            }

            return typeof(UnityEngine.Object).IsAssignableFrom(parameter) ? ObjectMode : -1;
        }

        // Growing the array copies the last call's values: start the new one clean.
        private static void Reset(SerializedProperty call)
        {
            SerializedProperty arguments = call.FindPropertyRelative("m_Arguments");
            arguments.FindPropertyRelative("m_ObjectArgument").objectReferenceValue = null;
            arguments.FindPropertyRelative("m_ObjectArgumentAssemblyTypeName").stringValue = "UnityEngine.Object, UnityEngine";
            arguments.FindPropertyRelative("m_IntArgument").intValue = 0;
            arguments.FindPropertyRelative("m_FloatArgument").floatValue = 0;
            arguments.FindPropertyRelative("m_StringArgument").stringValue = "";
            arguments.FindPropertyRelative("m_BoolArgument").boolValue = false;
        }

        private static string SetArgument(SerializedProperty arguments, int mode, Type parameter, string text)
        {
            string field = mode == IntMode ? "m_IntArgument"
                : mode == FloatMode ? "m_FloatArgument"
                : mode == StringMode ? "m_StringArgument"
                : mode == BoolMode ? "m_BoolArgument"
                : "m_ObjectArgument";
            if (mode == ObjectMode)
            {
                arguments.FindPropertyRelative("m_ObjectArgumentAssemblyTypeName").stringValue = TypeName(parameter);
            }

            string problem = Values.Parse(arguments.FindPropertyRelative(field), text);
            return problem == null ? null : "--arg: " + problem;
        }

        // `UiHandler, Assembly-CSharp`: the form the inspector writes and Type.GetType reads.
        private static string TypeName(Type type) => type.FullName + ", " + type.Assembly.GetName().Name;

        private static string Describe(SerializedProperty call)
        {
            UnityEngine.Object target = call.FindPropertyRelative("m_Target").objectReferenceValue;
            string owner = target == null ? "missing target"
                : target is Component component ? Addresses.Of(component.gameObject) + ":" + Addresses.ComponentName(component)
                : target is GameObject go ? Addresses.Of(go)
                : target.name;
            SerializedProperty arguments = call.FindPropertyRelative("m_Arguments");
            int mode = call.FindPropertyRelative("m_Mode").enumValueIndex;
            string argument = mode == EventDefined ? "event argument"
                : mode == IntMode ? arguments.FindPropertyRelative("m_IntArgument").intValue.ToString(CultureInfo.InvariantCulture)
                : mode == FloatMode ? Values.Number(arguments.FindPropertyRelative("m_FloatArgument").floatValue)
                : mode == StringMode ? Values.Quote(arguments.FindPropertyRelative("m_StringArgument").stringValue)
                : mode == BoolMode ? (arguments.FindPropertyRelative("m_BoolArgument").boolValue ? "true" : "false")
                : mode == ObjectMode ? Values.Reference(arguments.FindPropertyRelative("m_ObjectArgument").objectReferenceValue, 0)
                : "";
            int state = call.FindPropertyRelative("m_CallState").enumValueIndex;
            return owner + "." + call.FindPropertyRelative("m_MethodName").stringValue + "(" + argument + ")"
                + (state == 0 ? "  off" : state == 1 ? "  editor and runtime" : "");
        }
    }
}
