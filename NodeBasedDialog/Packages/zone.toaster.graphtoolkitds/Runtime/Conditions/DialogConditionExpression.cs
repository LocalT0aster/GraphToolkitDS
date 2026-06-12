using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace cherrydev
{
    public sealed class DialogConditionExpression
    {
        readonly ExpressionNode root;
        readonly string[] referencedVariables;

        DialogConditionExpression(string source, ExpressionNode root)
        {
            Source = source ?? string.Empty;
            this.root = root;

            var variables = new SortedSet<string>(StringComparer.Ordinal);
            root?.CollectReferencedVariables(variables);
            referencedVariables = new List<string>(variables).ToArray();
        }

        public string Source { get; }
        public IReadOnlyList<string> ReferencedVariables => referencedVariables;

        public static bool TryParse(string source, out DialogConditionExpression expression, out string error)
        {
            expression = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(source))
            {
                error = "Condition expression is empty.";
                return false;
            }

            var parser = new Parser(source);
            ExpressionNode root = parser.ParseExpression();

            if (!string.IsNullOrEmpty(parser.Error))
            {
                error = parser.Error;
                return false;
            }

            if (!parser.IsAtEnd)
            {
                error = $"Unexpected token '{parser.Current.Text}'.";
                return false;
            }

            expression = new DialogConditionExpression(source.Trim(), root);
            return true;
        }

        public bool Evaluate(DialogVariablesHandler variablesHandler)
        {
            if (variablesHandler == null)
            {
                Debug.LogWarning($"Cannot evaluate dialog condition '{Source}' without variables.");
                return false;
            }

            ConditionValue value = root.Evaluate(variablesHandler);

            if (value.Kind == ConditionValueKind.Bool)
                return value.BoolValue;

            Debug.LogWarning($"Dialog condition '{Source}' did not evaluate to a boolean value.");
            return false;
        }

        public bool Validate(VariablesConfig variablesConfig, out string error)
        {
            error = string.Empty;

            if (variablesConfig == null)
                return true;

            ConditionValueKind kind = root.Validate(new ValidationContext(variablesConfig), out error);

            if (kind == ConditionValueKind.Bool)
                return true;

            if (string.IsNullOrEmpty(error))
                error = "Condition expression must evaluate to a boolean value.";

            return false;
        }

        abstract class ExpressionNode
        {
            public abstract ConditionValue Evaluate(DialogVariablesHandler variablesHandler);
            public abstract ConditionValueKind Validate(ValidationContext context, out string error);
            public virtual void CollectReferencedVariables(ISet<string> variables)
            {
            }
        }

        sealed class LiteralNode : ExpressionNode
        {
            readonly ConditionValue value;

            public LiteralNode(ConditionValue value)
            {
                this.value = value;
            }

            public override ConditionValue Evaluate(DialogVariablesHandler variablesHandler) => value;

            public override ConditionValueKind Validate(ValidationContext context, out string error)
            {
                error = string.Empty;
                return value.Kind;
            }
        }

        sealed class VariableNode : ExpressionNode
        {
            readonly string variableName;

            public VariableNode(string variableName)
            {
                this.variableName = variableName ?? string.Empty;
            }

            public override ConditionValue Evaluate(DialogVariablesHandler variablesHandler)
            {
                Variable variable = variablesHandler.GetVariable(variableName);

                if (variable == null)
                {
                    Debug.LogWarning($"Dialog condition variable '{variableName}' was not found.");
                    return ConditionValue.False;
                }

                return ConditionValue.FromVariable(variable);
            }

            public override ConditionValueKind Validate(ValidationContext context, out string error)
            {
                Variable variable = context.VariablesConfig.GetVariable(variableName);

                if (variable == null)
                {
                    error = $"Condition variable '{variableName}' is not declared.";
                    return ConditionValueKind.Invalid;
                }

                error = string.Empty;
                return ConditionValue.KindFromVariableType(variable.Type);
            }

            public override void CollectReferencedVariables(ISet<string> variables)
            {
                if (!string.IsNullOrWhiteSpace(variableName))
                    variables.Add(variableName);
            }
        }

        sealed class UnaryNode : ExpressionNode
        {
            readonly ConditionUnaryOperator op;
            readonly ExpressionNode operand;

            public UnaryNode(ConditionUnaryOperator op, ExpressionNode operand)
            {
                this.op = op;
                this.operand = operand;
            }

            public override ConditionValue Evaluate(DialogVariablesHandler variablesHandler)
            {
                ConditionValue value = operand.Evaluate(variablesHandler);

                if (op == ConditionUnaryOperator.Not && value.Kind == ConditionValueKind.Bool)
                    return ConditionValue.FromBool(!value.BoolValue);

                Debug.LogWarning("Dialog condition unary operator expected a boolean operand.");
                return ConditionValue.False;
            }

            public override ConditionValueKind Validate(ValidationContext context, out string error)
            {
                ConditionValueKind operandKind = operand.Validate(context, out error);

                if (!string.IsNullOrEmpty(error))
                    return ConditionValueKind.Invalid;

                if (op == ConditionUnaryOperator.Not && operandKind == ConditionValueKind.Bool)
                    return ConditionValueKind.Bool;

                error = "Operator 'not' expects a boolean operand.";
                return ConditionValueKind.Invalid;
            }

            public override void CollectReferencedVariables(ISet<string> variables) =>
                operand.CollectReferencedVariables(variables);
        }

        sealed class BinaryNode : ExpressionNode
        {
            readonly ConditionBinaryOperator op;
            readonly ExpressionNode left;
            readonly ExpressionNode right;

            public BinaryNode(ConditionBinaryOperator op, ExpressionNode left, ExpressionNode right)
            {
                this.op = op;
                this.left = left;
                this.right = right;
            }

            public override ConditionValue Evaluate(DialogVariablesHandler variablesHandler)
            {
                ConditionValue leftValue = left.Evaluate(variablesHandler);

                if (op == ConditionBinaryOperator.And)
                {
                    if (leftValue.Kind != ConditionValueKind.Bool)
                        return ConditionValue.False;

                    return leftValue.BoolValue
                        ? ConditionValue.FromBool(EvaluateBoolRight(variablesHandler))
                        : ConditionValue.False;
                }

                if (op == ConditionBinaryOperator.Or)
                {
                    if (leftValue.Kind != ConditionValueKind.Bool)
                        return ConditionValue.False;

                    return leftValue.BoolValue
                        ? ConditionValue.True
                        : ConditionValue.FromBool(EvaluateBoolRight(variablesHandler));
                }

                ConditionValue rightValue = right.Evaluate(variablesHandler);
                return ConditionValue.FromBool(Compare(leftValue, rightValue));
            }

            bool EvaluateBoolRight(DialogVariablesHandler variablesHandler)
            {
                ConditionValue rightValue = right.Evaluate(variablesHandler);
                return rightValue.Kind == ConditionValueKind.Bool && rightValue.BoolValue;
            }

            bool Compare(ConditionValue leftValue, ConditionValue rightValue)
            {
                if (leftValue.Kind == ConditionValueKind.Number && rightValue.Kind == ConditionValueKind.Number)
                {
                    int comparison = leftValue.NumberValue.CompareTo(rightValue.NumberValue);
                    return CompareOrdinal(comparison);
                }

                if (leftValue.Kind == ConditionValueKind.String && rightValue.Kind == ConditionValueKind.String)
                {
                    if (op != ConditionBinaryOperator.Equal && op != ConditionBinaryOperator.NotEqual)
                        return false;

                    return CompareOrdinal(string.Compare(leftValue.StringValue, rightValue.StringValue, StringComparison.Ordinal));
                }

                if (leftValue.Kind == ConditionValueKind.Bool && rightValue.Kind == ConditionValueKind.Bool)
                {
                    if (op != ConditionBinaryOperator.Equal && op != ConditionBinaryOperator.NotEqual)
                        return false;

                    int comparison = leftValue.BoolValue.CompareTo(rightValue.BoolValue);
                    return CompareOrdinal(comparison);
                }

                return false;
            }

            bool CompareOrdinal(int comparison)
            {
                return op switch
                {
                    ConditionBinaryOperator.Equal => comparison == 0,
                    ConditionBinaryOperator.NotEqual => comparison != 0,
                    ConditionBinaryOperator.Less => comparison < 0,
                    ConditionBinaryOperator.LessOrEqual => comparison <= 0,
                    ConditionBinaryOperator.Greater => comparison > 0,
                    ConditionBinaryOperator.GreaterOrEqual => comparison >= 0,
                    _ => false
                };
            }

            public override ConditionValueKind Validate(ValidationContext context, out string error)
            {
                ConditionValueKind leftKind = left.Validate(context, out error);

                if (!string.IsNullOrEmpty(error))
                    return ConditionValueKind.Invalid;

                ConditionValueKind rightKind = right.Validate(context, out error);

                if (!string.IsNullOrEmpty(error))
                    return ConditionValueKind.Invalid;

                if (op == ConditionBinaryOperator.And || op == ConditionBinaryOperator.Or)
                {
                    if (leftKind == ConditionValueKind.Bool && rightKind == ConditionValueKind.Bool)
                        return ConditionValueKind.Bool;

                    error = $"Operator '{GetOperatorText(op)}' expects boolean operands.";
                    return ConditionValueKind.Invalid;
                }

                if (leftKind != rightKind)
                {
                    error = $"Cannot compare {leftKind} with {rightKind}.";
                    return ConditionValueKind.Invalid;
                }

                if ((leftKind == ConditionValueKind.Bool || leftKind == ConditionValueKind.String) &&
                    op != ConditionBinaryOperator.Equal &&
                    op != ConditionBinaryOperator.NotEqual)
                {
                    error = $"Operator '{GetOperatorText(op)}' is only valid for numeric operands.";
                    return ConditionValueKind.Invalid;
                }

                return ConditionValueKind.Bool;
            }

            public override void CollectReferencedVariables(ISet<string> variables)
            {
                left.CollectReferencedVariables(variables);
                right.CollectReferencedVariables(variables);
            }
        }

        readonly struct ValidationContext
        {
            public ValidationContext(VariablesConfig variablesConfig)
            {
                VariablesConfig = variablesConfig;
            }

            public VariablesConfig VariablesConfig { get; }
        }

        readonly struct ConditionValue
        {
            public static readonly ConditionValue False = FromBool(false);
            public static readonly ConditionValue True = FromBool(true);

            ConditionValue(ConditionValueKind kind, bool boolValue, double numberValue, string stringValue)
            {
                Kind = kind;
                BoolValue = boolValue;
                NumberValue = numberValue;
                StringValue = stringValue ?? string.Empty;
            }

            public ConditionValueKind Kind { get; }
            public bool BoolValue { get; }
            public double NumberValue { get; }
            public string StringValue { get; }

            public static ConditionValue FromBool(bool value) =>
                new(ConditionValueKind.Bool, value, 0d, string.Empty);

            public static ConditionValue FromNumber(double value) =>
                new(ConditionValueKind.Number, false, value, string.Empty);

            public static ConditionValue FromString(string value) =>
                new(ConditionValueKind.String, false, 0d, value);

            public static ConditionValue FromVariable(Variable variable)
            {
                return variable.Type switch
                {
                    VariableType.Bool => FromBool(variable.GetBoolValue()),
                    VariableType.Int => FromNumber(variable.GetIntValue()),
                    VariableType.Float => FromNumber(variable.GetFloatValue()),
                    VariableType.String => FromString(variable.GetStringValue()),
                    _ => False
                };
            }

            public static ConditionValueKind KindFromVariableType(VariableType variableType)
            {
                return variableType switch
                {
                    VariableType.Bool => ConditionValueKind.Bool,
                    VariableType.Int => ConditionValueKind.Number,
                    VariableType.Float => ConditionValueKind.Number,
                    VariableType.String => ConditionValueKind.String,
                    _ => ConditionValueKind.Invalid
                };
            }
        }

        enum ConditionValueKind
        {
            Invalid,
            Bool,
            Number,
            String
        }

        enum ConditionUnaryOperator
        {
            Not
        }

        enum ConditionBinaryOperator
        {
            And,
            Or,
            Equal,
            NotEqual,
            Less,
            LessOrEqual,
            Greater,
            GreaterOrEqual
        }

        enum TokenKind
        {
            End,
            Identifier,
            Bool,
            Number,
            String,
            And,
            Or,
            Not,
            Equal,
            NotEqual,
            Less,
            LessOrEqual,
            Greater,
            GreaterOrEqual,
            OpenParen,
            CloseParen,
            Invalid
        }

        readonly struct Token
        {
            public Token(TokenKind kind, string text, int position)
            {
                Kind = kind;
                Text = text ?? string.Empty;
                Position = position;
            }

            public TokenKind Kind { get; }
            public string Text { get; }
            public int Position { get; }
        }

        sealed class Parser
        {
            readonly List<Token> tokens;
            int position;

            public Parser(string source)
            {
                tokens = Tokenize(source ?? string.Empty, out string error);
                Error = error;
            }

            public string Error { get; private set; }
            public Token Current => position < tokens.Count ? tokens[position] : tokens[tokens.Count - 1];
            public bool IsAtEnd => Current.Kind == TokenKind.End;

            public ExpressionNode ParseExpression() => ParseOr();

            ExpressionNode ParseOr()
            {
                ExpressionNode left = ParseAnd();

                while (Match(TokenKind.Or))
                    left = new BinaryNode(ConditionBinaryOperator.Or, left, ParseAnd());

                return left;
            }

            ExpressionNode ParseAnd()
            {
                ExpressionNode left = ParseUnary();

                while (Match(TokenKind.And))
                    left = new BinaryNode(ConditionBinaryOperator.And, left, ParseUnary());

                return left;
            }

            ExpressionNode ParseUnary()
            {
                if (Match(TokenKind.Not))
                    return new UnaryNode(ConditionUnaryOperator.Not, ParseUnary());

                return ParseComparison();
            }

            ExpressionNode ParseComparison()
            {
                ExpressionNode left = ParsePrimary();

                if (TryReadComparisonOperator(Current.Kind, out ConditionBinaryOperator op))
                {
                    Advance();
                    left = new BinaryNode(op, left, ParsePrimary());
                }

                return left;
            }

            ExpressionNode ParsePrimary()
            {
                Token token = Current;

                if (Match(TokenKind.OpenParen))
                {
                    ExpressionNode expression = ParseExpression();

                    if (!Match(TokenKind.CloseParen))
                        Error = "Expected ')' in condition expression.";

                    return expression;
                }

                if (Match(TokenKind.Identifier))
                    return new VariableNode(token.Text);

                if (Match(TokenKind.Bool))
                    return new LiteralNode(ConditionValue.FromBool(string.Equals(token.Text, "true", StringComparison.OrdinalIgnoreCase)));

                if (Match(TokenKind.Number))
                {
                    if (double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        return new LiteralNode(ConditionValue.FromNumber(value));

                    Error = $"Could not parse number '{token.Text}'.";
                    return new LiteralNode(ConditionValue.False);
                }

                if (Match(TokenKind.String))
                    return new LiteralNode(ConditionValue.FromString(token.Text));

                Error = $"Expected condition expression near '{token.Text}'.";
                return new LiteralNode(ConditionValue.False);
            }

            bool Match(TokenKind kind)
            {
                if (Current.Kind != kind)
                    return false;

                Advance();
                return true;
            }

            void Advance()
            {
                if (position < tokens.Count - 1)
                    position++;
            }

            static bool TryReadComparisonOperator(TokenKind kind, out ConditionBinaryOperator op)
            {
                switch (kind)
                {
                    case TokenKind.Equal:
                        op = ConditionBinaryOperator.Equal;
                        return true;
                    case TokenKind.NotEqual:
                        op = ConditionBinaryOperator.NotEqual;
                        return true;
                    case TokenKind.Less:
                        op = ConditionBinaryOperator.Less;
                        return true;
                    case TokenKind.LessOrEqual:
                        op = ConditionBinaryOperator.LessOrEqual;
                        return true;
                    case TokenKind.Greater:
                        op = ConditionBinaryOperator.Greater;
                        return true;
                    case TokenKind.GreaterOrEqual:
                        op = ConditionBinaryOperator.GreaterOrEqual;
                        return true;
                    default:
                        op = ConditionBinaryOperator.Equal;
                        return false;
                }
            }
        }

        static string GetOperatorText(ConditionBinaryOperator op)
        {
            return op switch
            {
                ConditionBinaryOperator.And => "&&",
                ConditionBinaryOperator.Or => "||",
                ConditionBinaryOperator.Equal => "==",
                ConditionBinaryOperator.NotEqual => "!=",
                ConditionBinaryOperator.Less => "<",
                ConditionBinaryOperator.LessOrEqual => "<=",
                ConditionBinaryOperator.Greater => ">",
                ConditionBinaryOperator.GreaterOrEqual => ">=",
                _ => "?"
            };
        }

        static List<Token> Tokenize(string source, out string error)
        {
            error = string.Empty;
            var result = new List<Token>();
            int position = 0;

            while (position < source.Length)
            {
                char current = source[position];

                if (char.IsWhiteSpace(current))
                {
                    position++;
                    continue;
                }

                if (char.IsLetter(current) || current == '_')
                {
                    int start = position;
                    position++;

                    while (position < source.Length &&
                           (char.IsLetterOrDigit(source[position]) || source[position] == '_' || source[position] == '.'))
                    {
                        position++;
                    }

                    string text = source.Substring(start, position - start);
                    result.Add(new Token(KeywordKind(text), text, start));
                    continue;
                }

                if (char.IsDigit(current) ||
                    (current == '-' && position + 1 < source.Length && char.IsDigit(source[position + 1])))
                {
                    int start = position;
                    position++;

                    while (position < source.Length && (char.IsDigit(source[position]) || source[position] == '.'))
                        position++;

                    result.Add(new Token(TokenKind.Number, source.Substring(start, position - start), start));
                    continue;
                }

                if (current == '"' || current == '\'')
                {
                    char quote = current;
                    int start = position;
                    position++;
                    var chars = new List<char>();

                    while (position < source.Length && source[position] != quote)
                    {
                        if (source[position] == '\\' && position + 1 < source.Length)
                        {
                            position++;
                            chars.Add(source[position] == 'n' ? '\n' : source[position]);
                            position++;
                            continue;
                        }

                        chars.Add(source[position]);
                        position++;
                    }

                    if (position >= source.Length)
                    {
                        error = $"Unterminated string literal at position {start}.";
                        result.Add(new Token(TokenKind.Invalid, source.Substring(start), start));
                        break;
                    }

                    position++;
                    result.Add(new Token(TokenKind.String, new string(chars.ToArray()), start));
                    continue;
                }

                if (TryReadTwoCharacterToken(source, position, out TokenKind twoCharacterKind))
                {
                    result.Add(new Token(twoCharacterKind, source.Substring(position, 2), position));
                    position += 2;
                    continue;
                }

                TokenKind singleKind = current switch
                {
                    '!' => TokenKind.Not,
                    '<' => TokenKind.Less,
                    '>' => TokenKind.Greater,
                    '(' => TokenKind.OpenParen,
                    ')' => TokenKind.CloseParen,
                    _ => TokenKind.Invalid
                };

                if (singleKind == TokenKind.Invalid)
                {
                    error = $"Unexpected character '{current}' at position {position}.";
                    result.Add(new Token(TokenKind.Invalid, current.ToString(), position));
                    break;
                }

                result.Add(new Token(singleKind, current.ToString(), position));
                position++;
            }

            result.Add(new Token(TokenKind.End, string.Empty, source.Length));
            return result;
        }

        static bool TryReadTwoCharacterToken(string source, int position, out TokenKind kind)
        {
            kind = TokenKind.Invalid;

            if (position + 1 >= source.Length)
                return false;

            string token = source.Substring(position, 2);
            kind = token switch
            {
                "&&" => TokenKind.And,
                "||" => TokenKind.Or,
                "==" => TokenKind.Equal,
                "!=" => TokenKind.NotEqual,
                "<=" => TokenKind.LessOrEqual,
                ">=" => TokenKind.GreaterOrEqual,
                _ => TokenKind.Invalid
            };

            return kind != TokenKind.Invalid;
        }

        static TokenKind KeywordKind(string text)
        {
            if (string.Equals(text, "and", StringComparison.OrdinalIgnoreCase))
                return TokenKind.And;

            if (string.Equals(text, "or", StringComparison.OrdinalIgnoreCase))
                return TokenKind.Or;

            if (string.Equals(text, "not", StringComparison.OrdinalIgnoreCase))
                return TokenKind.Not;

            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
            {
                return TokenKind.Bool;
            }

            return TokenKind.Identifier;
        }
    }
}
