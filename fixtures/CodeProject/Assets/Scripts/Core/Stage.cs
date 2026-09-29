using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Core
{
    public interface IOpenable
    {
        void Open(int level);
    }

    public abstract class Stage : MonoBehaviour, IOpenable
    {
        public abstract void Open(int level);

        protected virtual void OnClosed() { }
    }

    [Serializable]
    public class StageData
    {
        public int level;
        public List<Vector3> points;
        private Dictionary<string, int> _lookup;
    }
}
