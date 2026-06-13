using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace cherrydev.Editor.GraphToolkit.Tests
{
    public sealed class DialogBehaviourSuspensionTests
    {
        [UnityTest]
        public IEnumerator SuspensionFreezesTypingUntilResumed()
        {
            GameObject gameObject = new("Dialog Behaviour Suspension Typing Test");
            DialogBehaviour behaviour = gameObject.AddComponent<DialogBehaviour>();
            DialogNodeGraph graph = ScriptableObject.CreateInstance<DialogNodeGraph>();
            SentenceNode sentence = ScriptableObject.CreateInstance<SentenceNode>();
            int writtenCharacters = 0;

            try
            {
                behaviour.SetCharDelay(0f);
                behaviour.DialogTextCharWrote += () => writtenCharacters++;

                sentence.Configure(
                    new Sentence(string.Empty, "ABCDE"),
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty);
                graph.NodesList = new() { sentence };

                behaviour.StartDialog(graph);
                using (behaviour.SuspendDialog())
                {
                    for (int frame = 0; frame < 5; frame++)
                        yield return null;

                    Assert.AreEqual(0, writtenCharacters);
                }

                for (int frame = 0; frame < 20 && writtenCharacters == 0; frame++)
                    yield return null;

                Assert.Greater(writtenCharacters, 0);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(graph);
                Object.DestroyImmediate(sentence);
            }
        }

        [UnityTest]
        public IEnumerator SuspensionFreezesAutoAdvanceUntilResumed()
        {
            GameObject gameObject = new("Dialog Behaviour Suspension Auto Advance Test");
            DialogBehaviour behaviour = gameObject.AddComponent<DialogBehaviour>();
            DialogNodeGraph graph = ScriptableObject.CreateInstance<DialogNodeGraph>();
            SentenceNode first = ScriptableObject.CreateInstance<SentenceNode>();
            SentenceNode second = ScriptableObject.CreateInstance<SentenceNode>();

            try
            {
                behaviour.SetCharDelay(0f);
                behaviour.SetAutoAdvanceSentenceNodes(true);
                behaviour.SetAutoAdvanceSentenceDelay(0f);

                first.Configure(
                    new Sentence(string.Empty, "First"),
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty);
                second.Configure(
                    new Sentence(string.Empty, "Second"),
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty);
                first.ChildNode = second;
                graph.NodesList = new() { first, second };

                behaviour.StartDialog(graph);
                using (behaviour.SuspendDialog())
                {
                    for (int frame = 0; frame < 5; frame++)
                        yield return null;

                    Assert.AreSame(first, behaviour.CurrentSentenceNode);
                }

                for (int frame = 0; frame < 20 && behaviour.CurrentSentenceNode != second; frame++)
                    yield return null;

                Assert.AreSame(second, behaviour.CurrentSentenceNode);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(graph);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void SuspensionIgnoresAnswerSelectionUntilResumed()
        {
            GameObject gameObject = new("Dialog Behaviour Suspension Answer Test");
            DialogBehaviour behaviour = gameObject.AddComponent<DialogBehaviour>();
            DialogNodeGraph graph = ScriptableObject.CreateInstance<DialogNodeGraph>();
            AnswerNode answer = ScriptableObject.CreateInstance<AnswerNode>();
            SentenceNode child = ScriptableObject.CreateInstance<SentenceNode>();

            try
            {
                answer.Configure(new[] { "Continue" });
                answer.ChildNodes[0] = child;
                child.Configure(
                    new Sentence(string.Empty, "Next"),
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty);
                graph.NodesList = new() { answer, child };

                behaviour.StartDialog(graph);
                using (behaviour.SuspendDialog())
                {
                    behaviour.SetCurrentNodeAndHandleDialogGraph(0);
                    Assert.AreSame(answer, behaviour.CurrentAnswerNode);
                    Assert.IsNull(behaviour.CurrentSentenceNode);
                }

                behaviour.SetCurrentNodeAndHandleDialogGraph(0);
                Assert.AreSame(child, behaviour.CurrentSentenceNode);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(graph);
                Object.DestroyImmediate(answer);
                Object.DestroyImmediate(child);
            }
        }
    }
}
