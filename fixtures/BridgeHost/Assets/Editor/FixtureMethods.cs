using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace BridgeHost
{
    /// <summary>Targets for `ug run` in the end-to-end test.</summary>
    public static class FixtureMethods
    {
        public static string Hello() => "hello from BridgeHost";

        public static void Throws() => throw new InvalidOperationException("thrown on purpose");

        public static void LogError() => Debug.LogError("e2e fixture error");

        [MenuItem("Tools/BridgeHost/Log Hello")]
        public static void LogHello() => Debug.Log("hello from the menu");

        // Arguments, a default, the undo group and the changed scene.
        public static GameObject[] Spawn(string name, Vector3 position, int count = 1)
        {
            var made = new GameObject[count];
            for (int i = 0; i < count; i++)
            {
                made[i] = new GameObject(name);
                made[i].transform.position = position;
                Undo.RegisterCreatedObjectUndo(made[i], "spawn");
            }

            Debug.Log("spawned " + count + " " + name);
            return made;
        }

        // Overloads: a whole number picks int, a fraction picks double.
        public static string Add(int a, int b) => "int " + (a + b);

        public static string Add(double a, double b) => "double " + (a + b);

        public static bool TryHalf(int value, out int half)
        {
            half = value / 2;
            return value % 2 == 0;
        }

        // Waits across editor updates.
        public static async Task<string> Later(int milliseconds)
        {
            await Task.Delay(milliseconds);
            return "waited " + milliseconds + " ms";
        }

        public static IEnumerator Steps(int count)
        {
            for (int i = 0; i < count; i++)
            {
                yield return null;
            }
        }
    }
}
