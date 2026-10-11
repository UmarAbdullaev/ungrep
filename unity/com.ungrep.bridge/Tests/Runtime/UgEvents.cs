using UnityEngine;
using UnityEngine.Events;

namespace Ungrep.Bridge.Tests
{
    /// <summary>A component with UnityEvents and methods to wire them to, for the event ops tests.</summary>
    public class UgEvents : MonoBehaviour
    {
        public UnityEvent fired = new UnityEvent();
        public UnityEvent<float> changed = new UnityEvent<float>();

        public int pings;
        public float level;
        public string said = "";
        public bool flag;
        public GameObject picked;

        public void Ping() => pings++;

        public void SetLevel(float value) => level = value;

        public void Say(string text) => said = text;

        public void Flag(bool value) => flag = value;

        public void Pick(GameObject target) => picked = target;

        public void Both() => pings += 10;

        public void Both(int count) => pings += count;

        public void Wide(int a, int b)
        {
        }

        public int Value() => 1;
    }
}
