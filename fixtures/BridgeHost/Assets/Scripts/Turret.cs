using UnityEngine;

namespace BridgeHost
{
    /// <summary>A component with serialized fields for the editor-ops end-to-end test.</summary>
    public sealed class Turret : MonoBehaviour
    {
        public float speed = 5f;

        public Transform target;
    }
}
