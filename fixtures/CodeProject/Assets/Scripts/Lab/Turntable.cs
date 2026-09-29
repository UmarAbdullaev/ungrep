using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Lab
{
    public class Turntable : MonoBehaviour
    {
        [FormerlySerializedAs("rpm")]
        [FormerlySerializedAs("_rpm")]
        [SerializeField] private float _speed;

        [field: SerializeField] public Color Tint { get; private set; }

        public event Action<float> Rotated;

        public void Rotate(float amount)
        {
            _speed += amount;
            Rotated?.Invoke(_speed);
        }

        public void Stop() => _speed = 0;

        private void OnOpened() { }
    }
}
