using UnityEngine;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>A ScriptableObject for `ug asset create` to make by type name.</summary>
    public class UgData : ScriptableObject
    {
        public int count = 3;
        public string label = "data";
    }
}
