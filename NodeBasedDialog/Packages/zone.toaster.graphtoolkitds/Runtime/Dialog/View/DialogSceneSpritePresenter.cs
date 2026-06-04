using System.Collections;
using UnityEngine;

namespace cherrydev
{
    public class DialogSceneSpritePresenter : MonoBehaviour
    {
        [SerializeField] private DialogBehaviour dialogBehaviour;
        [SerializeField] private SpriteRenderer targetRenderer;
        [SerializeField] private bool hideWhenSpriteIsNull = true;
        [SerializeField] private bool keepVisibleAfterDialog = true;

        [Header("Pose Change Pop")]
        [SerializeField] private bool animateSpriteChanges = true;
        [SerializeField] private float popScale = 1.045f;
        [SerializeField] private float popOutDuration = 0.06f;
        [SerializeField] private float popBackDuration = 0.09f;

        private Coroutine popRoutine;
        private Vector3 baseScale;
        private Sprite currentSprite;

        private void Awake()
        {
            if (targetRenderer == null)
                targetRenderer = GetComponent<SpriteRenderer>();

            baseScale = targetRenderer != null ? targetRenderer.transform.localScale : transform.localScale;
        }

        private void OnEnable()
        {
            if (dialogBehaviour != null)
            {
                dialogBehaviour.SentenceNodeActivatedWithParameter += HandleSentence;
                dialogBehaviour.OnDialogFinished.AddListener(KeepVisibleAfterDialog);
                dialogBehaviour.DialogDisabled += KeepVisibleAfterDialog;
            }
        }

        private void OnDisable()
        {
            if (dialogBehaviour != null)
            {
                dialogBehaviour.SentenceNodeActivatedWithParameter -= HandleSentence;
                dialogBehaviour.OnDialogFinished.RemoveListener(KeepVisibleAfterDialog);
                dialogBehaviour.DialogDisabled -= KeepVisibleAfterDialog;
            }
        }

        public void SetDialogBehaviour(DialogBehaviour behaviour)
        {
            if (dialogBehaviour == behaviour)
                return;

            if (isActiveAndEnabled && dialogBehaviour != null)
            {
                dialogBehaviour.SentenceNodeActivatedWithParameter -= HandleSentence;
                dialogBehaviour.OnDialogFinished.RemoveListener(KeepVisibleAfterDialog);
                dialogBehaviour.DialogDisabled -= KeepVisibleAfterDialog;
            }

            dialogBehaviour = behaviour;

            if (isActiveAndEnabled && dialogBehaviour != null)
            {
                dialogBehaviour.SentenceNodeActivatedWithParameter += HandleSentence;
                dialogBehaviour.OnDialogFinished.AddListener(KeepVisibleAfterDialog);
                dialogBehaviour.DialogDisabled += KeepVisibleAfterDialog;
            }
        }

        public void Hide()
        {
            if (keepVisibleAfterDialog)
            {
                KeepVisibleAfterDialog();
                return;
            }

            if (targetRenderer != null && hideWhenSpriteIsNull)
                targetRenderer.enabled = false;
        }

        private void HandleSentence(string characterName, string text, Sprite sprite)
        {
            if (targetRenderer == null)
                return;

            bool spriteChanged = currentSprite != sprite;
            currentSprite = sprite;
            targetRenderer.sprite = sprite;

            if (hideWhenSpriteIsNull)
                targetRenderer.enabled = sprite != null;

            if (spriteChanged && animateSpriteChanges && sprite != null)
                PlayPop();
        }

        private void KeepVisibleAfterDialog()
        {
            if (!keepVisibleAfterDialog || targetRenderer == null)
                return;

            if (currentSprite != null)
            {
                targetRenderer.enabled = true;
                targetRenderer.sprite = currentSprite;
            }
        }

        private void PlayPop()
        {
            if (popRoutine != null)
                StopCoroutine(popRoutine);

            popRoutine = StartCoroutine(PopRoutine());
        }

        private IEnumerator PopRoutine()
        {
            Transform target = targetRenderer.transform;
            Vector3 from = target.localScale;
            Vector3 peak = baseScale * popScale;

            yield return ScaleRoutine(target, from, peak, popOutDuration);
            yield return ScaleRoutine(target, peak, baseScale, popBackDuration);
            popRoutine = null;
        }

        private static IEnumerator ScaleRoutine(Transform target, Vector3 from, Vector3 to, float duration)
        {
            if (duration <= 0f)
            {
                target.localScale = to;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                target.localScale = Vector3.Lerp(from, to, t);
                yield return null;
            }

            target.localScale = to;
        }
    }
}
