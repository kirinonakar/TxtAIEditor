using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeWorkbookCellFormatter
    {
        internal static string FormatWorkbookCellValue(string rawValue, ViewerCellStyle style, bool use1904Dates)
        {
            string formatCode = style.NumberFormatCode ?? string.Empty;
            if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double numericValue))
            {
                return rawValue;
            }

            if (string.IsNullOrWhiteSpace(formatCode) ||
                formatCode.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                return FormatWorkbookGeneralNumberValue(numericValue, rawValue);
            }

            if (IsWorkbookDateFormat(formatCode) &&
                TryConvertExcelSerialDate(numericValue, use1904Dates, out DateTime dateTime))
            {
                return FormatWorkbookDateValue(dateTime, numericValue, formatCode);
            }

            return FormatWorkbookNumberValue(numericValue, formatCode, rawValue);
        }

        private static string FormatWorkbookGeneralNumberValue(double numericValue, string rawValue)
        {
            if (double.IsNaN(numericValue) || double.IsInfinity(numericValue))
            {
                return rawValue;
            }

            try
            {
                return numericValue.ToString("G15", CultureInfo.CurrentCulture);
            }
            catch
            {
                return rawValue;
            }
        }

        internal static bool IsWorkbookDateFormat(string formatCode)
        {
            string cleaned = RemoveWorkbookFormatLiterals(formatCode);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]+\]", string.Empty);
            return Regex.IsMatch(cleaned, @"(?<!\\)[ymdhHsS]", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(cleaned, @"[0#?](?:\.[0#?]+)?\s*%?");
        }

        internal static bool TryConvertExcelSerialDate(double serial, bool use1904Dates, out DateTime dateTime)
        {
            dateTime = default;
            if (double.IsNaN(serial) || double.IsInfinity(serial))
            {
                return false;
            }

            try
            {
                dateTime = use1904Dates
                    ? new DateTime(1904, 1, 1).AddDays(serial)
                    : new DateTime(1899, 12, 30).AddDays(serial);
                return dateTime.Year >= 1 && dateTime.Year <= 9999;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatWorkbookDateValue(DateTime dateTime, double serial, string formatCode)
        {
            if (ShouldUseIsoDateFormat(formatCode))
            {
                return dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            string section = SelectWorkbookFormatSection(formatCode, serial);
            section = CleanWorkbookFormatSection(section);
            section = Regex.Replace(section, @"\[\$-[^\]]+\]", string.Empty);
            section = Regex.Replace(section, @"\[[^\]]+\]", match =>
            {
                string token = match.Value.Trim('[', ']');
                return token.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("hh", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("m", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("mm", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("ss", StringComparison.OrdinalIgnoreCase)
                    ? token
                    : string.Empty;
            });

            string dotNetFormat = ConvertExcelDateFormatToDotNet(section);
            if (string.IsNullOrWhiteSpace(dotNetFormat))
            {
                return dateTime.ToString(CultureInfo.CurrentCulture);
            }

            try
            {
                return dateTime.ToString(dotNetFormat, CultureInfo.CurrentCulture);
            }
            catch
            {
                return dateTime.ToString(CultureInfo.CurrentCulture);
            }
        }

        private static bool ShouldUseIsoDateFormat(string formatCode)
        {
            string cleaned = RemoveWorkbookFormatLiterals(formatCode);
            cleaned = Regex.Replace(cleaned, @"\[[^\]]+\]", string.Empty);
            return Regex.IsMatch(cleaned, @"(?<!\\)[yd]", RegexOptions.IgnoreCase);
        }

        private static string ConvertExcelDateFormatToDotNet(string format)
        {
            var builder = new StringBuilder();
            bool hasAmPm = format.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) ||
                format.Contains("A/P", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < format.Length;)
            {
                char ch = format[i];
                if (ch == '"')
                {
                    int end = format.IndexOf('"', i + 1);
                    string literal = end > i ? format.Substring(i + 1, end - i - 1) : string.Empty;
                    AppendDateLiteral(builder, literal);
                    i = end > i ? end + 1 : format.Length;
                    continue;
                }

                if (ch == '\\')
                {
                    if (i + 1 < format.Length)
                    {
                        AppendDateLiteral(builder, format.Substring(i + 1, 1));
                    }

                    i += 2;
                    continue;
                }

                if (ch == '_' || ch == '*')
                {
                    i += 2;
                    continue;
                }

                string remaining = format.Substring(i);
                if (remaining.StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("tt");
                    i += 5;
                    continue;
                }

                if (remaining.StartsWith("A/P", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Append("tt");
                    i += 3;
                    continue;
                }

                int runLength = CountRepeatedDateFormatChars(format, i);
                char lower = char.ToLowerInvariant(ch);
                switch (lower)
                {
                    case 'y':
                        builder.Append(runLength <= 2 ? "yy" : "yyyy");
                        i += runLength;
                        break;
                    case 'd':
                        builder.Append(runLength switch
                        {
                            1 => "d",
                            2 => "dd",
                            3 => "ddd",
                            _ => "dddd"
                        });
                        i += runLength;
                        break;
                    case 'h':
                        builder.Append(runLength <= 1 ? (hasAmPm ? "h" : "H") : (hasAmPm ? "hh" : "HH"));
                        i += runLength;
                        break;
                    case 's':
                        builder.Append(runLength <= 1 ? "s" : "ss");
                        i += runLength;
                        break;
                    case 'm':
                        bool minute = IsMinuteToken(format, i);
                        builder.Append(minute
                            ? (runLength <= 1 ? "m" : "mm")
                            : runLength switch
                            {
                                1 => "M",
                                2 => "MM",
                                3 => "MMM",
                                _ => "MMMM"
                            });
                        i += runLength;
                        break;
                    default:
                        AppendDateLiteral(builder, ch.ToString());
                        i++;
                        break;
                }
            }

            return builder.ToString();
        }

        private static int CountRepeatedDateFormatChars(string format, int start)
        {
            char ch = char.ToLowerInvariant(format[start]);
            int count = 0;
            while (start + count < format.Length &&
                char.ToLowerInvariant(format[start + count]) == ch)
            {
                count++;
            }

            return count;
        }

        private static bool IsMinuteToken(string format, int index)
        {
            int previous = FindPreviousDateFormatToken(format, index);
            int next = FindNextDateFormatToken(format, index);
            return (previous >= 0 && "hHsS".IndexOf(format[previous]) >= 0) ||
                (next >= 0 && "hHsS".IndexOf(format[next]) >= 0);
        }

        private static int FindPreviousDateFormatToken(string format, int index)
        {
            for (int i = index - 1; i >= 0; i--)
            {
                char ch = format[i];
                if (char.IsWhiteSpace(ch) || ch == ':' || ch == '/' || ch == '-' || ch == '.')
                {
                    continue;
                }

                if ("yYmMdDhHsS".IndexOf(ch) >= 0)
                {
                    return i;
                }

                return -1;
            }

            return -1;
        }

        private static int FindNextDateFormatToken(string format, int index)
        {
            for (int i = index + 1; i < format.Length; i++)
            {
                char ch = format[i];
                if (char.IsWhiteSpace(ch) || ch == ':' || ch == '/' || ch == '-' || ch == '.')
                {
                    continue;
                }

                if ("yYmMdDhHsS".IndexOf(ch) >= 0)
                {
                    return i;
                }

                return -1;
            }

            return -1;
        }

        private static void AppendDateLiteral(StringBuilder builder, string literal)
        {
            foreach (char ch in literal)
            {
                if (ch == '\'')
                {
                    builder.Append("''");
                }
                else if (char.IsLetter(ch))
                {
                    builder.Append('\'').Append(ch).Append('\'');
                }
                else
                {
                    builder.Append(ch);
                }
            }
        }

        private static string FormatWorkbookNumberValue(double numericValue, string formatCode, string rawValue)
        {
            List<string> sections = SplitWorkbookFormatSections(formatCode);
            bool usesNegativeSection = numericValue < 0 && sections.Count > 1;
            string section = CleanWorkbookFormatSection(SelectWorkbookFormatSection(formatCode, numericValue));
            if (string.IsNullOrWhiteSpace(section) ||
                section.Equals("General", StringComparison.OrdinalIgnoreCase) ||
                section.Contains("/", StringComparison.Ordinal) && section.Contains("?", StringComparison.Ordinal))
            {
                return rawValue;
            }

            try
            {
                double valueToFormat = usesNegativeSection ? Math.Abs(numericValue) : numericValue;
                return valueToFormat.ToString(section, CultureInfo.CurrentCulture);
            }
            catch
            {
                return rawValue;
            }
        }

        private static string SelectWorkbookFormatSection(string formatCode, double value)
        {
            List<string> sections = SplitWorkbookFormatSections(formatCode);
            if (sections.Count == 0)
            {
                return formatCode;
            }

            if (sections.Count == 1)
            {
                return sections[0];
            }

            if (value > 0)
            {
                return sections[0];
            }

            if (value < 0)
            {
                return sections.Count > 1 ? sections[1] : sections[0];
            }

            return sections.Count > 2 ? sections[2] : sections[0];
        }

        private static List<string> SplitWorkbookFormatSections(string formatCode)
        {
            var sections = new List<string>();
            var current = new StringBuilder();
            bool inQuote = false;
            bool escaped = false;
            foreach (char ch in formatCode)
            {
                if (escaped)
                {
                    current.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    current.Append(ch);
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inQuote = !inQuote;
                    current.Append(ch);
                    continue;
                }

                if (ch == ';' && !inQuote)
                {
                    sections.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            sections.Add(current.ToString());
            return sections;
        }

        private static string CleanWorkbookFormatSection(string section)
        {
            section = Regex.Replace(section, @"\[[^\]]+\]", match =>
            {
                string value = match.Value.Trim('[', ']');
                return value.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("hh", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("m", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("mm", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("ss", StringComparison.OrdinalIgnoreCase)
                    ? match.Value
                    : string.Empty;
            });
            section = Regex.Replace(section, @"\[\$-[^\]]+\]", string.Empty);
            section = section.Replace("_-", string.Empty, StringComparison.Ordinal)
                .Replace("_)", string.Empty, StringComparison.Ordinal)
                .Replace("_(", string.Empty, StringComparison.Ordinal)
                .Replace("_ ", string.Empty, StringComparison.Ordinal);
            section = Regex.Replace(section, @"_.", string.Empty);
            section = Regex.Replace(section, @"\*.", string.Empty);
            return section.Trim();
        }

        private static string RemoveWorkbookFormatLiterals(string formatCode)
        {
            var builder = new StringBuilder();
            bool inQuote = false;
            bool escaped = false;
            foreach (char ch in formatCode)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inQuote = !inQuote;
                    continue;
                }

                if (!inQuote)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString();
        }
    }
}
