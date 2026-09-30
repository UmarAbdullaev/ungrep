using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGraph.Bridge.Tests
{
    public enum UgMode
    {
        Walk,
        Run,
        Fly,
    }

    [Serializable]
    public struct UgWave
    {
        public int count;
        public float delay;
    }

    /// <summary>A component with one field of each kind the ops tests read and set.</summary>
    public class UgProbe : MonoBehaviour
    {
        public float speed = 5;
        public int lives = 3;
        public bool armed;
        public string title = "probe";
        public UgMode mode = UgMode.Run;
        public Color tint = Color.red;
        public Vector3 offset = new Vector3(1, 2, 3);
        public LayerMask mask = 1;
        public Transform target;
        public Material paint;
        public GameObject[] guns = new GameObject[2];
        public List<UgWave> waves = new List<UgWave> { new UgWave { count = 3, delay = 0.5f } };
        public UgWave boss = new UgWave { count = 1, delay = 2 };

        [SerializeField]
        private int _secret = 7;

        public int Secret => _secret;
    }
}
