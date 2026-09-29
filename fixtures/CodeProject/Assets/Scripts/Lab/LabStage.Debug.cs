using UnityEngine;

namespace Game.Lab
{
    public partial class LabStage : ILabDebug
    {
        public void Trace(int level) => Debug.Log(level);
    }

    public interface ILabDebug
    {
        void Trace(int level);
    }
}
