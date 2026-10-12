using System.Globalization;
using System.Text;

namespace DocumentTextFunction;

internal static class RtfTextExtractor
{
    public static string Extract(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Extract(Encoding.GetEncoding(1252).GetString(bytes));
    }

    public static string Extract(string rtf)
    {
        var text = new StringBuilder();
        var skipStack = new Stack<int>();
        var skip = 0;
        var fallbackChars = 1;
        var index = 0;
        var windows1252 = Encoding.GetEncoding(1252);

        while (index < rtf.Length)
        {
            var character = rtf[index];
            if (character == '{')
            {
                skipStack.Push(skip);
                index++;
                continue;
            }
            if (character == '}')
            {
                if (skipStack.Count > 0) skip = skipStack.Pop();
                index++;
                continue;
            }
            if (character == '\\')
            {
                index++;
                if (index >= rtf.Length) break;
                var next = rtf[index];
                if (next is '\\' or '{' or '}')
                {
                    if (skip == 0) text.Append(next);
                    index++;
                    continue;
                }
                if (next == '\'')
                {
                    if (index + 2 < rtf.Length &&
                        int.TryParse(rtf.Substring(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    {
                        if (skip == 0) text.Append(windows1252.GetString(new[] { (byte)value }));
                        index += 3;
                    }
                    else index++;
                    continue;
                }
                if (next == '*')
                {
                    skip = 1;
                    index++;
                    continue;
                }
                if (next is '\r' or '\n') { index++; continue; }

                var start = index;
                while (index < rtf.Length && char.IsLetter(rtf[index])) index++;
                var word = rtf.Substring(start, index - start);
                int? number = null;
                if (index < rtf.Length && (rtf[index] == '-' || char.IsDigit(rtf[index])))
                {
                    var sign = rtf[index] == '-' ? -1 : 1;
                    if (rtf[index] == '-') index++;
                    var numberStart = index;
                    while (index < rtf.Length && char.IsDigit(rtf[index])) index++;
                    if (index > numberStart && int.TryParse(rtf.Substring(numberStart, index - numberStart), out var parsed))
                        number = parsed * sign;
                }
                if (index < rtf.Length && rtf[index] == ' ') index++;

                if (skip != 0)
                {
                    if (word is "shptxt" or "dptxbxtext" or "textboxtext")
                    {
                        skip = 0;
                        text.Append("\n[Text box] ");
                    }
                    else if (word == "bin" && number is > 0)
                        index = Math.Min(rtf.Length, index + number.Value);
                    continue;
                }

                switch (word)
                {
                    case "par" or "line": text.Append('\n'); break;
                    case "shptxt" or "dptxbxtext" or "textboxtext": text.Append("\n[Text box] "); break;
                    case "tab": text.Append('\t'); break;
                    case "uc" when number is int count: fallbackChars = count; break;
                    case "u" when number is int code:
                        text.Append((char)(code & 0xFFFF));
                        var remaining = fallbackChars;
                        while (remaining > 0 && index < rtf.Length && rtf[index] is not ('\\' or '{' or '}'))
                        {
                            index++;
                            remaining--;
                        }
                        break;
                    case "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "object"
                        or "datastore" or "themedata" or "filetbl" or "listtable" or "listoverridetable" or "revtbl"
                        or "shprslt":
                        skip = 1;
                        break;
                    case "bin" when number is > 0:
                        index = Math.Min(rtf.Length, index + number.Value);
                        break;
                }
                continue;
            }
            if (character is '\r' or '\n') { index++; continue; }
            if (skip == 0) text.Append(character);
            index++;
        }

        return text.ToString().Trim();
    }
}
