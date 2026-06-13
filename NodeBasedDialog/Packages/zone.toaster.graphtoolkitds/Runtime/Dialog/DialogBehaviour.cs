using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;
#if UNITY_LOCALIZATION
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
#endif

namespace cherrydev
{
    public class DialogBehaviour : MonoBehaviour
    {
        [SerializeField] private DialogPresentationConfig _presentationConfig;
        [SerializeField] private float _dialogCharDelay;
        [SerializeField] private List<KeyCode> _nextSentenceKeyCodes;
        [SerializeField] private bool _isCanSkippingText = true;
        [SerializeField] private bool _autoAdvanceSentenceNodes;
        [SerializeField] private float _autoAdvanceSentenceDelay = 0.65f;
        [SerializeField] private bool _enableMarkdownFormatting = true;

        [Header("Existing Answer Buttons")]
        [SerializeField] private bool _useExistingAnswerButtons = true;
        [SerializeField] private GameObject _existingAnswerButtonsRoot;
        [SerializeField] private bool _activateAnswerButtonParents = true;
        [SerializeField] private Button[] _answerButtons;
        [SerializeField] private Color _answerButtonNormalColor = Color.white;
        [SerializeField] private Color _answerButtonHoverColor = Color.white;
#if UNITY_LOCALIZATION
        [SerializeField] private bool _reloadTextOnLanguageChange = true;
#endif

        [Space(10)] 
        [SerializeField] private UnityEvent _onDialogStarted;
        [SerializeField] private UnityEvent _onDialogFinished;

        private DialogNodeGraph _currentNodeGraph;
        private Node _currentNode;
        private DialogVariablesHandler _variablesHandler;

        public AnswerNode CurrentAnswerNode { get; private set; }
        public SentenceNode CurrentSentenceNode { get; private set; }
        public ModifyVariableNode CurrentModifyVariableNode { get; private set; }
        public VariableConditionNode CurrentVariableConditionNode { get; private set; }
        public ExternalFunctionNode CurrentExternalFunctionNode { get; private set; }
        
        public UnityEvent OnDialogStarted => _onDialogStarted;
        public UnityEvent OnDialogFinished => _onDialogFinished;

#if UNITY_LOCALIZATION
        public event Action LanguageChanged;
#endif

        private int _maxAmountOfAnswerButtons;

        private bool _isDialogStarted;
        private bool _isCurrentSentenceSkipped;
        private bool _isCurrentSentenceTyping;
        private bool _isNextSentenceRequested;

        private readonly List<string> _boundFunctionNames = new();
        private readonly List<string> _boundFunctionPrefixes = new();
        private readonly List<int> _visibleAnswerIndices = new();

        public bool IsActive { get; set; } = true;

        public bool IsCanSkippingText
        {
            get => _isCanSkippingText;
            set => _isCanSkippingText = value;
        }

        public event Action SentenceStarted;
        public event Action SentenceEnded;
        public event Action SentenceNodeActivated;
        public event Action<string, string, Sprite> SentenceNodeActivatedWithParameter;
        public event Action AnswerNodeActivated;
        public event Action<int, AnswerNode> AnswerButtonSetUp;
        public event Action<int> MaxAmountOfAnswerButtonsCalculated;
        public event Action<int> AnswerNodeActivatedWithParameter;
        public event Action<int, string> AnswerNodeSetUp;
        public event Action DialogTextCharWrote;
        public event Action<string> DialogTextSkipped;
        public event Action DialogDisabled;

        public event Action<ModifyVariableNode> ModifyVariableNodeActivated;
        public event Action<string> VariableChanged;
        public event Action<string, object> VariableValueChanged;

        public event Action<VariableConditionNode> VariableConditionNodeActivated;
        public event Action<string, bool> VariableConditionEvaluated;
        
        private event Action<DialogVariablesHandler> _dialogFinished;


        public DialogExternalFunctionsHandler ExternalFunctionsHandler { get; private set; }
        public DialogVariablesHandler VariablesHandler => _variablesHandler;
        public bool UseExistingAnswerButtons => _useExistingAnswerButtons && _answerButtons != null && _answerButtons.Length > 0;
        public DialogPresentationConfig PresentationConfig
        {
            get => _presentationConfig;
            set => _presentationConfig = value;
        }

        public float DialogCharDelay => _presentationConfig != null
            ? _presentationConfig.DialogCharDelay
            : Mathf.Max(0f, _dialogCharDelay);

        public bool AutoAdvanceSentenceNodes => _presentationConfig != null
            ? _presentationConfig.AutoAdvanceSentenceNodes
            : _autoAdvanceSentenceNodes;

        public float AutoAdvanceSentenceDelay => _presentationConfig != null
            ? _presentationConfig.AutoAdvanceSentenceDelay
            : Mathf.Max(0f, _autoAdvanceSentenceDelay);

