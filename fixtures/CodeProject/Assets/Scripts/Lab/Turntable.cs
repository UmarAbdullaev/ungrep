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

    public class Spectator
    {
        private Turntable _deck;
        private UnityEngine.Events.UnityEvent _cheered = new UnityEngine.Events.UnityEvent();
        public Action<float> Watched;

        public void Watch(Turntable deck)
        {
            _deck = deck;
            _deck.Rotated += OnRotated;
            _deck.Rotated += amount => Watched?.Invoke(amount);
            _cheered.AddListener(OnCheered);
        }

        public void Leave()
        {
            _deck.Rotated -= OnRotated;
            _cheered.RemoveListener(OnCheered);
            Watched = null;
        }

        private void OnRotated(float speed)
        {
            if (_deck.Tint.a > 0)
            {
                _cheered.Invoke();
            }
        }

        private void OnCheered() { }
    }
}
