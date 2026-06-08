using UnityEngine;

namespace cherrydev
{
    [CreateAssetMenu(fileName = "DialogPresentationConfig", menuName = "Dialog System/Presentation Config")]
    public sealed class DialogPresentationConfig : ScriptableObject
    {
        [SerializeField, Min(0f)] private float dialogCharDelay = 0.09f;
        [SerializeField] private bool autoAdvanceSentenceNodes;
        [SerializeField, Min(0f)] private float autoAdvanceSentenceDelay = 0.65f;

        public float DialogCharDelay => Mathf.Max(0f, dialogCharDelay);
        public bool AutoAdvanceSentenceNodes => autoAdvanceSentenceNodes;
        public float AutoAdvanceSentenceDelay => Mathf.Max(0f, autoAdvanceSentenceDelay);
    }
}