        public bool EnableMarkdownFormatting => _presentationConfig != null
            ? _presentationConfig.EnableMarkdownFormatting
            : _enableMarkdownFormatting;

        private void Awake()
        {
            ExternalFunctionsHandler = new DialogExternalFunctionsHandler();
            HideExistingAnswerButtons();
        }

        private void OnEnable()
        {
#if UNITY_LOCALIZATION
            if (_reloadTextOnLanguageChange)
                LocalizationSettings.SelectedLocaleChanged += OnSelectedLocaleChanged;
#endif
        }

#if UNITY_LOCALIZATION
        private void OnSelectedLocaleChanged(Locale obj)
        {
            if (_isDialogStarted && _currentNode != null)
            {
                LanguageChanged?.Invoke();

                if (_currentNode is SentenceNode sentenceNode)
                {
                    string updatedText = PrepareVisibleText(sentenceNode.GetText());
                    string updatedCharName = PrepareVisibleText(sentenceNode.GetCharacterName());

                    SentenceNodeActivatedWithParameter?.Invoke(updatedCharName, updatedText,
                        sentenceNode.GetCharacterSprite());

                    if (_isCurrentSentenceTyping)
                    {
                        StopAllCoroutines();
                        WriteDialogText(updatedText);
                    }
                    else
                        DialogTextSkipped?.Invoke(updatedText);
                }
                else if (_currentNode is AnswerNode)
                    HandleAnswerNode(_currentNode);
            }
        }
#endif

        private void OnDestroy()
        {
#if UNITY_LOCALIZATION
            if (_reloadTextOnLanguageChange)
                LocalizationSettings.SelectedLocaleChanged -= OnSelectedLocaleChanged;
#endif

            if (_variablesHandler != null)
            {
                _variablesHandler.VariableChanged -= OnVariableChanged;
                _variablesHandler.VariableModified -= OnVariableModified;
            }
        }

        private void Update() => HandleSentenceSkipping();

        /// <summary>
        /// Disable dialog panel
        /// </summary>
        public void Disable() => DialogDisabled?.Invoke();

        public void SetPresentationConfig(DialogPresentationConfig value) => _presentationConfig = value;

        /// <summary>
        /// Setting dialogCharDelay float parameter
        /// </summary>
        /// <param name="value"></param>
        public void SetCharDelay(float value) => _dialogCharDelay = Mathf.Max(0f, value);

        public void SetAutoAdvanceSentenceNodes(bool value) => _autoAdvanceSentenceNodes = value;

        public void SetAutoAdvanceSentenceDelay(float value) => _autoAdvanceSentenceDelay = Mathf.Max(0f, value);

        public void SetEnableMarkdownFormatting(bool value) => _enableMarkdownFormatting = value;

        public void ConfigurePacing(float dialogCharDelay, bool autoAdvanceSentenceNodes, float autoAdvanceSentenceDelay)
        {
            SetCharDelay(dialogCharDelay);
            SetAutoAdvanceSentenceNodes(autoAdvanceSentenceNodes);
            SetAutoAdvanceSentenceDelay(autoAdvanceSentenceDelay);
        }

        /// <summary>
        /// Setting nextSentenceKeyCodes
        /// </summary>
        /// <param name="keyCodes"></param>
        public void SetNextSentenceKeyCodes(List<KeyCode> keyCodes) => _nextSentenceKeyCodes = keyCodes;

        /// <summary>
        /// Requests current sentence skip or advances to the next node. Designed for UI Button OnClick.
        /// </summary>
        public void RequestNextSentence()
        {
            if (!_isDialogStarted || !IsActive)
                return;

            if (_isCurrentSentenceTyping && _isCanSkippingText)
            {
                _isCurrentSentenceSkipped = true;
                return;
            }

            _isNextSentenceRequested = true;
        }

        /// <summary>
        /// Start a dialog
        /// </summary>
        /// <param name="dialogNodeGraph"></param>
        /// <param name="onVariablesHandlerInitialized"></param>
        /// <param name="onDialogFinished"></param>
        public void StartDialog(
            DialogNodeGraph dialogNodeGraph, 
            Action<DialogVariablesHandler> onVariablesHandlerInitialized = null, 
            Action<DialogVariablesHandler> onDialogFinished = null)
        {
            _isDialogStarted = true;
            _boundFunctionNames.Clear();

            if (dialogNodeGraph.NodesList == null)
            {
                Debug.LogWarning("Dialog Graph's node list is empty");
                return;
            }

            _onDialogStarted?.Invoke();
            _currentNodeGraph = dialogNodeGraph;

            InitializeVariablesHandler(dialogNodeGraph);
            
            onVariablesHandlerInitialized?.Invoke(_variablesHandler);
            _dialogFinished = onDialogFinished;
            
            DefineFirstNode(dialogNodeGraph);
            if (!UseExistingAnswerButtons)
                CalculateMaxAmountOfAnswerButtons();

            HandleDialogGraphCurrentNode(_currentNode);
        }

