using System;

namespace cherrydev.Editor.GraphToolkit
{
    internal static class DialogGraphPorts
    {
        public const string Input = "in";
        public const string Next = "next";
        public const string True = "true";
        public const string False = "false";
        public const int MaxAnswerPorts = 12;

        public static string Answer(int index) => $"answer_{index}";

        public static bool TryGetAnswerIndex(string portName, out int index)
        {
            index = -1;

            if (string.IsNullOrEmpty(portName) || !portName.StartsWith("answer_", StringComparison.Ordinal))
                return false;

            return int.TryParse(portName.Substring("answer_".Length), out index);
        }
    }

    internal static class DialogGraphOptions
    {
        public const string CharacterName = "characterName";
        public const string SentenceText = "sentenceText";
        public const string CharacterSprite = "characterSprite";
        public const string CharacterNameKey = "characterNameKey";
        public const string SentenceTextKey = "sentenceTextKey";
        public const string UseInlineExternalFunction = "useInlineExternalFunction";
        public const string InlineExternalFunctionName = "inlineExternalFunctionName";

        public const string AnswerCount = "answerCount";
        public const string AnswerTextPrefix = "answerText_";
        public const string AnswerKeyPrefix = "answerKey_";
        public const string AnswerConditionPrefix = "answerCondition_";

        public const string FunctionName = "functionName";
        public const string FunctionDescription = "functionDescription";

        public const string VariablesConfig = "variablesConfig";
        public const string LocalizationTableName = "localizationTableName";
        public const string CharacterNamesLocalizationName = "characterNamesLocalizationName";

        public const string VariableName = "variableName";
        public const string ConditionExpression = "conditionExpression";
        public const string ModificationType = "modificationType";
        public const string ConditionType = "conditionType";
        public const string BoolValue = "boolValue";
        public const string IntValue = "intValue";
        public const string FloatValue = "floatValue";
        public const string StringValue = "stringValue";

        public const string CompilerSourceKey = "compilerSourceKey";
    }

    public sealed class DialogFlow
    {
    }
}
