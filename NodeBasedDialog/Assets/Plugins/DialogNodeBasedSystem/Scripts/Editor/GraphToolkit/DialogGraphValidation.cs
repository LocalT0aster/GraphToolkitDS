using System;
using System.Collections.Generic;
using System.Linq;
using Unity.GraphToolkit.Editor;
using GtkNode = Unity.GraphToolkit.Editor.Node;

namespace cherrydev.Editor.GraphToolkit
{
    internal static class DialogGraphOptionReader
    {
        public static T Read<T>(GtkNode node, string optionName, T fallback)
        {
            INodeOption option = node.GetNodeOptionByName(optionName);
            return option != null && option.TryGetValue(out T value) ? value : fallback;
        }
    }

    internal readonly struct DialogGraphIssue
    {
        public DialogGraphIssue(string message, bool isError, object context = null)
        {
            Message = message;
            IsError = isError;
            Context = context;
        }

        public string Message { get; }
        public bool IsError { get; }
        public object Context { get; }
    }

    internal static class DialogGraphValidator
    {
        public static IReadOnlyList<DialogGraphIssue> Validate(DialogAuthoringGraph graph)
        {
            List<DialogGraphIssue> issues = new();

            if (graph == null)
            {
                issues.Add(new DialogGraphIssue("Dialog graph is null.", true));
                return issues;
            }

            List<INode> nodes = graph.GetNodes().ToList();
            List<DialogStartNode> startNodes = nodes.OfType<DialogStartNode>().ToList();

            if (startNodes.Count != 1)
                issues.Add(new DialogGraphIssue($"Dialog graph must contain exactly one Start node; found {startNodes.Count}.", true));
            else if (GetConnectedTargets(startNodes[0], DialogGraphPorts.Next).Count != 1)
                issues.Add(new DialogGraphIssue("Start node must connect to exactly one runtime node.", true, startNodes[0]));

            int settingsNodeCount = nodes.OfType<DialogGraphSettingsNode>().Count();

            if (settingsNodeCount > 1)
                issues.Add(new DialogGraphIssue($"Dialog graph should contain at most one Graph Settings node; found {settingsNodeCount}.", false));

            foreach (INode node in nodes)
                ValidateNode(node, issues);

            ValidateCycles(nodes, issues);
            return issues;
        }

        private static void ValidateNode(INode node, List<DialogGraphIssue> issues)
        {
            switch (node)
            {
                case DialogAnswerNode answerNode:
                    ValidateAnswerNode(answerNode, issues);
                    break;
                case DialogVariableConditionNode conditionNode:
                    ValidateSingleOutput(conditionNode, DialogGraphPorts.True, "True", true, issues);
                    ValidateSingleOutput(conditionNode, DialogGraphPorts.False, "False", true, issues);
                    break;
                case DialogSentenceNode sentenceNode:
                    ValidateSingleOutput(sentenceNode, DialogGraphPorts.Next, "Next", false, issues);
                    if (DialogGraphOptionReader.Read(sentenceNode, DialogGraphOptions.UseInlineExternalFunction, false) &&
                        string.IsNullOrWhiteSpace(DialogGraphOptionReader.Read(sentenceNode, DialogGraphOptions.InlineExternalFunctionName, string.Empty)))
                    {
                        issues.Add(new DialogGraphIssue("Sentence node has inline external function enabled but no function name.", true, sentenceNode));
                    }
                    break;
                case DialogExternalFunctionNode externalNode:
                    ValidateSingleOutput(externalNode, DialogGraphPorts.Next, "Next", false, issues);
                    if (string.IsNullOrWhiteSpace(DialogGraphOptionReader.Read(externalNode, DialogGraphOptions.FunctionName, string.Empty)))
                        issues.Add(new DialogGraphIssue("External Function node has no function name.", true, externalNode));
                    break;
                case DialogModifyVariableNode modifyNode:
                    ValidateSingleOutput(modifyNode, DialogGraphPorts.Next, "Next", false, issues);
                    if (string.IsNullOrWhiteSpace(DialogGraphOptionReader.Read(modifyNode, DialogGraphOptions.VariableName, string.Empty)))
                        issues.Add(new DialogGraphIssue("Modify Variable node has no variable name.", true, modifyNode));
                    break;
                case DialogStartNode:
                case DialogGraphSettingsNode:
                    break;
                default:
                    issues.Add(new DialogGraphIssue($"Unsupported node type '{node.GetType().Name}' will not compile.", true, node));
                    break;
            }
        }

