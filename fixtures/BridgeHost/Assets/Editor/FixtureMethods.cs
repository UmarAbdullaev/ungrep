using System;
using UnityEditor;
using UnityEngine;

namespace BridgeHost
{
    /// <summary>Targets for `ug run` in the end-to-end test.</summary>
    public static class FixtureMethods
    {
        public static string Hello() => "hello from BridgeHost";

        public static void Throws() => throw new InvalidOperationException("thrown on purpose");

        [MenuItem("Tools/BridgeHost/Log Hello")]
        public static void LogHello() => Debug.Log("hello from the menu");
    }
}
