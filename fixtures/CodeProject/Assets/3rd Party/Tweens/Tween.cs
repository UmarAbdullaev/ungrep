using UnityEngine;

namespace Tweens
{
    // Vendored: indexed for declarations only.
    public class Tween : MonoBehaviour
    {
        public float duration;

        public void Play()
        {
            Debug.Log(duration);
            Stop();
        }

        public void Stop() { }
    }
}