        /// <summary>
        /// Initialize the variables handler for this dialog
        /// </summary>
        /// <param name="dialogNodeGraph"></param>
        private void InitializeVariablesHandler(DialogNodeGraph dialogNodeGraph)
        {
            if (_variablesHandler != null)
            {
                _variablesHandler.VariableChanged -= OnVariableChanged;
                _variablesHandler.VariableModified -= OnVariableModified;
            }

            if (dialogNodeGraph.VariablesConfig != null)
            {
                _variablesHandler = new DialogVariablesHandler(dialogNodeGraph.VariablesConfig);
                _variablesHandler.VariableChanged += OnVariableChanged;
                _variablesHandler.VariableModified += OnVariableModified;
            }
        }

        /// <summary>
        /// Called when a variable changes
        /// </summary>
        /// <param name="variableName"></param>
        private void OnVariableChanged(string variableName)
        {
            VariableChanged?.Invoke(variableName);

            if (_variablesHandler != null)
            {
                Variable variable = _variablesHandler.GetVariable(variableName);

                if (variable != null)
                    VariableValueChanged?.Invoke(variableName, variable.GetValue());
            }
        }

        /// <summary>
        /// Called when a modify variable node is executed
        /// </summary>
        /// <param name="modifyNode"></param>
        private void OnVariableModified(ModifyVariableNode modifyNode) =>
            ModifyVariableNodeActivated?.Invoke(modifyNode);

        /// <summary>
        /// Get variable value by name
        /// </summary>
        /// <typeparam name="T">Type of the variable</typeparam>
        /// <param name="variableName">Name of the variable</param>
        /// <returns>Variable value</returns>
        public T GetVariableValue<T>(string variableName)
        {
            if (_variablesHandler == null)
                return default!;

            return _variablesHandler.GetVariableValue<T>(variableName);
        }

        /// <summary>
        /// Set variable value by name
        /// </summary>
        /// <param name="variableName">Name of the variable</param>
        /// <param name="value">Value to set</param>
        public void SetVariableValue(string variableName, object value) =>
            _variablesHandler?.SetVariableValue(variableName, value);

        /// <summary>
        /// Set variable value directly
        /// </summary>
        public void SetVariableValue(string variableName, bool value) =>
            _variablesHandler?.SetVariableValueDirect(variableName, value);

        public void SetVariableValue(string variableName, int value) =>
            _variablesHandler?.SetVariableValueDirect(variableName, value);

        public void SetVariableValue(string variableName, float value) =>
            _variablesHandler?.SetVariableValueDirect(variableName, value);

        public void SetVariableValue(string variableName, string value) =>
            _variablesHandler?.SetVariableValueDirect(variableName, value);

        public string GetCurrentAnswerTextForDisplayIndex(int displayIndex)
        {
            if (CurrentAnswerNode == null)
                return string.Empty;

            int answerIndex = ResolveAnswerIndex(displayIndex);
            return GetAnswerTextForDisplay(CurrentAnswerNode, answerIndex);
        }

        /// <summary>
        /// This method is designed for ease of use. Calls a method 
        /// BindExternalFunction of the class DialogExternalFunctionsHandler
        /// </summary>
        /// <param name="funcName"></param>
        /// <param name="function"></param>
        public void BindExternalFunction(string funcName, Action function)
        {
            ExternalFunctionsHandler.BindExternalFunction(funcName, function);

            if (!_boundFunctionNames.Contains(funcName))
                _boundFunctionNames.Add(funcName);
        }

        public void BindExternalFunctionPrefix(string prefix, Action<string> function)
        {
            ExternalFunctionsHandler.BindExternalFunctionPrefix(prefix, function);

            if (!_boundFunctionPrefixes.Contains(prefix))
                _boundFunctionPrefixes.Add(prefix);
        }

        /// <summary>
        /// Adding listener to OnDialogFinished UnityEvent
        /// </summary>
        /// <param name="action"></param>
        public void AddListenerToDialogFinishedEvent(UnityAction action) =>
            _onDialogFinished.AddListener(action);

        /// <summary>
        /// Setting currentNode field to Node and call HandleDialogGraphCurrentNode method
        /// </summary>
        /// <param name="node"></param>
        public void SetCurrentNodeAndHandleDialogGraph(Node node)
        {
            _currentNode = node;
            HandleDialogGraphCurrentNode(_currentNode);
        }