        private static void ValidateAnswerNode(DialogAnswerNode answerNode, List<DialogGraphIssue> issues)
        {
            int answerCount = GetAnswerCount(answerNode);
            bool foundGap = false;

            for (int i = 0; i < answerCount; i++)
            {
                List<INode> targets = GetConnectedTargets(answerNode, DialogGraphPorts.Answer(i));

                if (targets.Count > 1)
                {
                    issues.Add(new DialogGraphIssue($"Answer {i + 1} has multiple outgoing connections.", true, answerNode));
                    continue;
                }

                if (targets.Count == 0)
                {
                    if (i == 0)
                        issues.Add(new DialogGraphIssue("Answer node must connect at least Answer 1.", true, answerNode));

                    foundGap = true;
                    continue;
                }

                if (foundGap)
                    issues.Add(new DialogGraphIssue($"Answer {i + 1} is connected after an earlier disconnected answer output.", true, answerNode));

                if (targets[0] is DialogAnswerNode)
                    issues.Add(new DialogGraphIssue("Answer nodes cannot connect directly to other Answer nodes.", true, answerNode));
            }
        }

        private static void ValidateSingleOutput(
            INode node,
            string portName,
            string displayName,
            bool requireConnection,
            List<DialogGraphIssue> issues)
        {
            List<INode> targets = GetConnectedTargets(node, portName);

            if (targets.Count > 1)
                issues.Add(new DialogGraphIssue($"{node.GetType().Name} {displayName} output has multiple connections.", true, node));
            else if (requireConnection && targets.Count == 0)
                issues.Add(new DialogGraphIssue($"{node.GetType().Name} {displayName} output must be connected.", true, node));
            else if (targets.Count == 1 && node is DialogAnswerNode && targets[0] is DialogAnswerNode)
                issues.Add(new DialogGraphIssue("Answer nodes cannot connect directly to other Answer nodes.", true, node));
        }

        private static void ValidateCycles(List<INode> nodes, List<DialogGraphIssue> issues)
        {
            Dictionary<INode, List<INode>> edges = nodes.ToDictionary(node => node, GetAllConnectedTargets);
            HashSet<INode> visiting = new();
            HashSet<INode> visited = new();

            foreach (INode node in nodes)
            {
                if (Visit(node))
                {
                    issues.Add(new DialogGraphIssue("Dialog graph contains a cycle. Runtime execution expects an acyclic flow graph.", true, node));
                    return;
                }
            }

            bool Visit(INode node)
            {
                if (visited.Contains(node))
                    return false;

                if (!visiting.Add(node))
                    return true;

                if (edges.TryGetValue(node, out List<INode> targets))
                {
                    foreach (INode target in targets)
                    {
                        if (Visit(target))
                            return true;
                    }
                }

                visiting.Remove(node);
                visited.Add(node);
                return false;
            }
        }

        public static int GetAnswerCount(DialogAnswerNode answerNode) =>
            Clamp(DialogGraphOptionReader.Read(answerNode, DialogGraphOptions.AnswerCount, 2), 1, DialogGraphPorts.MaxAnswerPorts);

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;

            return value > max ? max : value;
        }

        public static List<INode> GetConnectedTargets(INode node, string outputPortName)
        {
            IPort outputPort = node.GetOutputPortByName(outputPortName);

            if (outputPort == null)
                return new List<INode>();

            List<IPort> ports = new();
            outputPort.GetConnectedPorts(ports);
            return ports.Select(port => port.GetNode()).Where(target => target != null).ToList();
        }

        public static List<INode> GetAllConnectedTargets(INode node)
        {
            List<INode> targets = new();

            foreach (IPort outputPort in node.GetOutputPorts())
            {
                List<IPort> connectedPorts = new();
                outputPort.GetConnectedPorts(connectedPorts);
                targets.AddRange(connectedPorts.Select(port => port.GetNode()).Where(target => target != null));
            }

            return targets;
        }
    }
}
