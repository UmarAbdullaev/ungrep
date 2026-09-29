using Game.Lab;
using UnityEditor;

namespace Game.EditorTools
{
    [InitializeOnLoad]
    public static class LabTools
    {
        [MenuItem("Game/Open Lab")]
        public static void OpenLab()
        {
            new LabStage().Open(1);
        }
    }

    [CustomEditor(typeof(LabStage))]
    public class LabStageEditor : Editor
    {
        public override void OnInspectorGUI() => LabTools.OpenLab();
    }
}