        /// <summary>
        /// Setting currentNode field to Node and call HandleDialogGraphCurrentNode method
        /// This method should be called when an answer button is clicked with the button index
        /// </summary>
        /// <param name="answerIndex">Index of the selected answer</param>
        public void SetCurrentNodeAndHandleDialogGraph(int answerIndex)
        {
            int resolvedAnswerIndex = ResolveAnswerIndex(answerIndex);

            if (CurrentAnswerNode != null && resolvedAnswerIndex >= 0 && resolvedAnswerIndex < CurrentAnswerNode.ChildNodes.Count)
            {
                Node selectedNode = CurrentAnswerNode.ChildNodes[resolvedAnswerIndex];
                if (selectedNode != null)
                {
                    HideExistingAnswerButtons();
                    _visibleAnswerIndices.Clear();
                    _currentNode = selectedNode;
                    HandleDialogGraphCurrentNode(_currentNode);
                }
                else
                {
                    Debug.LogWarning($"No child node found at answer index {resolvedAnswerIndex}");
                    EndDialog();
                }
            }
            else
            {
                Debug.LogWarning("Invalid answer index or no current answer node");
                EndDialog();
            }
        }

        public void PerformSentenceNode(SentenceNode sentenceNode, float progress)
        {
            if (sentenceNode == null)
                return;

            CurrentSentenceNode = sentenceNode;
            SentenceNodeActivated?.Invoke();

            string charName = PrepareVisibleText(sentenceNode.GetCharacterName());
            string fullText = PrepareVisibleText(sentenceNode.GetText());
            Sprite charSprite = sentenceNode.GetCharacterSprite();

            SentenceNodeActivatedWithParameter?.Invoke(charName, fullText, charSprite);

            if (!string.IsNullOrEmpty(fullText))
            {
                int visibleCharacters = DialogMarkdownFormatter.CountVisibleCharacters(fullText);
                int charsToShow = Mathf.CeilToInt(visibleCharacters * progress);
                charsToShow = Mathf.Clamp(charsToShow, 0, visibleCharacters);
                string subText = DialogMarkdownFormatter.TakeVisibleCharacters(fullText, charsToShow);
                DialogTextSkipped?.Invoke(subText);
            }
        }

        /// <summary>
        /// Processing dialog current node
        /// </summary>
        /// <param name="currentNode"></param>
        private void HandleDialogGraphCurrentNode(Node currentNode)
        {
            StopAllCoroutines();

            if (currentNode.GetType() == typeof(SentenceNode))
                HandleSentenceNode(currentNode);
            else if (currentNode.GetType() == typeof(AnswerNode))
                HandleAnswerNode(currentNode);
            else if (currentNode.GetType() == typeof(ModifyVariableNode))
                HandleModifyVariableNode(currentNode);
            else if (currentNode.GetType() == typeof(VariableConditionNode))
                HandleVariableConditionNode(currentNode);
            else if (currentNode.GetType() == typeof(ExternalFunctionNode))
                HandleExternalFunctionNode(currentNode);
        }

        /// <summary>
        /// Processing sentence node
        /// </summary>
        /// <param name="currentNode"></param>
        private void HandleSentenceNode(Node currentNode)
        {
            SentenceNode sentenceNode = (SentenceNode)currentNode;
            CurrentSentenceNode = sentenceNode;

            HideExistingAnswerButtons();
            _isCurrentSentenceSkipped = false;

            SentenceNodeActivated?.Invoke();

            string localizedCharName = PrepareVisibleText(sentenceNode.GetCharacterName());
            string localizedText = PrepareVisibleText(sentenceNode.GetText());

            SentenceNodeActivatedWithParameter?.Invoke(localizedCharName, localizedText,
                sentenceNode.GetCharacterSprite());

            if (sentenceNode.IsExternalFunc())
                CallExternalFunction(sentenceNode.GetExternalFunctionName());

            WriteDialogText(localizedText);
        }

        /// <summary>
        /// Processing answer node
        /// </summary>
        /// <param name="currentNode"></param>
        private void HandleAnswerNode(Node currentNode)
        {
            AnswerNode answerNode = (AnswerNode)currentNode;
            CurrentAnswerNode = answerNode;
            RebuildVisibleAnswerIndices(answerNode);

            int amountOfActiveButtons = 0;

            if (UseExistingAnswerButtons)
            {
                amountOfActiveButtons = SetUpExistingAnswerButtons(answerNode);

                if (amountOfActiveButtons == 0)
                {
                    EndDialog();
                    return;
                }

                return;
            }

            AnswerNodeActivated?.Invoke();

            for (int displayIndex = 0; displayIndex < _visibleAnswerIndices.Count; displayIndex++)
            {
                int answerIndex = _visibleAnswerIndices[displayIndex];
                string answerText = GetAnswerTextForDisplay(answerNode, answerIndex);

                AnswerNodeSetUp?.Invoke(displayIndex, answerText);
                AnswerButtonSetUp?.Invoke(displayIndex, answerNode);

                amountOfActiveButtons++;
            }

            if (amountOfActiveButtons == 0)
            {
                EndDialog();
                return;
            }

            AnswerNodeActivatedWithParameter?.Invoke(amountOfActiveButtons);
        }

