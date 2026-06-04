using System;
using Unity.GraphToolkit.Editor;
using UnityEngine;
using GtkNode = Unity.GraphToolkit.Editor.Node;

namespace cherrydev.Editor.GraphToolkit
{
    [Serializable]
    public class DialogStartNode : GtkNode
    {
        protected override void OnDefinePorts(IPortDefinitionContext context) =>
            context.AddOutputPort<DialogFlow>(DialogGraphPorts.Next)
                .WithDisplayName("Start")
                .WithConnectorUI(PortConnectorUI.Arrowhead)
                .Build();
    }

    [Serializable]
    public class DialogGraphSettingsNode : GtkNode
    {
        protected override void OnDefineOptions(IOptionDefinitionContext context)
        {
            context.AddOption(DialogGraphOptions.VariablesConfig, typeof(VariablesConfig))
                .WithDisplayName("Variables Config")
                .Build();
            context.AddOption<string>(DialogGraphOptions.LocalizationTableName)
                .WithDisplayName("Localization Table")
                .Delayed()
                .Build();
            context.AddOption<string>(DialogGraphOptions.CharacterNamesLocalizationName)
                .WithDisplayName("Character Names Table")
                .Delayed()
                .Build();
        }
    }

    [Serializable]
    public class DialogSentenceNode : GtkNode
    {
        protected override void OnDefineOptions(IOptionDefinitionContext context)
        {
            context.AddOption<string>(DialogGraphOptions.CharacterName)
                .WithDisplayName("Character")
                .Delayed()
                .Build();
            context.AddOption<string>(DialogGraphOptions.SentenceText)
                .WithDisplayName("Text")
                .Delayed()
                .Build();
            context.AddOption(DialogGraphOptions.CharacterSprite, typeof(Sprite))
                .WithDisplayName("Sprite")
                .ShowInInspectorOnly()
                .Build();
            context.AddOption<string>(DialogGraphOptions.CharacterNameKey)
                .WithDisplayName("Character Key")
                .ShowInInspectorOnly()
                .Delayed()
                .Build();
            context.AddOption<string>(DialogGraphOptions.SentenceTextKey)
                .WithDisplayName("Text Key")
                .ShowInInspectorOnly()
                .Delayed()
                .Build();
            context.AddOption<bool>(DialogGraphOptions.UseInlineExternalFunction)
                .WithDisplayName("Inline Function")
                .ShowInInspectorOnly()
                .Build();
            context.AddOption<string>(DialogGraphOptions.InlineExternalFunctionName)
                .WithDisplayName("Inline Function Name")
                .ShowInInspectorOnly()
                .Delayed()
                .Build();
        }

        protected override void OnDefinePorts(IPortDefinitionContext context)
        {
            AddFlowInput(context);
            context.AddOutputPort<DialogFlow>(DialogGraphPorts.Next)
                .WithDisplayName("Next")
                .WithConnectorUI(PortConnectorUI.Arrowhead)
                .Build();
        }

        private static void AddFlowInput(IPortDefinitionContext context) =>
            context.AddInputPort<DialogFlow>(DialogGraphPorts.Input)
                .WithDisplayName("In")
                .WithConnectorUI(PortConnectorUI.Circle)
                .Build();
    }

    [Serializable]
    public class DialogAnswerNode : GtkNode
    {
        protected override void OnDefineOptions(IOptionDefinitionContext context)
        {
            context.AddOption<int>(DialogGraphOptions.AnswerCount)
                .WithDisplayName("Answers")
                .WithDefaultValue(2)
                .Build();

            for (int i = 0; i < DialogGraphPorts.MaxAnswerPorts; i++)
            {
                context.AddOption<string>(DialogGraphOptions.AnswerTextPrefix + i)
                    .WithDisplayName($"Answer {i + 1}")
                    .ShowInInspectorOnly()
                    .Delayed()
                    .Build();
                context.AddOption<string>(DialogGraphOptions.AnswerKeyPrefix + i)
                    .WithDisplayName($"Answer {i + 1} Key")
                    .ShowInInspectorOnly()
                    .Delayed()
                    .Build();
            }
        }

        protected override void OnDefinePorts(IPortDefinitionContext context)
        {
            context.AddInputPort<DialogFlow>(DialogGraphPorts.Input)
                .WithDisplayName("In")
                .WithConnectorUI(PortConnectorUI.Circle)
                .Build();

            int answerCount = Mathf.Clamp(
                DialogGraphOptionReader.Read(this, DialogGraphOptions.AnswerCount, 2),
                1,
                DialogGraphPorts.MaxAnswerPorts);

            for (int i = 0; i < answerCount; i++)
            {
                context.AddOutputPort<DialogFlow>(DialogGraphPorts.Answer(i))
                    .WithDisplayName($"Answer {i + 1}")
                    .WithConnectorUI(PortConnectorUI.Arrowhead)
                    .Build();
            }
        }
    }

