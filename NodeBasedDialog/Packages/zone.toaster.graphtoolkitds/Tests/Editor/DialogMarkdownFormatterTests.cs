using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace cherrydev.Editor.GraphToolkit.Tests
{
    public sealed class DialogMarkdownFormatterTests
    {
        [Test]
        public void ToTmpRichTextConvertsSupportedMarkdownSpans()
        {
            string formatted = DialogMarkdownFormatter.ToTmpRichText(
                "**bold** *italic* _also_ ++under++ ~~strike~~ ==mark== `mono` ^sup^ ~sub~");

            Assert.AreEqual(
                "<b>bold</b> <i>italic</i> <i>also</i> <u>under</u> <s>strike</s> " +
                "<mark=#FFFF0080>mark</mark> <mspace=0.6em>mono</mspace> <sup>sup</sup> <sub>sub</sub>",
                formatted);
        }

        [Test]
        public void ToTmpRichTextSupportsNestedFormattingAndRawTmpTags()
        {
            string formatted = DialogMarkdownFormatter.ToTmpRichText(
                "**bold _italic_ <color=red>raw</color>** and 1 < 2 > 1");

            Assert.AreEqual(
                "<b>bold <i>italic</i> <color=red>raw</color></b> and 1 < 2 > 1",
                formatted);
        }

        [Test]
        public void ToTmpRichTextSupportsEscapesAndLineBreaks()
        {
            string formatted = DialogMarkdownFormatter.ToTmpRichText(
                @"\*literal\* \_also\_ \~sub\~ \`code\` \nnext");

            Assert.AreEqual("*literal* _also_ ~sub~ `code` <br>next", formatted);
        }

        [Test]
        public void ToTmpRichTextLeavesUnmatchedMarkersLiteral()
        {
            string formatted = DialogMarkdownFormatter.ToTmpRichText("This *stays and ==also stays");

            Assert.AreEqual("This *stays and ==also stays", formatted);
        }

        [Test]
        public void CountVisibleCharactersIgnoresTmpTagsAndCountsVisibleTmpElements()
        {
            string richText = "A <b>bold</b><br><sprite=0>";

            Assert.AreEqual(8, DialogMarkdownFormatter.CountVisibleCharacters(richText));
        }

        [Test]
        public void TakeVisibleCharactersReturnsWellFormedRichTextPrefix()
        {
            string richText = "A <b>bold</b> text";

            Assert.AreEqual("A <b>bo</b>", DialogMarkdownFormatter.TakeVisibleCharacters(richText, 4));
        }

        [Test]
        public void DialogBehaviourFormatsSentenceNameAndTextAfterVariableInterpolation()
        {
            GameObject gameObject = new("Dialog Behaviour Markdown Test");
            DialogBehaviour behaviour = gameObject.AddComponent<DialogBehaviour>();
            DialogNodeGraph graph = ScriptableObject.CreateInstance<DialogNodeGraph>();
            SentenceNode sentence = ScriptableObject.CreateInstance<SentenceNode>();
            SentenceNode child = ScriptableObject.CreateInstance<SentenceNode>();
            VariablesConfig variables = CreateVariables(
                CreateStringVariable("speaker", "Alex"),
                CreateStringVariable("word", "world"));

            try
            {
                sentence.Configure(
                    new Sentence("**{speaker}**", "Hello =={word}=="),
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty);
                sentence.ChildNode = child;
                graph.NodesList = new List<Node> { sentence, child };
                graph.VariablesConfig = variables;

                string capturedName = string.Empty;
                string capturedText = string.Empty;
                behaviour.SentenceNodeActivatedWithParameter += (characterName, text, _) =>
                {
                    capturedName = characterName;
                    capturedText = text;
                };

                behaviour.StartDialog(graph);

                Assert.AreEqual("<b>Alex</b>", capturedName);
                Assert.AreEqual("Hello <mark=#FFFF0080>world</mark>", capturedText);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(graph);
                Object.DestroyImmediate(sentence);
                Object.DestroyImmediate(child);
                Object.DestroyImmediate(variables);
            }
        }

        [Test]
        public void DialogBehaviourFormatsAnswerChoicesAfterVariableInterpolation()
        {
            GameObject gameObject = new("Dialog Behaviour Answer Markdown Test");
            DialogBehaviour behaviour = gameObject.AddComponent<DialogBehaviour>();
            DialogNodeGraph graph = ScriptableObject.CreateInstance<DialogNodeGraph>();
            AnswerNode answer = ScriptableObject.CreateInstance<AnswerNode>();
            SentenceNode child = ScriptableObject.CreateInstance<SentenceNode>();
            VariablesConfig variables = CreateVariables(CreateStringVariable("choice", "Take it"));

            try
            {
                answer.Configure(new[] { "**{choice}**" });
                answer.ChildNodes[0] = child;
                graph.NodesList = new List<Node> { answer, child };
                graph.VariablesConfig = variables;

                string capturedAnswer = string.Empty;
                behaviour.AnswerNodeSetUp += (_, text) => capturedAnswer = text;

                behaviour.StartDialog(graph);

                Assert.AreEqual("<b>Take it</b>", capturedAnswer);
                Assert.AreEqual("<b>Take it</b>", behaviour.GetCurrentAnswerTextForDisplayIndex(0));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(graph);
                Object.DestroyImmediate(answer);
                Object.DestroyImmediate(child);
                Object.DestroyImmediate(variables);
            }
        }

        [Test]
        public void DialogBehaviourCanDisableMarkdownFormattingWithoutDisablingVariables()
        {
            GameObject gameObject = new("Dialog Behaviour Plain Markdown Test");
            DialogBehaviour behaviour = gameObject.AddComponent<DialogBehaviour>();
            DialogNodeGraph graph = ScriptableObject.CreateInstance<DialogNodeGraph>();
            SentenceNode sentence = ScriptableObject.CreateInstance<SentenceNode>();
            SentenceNode child = ScriptableObject.CreateInstance<SentenceNode>();
            VariablesConfig variables = CreateVariables(CreateStringVariable("word", "world"));

            try
            {
                behaviour.SetEnableMarkdownFormatting(false);
                sentence.Configure(
                    new Sentence(string.Empty, "Hello **{word}**"),
                    false,
                    string.Empty,
                    string.Empty,
                    string.Empty);
                sentence.ChildNode = child;
                graph.NodesList = new List<Node> { sentence, child };
                graph.VariablesConfig = variables;

                string capturedText = string.Empty;
                behaviour.SentenceNodeActivatedWithParameter += (_, text, _) => capturedText = text;

                behaviour.StartDialog(graph);

                Assert.AreEqual("Hello **world**", capturedText);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(graph);
                Object.DestroyImmediate(sentence);
                Object.DestroyImmediate(child);
                Object.DestroyImmediate(variables);
            }
        }

        static VariablesConfig CreateVariables(params Variable[] variables)
        {
            VariablesConfig config = ScriptableObject.CreateInstance<VariablesConfig>();

            foreach (Variable variable in variables)
                config.AddVariable(variable);

            return config;
        }

        static Variable CreateStringVariable(string name, string value)
        {
            var variable = new Variable(name, VariableType.String, false);
            variable.SetValue(value);
            return variable;
        }
    }
}