        /// <summary>
        /// Processing modify variable node
        /// </summary>
        /// <param name="currentNode"></param>
        private void HandleModifyVariableNode(Node currentNode)
        {
            ModifyVariableNode modifyVariableNode = (ModifyVariableNode)currentNode;
            CurrentModifyVariableNode = modifyVariableNode;

            if (_variablesHandler != null)
                _variablesHandler.ExecuteModifyVariableNode(modifyVariableNode);
            else
                Debug.LogWarning("Variables handler is null, cannot execute ModifyVariableNode");

            if (modifyVariableNode.ChildNode != null)
            {
                _currentNode = modifyVariableNode.ChildNode;
                HandleDialogGraphCurrentNode(_currentNode);
            }
            else
                EndDialog();
        }

        /// <summary>
        /// Processing variable condition node
        /// </summary>
        /// <param name="currentNode"></param>
        private void HandleVariableConditionNode(Node currentNode)
        {
            VariableConditionNode variableConditionNode = (VariableConditionNode)currentNode;
            CurrentVariableConditionNode = variableConditionNode;

            if (_variablesHandler == null)
            {
                Debug.LogWarning("Variables handler is null, cannot evaluate VariableConditionNode");
                EndDialog();
                return;
            }

            bool conditionResult = variableConditionNode.EvaluateCondition(_variablesHandler);

            VariableConditionNodeActivated?.Invoke(variableConditionNode);
            VariableConditionEvaluated?.Invoke(
                !string.IsNullOrWhiteSpace(variableConditionNode.ConditionExpression)
                    ? variableConditionNode.ConditionExpression
                    : variableConditionNode.VariableName,
                conditionResult);

            Node nextNode = null;

            if (conditionResult)
            {
                nextNode = variableConditionNode.TrueChildNode;
                Debug.Log($"Variable condition '{variableConditionNode.VariableName}' evaluated to TRUE");
            }
            else
            {
                nextNode = variableConditionNode.FalseChildNode;
                Debug.Log($"Variable condition '{variableConditionNode.VariableName}' evaluated to FALSE");
            }

            if (nextNode != null)
            {
                _currentNode = nextNode;
                HandleDialogGraphCurrentNode(_currentNode);
            }
            else
            {
                Debug.LogWarning(
                    $"No {(conditionResult ? "TRUE" : "FALSE")} path connected for variable condition node");
                EndDialog();
            }
        }

        /// <summary>
        /// Processing external function node
        /// </summary>
        /// <param name="currentNode"></param>
        private void HandleExternalFunctionNode(Node currentNode)
        {
            ExternalFunctionNode externalFunctionNode = (ExternalFunctionNode)currentNode;
            CurrentExternalFunctionNode = externalFunctionNode;

            ExternalFunctionsHandler.CallExternalFunction(externalFunctionNode.GetExternalFunctionName());

            if (externalFunctionNode.ChildNode != null)
            {
                _currentNode = externalFunctionNode.ChildNode;
                HandleDialogGraphCurrentNode(_currentNode);
            }
            else
                EndDialog();
        }

        /// <summary>
        /// Ends the dialog and unbinds all tracked external functions
        /// </summary>
        private void EndDialog()
        {
            _isDialogStarted = false;
            HideExistingAnswerButtons();
            _visibleAnswerIndices.Clear();

            _dialogFinished?.Invoke(_variablesHandler);
            
            foreach (string funcName in _boundFunctionNames)
                ExternalFunctionsHandler.UnbindExternalFunction(funcName);

            foreach (string prefix in _boundFunctionPrefixes)
                ExternalFunctionsHandler.UnbindExternalFunctionPrefix(prefix);

            _boundFunctionNames.Clear();
            _boundFunctionPrefixes.Clear();
            _onDialogFinished?.Invoke();
            _dialogFinished = null;
        }

