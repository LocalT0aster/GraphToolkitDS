using System;
using System.Collections.Generic;
using System.Text;

namespace cherrydev
{
    public static class DialogMarkdownFormatter
    {
        private const string MarkColor = "#FFFF0080";
        private const int MaxTmpTagLength = 128;

        private static readonly InlineMarker[] Markers =
        {
            new("**", "<b>", "</b>"),
            new("++", "<u>", "</u>"),
            new("~~", "<s>", "</s>"),
            new("==", $"<mark={MarkColor}>", "</mark>"),
            new("`", "<mspace=0.6em>", "</mspace>"),
            new("*", "<i>", "</i>"),
            new("_", "<i>", "</i>"),
            new("^", "<sup>", "</sup>"),
            new("~", "<sub>", "</sub>")
        };

        private static readonly HashSet<char> EscapableCharacters = new()
        {
            '\\',
            '*',
            '_',
            '~',
            '`',
            '+',
            '=',
            '^'
        };

        private static readonly HashSet<string> VisibleTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "br",
            "sprite"
        };

        private static readonly HashSet<string> ClosableTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "align",
            "allcaps",
            "alpha",
            "b",
            "color",
            "cspace",
            "font",
            "font-weight",
            "gradient",
            "i",
            "indent",
            "line-height",
            "line-indent",
            "link",
            "lowercase",
            "margin",
            "mark",
            "mspace",
            "nobr",
            "noparse",
            "rotate",
            "s",
            "size",
            "smallcaps",
            "strikethrough",
            "style",
            "sub",
            "sup",
            "u",
            "uppercase",
            "voffset",
            "width"
        };

        public static string ToTmpRichText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? string.Empty;

            return FormatRange(text, 0, text.Length);
        }

        public static int CountVisibleCharacters(string richText)
        {
            if (string.IsNullOrEmpty(richText))
                return 0;

            int count = 0;
            int index = 0;

            while (index < richText.Length)
            {
                if (TryReadTmpTag(richText, index, richText.Length, out int tagEnd, out string tagName, out _, out _))
                {
                    if (VisibleTags.Contains(tagName))
                        count++;

                    index = tagEnd;
                    continue;
                }

                index += GetTextElementLength(richText, index);
                count++;
            }

            return count;
        }

        public static string TakeVisibleCharacters(string richText, int visibleCharacterCount)
        {
            if (string.IsNullOrEmpty(richText) || visibleCharacterCount <= 0)
                return string.Empty;

            var builder = new StringBuilder(richText.Length);
            var openTags = new List<string>();
            int visibleCount = 0;
            int index = 0;

            while (index < richText.Length && visibleCount < visibleCharacterCount)
            {
                if (TryReadTmpTag(
                        richText,
                        index,
                        richText.Length,
                        out int tagEnd,
                        out string tagName,
                        out bool isClosingTag,
                        out string rawTag))
                {
                    builder.Append(rawTag);
                    TrackOpenTag(openTags, tagName, isClosingTag);

                    if (VisibleTags.Contains(tagName))
                        visibleCount++;

                    index = tagEnd;
                    continue;
                }

                int textElementLength = GetTextElementLength(richText, index);
                builder.Append(richText, index, textElementLength);
                visibleCount++;
                index += textElementLength;
            }

            for (int tagIndex = openTags.Count - 1; tagIndex >= 0; tagIndex--)
                builder.Append("</").Append(openTags[tagIndex]).Append('>');

            return builder.ToString();
        }

        private static string FormatRange(string text, int start, int end)
        {
            var builder = new StringBuilder(end - start);
            int index = start;

            while (index < end)
            {
                if (TryReadEscape(text, index, end, out string escapedText, out int escapeLength))
                {
                    builder.Append(escapedText);
                    index += escapeLength;
                    continue;
                }

                if (TryReadTmpTag(text, index, end, out int tagEnd, out _, out _, out string rawTag))
                {
                    builder.Append(rawTag);
                    index = tagEnd;
                    continue;
                }

                InlineMarker marker = FindMarker(text, index, end);

                if (marker.IsValid)
                {
                    int closeIndex = FindClosingMarker(text, index + marker.Text.Length, end, marker.Text);

                    if (closeIndex >= 0)
                    {
                        builder.Append(marker.OpenTag);
                        builder.Append(FormatRange(text, index + marker.Text.Length, closeIndex));
                        builder.Append(marker.CloseTag);
                        index = closeIndex + marker.Text.Length;
                        continue;
                    }
                }

                builder.Append(text[index]);
                index++;
            }

            return builder.ToString();
        }

        private static bool TryReadEscape(
            string text,
            int index,
            int end,
            out string escapedText,
            out int escapeLength)
        {
            escapedText = string.Empty;
            escapeLength = 0;

            if (text[index] != '\\' || index + 1 >= end)
                return false;

            char next = text[index + 1];

            if (next == 'n')
            {
                escapedText = "<br>";
                escapeLength = 2;
                return true;
            }

            if (!EscapableCharacters.Contains(next))
                return false;

            escapedText = next.ToString();
            escapeLength = 2;
            return true;
        }

        private static InlineMarker FindMarker(string text, int index, int end)
        {
            foreach (InlineMarker marker in Markers)
            {
                if (HasMarker(text, index, end, marker.Text))
                    return marker;
            }

            return default;
        }

        private static int FindClosingMarker(string text, int index, int end, string marker)
        {
            while (index < end)
            {
                if (TryReadEscape(text, index, end, out _, out int escapeLength))
                {
                    index += escapeLength;
                    continue;
                }

                if (TryReadTmpTag(text, index, end, out int tagEnd, out _, out _, out _))
                {
                    index = tagEnd;
                    continue;
                }

                if (HasMarker(text, index, end, marker))
                    return index;

                index++;
            }

            return -1;
        }

        private static bool HasMarker(string text, int index, int end, string marker)
        {
            if (index + marker.Length > end)
                return false;

            for (int offset = 0; offset < marker.Length; offset++)
            {
                if (text[index + offset] != marker[offset])
                    return false;
            }

            return true;
        }

        private static bool TryReadTmpTag(
            string text,
            int index,
            int end,
            out int tagEnd,
            out string tagName,
            out bool isClosingTag,
            out string rawTag)
        {
            tagEnd = index;
            tagName = string.Empty;
            isClosingTag = false;
            rawTag = string.Empty;

            if (text[index] != '<' || index + 1 >= end || !CanStartTmpTag(text[index + 1]))
                return false;

            int maxEnd = Math.Min(end, index + MaxTmpTagLength + 2);

            for (int cursor = index + 1; cursor < maxEnd; cursor++)
            {
                char current = text[cursor];

                if (current == '\n' || current == '\r')
                    return false;

                if (current != '>')
                    continue;

                string body = text.Substring(index + 1, cursor - index - 1).Trim();

                if (!TryReadTagName(body, out tagName, out isClosingTag))
                    return false;

                tagEnd = cursor + 1;
                rawTag = text.Substring(index, tagEnd - index);
                return true;
            }

            return false;
        }

        private static bool CanStartTmpTag(char value) =>
            value == '/' || value == '#' || char.IsLetter(value);

        private static bool TryReadTagName(string tagBody, out string tagName, out bool isClosingTag)
        {
            tagName = string.Empty;
            isClosingTag = false;

            if (string.IsNullOrWhiteSpace(tagBody))
                return false;

            tagBody = tagBody.Trim();

            if (tagBody[0] == '/')
            {
                isClosingTag = true;
                tagBody = tagBody.Substring(1).TrimStart();
            }

            if (string.IsNullOrWhiteSpace(tagBody))
                return false;

            if (tagBody[0] == '#')
            {
                tagName = "color";
                return true;
            }

            int length = 0;

            while (length < tagBody.Length &&
                   (char.IsLetterOrDigit(tagBody[length]) || tagBody[length] == '-'))
            {
                length++;
            }

            if (length == 0)
                return false;

            tagName = tagBody.Substring(0, length);
            return true;
        }

        private static void TrackOpenTag(List<string> openTags, string tagName, bool isClosingTag)
        {
            if (!ClosableTags.Contains(tagName))
                return;

            if (!isClosingTag)
            {
                openTags.Add(tagName);
                return;
            }

            for (int index = openTags.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(openTags[index], tagName, StringComparison.OrdinalIgnoreCase))
                    continue;

                openTags.RemoveAt(index);
                return;
            }
        }

        private static int GetTextElementLength(string text, int index)
        {
            if (char.IsHighSurrogate(text[index]) &&
                index + 1 < text.Length &&
                char.IsLowSurrogate(text[index + 1]))
            {
                return 2;
            }

            return 1;
        }

        private readonly struct InlineMarker
        {
            public InlineMarker(string text, string openTag, string closeTag)
            {
                Text = text;
                OpenTag = openTag;
                CloseTag = closeTag;
            }

            public string Text { get; }
            public string OpenTag { get; }
            public string CloseTag { get; }
            public bool IsValid => !string.IsNullOrEmpty(Text);
        }
    }
}
