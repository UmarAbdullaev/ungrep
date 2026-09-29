using System.Collections;
using Game.Core;
using UnityEngine;

namespace Game.Lab
{
    [RequireComponent(typeof(Turntable))]
    public partial class LabStage : Stage
    {
        private const string ResetMethod = "ResetStage";

        [SerializeField] private Turntable _turntable;
        [SerializeField] private StageData _data;
        public float speed = 2f;
        public static int Count;
        private int _opened;

        private void Awake()
        {
            _turntable = GetComponent<Turntable>();
            Count++;
        }

        public override void Open(int level)
        {
            _opened = level;
            _turntable.Rotate(speed);
            _turntable.Rotate(speed * 2);
            Invoke(ResetMethod, 1f);
            StartCoroutine(nameof(Spin));
            SendMessage("OnOpened");
#if GAME_DEBUG
            Debug.Log(level);
            Trace(level);
#endif
        }

        private void ResetStage() => _opened = 0;

        private IEnumerator Spin()
        {
            yield return null;
            _turntable.Rotate(1f);
        }

        protected override void OnClosed()
        {
            base.OnClosed();
            var pool = new Pool<Turntable>();
            pool.Release(_turntable);
            System.Action later = () => _turntable.Stop();
            later();
        }

        [ContextMenu("Open level 1")]
        private void OpenFirst() => Open(1);
    }
}