        /// <summary>
        /// Finds the first node that does not have any parent nodes but has child connections
        /// Updated to work with multiple parents system
        /// </summary>
        /// <param name="dialogNodeGraph"></param>
        private void DefineFirstNode(DialogNodeGraph dialogNodeGraph)
        {
            if (dialogNodeGraph.NodesList.Count == 0)
            {
                Debug.LogWarning("The list of nodes in the DialogNodeGraph is empty");
                return;
            }

            foreach (Node node in dialogNodeGraph.NodesList)
            {
                bool hasParents = false;
                bool hasChildren = false;

                if (node.GetType() == typeof(SentenceNode))
                {
                    SentenceNode sentenceNode = (SentenceNode)node;
                    hasParents = sentenceNode.ParentNodes.Count > 0;
                    hasChildren = sentenceNode.ChildNode != null;
                }
                else if (node.GetType() == typeof(AnswerNode))
                {
                    AnswerNode answerNode = (AnswerNode)node;
                    hasParents = answerNode.ParentNodes.Count > 0;
                    hasChildren = answerNode.ChildNodes.Count > 0 && answerNode.ChildNodes.Any(child => child != null);
                }
                else if (node.GetType() == typeof(ExternalFunctionNode))
                {
                    ExternalFunctionNode externalFunctionNode = (ExternalFunctionNode)node;
                    hasParents = externalFunctionNode.ParentNodes.Count > 0;
                    hasChildren = externalFunctionNode.ChildNode != null;
                }
                else if (node.GetType() == typeof(ModifyVariableNode))
                {
                    ModifyVariableNode modifyVariableNode = (ModifyVariableNode)node;
                    hasParents = modifyVariableNode.ParentNodes.Count > 0;
                    hasChildren = modifyVariableNode.ChildNode != null;
                }
                else if (node.GetType() == typeof(VariableConditionNode))
                {
                    VariableConditionNode variableConditionNode = (VariableConditionNode)node;
                    hasParents = variableConditionNode.ParentNodes.Count > 0;
                    hasChildren = variableConditionNode.TrueChildNode != null ||
                                  variableConditionNode.FalseChildNode != null;
                }

                if (!hasParents && hasChildren)
                {
                    _currentNode = node;
                    return;
                }
            }

            _currentNode = dialogNodeGraph.NodesList[0];
            Debug.LogWarning("No clear starting node found (node without parents). Using first node in list.");
        }

        public void CallExternalFunction(string getExternalFunctionName) =>
            ExternalFunctionsHandler.CallExternalFunction(getExternalFunctionName);

        /// <summary>
        /// Writing dialog text
        /// </summary>
        /// <param name="text"></param>
        private void WriteDialogText(string text) => StartCoroutine(WriteDialogTextRoutine(text));

        /// <summary>
        /// Writing dialog text coroutine
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private IEnumerator WriteDialogTextRoutine(string text)
        {
            _isCurrentSentenceTyping = true;
            _isNextSentenceRequested = false;
            SentenceStarted?.Invoke();

            int visibleCharacters = DialogMarkdownFormatter.CountVisibleCharacters(text);

            for (int index = 0; index < visibleCharacters; index++)
            {
                if (_isCurrentSentenceSkipped)
                {
                    DialogTextSkipped?.Invoke(text);
                    _isCurrentSentenceTyping = false;
                    break;
                }

                DialogTextCharWrote?.Invoke();

                yield return new WaitForSeconds(DialogCharDelay);
            }

            _isCurrentSentenceTyping = false;
            SentenceEnded?.Invoke();

            if (IsCurrentSentenceLeadingToAnswerNode())
            {
                CheckForDialogNextNode();
                yield break;
            }

            if (ShouldAutoAdvanceCurrentSentence())
            {
                float elapsed = 0f;
                float autoAdvanceDelay = AutoAdvanceSentenceDelay;
                while (elapsed < autoAdvanceDelay && !_isNextSentenceRequested && IsActive)
                {
                    if (CheckNextSentenceKeyCodes())
                    {
                        _isNextSentenceRequested = true;
                        break;
                    }

                    elapsed += Time.deltaTime;
                    yield return null;
                }
            }
            else
            {
                yield return new WaitUntil(() => IsActive && (_isNextSentenceRequested || CheckNextSentenceKeyCodes()));
            }

            _isNextSentenceRequested = false;
            CheckForDialogNextNode();
        }

        private bool IsCurrentSentenceLeadingToAnswerNode()
        {
            if (_currentNode == null || _currentNode.GetType() != typeof(SentenceNode))
                return false;

            SentenceNode sentenceNode = (SentenceNode)_currentNode;
            return sentenceNode.ChildNode != null && sentenceNode.ChildNode.GetType() == typeof(AnswerNode);
        }

        private bool ShouldAutoAdvanceCurrentSentence()
        {
            if (!AutoAdvanceSentenceNodes || _currentNode == null || _currentNode.GetType() != typeof(SentenceNode))
                return false;

            SentenceNode sentenceNode = (SentenceNode)_currentNode;
            return sentenceNode.ChildNode != null && sentenceNode.ChildNode.GetType() == typeof(SentenceNode);
        }