    [Serializable]
    public class DialogExternalFunctionNode : GtkNode
    {
        protected override void OnDefineOptions(IOptionDefinitionContext context)
        {
            context.AddOption<string>(DialogGraphOptions.FunctionName)
                .WithDisplayName("Function")
                .Delayed()
                .Build();
            context.AddOption<string>(DialogGraphOptions.FunctionDescription)
                .WithDisplayName("Description")
                .ShowInInspectorOnly()
                .Delayed()
                .Build();
        }

        protected override void OnDefinePorts(IPortDefinitionContext context)
        {
            context.AddInputPort<DialogFlow>(DialogGraphPorts.Input)
                .WithDisplayName("In")
                .WithConnectorUI(PortConnectorUI.Circle)
                .Build();
            context.AddOutputPort<DialogFlow>(DialogGraphPorts.Next)
                .WithDisplayName("Next")
                .WithConnectorUI(PortConnectorUI.Arrowhead)
                .Build();
        }
    }

    [Serializable]
    public class DialogModifyVariableNode : GtkNode
    {
        protected override void OnDefineOptions(IOptionDefinitionContext context)
        {
            AddVariableValueOptions(context);
            context.AddOption<ModificationType>(DialogGraphOptions.ModificationType)
                .WithDisplayName("Action")
                .WithDefaultValue(ModificationType.Set)
                .Build();
        }

        protected override void OnDefinePorts(IPortDefinitionContext context)
        {
            context.AddInputPort<DialogFlow>(DialogGraphPorts.Input)
                .WithDisplayName("In")
                .WithConnectorUI(PortConnectorUI.Circle)
                .Build();
            context.AddOutputPort<DialogFlow>(DialogGraphPorts.Next)
                .WithDisplayName("Next")
                .WithConnectorUI(PortConnectorUI.Arrowhead)
                .Build();
        }

        private static void AddVariableValueOptions(IOptionDefinitionContext context)
        {
            context.AddOption<string>(DialogGraphOptions.VariableName)
                .WithDisplayName("Variable")
                .Delayed()
                .Build();
            context.AddOption<bool>(DialogGraphOptions.BoolValue)
                .WithDisplayName("Bool")
                .ShowInInspectorOnly()
                .Build();
            context.AddOption<int>(DialogGraphOptions.IntValue)
                .WithDisplayName("Int")
                .ShowInInspectorOnly()
                .Build();
            context.AddOption<float>(DialogGraphOptions.FloatValue)
                .WithDisplayName("Float")
                .ShowInInspectorOnly()
                .Build();
            context.AddOption<string>(DialogGraphOptions.StringValue)
                .WithDisplayName("String")
                .ShowInInspectorOnly()
                .Delayed()
                .Build();
        }
    }

    [Serializable]
    public class DialogVariableConditionNode : GtkNode
    {
        protected override void OnDefineOptions(IOptionDefinitionContext context)
        {
            context.AddOption<string>(DialogGraphOptions.VariableName)
                .WithDisplayName("Variable")
                .Delayed()
                .Build();
            context.AddOption<ConditionType>(DialogGraphOptions.ConditionType)
                .WithDisplayName("Condition")
                .WithDefaultValue(ConditionType.Equal)
                .Build();
            context.AddOption<bool>(DialogGraphOptions.BoolValue)
                .WithDisplayName("Bool")
                .ShowInInspectorOnly()
                .Build();
            context.AddOption<int>(DialogGraphOptions.IntValue)
                .WithDisplayName("Int")
                .ShowInInspectorOnly()
                .Build();
            context.AddOption<float>(DialogGraphOptions.FloatValue)
                .WithDisplayName("Float")
                .ShowInInspectorOnly()
                .Build();
            context.AddOption<string>(DialogGraphOptions.StringValue)
                .WithDisplayName("String")
                .ShowInInspectorOnly()
                .Delayed()
                .Build();
        }

        protected override void OnDefinePorts(IPortDefinitionContext context)
        {
            context.AddInputPort<DialogFlow>(DialogGraphPorts.Input)
                .WithDisplayName("In")
                .WithConnectorUI(PortConnectorUI.Circle)
                .Build();
            context.AddOutputPort<DialogFlow>(DialogGraphPorts.True)
                .WithDisplayName("True")
                .WithConnectorUI(PortConnectorUI.Arrowhead)
                .Build();
            context.AddOutputPort<DialogFlow>(DialogGraphPorts.False)
                .WithDisplayName("False")
                .WithConnectorUI(PortConnectorUI.Arrowhead)
                .Build();
        }
    }
}
