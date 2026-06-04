using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace cherrydev
{
    [RequireComponent(typeof(Selectable))]
    public class DialogAnswerButtonHoverHighlight : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
    {
        [SerializeField] private Color normalColor = Color.white;
        [SerializeField] private Color hoverColor = Color.white;
        [SerializeField] private Vector2 outlineDistance = new Vector2(3f, -3f);

        private Outline outline;

        private void Awake()
        {
            EnsureOutline();
            SetVisible(false);
        }

        public void SetColors(Color normal, Color hover)
        {
            normalColor = normal;
            hoverColor = hover;
            EnsureOutline();
            outline.effectColor = hoverColor;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            SetVisible(true);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            SetVisible(false);
        }

        public void OnSelect(BaseEventData eventData)
        {
            SetVisible(true);
        }

        public void OnDeselect(BaseEventData eventData)
        {
            SetVisible(false);
        }

        private void EnsureOutline()
        {
            if (outline != null)
                return;

            outline = GetComponent<Outline>();
            if (outline == null)
                outline = gameObject.AddComponent<Outline>();

            outline.effectColor = hoverColor;
            outline.effectDistance = outlineDistance;
            outline.useGraphicAlpha = true;
        }

        private void SetVisible(bool visible)
        {
            EnsureOutline();
            outline.enabled = visible;
        }
    }
}