        /// <summary>
        /// Checking is next dialog node has a child node
        /// </summary>
        private void CheckForDialogNextNode()
        {
            if (_currentNode.GetType() == typeof(SentenceNode))
            {
                SentenceNode sentenceNode = (SentenceNode)_currentNode;

                if (sentenceNode.ChildNode != null)
                {
                    _currentNode = sentenceNode.ChildNode;
                    HandleDialogGraphCurrentNode(_currentNode);
                }
                else
                    EndDialog();
            }
            else if (_currentNode.GetType() == typeof(ExternalFunctionNode))
            {
                ExternalFunctionNode externalFunctionNode = (ExternalFunctionNode)_currentNode;

                if (externalFunctionNode.ChildNode != null)
                {
                    _currentNode = externalFunctionNode.ChildNode;
                    HandleDialogGraphCurrentNode(_currentNode);
                }
                else
                    EndDialog();
            }
            else if (_currentNode.GetType() == typeof(ModifyVariableNode))
            {
                ModifyVariableNode modifyVariableNode = (ModifyVariableNode)_currentNode;

                if (modifyVariableNode.ChildNode != null)
                {
                    _currentNode = modifyVariableNode.ChildNode;
                    HandleDialogGraphCurrentNode(_currentNode);
                }
                else
                    EndDialog();
            }
        }

        /// <summary>
        /// Calculate max amount of answer buttons
        /// </summary>
        private void CalculateMaxAmountOfAnswerButtons()
        {
            foreach (Node node in _currentNodeGraph.NodesList)
            {
                if (node.GetType() == typeof(AnswerNode))
                {
                    AnswerNode answerNode = (AnswerNode)node;

                    if (answerNode.Answers.Count > _maxAmountOfAnswerButtons)
                        _maxAmountOfAnswerButtons = answerNode.Answers.Count;
                }
            }

            MaxAmountOfAnswerButtonsCalculated?.Invoke(_maxAmountOfAnswerButtons);
        }

        private int SetUpExistingAnswerButtons(AnswerNode answerNode)
        {
            HideExistingAnswerButtons();

            if (_existingAnswerButtonsRoot != null)
                _existingAnswerButtonsRoot.SetActive(true);

            int activeCount = 0;
            int buttonCount = Mathf.Min(_answerButtons.Length, _visibleAnswerIndices.Count);
            for (int displayIndex = 0; displayIndex < buttonCount; displayIndex++)
            {
                int answerIndex = _visibleAnswerIndices[displayIndex];
                Button button = _answerButtons[displayIndex];
                if (button == null)
                    continue;

                string answerText = GetAnswerTextForDisplay(answerNode, answerIndex);

                SetAnswerButtonText(button, answerText);
                SetAnswerButtonColors(button);
                SetAnswerButtonHoverHighlight(button);

                button.onClick.RemoveAllListeners();
                int capturedDisplayIndex = displayIndex;
                button.onClick.AddListener(() => SetCurrentNodeAndHandleDialogGraph(capturedDisplayIndex));

                if (_activateAnswerButtonParents)
                    SetParentsActive(button.transform);

                button.gameObject.SetActive(true);
                button.interactable = true;
                activeCount++;
            }

            return activeCount;
        }

        private void RebuildVisibleAnswerIndices(AnswerNode answerNode)
        {
            _visibleAnswerIndices.Clear();

            if (answerNode == null)
                return;

            int answerCount = Mathf.Min(answerNode.ChildNodes.Count, answerNode.Answers.Count);

            for (int answerIndex = 0; answerIndex < answerCount; answerIndex++)
            {
                if (answerNode.ChildNodes[answerIndex] == null)
                    continue;

                if (!answerNode.IsAnswerAvailable(answerIndex, _variablesHandler))
                    continue;

                _visibleAnswerIndices.Add(answerIndex);
            }

            if (_visibleAnswerIndices.Count == 0)
                Debug.LogWarning("Answer node has no available answers.");
        }

        private int ResolveAnswerIndex(int displayIndex)
        {
            if (displayIndex >= 0 && displayIndex < _visibleAnswerIndices.Count)
                return _visibleAnswerIndices[displayIndex];

            return displayIndex;
        }

        private string GetAnswerTextForDisplay(AnswerNode answerNode, int answerIndex)
        {
            if (answerNode == null)
                return string.Empty;

            return PrepareVisibleText(answerNode.GetAnswerText(answerIndex));
        }

        private string PrepareVisibleText(string text)
        {
            string processedText = text ?? string.Empty;

            if (_variablesHandler != null)
                processedText = DialogTextProcessor.ProcessText(processedText, _variablesHandler);

            return EnableMarkdownFormatting
                ? DialogMarkdownFormatter.ToTmpRichText(processedText)
                : processedText;
        }

        private void HideExistingAnswerButtons()
        {
            if (_answerButtons == null)
                return;

            if (_existingAnswerButtonsRoot != null)
                _existingAnswerButtonsRoot.SetActive(false);

            for (int i = 0; i < _answerButtons.Length; i++)
            {
                if (_answerButtons[i] != null)
                    _answerButtons[i].gameObject.SetActive(false);
            }
        }

        private static void SetAnswerButtonText(Button button, string text)
        {
            TMP_Text tmpText = button.GetComponentInChildren<TMP_Text>(true);
            if (tmpText != null)
            {
                tmpText.text = text;
                return;
            }

            Text legacyText = button.GetComponentInChildren<Text>(true);
            if (legacyText != null)
                legacyText.text = text;
        }

