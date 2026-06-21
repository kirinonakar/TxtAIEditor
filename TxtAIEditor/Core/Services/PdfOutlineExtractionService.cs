using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    public sealed class PdfOutlineItem
    {
        public string Title { get; init; } = string.Empty;
        public int PageNumber { get; init; }
        public int Level { get; init; }
    }

    public sealed class PdfOutlineExtractionService
    {
        private const int MaxOutlineItems = 1000;
        private const int MaxTraversalDepth = 12;

        public async Task<IReadOnlyList<PdfOutlineItem>> ExtractOutlineAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return Array.Empty<PdfOutlineItem>();
            }

            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            string raw = Encoding.Latin1.GetString(bytes);
            var objects = ParseObjects(raw);
            if (objects.Count == 0)
            {
                return Array.Empty<PdfOutlineItem>();
            }

            var pageNumbers = BuildPageNumberMap(objects);
            if (pageNumbers.Count == 0)
            {
                return Array.Empty<PdfOutlineItem>();
            }

            var namedDestinations = BuildNamedDestinationMap(objects, pageNumbers);
            string? firstOutlineRef = FindFirstOutlineRef(objects);
            if (string.IsNullOrEmpty(firstOutlineRef))
            {
                return Array.Empty<PdfOutlineItem>();
            }

            var items = new List<PdfOutlineItem>();
            TraverseOutlineChain(firstOutlineRef, 0, objects, pageNumbers, namedDestinations, items, new HashSet<string>());
            return items;
        }

        private static Dictionary<string, string> ParseObjects(string raw)
        {
            var matches = Regex.Matches(raw, @"(?m)(\d+)\s+(\d+)\s+obj\b");
            var objects = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                int contentStart = match.Index + match.Length;
                int contentEnd = raw.IndexOf("endobj", contentStart, StringComparison.Ordinal);
                if (contentEnd < 0)
                {
                    continue;
                }

                string key = MakeRef(match.Groups[1].Value, match.Groups[2].Value);
                if (!objects.ContainsKey(key))
                {
                    objects[key] = raw.Substring(contentStart, contentEnd - contentStart);
                }
            }

            return objects;
        }

        private static Dictionary<string, int> BuildPageNumberMap(IReadOnlyDictionary<string, string> objects)
        {
            string? pagesRef = FindCatalogPagesRef(objects);
            if (!string.IsNullOrEmpty(pagesRef))
            {
                var orderedPages = new List<string>();
                AppendPageRefsFromTree(pagesRef, objects, orderedPages, new HashSet<string>());
                if (orderedPages.Count > 0)
                {
                    return orderedPages
                        .Select((pageRef, index) => new { pageRef, pageNumber = index + 1 })
                        .GroupBy(item => item.pageRef, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First().pageNumber, StringComparer.Ordinal);
                }
            }

            int pageNumber = 1;
            var fallback = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in objects.OrderBy(pair => GetObjectNumber(pair.Key)))
            {
                if (IsPageObject(pair.Value))
                {
                    fallback[pair.Key] = pageNumber++;
                }
            }

            return fallback;
        }

        private static string? FindCatalogPagesRef(IReadOnlyDictionary<string, string> objects)
        {
            foreach (string content in objects.Values)
            {
                if (!Regex.IsMatch(content, @"/Type\s*/Catalog\b"))
                {
                    continue;
                }

                Match pagesMatch = Regex.Match(content, @"/Pages\s+(\d+)\s+(\d+)\s+R\b");
                if (pagesMatch.Success)
                {
                    return MakeRef(pagesMatch.Groups[1].Value, pagesMatch.Groups[2].Value);
                }
            }

            return null;
        }

        private static void AppendPageRefsFromTree(
            string nodeRef,
            IReadOnlyDictionary<string, string> objects,
            List<string> orderedPages,
            HashSet<string> visited)
        {
            if (!visited.Add(nodeRef) || !objects.TryGetValue(nodeRef, out string? content))
            {
                return;
            }

            if (IsPageObject(content))
            {
                orderedPages.Add(nodeRef);
                return;
            }

            Match kidsMatch = Regex.Match(content, @"/Kids\s*\[(?<kids>.*?)\]", RegexOptions.Singleline);
            if (!kidsMatch.Success)
            {
                return;
            }

            foreach (Match childMatch in Regex.Matches(kidsMatch.Groups["kids"].Value, @"(\d+)\s+(\d+)\s+R\b"))
            {
                AppendPageRefsFromTree(
                    MakeRef(childMatch.Groups[1].Value, childMatch.Groups[2].Value),
                    objects,
                    orderedPages,
                    visited);
            }
        }

        private static bool IsPageObject(string content)
        {
            return Regex.IsMatch(content, @"/Type\s*/Page\b") &&
                !Regex.IsMatch(content, @"/Type\s*/Pages\b");
        }

        private static string? FindFirstOutlineRef(IReadOnlyDictionary<string, string> objects)
        {
            foreach (string content in objects.Values)
            {
                if (!Regex.IsMatch(content, @"/Type\s*/Catalog\b"))
                {
                    continue;
                }

                Match outlinesMatch = Regex.Match(content, @"/Outlines\s+(\d+)\s+(\d+)\s+R\b");
                if (outlinesMatch.Success &&
                    objects.TryGetValue(MakeRef(outlinesMatch.Groups[1].Value, outlinesMatch.Groups[2].Value), out string? outlinesContent))
                {
                    string? first = ReadReference(outlinesContent, "First");
                    if (!string.IsNullOrEmpty(first))
                    {
                        return first;
                    }
                }
            }

            foreach (string content in objects.Values)
            {
                if (!Regex.IsMatch(content, @"/Type\s*/Outlines\b"))
                {
                    continue;
                }

                string? first = ReadReference(content, "First");
                if (!string.IsNullOrEmpty(first))
                {
                    return first;
                }
            }

            return null;
        }

        private static Dictionary<string, int> BuildNamedDestinationMap(
            IReadOnlyDictionary<string, string> objects,
            IReadOnlyDictionary<string, int> pageNumbers)
        {
            string? rootRef = FindNamedDestinationRootRef(objects);
            var namedDestinations = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(rootRef))
            {
                return namedDestinations;
            }

            AppendNamedDestinations(rootRef, objects, pageNumbers, namedDestinations, new HashSet<string>());
            return namedDestinations;
        }

        private static string? FindNamedDestinationRootRef(IReadOnlyDictionary<string, string> objects)
        {
            foreach (string content in objects.Values)
            {
                if (!Regex.IsMatch(content, @"/Type\s*/Catalog\b"))
                {
                    continue;
                }

                string? destsRef = ReadReference(content, "Dests");
                if (!string.IsNullOrEmpty(destsRef))
                {
                    return destsRef;
                }

                string? namesRef = ReadReference(content, "Names");
                if (!string.IsNullOrEmpty(namesRef) &&
                    objects.TryGetValue(namesRef, out string? namesContent))
                {
                    destsRef = ReadReference(namesContent, "Dests");
                    if (!string.IsNullOrEmpty(destsRef))
                    {
                        return destsRef;
                    }
                }
            }

            return null;
        }

        private static void AppendNamedDestinations(
            string nodeRef,
            IReadOnlyDictionary<string, string> objects,
            IReadOnlyDictionary<string, int> pageNumbers,
            Dictionary<string, int> namedDestinations,
            HashSet<string> visited)
        {
            if (!visited.Add(nodeRef) || !objects.TryGetValue(nodeRef, out string? content))
            {
                return;
            }

            Match kidsMatch = Regex.Match(content, @"/Kids\s*\[(?<kids>.*?)\]", RegexOptions.Singleline);
            if (kidsMatch.Success)
            {
                foreach (Match childMatch in Regex.Matches(kidsMatch.Groups["kids"].Value, @"(\d+)\s+(\d+)\s+R\b"))
                {
                    AppendNamedDestinations(
                        MakeRef(childMatch.Groups[1].Value, childMatch.Groups[2].Value),
                        objects,
                        pageNumbers,
                        namedDestinations,
                        visited);
                }
            }

            Match namesMatch = Regex.Match(content, @"/Names\s*\[(?<names>.*?)\]", RegexOptions.Singleline);
            if (!namesMatch.Success)
            {
                return;
            }

            ParseNameArray(namesMatch.Groups["names"].Value, objects, pageNumbers, namedDestinations);
        }

        private static void ParseNameArray(
            string namesArray,
            IReadOnlyDictionary<string, string> objects,
            IReadOnlyDictionary<string, int> pageNumbers,
            Dictionary<string, int> namedDestinations)
        {
            int index = 0;
            while (index < namesArray.Length)
            {
                if (!TryReadPdfStringToken(namesArray, index, out string name, out index))
                {
                    index++;
                    continue;
                }

                SkipWhiteSpace(namesArray, ref index);
                int pageNumber = 0;
                if (index < namesArray.Length && namesArray[index] == '[')
                {
                    int end = FindMatchingBracket(namesArray, index, '[', ']');
                    if (end > index)
                    {
                        pageNumber = ResolveDestinationArrayPageNumber(namesArray.Substring(index, end - index + 1), pageNumbers);
                        index = end + 1;
                    }
                }
                else
                {
                    Match refMatch = Regex.Match(namesArray.Substring(index), @"^(\d+)\s+(\d+)\s+R\b");
                    if (refMatch.Success)
                    {
                        string destRef = MakeRef(refMatch.Groups[1].Value, refMatch.Groups[2].Value);
                        if (objects.TryGetValue(destRef, out string? destinationContent))
                        {
                            pageNumber = ResolveDestinationObjectPageNumber(destinationContent, pageNumbers);
                        }

                        index += refMatch.Length;
                    }
                }

                if (!string.IsNullOrWhiteSpace(name) && pageNumber > 0)
                {
                    namedDestinations[name] = pageNumber;
                }
            }
        }

        private static void TraverseOutlineChain(
            string? firstRef,
            int level,
            IReadOnlyDictionary<string, string> objects,
            IReadOnlyDictionary<string, int> pageNumbers,
            IReadOnlyDictionary<string, int> namedDestinations,
            List<PdfOutlineItem> items,
            HashSet<string> visited)
        {
            string? currentRef = firstRef;
            while (!string.IsNullOrEmpty(currentRef) &&
                   items.Count < MaxOutlineItems &&
                   level <= MaxTraversalDepth &&
                   visited.Add(currentRef))
            {
                if (!objects.TryGetValue(currentRef, out string? content))
                {
                    break;
                }

                string title = ReadPdfString(content, "Title");
                int pageNumber = ResolvePageNumber(content, objects, pageNumbers, namedDestinations, new HashSet<string>());
                if (!string.IsNullOrWhiteSpace(title) && pageNumber > 0)
                {
                    items.Add(new PdfOutlineItem
                    {
                        Title = title,
                        PageNumber = pageNumber,
                        Level = level
                    });
                }

                string? firstChildRef = ReadReference(content, "First");
                if (!string.IsNullOrEmpty(firstChildRef))
                {
                    TraverseOutlineChain(firstChildRef, level + 1, objects, pageNumbers, namedDestinations, items, visited);
                }

                currentRef = ReadReference(content, "Next");
            }
        }

        private static int ResolvePageNumber(
            string content,
            IReadOnlyDictionary<string, string> objects,
            IReadOnlyDictionary<string, int> pageNumbers,
            IReadOnlyDictionary<string, int> namedDestinations,
            HashSet<string> visitedActionRefs)
        {
            int pageNumber = ResolveDestinationObjectPageNumber(content, pageNumbers);
            if (pageNumber > 0)
            {
                return pageNumber;
            }

            string namedDestination = ReadPdfString(content, "Dest");
            if (!string.IsNullOrWhiteSpace(namedDestination) &&
                namedDestinations.TryGetValue(namedDestination, out pageNumber))
            {
                return pageNumber;
            }

            namedDestination = ReadPdfString(content, "D");
            if (!string.IsNullOrWhiteSpace(namedDestination) &&
                namedDestinations.TryGetValue(namedDestination, out pageNumber))
            {
                return pageNumber;
            }

            string? actionRef = ReadReference(content, "A");
            if (!string.IsNullOrEmpty(actionRef) &&
                visitedActionRefs.Add(actionRef) &&
                objects.TryGetValue(actionRef, out string? actionContent))
            {
                return ResolvePageNumber(actionContent, objects, pageNumbers, namedDestinations, visitedActionRefs);
            }

            return 0;
        }

        private static int ResolveDestinationObjectPageNumber(
            string content,
            IReadOnlyDictionary<string, int> pageNumbers)
        {
            foreach (string pattern in new[]
            {
                @"/Dest\s*\[\s*(\d+)\s+(\d+)\s+R\b",
                @"/D\s*\[\s*(\d+)\s+(\d+)\s+R\b"
            })
            {
                Match match = Regex.Match(content, pattern, RegexOptions.Singleline);
                if (match.Success &&
                    pageNumbers.TryGetValue(MakeRef(match.Groups[1].Value, match.Groups[2].Value), out int pageNumber))
                {
                    return pageNumber;
                }
            }

            return ResolveDestinationArrayPageNumber(content, pageNumbers);
        }

        private static int ResolveDestinationArrayPageNumber(
            string content,
            IReadOnlyDictionary<string, int> pageNumbers)
        {
            Match directArrayMatch = Regex.Match(content, @"\[\s*(\d+)\s+(\d+)\s+R\b", RegexOptions.Singleline);
            if (directArrayMatch.Success &&
                pageNumbers.TryGetValue(MakeRef(directArrayMatch.Groups[1].Value, directArrayMatch.Groups[2].Value), out int pageNumber))
            {
                return pageNumber;
            }

            return 0;
        }

        private static string? ReadReference(string content, string key)
        {
            Match match = Regex.Match(content, "/" + Regex.Escape(key) + @"\s+(\d+)\s+(\d+)\s+R\b");
            return match.Success ? MakeRef(match.Groups[1].Value, match.Groups[2].Value) : null;
        }

        private static string ReadPdfString(string content, string key)
        {
            int keyIndex = content.IndexOf("/" + key, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return string.Empty;
            }

            int index = keyIndex + key.Length + 1;
            while (index < content.Length && char.IsWhiteSpace(content[index]))
            {
                index++;
            }

            if (index >= content.Length)
            {
                return string.Empty;
            }

            if (content[index] == '(')
            {
                return DecodeLiteralString(ReadBalancedLiteralString(content, index));
            }

            if (content[index] == '<' && index + 1 < content.Length && content[index + 1] != '<')
            {
                int end = content.IndexOf('>', index + 1);
                return end > index
                    ? DecodeHexString(content.Substring(index + 1, end - index - 1))
                    : string.Empty;
            }

            if (content[index] == '/')
            {
                int start = index + 1;
                int end = start;
                while (end < content.Length && !char.IsWhiteSpace(content[end]) && content[end] != '/' && content[end] != '>' && content[end] != '<')
                {
                    end++;
                }

                return content.Substring(start, end - start);
            }

            return string.Empty;
        }

        private static bool TryReadPdfStringToken(string content, int start, out string value, out int nextIndex)
        {
            value = string.Empty;
            nextIndex = start;
            SkipWhiteSpace(content, ref nextIndex);
            if (nextIndex >= content.Length)
            {
                return false;
            }

            if (content[nextIndex] == '(')
            {
                int end = FindMatchingBracket(content, nextIndex, '(', ')');
                if (end <= nextIndex)
                {
                    return false;
                }

                value = DecodeLiteralString(ReadBalancedLiteralString(content, nextIndex));
                nextIndex = end + 1;
                return true;
            }

            if (content[nextIndex] == '<' && nextIndex + 1 < content.Length && content[nextIndex + 1] != '<')
            {
                int end = content.IndexOf('>', nextIndex + 1);
                if (end <= nextIndex)
                {
                    return false;
                }

                value = DecodeHexString(content.Substring(nextIndex + 1, end - nextIndex - 1));
                nextIndex = end + 1;
                return true;
            }

            return false;
        }

        private static void SkipWhiteSpace(string content, ref int index)
        {
            while (index < content.Length && char.IsWhiteSpace(content[index]))
            {
                index++;
            }
        }

        private static int FindMatchingBracket(string content, int start, char open, char close)
        {
            int depth = 0;
            bool escaped = false;
            for (int i = start; i < content.Length; i++)
            {
                char ch = content[i];
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

                if (ch == open)
                {
                    depth++;
                    continue;
                }

                if (ch == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string ReadBalancedLiteralString(string content, int start)
        {
            var builder = new StringBuilder();
            int depth = 0;
            bool escaped = false;
            for (int i = start + 1; i < content.Length; i++)
            {
                char ch = content[i];
                if (escaped)
                {
                    builder.Append('\\');
                    builder.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                    builder.Append(ch);
                    continue;
                }

                if (ch == ')')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                    builder.Append(ch);
                    continue;
                }

                builder.Append(ch);
            }

            if (escaped)
            {
                builder.Append('\\');
            }

            return builder.ToString();
        }

        private static string DecodeLiteralString(string value)
        {
            var bytes = new List<byte>(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length)
                {
                    bytes.Add((byte)(ch & 0xFF));
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '(':
                    case ')':
                    case '\\':
                        bytes.Add((byte)next);
                        break;
                    case '\r':
                        if (i + 1 < value.Length && value[i + 1] == '\n')
                        {
                            i++;
                        }
                        break;
                    case '\n':
                        break;
                    default:
                        if (next >= '0' && next <= '7')
                        {
                            int octal = next - '0';
                            int count = 1;
                            while (count < 3 && i + 1 < value.Length && value[i + 1] >= '0' && value[i + 1] <= '7')
                            {
                                octal = (octal * 8) + (value[++i] - '0');
                                count++;
                            }
                            bytes.Add((byte)(octal & 0xFF));
                        }
                        else
                        {
                            bytes.Add((byte)(next & 0xFF));
                        }
                        break;
                }
            }

            return DecodePdfBytes(bytes.ToArray());
        }

        private static string DecodeHexString(string hex)
        {
            string compact = Regex.Replace(hex, @"\s+", string.Empty);
            if (compact.Length == 0)
            {
                return string.Empty;
            }

            if (compact.Length % 2 == 1)
            {
                compact += "0";
            }

            var bytes = new byte[compact.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
            }

            return DecodePdfBytes(bytes);
        }

        private static string DecodePdfBytes(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).Trim();
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Trim();
            }

            string utf8 = Encoding.UTF8.GetString(bytes).Trim();
            return utf8.Contains('\uFFFD')
                ? Encoding.Latin1.GetString(bytes).Trim()
                : utf8;
        }

        private static string MakeRef(string objectNumber, string generationNumber)
        {
            return objectNumber + " " + generationNumber;
        }

        private static int GetObjectNumber(string objectRef)
        {
            int spaceIndex = objectRef.IndexOf(' ', StringComparison.Ordinal);
            string numberPart = spaceIndex > 0 ? objectRef.Substring(0, spaceIndex) : objectRef;
            return int.TryParse(numberPart, out int number) ? number : int.MaxValue;
        }
    }
}