        private void SetAnswerButtonColors(Button button)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = _answerButtonNormalColor;
            colors.highlightedColor = _answerButtonHoverColor;
            colors.selectedColor = _answerButtonHoverColor;
            button.colors = colors;
            button.transition = Selectable.Transition.ColorTint;
        }

        private void SetAnswerButtonHoverHighlight(Button button)
        {
            DialogAnswerButtonHoverHighlight highlight = button.GetComponent<DialogAnswerButtonHoverHighlight>();
            if (highlight == null)
                highlight = button.gameObject.AddComponent<DialogAnswerButtonHoverHighlight>();

            highlight.SetColors(_answerButtonNormalColor, _answerButtonHoverColor);
        }

        private void SetParentsActive(Transform target)
        {
            Transform current = target.parent;
            while (current != null)
            {
                current.gameObject.SetActive(true);

                if (_existingAnswerButtonsRoot != null && current.gameObject == _existingAnswerButtonsRoot)
                    break;

                current = current.parent;
            }
        }

        /// <summary>
        /// Handles text skipping mechanics
        /// </summary>
        private void HandleSentenceSkipping()
        {
            if (!_isDialogStarted || !_isCanSkippingText)
                return;

            if (CheckNextSentenceKeyCodes() && !_isCurrentSentenceSkipped)
                _isCurrentSentenceSkipped = true;
        }

        /// <summary>
        /// Checking whether at least one key from the nextSentenceKeyCodes was pressed
        /// </summary>
        /// <returns></returns>
        private bool CheckNextSentenceKeyCodes()
        {
            if (_nextSentenceKeyCodes == null)
                return false;

            for (int i = 0; i < _nextSentenceKeyCodes.Count; i++)
            {
                if (WasKeyPressedThisFrame(_nextSentenceKeyCodes[i]))
                    return true;
            }

            return false;
        }

        private static bool WasKeyPressedThisFrame(KeyCode keyCode)
        {
            if (Keyboard.current != null && TryConvertKeyCode(keyCode, out Key key))
            {
                KeyControl keyControl = Keyboard.current[key];
                if (keyControl != null && keyControl.wasPressedThisFrame)
                    return true;
            }

            if (Mouse.current != null)
            {
                switch (keyCode)
                {
                    case KeyCode.Mouse0:
                        return Mouse.current.leftButton.wasPressedThisFrame;
                    case KeyCode.Mouse1:
                        return Mouse.current.rightButton.wasPressedThisFrame;
                    case KeyCode.Mouse2:
                        return Mouse.current.middleButton.wasPressedThisFrame;
                }
            }

            return false;
        }

        private static bool TryConvertKeyCode(KeyCode keyCode, out Key key)
        {
            switch (keyCode)
            {
                case KeyCode.None:
                    key = Key.None;
                    return false;
                case KeyCode.Return:
                    key = Key.Enter;
                    return true;
                case KeyCode.KeypadEnter:
                    key = Key.NumpadEnter;
                    return true;
                case KeyCode.BackQuote:
                    key = Key.Backquote;
                    return true;
                case KeyCode.Minus:
                    key = Key.Minus;
                    return true;
                case KeyCode.Equals:
                    key = Key.Equals;
                    return true;
                case KeyCode.LeftBracket:
                    key = Key.LeftBracket;
                    return true;
                case KeyCode.RightBracket:
                    key = Key.RightBracket;
                    return true;
                case KeyCode.Semicolon:
                    key = Key.Semicolon;
                    return true;
                case KeyCode.Quote:
                    key = Key.Quote;
                    return true;
                case KeyCode.Comma:
                    key = Key.Comma;
                    return true;
                case KeyCode.Period:
                    key = Key.Period;
                    return true;
                case KeyCode.Slash:
                    key = Key.Slash;
                    return true;
                case KeyCode.Backslash:
                    key = Key.Backslash;
                    return true;
                case KeyCode.Alpha0:
                    key = Key.Digit0;
                    return true;
                case KeyCode.Alpha1:
                    key = Key.Digit1;
                    return true;
                case KeyCode.Alpha2:
                    key = Key.Digit2;
                    return true;
                case KeyCode.Alpha3:
                    key = Key.Digit3;
                    return true;
                case KeyCode.Alpha4:
                    key = Key.Digit4;
                    return true;
                case KeyCode.Alpha5:
                    key = Key.Digit5;
                    return true;
                case KeyCode.Alpha6:
                    key = Key.Digit6;
                    return true;
                case KeyCode.Alpha7:
                    key = Key.Digit7;
                    return true;
                case KeyCode.Alpha8:
                    key = Key.Digit8;
                    return true;
                case KeyCode.Alpha9:
                    key = Key.Digit9;
                    return true;
            }

            return Enum.TryParse(keyCode.ToString(), out key);
        }
    }
}
