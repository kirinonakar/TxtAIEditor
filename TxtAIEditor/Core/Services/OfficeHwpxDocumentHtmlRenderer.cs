using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxBinaryCatalog;
using static TxtAIEditor.Core.Services.OfficeHwpxStyleCatalog;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeHwpxDocumentHtmlRenderer
    {
        private readonly struct HwpxPoint
        {
            public HwpxPoint(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }

        private readonly struct HwpxMatrix
        {
            public HwpxMatrix(double e1, double e2, double e3, double e4, double e5, double e6)
            {
                E1 = e1;
                E2 = e2;
                E3 = e3;
                E4 = e4;
                E5 = e5;
                E6 = e6;
            }

            public static HwpxMatrix Identity { get; } = new(1, 0, 0, 0, 1, 0);

            public double E1 { get; }
            public double E2 { get; }
            public double E3 { get; }
            public double E4 { get; }
            public double E5 { get; }
            public double E6 { get; }

            public HwpxPoint Transform(HwpxPoint point)
            {
                return new HwpxPoint(
                    (E1 * point.X) + (E2 * point.Y) + E3,
                    (E4 * point.X) + (E5 * point.Y) + E6);
            }

            public static HwpxMatrix Multiply(HwpxMatrix outer, HwpxMatrix inner)
            {
                return new HwpxMatrix(
                    (outer.E1 * inner.E1) + (outer.E2 * inner.E4),
                    (outer.E1 * inner.E2) + (outer.E2 * inner.E5),
                    (outer.E1 * inner.E3) + (outer.E2 * inner.E6) + outer.E3,
                    (outer.E4 * inner.E1) + (outer.E5 * inner.E4),
                    (outer.E4 * inner.E2) + (outer.E5 * inner.E5),
                    (outer.E4 * inner.E3) + (outer.E5 * inner.E6) + outer.E6);
            }
        }

        public static async Task<string> BuildAsync(string filePath, Func<string, string, string> getString)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems = await LoadHwpxBinaryItemsAsync(archive).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> characterStyles = await LoadHwpxCharacterStylesAsync(archive).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> paragraphStyles = await LoadHwpxParagraphStylesAsync(archive).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> borderFillStyles = await LoadHwpxBorderFillStylesAsync(archive).ConfigureAwait(false);
            var sectionEntries = archive.Entries
                .Where(entry => Regex.IsMatch(entry.FullName, @"^Contents/section\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(entry => GetTrailingNumber(entry.FullName))
                .ToList();

            if (sectionEntries.Count == 0)
            {
                sectionEntries = archive.Entries
                    .Where(entry =>
                        entry.FullName.StartsWith("Contents/", StringComparison.OrdinalIgnoreCase) &&
                        entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                        !entry.FullName.EndsWith("/header.xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (sectionEntries.Count == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerHwpxStructureError", "Could not read the HWPX document structure."));
            }

            var content = new StringBuilder();
            foreach (ZipArchiveEntry sectionEntry in sectionEntries)
            {
                XDocument section = await LoadHwpxSectionXmlAsync(sectionEntry).ConfigureAwait(false);
                AppendHwpxChildrenHtml(
                    content,
                    archive,
                    binaryItems,
                    characterStyles,
                    paragraphStyles,
                    borderFillStyles,
                    section.Root?.Elements() ?? Enumerable.Empty<XElement>());
            }

            if (content.Length == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerNoContent", "No content to display."));
            }

            return BuildDocumentHtml(Path.GetFileName(filePath), content.ToString());
        }

        private static async Task<XDocument> LoadHwpxSectionXmlAsync(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            return await Task.Run(() =>
                XDocument.Load(stream, LoadOptions.PreserveWhitespace)).ConfigureAwait(false);
        }

        private static void AppendHwpxChildrenHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            IReadOnlyDictionary<string, string> borderFillStyles,
            IEnumerable<XElement> elements)
        {
            foreach (XElement element in elements)
            {
                AppendHwpxBlockHtml(
                    builder,
                    archive,
                    binaryItems,
                    characterStyles,
                    paragraphStyles,
                    borderFillStyles,
                    element);
            }
        }

        private static void AppendHwpxBlockHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            IReadOnlyDictionary<string, string> borderFillStyles,
            XElement block)
        {
            switch (block.Name.LocalName)
            {
                case "p":
                    builder.Append(BuildHwpxParagraphHtml(archive, binaryItems, characterStyles, paragraphStyles, block));
                    foreach (XElement table in block.Descendants().Where(e =>
                        e.Name.LocalName == "tbl" &&
                        !IsInsideNestedElement(block, e, "tbl")))
                    {
                        builder.Append(BuildHwpxTableHtml(
                            archive,
                            binaryItems,
                            characterStyles,
                            paragraphStyles,
                            borderFillStyles,
                            table));
                    }

                    break;
                case "tbl":
                    builder.Append(BuildHwpxTableHtml(
                        archive,
                        binaryItems,
                        characterStyles,
                        paragraphStyles,
                        borderFillStyles,
                        block));
                    break;
                default:
                    AppendHwpxChildrenHtml(
                        builder,
                        archive,
                        binaryItems,
                        characterStyles,
                        paragraphStyles,
                        borderFillStyles,
                        block.Elements());
                    break;
            }
        }

        private static string BuildHwpxParagraphHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            XElement paragraph)
        {
            var content = new StringBuilder();
            var renderedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var renderedContainers = new HashSet<XElement>();
            IReadOnlyDictionary<XText, string> textValues = GetHwpxRenderedTextValues(paragraph);
            foreach (XNode node in paragraph.DescendantNodes())
            {
                if (IsInsideNestedElement(paragraph, node, "tbl") ||
                    IsInsideRenderedContainer(paragraph, node, renderedContainers))
                {
                    continue;
                }

                if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                {
                    string text = textValues.TryGetValue(textNode, out string? renderedText)
                        ? renderedText
                        : textNode.Value;
                    AppendStyledText(content, text, GetHwpxTextStyle(textNode, characterStyles));
                    continue;
                }

                if (node is not XElement element)
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "tab":
                        content.Append('\t');
                        break;
                    case "lineBreak":
                    case "br":
                    case "cr":
                        content.Append("<br>");
                        break;
                    case "nbSpace":
                        content.Append(' ');
                        break;
                    case "fwSpace":
                        string? listMarkerSpacing = GetHwpxListMarkerSpacing(element);
                        if (listMarkerSpacing == null)
                        {
                            content.Append("&#12288;");
                        }
                        else
                        {
                            content.Append(listMarkerSpacing);
                        }

                        break;
                    case "pic":
                    case "img":
                        AppendHwpxImageHtml(content, archive, binaryItems, element, renderedImages);
                        break;
                    case "container":
                        if (TryAppendHwpxLayeredImageHtml(
                            content,
                            archive,
                            binaryItems,
                            characterStyles,
                            element,
                            renderedImages) ||
                            TryAppendHwpxGroupShapeHtml(
                                content,
                                characterStyles,
                                paragraphStyles,
                                element))
                        {
                            renderedContainers.Add(element);
                        }
                        break;
                }
            }

            string paragraphStyle = GetHwpxParagraphStyle(paragraph, paragraphStyles);
            string styleAttribute = string.IsNullOrWhiteSpace(paragraphStyle)
                ? string.Empty
                : " style=\"" + Html(paragraphStyle) + "\"";
            return content.Length == 0
                ? "<p class=\"doc-paragraph empty-paragraph\"" + styleAttribute + "></p>"
                : "<p class=\"doc-paragraph\"" + styleAttribute + ">" + content + "</p>";
        }

        private static IReadOnlyDictionary<XText, string> GetHwpxRenderedTextValues(XElement paragraph)
        {
            var textNodes = paragraph
                .DescendantNodes()
                .OfType<XText>()
                .Where(textNode =>
                    textNode.Parent?.Name.LocalName == "t" &&
                    !IsInsideNestedElement(paragraph, textNode, "tbl"))
                .ToList();
            var softWrapPositions = paragraph
                .Descendants()
                .Where(element =>
                    element.Name.LocalName == "lineseg" &&
                    !IsInsideNestedElement(paragraph, element, "tbl"))
                .Select(element => GetAttributeValue(element, "textpos"))
                .Select(value => int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int position)
                    ? position
                    : -1)
                .Where(position => position > 0)
                .ToHashSet();

            var renderedValues = new Dictionary<XText, string>();
            if (softWrapPositions.Count > 0)
            {
                int textOffset = 0;
                foreach (XText textNode in textNodes)
                {
                    string text = textNode.Value;
                    string renderedText = NormalizeHwpxSoftWrapPadding(text, textOffset, softWrapPositions);
                    if (!ReferenceEquals(renderedText, text))
                    {
                        renderedValues[textNode] = renderedText;
                    }

                    textOffset += text.Length;
                }
            }

            NormalizeHwpxLeadingSpaceRuns(paragraph, renderedValues);
            return renderedValues;
        }

        private static void NormalizeHwpxLeadingSpaceRuns(
            XElement paragraph,
            IDictionary<XText, string> renderedValues)
        {
            var leadingSpaceNodes = new List<XText>();
            int leadingSpaceCount = 0;
            foreach (XNode node in paragraph.DescendantNodes())
            {
                if (IsInsideNestedElement(paragraph, node, "tbl"))
                {
                    continue;
                }

                if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                {
                    string text = renderedValues.TryGetValue(textNode, out string? renderedText)
                        ? renderedText
                        : textNode.Value;
                    if (text.Length > 0 && text.All(value => value == ' '))
                    {
                        leadingSpaceNodes.Add(textNode);
                        leadingSpaceCount += text.Length;
                        continue;
                    }

                    if (leadingSpaceNodes.Count == 0 || string.IsNullOrEmpty(text))
                    {
                        return;
                    }

                    foreach (XText leadingSpaceNode in leadingSpaceNodes)
                    {
                        renderedValues[leadingSpaceNode] = string.Empty;
                    }

                    renderedValues[textNode] = new string(' ', leadingSpaceCount) + text;
                    return;
                }

                if (leadingSpaceNodes.Count > 0 &&
                    node is XElement element &&
                    element.Name.LocalName is "tab" or "lineBreak" or "br" or "cr" or
                        "nbSpace" or "fwSpace" or "pic" or "img" or "container")
                {
                    return;
                }
            }
        }

        private static string NormalizeHwpxSoftWrapPadding(
            string text,
            int textOffset,
            IReadOnlySet<int> softWrapPositions)
        {
            StringBuilder? normalized = null;
            int copyStart = 0;
            for (int index = 0; index < text.Length;)
            {
                if (text[index] != ' ')
                {
                    index++;
                    continue;
                }

                int whitespaceStart = index;
                while (index < text.Length && text[index] == ' ')
                {
                    index++;
                }

                int whitespaceEnd = index;
                if (whitespaceEnd - whitespaceStart < 2 ||
                    whitespaceStart == 0 ||
                    whitespaceEnd >= text.Length ||
                    !softWrapPositions.Any(position =>
                        position >= textOffset + whitespaceStart &&
                        position < textOffset + whitespaceEnd))
                {
                    continue;
                }

                normalized ??= new StringBuilder(text.Length);
                normalized.Append(text, copyStart, whitespaceStart - copyStart);
                if (!IsHangulSyllable(text[whitespaceStart - 1]) ||
                    !IsHangulSyllable(text[whitespaceEnd]))
                {
                    normalized.Append(' ');
                }

                copyStart = whitespaceEnd;
            }

            if (normalized == null)
            {
                return text;
            }

            normalized.Append(text, copyStart, text.Length - copyStart);
            return normalized.ToString();
        }

        private static bool IsHangulSyllable(char value)
        {
            return value >= '\uAC00' && value <= '\uD7A3';
        }

        private static string? GetHwpxListMarkerSpacing(XElement fixedWidthSpace)
        {
            if (fixedWidthSpace.Name.LocalName != "fwSpace")
            {
                return null;
            }

            XElement? paragraph = fixedWidthSpace.Ancestors()
                .FirstOrDefault(element => element.Name.LocalName == "p");
            if (paragraph == null)
            {
                return null;
            }

            var inlineNodes = paragraph.DescendantNodes()
                .Where(node =>
                    !IsInsideNestedElement(paragraph, node, "tbl") &&
                    (node is XText textNode && textNode.Parent?.Name.LocalName == "t" ||
                     node is XElement element &&
                     element.Name.LocalName is "tab" or "lineBreak" or "br" or "cr" or
                         "nbSpace" or "fwSpace" or "pic" or "img" or "container"))
                .ToList();
            int spaceIndex = inlineNodes.FindIndex(node => ReferenceEquals(node, fixedWidthSpace));
            if (spaceIndex < 0)
            {
                return null;
            }

            int sequenceStart = spaceIndex;
            while (sequenceStart > 0 &&
                   inlineNodes[sequenceStart - 1] is XElement previousSpace &&
                   previousSpace.Name.LocalName == "fwSpace")
            {
                sequenceStart--;
            }

            int sequenceEnd = spaceIndex;
            while (sequenceEnd + 1 < inlineNodes.Count &&
                   inlineNodes[sequenceEnd + 1] is XElement nextSpace &&
                   nextSpace.Name.LocalName == "fwSpace")
            {
                sequenceEnd++;
            }

            if (sequenceStart == 0 ||
                sequenceEnd + 1 >= inlineNodes.Count ||
                inlineNodes[sequenceStart - 1] is not XText ||
                inlineNodes[sequenceEnd + 1] is not XText suffix ||
                string.IsNullOrEmpty(suffix.Value))
            {
                return null;
            }

            int prefixStart = sequenceStart - 1;
            while (prefixStart > 0 && inlineNodes[prefixStart - 1] is XText)
            {
                prefixStart--;
            }

            string markerPrefix = string.Concat(
                inlineNodes
                    .Skip(prefixStart)
                    .Take(sequenceStart - prefixStart)
                    .OfType<XText>()
                    .Select(textNode => textNode.Value));
            Match markerMatch = Regex.Match(
                markerPrefix,
                @"(?:^|\s)(?:[가-힣]\.|(?<numeric>\d+\))|(?<note>※)|(?<dash>-))$");
            if (!markerMatch.Success)
            {
                return null;
            }

            if (sequenceEnd - sequenceStart + 1 < 2 &&
                !markerMatch.Groups["dash"].Success)
            {
                return null;
            }

            if (spaceIndex != sequenceStart)
            {
                return string.Empty;
            }

            if (markerMatch.Groups["numeric"].Success ||
                markerMatch.Groups["note"].Success)
            {
                return " ";
            }

            return suffix.Value.StartsWith(' ')
                ? string.Empty
                : " ";
        }

        private static string BuildHwpxTableHtml(
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            IReadOnlyDictionary<string, string> borderFillStyles,
            XElement table)
        {
            var rows = table.Elements().Where(e => e.Name.LocalName == "tr").ToList();
            if (rows.Count == 0)
            {
                rows = table.Descendants().Where(e => e.Name.LocalName == "tr").ToList();
            }

            var builder = new StringBuilder();
            bool hasColumnGrid = TryBuildHwpxTableTracks(
                table,
                "colCnt",
                "colAddr",
                "colSpan",
                "width",
                out IReadOnlyList<double> columnWidths);
            bool hasRowGrid = TryBuildHwpxTableTracks(
                table,
                "rowCnt",
                "rowAddr",
                "rowSpan",
                "height",
                out IReadOnlyList<double> rowHeights);
            string wrapperStyle = BuildHwpxTableWrapperStyle(table);
            string tableStyle = BuildHwpxTableStyle(table);
            builder.Append("<div class=\"doc-table-wrap hwpx-table-wrap\"");
            AppendStyleAttribute(builder, wrapperStyle);
            builder.Append("><table class=\"doc-table hwpx-table\"");
            AppendStyleAttribute(builder, tableStyle);
            builder.Append('>');
            if (hasColumnGrid)
            {
                double totalWidth = columnWidths.Sum();
                builder.Append("<colgroup>");
                foreach (double columnWidth in columnWidths)
                {
                    builder.Append("<col style=\"width:")
                        .Append(CssPercent(columnWidth / totalWidth * 100.0))
                        .Append("\">");
                }

                builder.Append("</colgroup>");
            }

            builder.Append("<tbody>");
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                XElement row = rows[rowIndex];
                builder.Append("<tr");
                if (hasRowGrid && rowIndex < rowHeights.Count)
                {
                    builder.Append(" style=\"height:")
                        .Append(HwpxPoints(rowHeights[rowIndex]))
                        .Append('"');
                }

                builder.Append('>');
                var cells = row.Elements().Where(e => e.Name.LocalName == "tc").ToList();
                if (cells.Count == 0)
                {
                    cells = row.Descendants().Where(e => e.Name.LocalName == "tc").ToList();
                }

                foreach (XElement cell in cells)
                {
                    string colspan = ReadPositiveIntegerAttribute(cell, "colSpan");
                    string rowspan = ReadPositiveIntegerAttribute(cell, "rowSpan");
                    builder.Append("<td");
                    if (!string.IsNullOrWhiteSpace(colspan))
                    {
                        builder.Append(" colspan=\"").Append(Html(colspan)).Append('"');
                    }

                    if (!string.IsNullOrWhiteSpace(rowspan))
                    {
                        builder.Append(" rowspan=\"").Append(Html(rowspan)).Append('"');
                    }

                    string borderFillId = GetAttributeValue(cell, "borderFillIDRef");
                    if (string.IsNullOrWhiteSpace(borderFillId))
                    {
                        borderFillId = GetAttributeValue(table, "borderFillIDRef");
                    }

                    var cellStyles = new List<string>();
                    if (borderFillStyles.TryGetValue(borderFillId, out string? borderFillStyle))
                    {
                        cellStyles.Add(borderFillStyle);
                    }

                    if (TryReadHwpxDimensions(cell, "cellSz", out double cellWidth, out double cellHeight))
                    {
                        if (!hasColumnGrid)
                        {
                            cellStyles.Add("width:" + HwpxPoints(cellWidth));
                        }

                        if (!hasRowGrid)
                        {
                            cellStyles.Add("height:" + HwpxPoints(cellHeight));
                        }
                    }

                    string cellPadding = BuildHwpxBoxSpacingStyle(cell, "cellMargin", "padding");
                    if (!string.IsNullOrWhiteSpace(cellPadding))
                    {
                        cellStyles.Add(cellPadding);
                    }

                    XElement? subList = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "subList");
                    string verticalAlignment = GetAttributeValue(subList, "vertAlign").ToUpperInvariant();
                    cellStyles.Add("vertical-align:" + (verticalAlignment switch
                    {
                        "CENTER" => "middle",
                        "BOTTOM" => "bottom",
                        _ => "top"
                    }));
                    AppendStyleAttribute(builder, string.Join(';', cellStyles));

                    builder.Append('>');
                    int before = builder.Length;
                    if (subList != null && HasHwpxFloatingContainer(subList))
                    {
                        foreach (XElement cellElement in cell.Elements())
                        {
                            if (ReferenceEquals(cellElement, subList))
                            {
                                AppendHwpxChildrenHtml(
                                    builder,
                                    archive,
                                    binaryItems,
                                    characterStyles,
                                    paragraphStyles,
                                    borderFillStyles,
                                    subList.Elements().Where(element =>
                                        !IsHwpxFloatingObjectSpacerParagraph(element)));
                            }
                            else
                            {
                                AppendHwpxBlockHtml(
                                    builder,
                                    archive,
                                    binaryItems,
                                    characterStyles,
                                    paragraphStyles,
                                    borderFillStyles,
                                    cellElement);
                            }
                        }
                    }
                    else
                    {
                        AppendHwpxChildrenHtml(
                            builder,
                            archive,
                            binaryItems,
                            characterStyles,
                            paragraphStyles,
                            borderFillStyles,
                            cell.Elements());
                    }

                    if (builder.Length == before)
                    {
                        builder.Append("&nbsp;");
                    }

                    builder.Append("</td>");
                }

                builder.Append("</tr>");
            }

            builder.Append("</tbody></table></div>");
            return builder.ToString();
        }

        private static bool HasHwpxFloatingContainer(XElement subList)
        {
            return subList.Descendants().Any(element =>
            {
                if (element.Name.LocalName != "container")
                {
                    return false;
                }

                XElement? position = element.Elements()
                    .FirstOrDefault(child => child.Name.LocalName == "pos");
                return GetAttributeValue(position, "treatAsChar") == "0";
            });
        }

        private static bool IsHwpxFloatingObjectSpacerParagraph(XElement element)
        {
            if (element.Name.LocalName != "p")
            {
                return false;
            }

            return !element.Descendants().Any(descendant =>
                descendant.Name.LocalName is "tbl" or "container" or "pic" or "img" ||
                descendant.Name.LocalName == "t" && !string.IsNullOrWhiteSpace(descendant.Value));
        }

        private static bool TryBuildHwpxTableTracks(
            XElement table,
            string trackCountAttribute,
            string addressAttribute,
            string spanAttribute,
            string sizeAttribute,
            out IReadOnlyList<double> trackSizes)
        {
            if (!int.TryParse(
                    GetAttributeValue(table, trackCountAttribute),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int trackCount) ||
                trackCount <= 0)
            {
                trackSizes = Array.Empty<double>();
                return false;
            }

            var edges = Enumerable.Range(0, trackCount + 1)
                .Select(_ => new List<(int Target, double Delta)>())
                .ToArray();
            foreach (XElement cell in table.Descendants().Where(element =>
                element.Name.LocalName == "tc" &&
                !IsInsideNestedElement(table, element, "tbl")))
            {
                XElement? address = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "cellAddr");
                XElement? span = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "cellSpan");
                XElement? size = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "cellSz");
                if (!int.TryParse(
                        GetAttributeValue(address, addressAttribute),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int start) ||
                    !int.TryParse(
                        GetAttributeValue(span, spanAttribute),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int length) ||
                    !double.TryParse(
                        GetAttributeValue(size, sizeAttribute),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double extent) ||
                    start < 0 ||
                    length <= 0 ||
                    start + length > trackCount ||
                    extent <= 0)
                {
                    continue;
                }

                int end = start + length;
                edges[start].Add((end, extent));
                edges[end].Add((start, -extent));
            }

            var boundaries = new double?[trackCount + 1];
            var pending = new Queue<int>();
            boundaries[0] = 0;
            pending.Enqueue(0);
            while (pending.Count > 0)
            {
                int current = pending.Dequeue();
                foreach ((int target, double delta) in edges[current])
                {
                    if (boundaries[target].HasValue)
                    {
                        continue;
                    }

                    boundaries[target] = boundaries[current]!.Value + delta;
                    pending.Enqueue(target);
                }
            }

            if (boundaries.Any(boundary => !boundary.HasValue))
            {
                trackSizes = Array.Empty<double>();
                return false;
            }

            var sizes = new List<double>(trackCount);
            for (int index = 0; index < trackCount; index++)
            {
                double size = boundaries[index + 1]!.Value - boundaries[index]!.Value;
                if (size <= 0)
                {
                    trackSizes = Array.Empty<double>();
                    return false;
                }

                sizes.Add(size);
            }

            trackSizes = sizes;
            return true;
        }

        private static string BuildHwpxTableWrapperStyle(XElement table)
        {
            XElement? outMargin = table.Elements().FirstOrDefault(e => e.Name.LocalName == "outMargin");
            if (outMargin == null)
            {
                return string.Empty;
            }

            double top = ReadHwpxDoubleAttribute(outMargin, "top");
            double bottom = ReadHwpxDoubleAttribute(outMargin, "bottom");
            return "margin:" + HwpxPoints(top) + " 0 " + HwpxPoints(bottom);
        }

        private static string BuildHwpxTableStyle(XElement table)
        {
            var styles = new List<string>();
            if (TryReadHwpxDimensions(table, "sz", out double width, out double height))
            {
                styles.Add("width:" + HwpxPoints(width));
                styles.Add("height:" + HwpxPoints(height));
            }

            double cellSpacing = ReadHwpxDoubleAttribute(table, "cellSpacing");
            if (cellSpacing > 0)
            {
                styles.Add("border-collapse:separate");
                styles.Add("border-spacing:" + HwpxPoints(cellSpacing));
            }
            else
            {
                styles.Add("border-collapse:collapse");
                styles.Add("border-spacing:0");
            }

            XElement? position = table.Elements().FirstOrDefault(e => e.Name.LocalName == "pos");
            string horizontalAlignment = GetAttributeValue(position, "horzAlign").ToUpperInvariant();
            switch (horizontalAlignment)
            {
                case "CENTER":
                    styles.Add("margin-left:auto");
                    styles.Add("margin-right:auto");
                    break;
                case "RIGHT":
                    styles.Add("margin-left:auto");
                    styles.Add("margin-right:0");
                    break;
                default:
                    styles.Add("margin-left:0");
                    styles.Add("margin-right:auto");
                    break;
            }

            return string.Join(';', styles);
        }

        private static string BuildHwpxBoxSpacingStyle(
            XElement element,
            string spacingElementName,
            string cssProperty)
        {
            XElement? spacing = element.Elements().FirstOrDefault(e => e.Name.LocalName == spacingElementName);
            if (spacing == null)
            {
                return string.Empty;
            }

            double top = ReadHwpxDoubleAttribute(spacing, "top");
            double right = ReadHwpxDoubleAttribute(spacing, "right");
            double bottom = ReadHwpxDoubleAttribute(spacing, "bottom");
            double left = ReadHwpxDoubleAttribute(spacing, "left");
            return cssProperty + ':' +
                HwpxPoints(top) + ' ' +
                HwpxPoints(right) + ' ' +
                HwpxPoints(bottom) + ' ' +
                HwpxPoints(left);
        }

        private static void AppendStyleAttribute(StringBuilder builder, string style)
        {
            if (!string.IsNullOrWhiteSpace(style))
            {
                builder.Append(" style=\"").Append(Html(style)).Append('"');
            }
        }

        private static bool TryAppendHwpxLayeredImageHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            XElement container,
            ISet<string> renderedImages)
        {
            XElement? picture = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "pic");
            XElement? drawText = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "drawText");
            XElement? textShape = drawText?.Ancestors().FirstOrDefault(e => ReferenceEquals(e.Parent, container));
            if (picture == null || drawText == null || textShape == null)
            {
                return false;
            }

            string imagePath = ResolveHwpxImagePath(picture, binaryItems);
            if (string.IsNullOrWhiteSpace(imagePath) || renderedImages.Contains(imagePath))
            {
                return false;
            }

            string? dataUri = TryReadImageDataUri(archive, imagePath);
            if (string.IsNullOrWhiteSpace(dataUri))
            {
                return false;
            }

            var text = new StringBuilder();
            foreach (XElement textParagraph in drawText.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                if (text.Length > 0)
                {
                    text.Append("<br>");
                }

                foreach (XNode node in textParagraph.DescendantNodes())
                {
                    if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                    {
                        AppendStyledText(text, textNode.Value, GetHwpxTextStyle(textNode, characterStyles));
                    }
                    else if (node is XElement element && element.Name.LocalName is "lineBreak" or "br" or "cr")
                    {
                        text.Append("<br>");
                    }
                }
            }

            if (text.Length == 0 ||
                !TryReadHwpxSize(container, out double containerWidth, out double containerHeight))
            {
                return false;
            }

            ReadHwpxOffset(container, out double containerX, out double containerY);
            ReadHwpxOffset(textShape, out double textX, out double textY);
            if (!TryReadHwpxSize(textShape, out double textWidth, out double textHeight))
            {
                return false;
            }

            double left = Math.Clamp((textX - containerX) / containerWidth * 100.0, 0.0, 100.0);
            double top = Math.Clamp((textY - containerY) / containerHeight * 100.0, 0.0, 100.0);
            double width = Math.Clamp(textWidth / containerWidth * 100.0, 0.0, 100.0 - left);
            double height = Math.Clamp(textHeight / containerHeight * 100.0, 0.0, 100.0 - top);

            renderedImages.Add(imagePath);
            builder.Append("<span class=\"hwpx-layered-image\" style=\"aspect-ratio:")
                .Append(CssNumber(containerWidth))
                .Append('/')
                .Append(CssNumber(containerHeight))
                .Append("\"><img src=\"")
                .Append(Html(dataUri))
                .Append("\" alt=\"\"><span class=\"hwpx-layered-text\" style=\"left:")
                .Append(CssPercent(left))
                .Append(";top:")
                .Append(CssPercent(top))
                .Append(";width:")
                .Append(CssPercent(width))
                .Append(";height:")
                .Append(CssPercent(height))
                .Append("\">")
                .Append(text)
                .Append("</span></span>");
            return true;
        }

        private static bool TryAppendHwpxGroupShapeHtml(
            StringBuilder builder,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            XElement container)
        {
            var shapes = container.Elements()
                .Where(element => element.Name.LocalName is "rect" or "line")
                .ToList();
            if (shapes.Count == 0 ||
                !TryReadHwpxSize(container, out double containerWidth, out double containerHeight))
            {
                return false;
            }

            var vectors = new StringBuilder();
            var textLayers = new StringBuilder();
            int renderedShapeCount = 0;
            foreach (XElement shape in shapes)
            {
                HwpxMatrix transform = ReadHwpxRenderingTransform(shape);
                XElement? lineShape = shape.Elements().FirstOrDefault(element => element.Name.LocalName == "lineShape");
                string strokeColor = HwpxShapeStrokeColor(lineShape);
                double strokeWidth = Math.Max(40, ReadHwpxDoubleAttribute(lineShape, "width"));

                if (shape.Name.LocalName == "rect" &&
                    TryReadHwpxRectanglePoints(shape, transform, out IReadOnlyList<HwpxPoint>? points))
                {
                    ReadHwpxBounds(points, out double left, out double top, out double width, out double height);
                    AppendHwpxRectangleVector(
                        vectors,
                        left,
                        top,
                        width,
                        height,
                        strokeColor,
                        strokeWidth,
                        GetAttributeValue(lineShape, "style"));
                    renderedShapeCount++;

                    XElement? drawText = shape.Elements().FirstOrDefault(element => element.Name.LocalName == "drawText");
                    string text = BuildHwpxShapeTextHtml(
                        drawText,
                        shape,
                        characterStyles,
                        paragraphStyles,
                        out bool usesPositionedLineLayout);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        AppendHwpxShapeTextLayer(
                            textLayers,
                            shape,
                            drawText,
                            text,
                            left,
                            top,
                            width,
                            height,
                            containerWidth,
                            containerHeight,
                            usesPositionedLineLayout);
                    }
                }
                else if (shape.Name.LocalName == "line" &&
                    TryReadHwpxLinePoints(shape, transform, out HwpxPoint start, out HwpxPoint end))
                {
                    AppendHwpxLineVector(
                        vectors,
                        start,
                        end,
                        strokeColor,
                        strokeWidth,
                        GetAttributeValue(lineShape, "style"),
                        GetAttributeValue(lineShape, "headStyle"),
                        GetAttributeValue(lineShape, "tailStyle"));
                    renderedShapeCount++;
                }
            }

            if (renderedShapeCount == 0)
            {
                return false;
            }

            builder.Append("<span class=\"hwpx-group-shape\" style=\"aspect-ratio:")
                .Append(CssNumber(containerWidth))
                .Append('/')
                .Append(CssNumber(containerHeight))
                .Append("\"><svg class=\"hwpx-group-shape-vectors\" viewBox=\"0 0 ")
                .Append(CssNumber(containerWidth))
                .Append(' ')
                .Append(CssNumber(containerHeight))
                .Append("\" preserveAspectRatio=\"none\" aria-hidden=\"true\">")
                .Append(vectors)
                .Append("</svg>")
                .Append(textLayers)
                .Append("</span>");
            return true;
        }

        private static string BuildHwpxShapeTextHtml(
            XElement? drawText,
            XElement shape,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            out bool usesPositionedLineLayout)
        {
            usesPositionedLineLayout = false;
            if (drawText == null)
            {
                return string.Empty;
            }

            var paragraphs = drawText.Descendants()
                .Where(element => element.Name.LocalName == "p")
                .ToList();
            ReadHwpxTextMargins(
                drawText,
                out double marginLeft,
                out double marginRight,
                out double marginTop,
                out double marginBottom);
            bool hasShapeSize = TryReadHwpxSize(shape, out double shapeWidth, out double shapeHeight);
            double textWidth = shapeWidth - marginLeft - marginRight;
            double textHeight = shapeHeight - marginTop - marginBottom;
            usesPositionedLineLayout =
                hasShapeSize &&
                textWidth > 0 &&
                textHeight > 0 &&
                paragraphs
                    .Where(paragraph => paragraph.Descendants()
                        .Any(element => element.Name.LocalName == "t" && !string.IsNullOrEmpty(element.Value)))
                    .All(paragraph => paragraph.Descendants()
                        .Count(element => element.Name.LocalName == "lineseg") == 1);

            var text = new StringBuilder();
            foreach (XElement paragraph in paragraphs)
            {
                var paragraphContent = new StringBuilder();
                IReadOnlyDictionary<XText, string> textValues = GetHwpxRenderedTextValues(paragraph);
                foreach (XNode node in paragraph.DescendantNodes())
                {
                    if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                    {
                        string value = textValues.TryGetValue(textNode, out string? renderedText)
                            ? renderedText
                            : textNode.Value;
                        AppendStyledText(
                            paragraphContent,
                            value,
                            GetHwpxTextStyle(textNode, characterStyles));
                    }
                    else if (node is XElement element && element.Name.LocalName is "lineBreak" or "br" or "cr")
                    {
                        paragraphContent.Append("<br>");
                    }
                }

                if (paragraphContent.Length == 0)
                {
                    continue;
                }

                text.Append("<span class=\"hwpx-group-shape-paragraph");
                string paragraphStyle = GetHwpxParagraphStyle(paragraph, paragraphStyles);
                if (usesPositionedLineLayout)
                {
                    text.Append(" hwpx-group-shape-line");
                    XElement lineSegment = paragraph.Descendants()
                        .First(element => element.Name.LocalName == "lineseg");
                    double left = Math.Clamp(
                        ReadHwpxDoubleAttribute(lineSegment, "horzpos") / textWidth * 100.0,
                        0.0,
                        100.0);
                    double top = Math.Clamp(
                        ReadHwpxDoubleAttribute(lineSegment, "vertpos") / textHeight * 100.0,
                        0.0,
                        100.0);
                    double width = Math.Clamp(
                        ReadHwpxDoubleAttribute(lineSegment, "horzsize") / textWidth * 100.0,
                        0.0,
                        100.0 - left);
                    double height = Math.Clamp(
                        ReadHwpxDoubleAttribute(lineSegment, "vertsize") / textHeight * 100.0,
                        0.0,
                        100.0 - top);
                    paragraphStyle = string.Join(
                        ';',
                        new[]
                        {
                            paragraphStyle,
                            "left:" + CssPercent(left),
                            "top:" + CssPercent(top),
                            "width:" + CssPercent(width),
                            "height:" + CssPercent(height),
                            "line-height:1"
                        }.Where(style => !string.IsNullOrWhiteSpace(style)));
                }

                text.Append('"');
                AppendStyleAttribute(text, paragraphStyle);
                text.Append('>')
                    .Append(paragraphContent)
                    .Append("</span>");
            }

            return text.ToString();
        }

        private static void AppendHwpxShapeTextLayer(
            StringBuilder builder,
            XElement shape,
            XElement? drawText,
            string text,
            double left,
            double top,
            double width,
            double height,
            double containerWidth,
            double containerHeight,
            bool usesPositionedLineLayout)
        {
            XElement? subList = drawText?.Descendants().FirstOrDefault(element => element.Name.LocalName == "subList");
            string verticalAlignment = GetAttributeValue(subList, "vertAlign").ToUpperInvariant();
            string justifyContent = verticalAlignment switch
            {
                "TOP" => "flex-start",
                "BOTTOM" => "flex-end",
                _ => "center"
            };
            if (TryReadHwpxSize(shape, out double shapeWidth, out double shapeHeight))
            {
                ReadHwpxTextMargins(
                    drawText,
                    out double marginLeft,
                    out double marginRight,
                    out double marginTop,
                    out double marginBottom);
                double horizontalScale = width / shapeWidth;
                double verticalScale = height / shapeHeight;
                left += marginLeft * horizontalScale;
                top += marginTop * verticalScale;
                width = Math.Max(0, width - ((marginLeft + marginRight) * horizontalScale));
                height = Math.Max(0, height - ((marginTop + marginBottom) * verticalScale));
            }

            double leftPercent = Math.Clamp(left / containerWidth * 100.0, 0.0, 100.0);
            double topPercent = Math.Clamp(top / containerHeight * 100.0, 0.0, 100.0);
            double widthPercent = Math.Clamp(width / containerWidth * 100.0, 0.0, 100.0 - leftPercent);
            double heightPercent = Math.Clamp(height / containerHeight * 100.0, 0.0, 100.0 - topPercent);

            builder.Append("<span class=\"hwpx-group-shape-text");
            if (usesPositionedLineLayout)
            {
                builder.Append(" hwpx-group-shape-text-positioned");
            }

            builder.Append("\" style=\"left:")
                .Append(CssPercent(leftPercent))
                .Append(";top:")
                .Append(CssPercent(topPercent))
                .Append(";width:")
                .Append(CssPercent(widthPercent))
                .Append(";height:")
                .Append(CssPercent(heightPercent))
                .Append(";justify-content:")
                .Append(justifyContent)
                .Append("\">")
                .Append(text)
                .Append("</span>");
        }

        private static void ReadHwpxTextMargins(
            XElement? drawText,
            out double left,
            out double right,
            out double top,
            out double bottom)
        {
            XElement? textMargin = drawText?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "textMargin");
            left = ReadHwpxDoubleAttribute(textMargin, "left");
            right = ReadHwpxDoubleAttribute(textMargin, "right");
            top = ReadHwpxDoubleAttribute(textMargin, "top");
            bottom = ReadHwpxDoubleAttribute(textMargin, "bottom");
        }

        private static void AppendHwpxRectangleVector(
            StringBuilder builder,
            double x,
            double y,
            double width,
            double height,
            string strokeColor,
            double strokeWidth,
            string lineStyle)
        {
            string normalizedStyle = lineStyle.ToUpperInvariant();
            if (normalizedStyle is "DOUBLE_SLIM" or "SLIM_THICK" or "THICK_SLIM" or "SLIM_THICK_SLIM")
            {
                double lineWidth = Math.Max(35, strokeWidth / 3.0);
                AppendHwpxSvgRectangle(builder, x, y, width, height, strokeColor, lineWidth, string.Empty);
                double inset = Math.Max(110, strokeWidth * 0.9);
                if (width > inset * 2 && height > inset * 2)
                {
                    AppendHwpxSvgRectangle(
                        builder,
                        x + inset,
                        y + inset,
                        width - (inset * 2),
                        height - (inset * 2),
                        strokeColor,
                        lineWidth,
                        string.Empty);
                }

                return;
            }

            string dashArray = normalizedStyle is "DASH" or "DASH_DOT" or "DASH_DOT_DOT" or "LONG_DASH"
                ? CssNumber(strokeWidth * 3.0) + ' ' + CssNumber(strokeWidth * 2.0)
                : normalizedStyle == "DOT"
                    ? CssNumber(strokeWidth) + ' ' + CssNumber(strokeWidth * 1.8)
                    : string.Empty;
            AppendHwpxSvgRectangle(builder, x, y, width, height, strokeColor, strokeWidth, dashArray);
        }

        private static void AppendHwpxSvgRectangle(
            StringBuilder builder,
            double x,
            double y,
            double width,
            double height,
            string strokeColor,
            double strokeWidth,
            string dashArray)
        {
            builder.Append("<rect x=\"")
                .Append(CssNumber(x))
                .Append("\" y=\"")
                .Append(CssNumber(y))
                .Append("\" width=\"")
                .Append(CssNumber(width))
                .Append("\" height=\"")
                .Append(CssNumber(height))
                .Append("\" fill=\"none\" stroke=\"")
                .Append(strokeColor)
                .Append("\" stroke-width=\"")
                .Append(CssNumber(strokeWidth))
                .Append('"');
            if (!string.IsNullOrWhiteSpace(dashArray))
            {
                builder.Append(" stroke-dasharray=\"").Append(dashArray).Append('"');
            }

            builder.Append(" />");
        }

        private static void AppendHwpxLineVector(
            StringBuilder builder,
            HwpxPoint start,
            HwpxPoint end,
            string strokeColor,
            double strokeWidth,
            string lineStyle,
            string headStyle,
            string tailStyle)
        {
            builder.Append("<line x1=\"")
                .Append(CssNumber(start.X))
                .Append("\" y1=\"")
                .Append(CssNumber(start.Y))
                .Append("\" x2=\"")
                .Append(CssNumber(end.X))
                .Append("\" y2=\"")
                .Append(CssNumber(end.Y))
                .Append("\" stroke=\"")
                .Append(strokeColor)
                .Append("\" stroke-width=\"")
                .Append(CssNumber(strokeWidth))
                .Append("\" stroke-linecap=\"square\"");
            string normalizedStyle = lineStyle.ToUpperInvariant();
            if (normalizedStyle is "DASH" or "DASH_DOT" or "DASH_DOT_DOT" or "LONG_DASH")
            {
                builder.Append(" stroke-dasharray=\"")
                    .Append(CssNumber(strokeWidth * 3.0))
                    .Append(' ')
                    .Append(CssNumber(strokeWidth * 2.0))
                    .Append('"');
            }

            builder.Append(" />");
            if (!tailStyle.Equals("NORMAL", StringComparison.OrdinalIgnoreCase) &&
                !tailStyle.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                AppendHwpxArrowHead(builder, start, end, strokeColor, strokeWidth);
            }

            if (!headStyle.Equals("NORMAL", StringComparison.OrdinalIgnoreCase) &&
                !headStyle.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                AppendHwpxArrowHead(builder, end, start, strokeColor, strokeWidth);
            }
        }

        private static void AppendHwpxArrowHead(
            StringBuilder builder,
            HwpxPoint start,
            HwpxPoint end,
            string fillColor,
            double strokeWidth)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= 0)
            {
                return;
            }

            double unitX = dx / length;
            double unitY = dy / length;
            double arrowLength = Math.Max(700, strokeWidth * 7.0);
            double halfWidth = Math.Max(320, strokeWidth * 3.2);
            var baseCenter = new HwpxPoint(end.X - (unitX * arrowLength), end.Y - (unitY * arrowLength));
            var upper = new HwpxPoint(baseCenter.X - (unitY * halfWidth), baseCenter.Y + (unitX * halfWidth));
            var lower = new HwpxPoint(baseCenter.X + (unitY * halfWidth), baseCenter.Y - (unitX * halfWidth));
            var notch = new HwpxPoint(
                end.X - (unitX * arrowLength * 0.64),
                end.Y - (unitY * arrowLength * 0.64));

            builder.Append("<polygon points=\"")
                .Append(CssNumber(end.X)).Append(',').Append(CssNumber(end.Y)).Append(' ')
                .Append(CssNumber(upper.X)).Append(',').Append(CssNumber(upper.Y)).Append(' ')
                .Append(CssNumber(notch.X)).Append(',').Append(CssNumber(notch.Y)).Append(' ')
                .Append(CssNumber(lower.X)).Append(',').Append(CssNumber(lower.Y))
                .Append("\" fill=\"")
                .Append(fillColor)
                .Append("\" />");
        }

        private static bool TryReadHwpxRectanglePoints(
            XElement rectangle,
            HwpxMatrix transform,
            out IReadOnlyList<HwpxPoint> points)
        {
            var transformedPoints = new List<HwpxPoint>();
            foreach (string pointName in new[] { "pt0", "pt1", "pt2", "pt3" })
            {
                XElement? point = rectangle.Elements().FirstOrDefault(element => element.Name.LocalName == pointName);
                if (!TryReadHwpxPoint(point, out HwpxPoint value))
                {
                    points = Array.Empty<HwpxPoint>();
                    return false;
                }

                transformedPoints.Add(transform.Transform(value));
            }

            points = transformedPoints;
            return true;
        }

        private static bool TryReadHwpxLinePoints(
            XElement line,
            HwpxMatrix transform,
            out HwpxPoint start,
            out HwpxPoint end)
        {
            XElement? startPoint = line.Elements().FirstOrDefault(element => element.Name.LocalName == "startPt");
            XElement? endPoint = line.Elements().FirstOrDefault(element => element.Name.LocalName == "endPt");
            if (!TryReadHwpxPoint(startPoint, out HwpxPoint sourceStart) ||
                !TryReadHwpxPoint(endPoint, out HwpxPoint sourceEnd))
            {
                start = default;
                end = default;
                return false;
            }

            start = transform.Transform(sourceStart);
            end = transform.Transform(sourceEnd);
            if (GetAttributeValue(line, "isReverseHV") == "1")
            {
                (start, end) = (end, start);
            }

            return true;
        }

        private static bool TryReadHwpxPoint(XElement? element, out HwpxPoint point)
        {
            bool hasX = double.TryParse(
                GetAttributeValue(element, "x"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double x);
            bool hasY = double.TryParse(
                GetAttributeValue(element, "y"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double y);
            point = new HwpxPoint(x, y);
            return hasX && hasY;
        }

        private static HwpxMatrix ReadHwpxRenderingTransform(XElement shape)
        {
            XElement? renderingInfo = shape.Elements().FirstOrDefault(element => element.Name.LocalName == "renderingInfo");
            HwpxMatrix transform = HwpxMatrix.Identity;
            if (renderingInfo == null)
            {
                return transform;
            }

            foreach (XElement matrixElement in renderingInfo.Elements().Where(element =>
                element.Name.LocalName is "transMatrix" or "scaMatrix" or "rotMatrix"))
            {
                var matrix = new HwpxMatrix(
                    ReadHwpxDoubleAttribute(matrixElement, "e1"),
                    ReadHwpxDoubleAttribute(matrixElement, "e2"),
                    ReadHwpxDoubleAttribute(matrixElement, "e3"),
                    ReadHwpxDoubleAttribute(matrixElement, "e4"),
                    ReadHwpxDoubleAttribute(matrixElement, "e5"),
                    ReadHwpxDoubleAttribute(matrixElement, "e6"));
                transform = HwpxMatrix.Multiply(transform, matrix);
            }

            return transform;
        }

        private static void ReadHwpxBounds(
            IReadOnlyList<HwpxPoint> points,
            out double left,
            out double top,
            out double width,
            out double height)
        {
            left = points.Min(point => point.X);
            top = points.Min(point => point.Y);
            width = points.Max(point => point.X) - left;
            height = points.Max(point => point.Y) - top;
        }

        private static string HwpxShapeStrokeColor(XElement? lineShape)
        {
            string color = GetAttributeValue(lineShape, "color");
            if (color.Equals("#000000", StringComparison.OrdinalIgnoreCase) ||
                !IsCssColor(color))
            {
                return "currentColor";
            }

            return color.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase)
                ? "transparent"
                : Html(color);
        }

        private static bool TryReadHwpxSize(XElement element, out double width, out double height)
        {
            return TryReadHwpxDimensions(element, "curSz", out width, out height);
        }

        private static bool TryReadHwpxDimensions(
            XElement element,
            string sizeElementName,
            out double width,
            out double height)
        {
            XElement? size = element.Elements().FirstOrDefault(e => e.Name.LocalName == sizeElementName);
            width = ReadHwpxDoubleAttribute(size, "width");
            height = ReadHwpxDoubleAttribute(size, "height");
            return width > 0 && height > 0;
        }

        private static double ReadHwpxDoubleAttribute(XElement? element, string attributeName)
        {
            return double.TryParse(
                GetAttributeValue(element, attributeName),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
                ? value
                : 0;
        }

        private static void ReadHwpxOffset(XElement element, out double x, out double y)
        {
            XElement? offset = element.Elements().FirstOrDefault(e => e.Name.LocalName == "offset");
            if (!double.TryParse(
                GetAttributeValue(offset, "x"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out x))
            {
                x = 0;
            }

            if (!double.TryParse(
                GetAttributeValue(offset, "y"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out y))
            {
                y = 0;
            }
        }

        private static string CssNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string CssPercent(double value)
        {
            return CssNumber(value) + "%";
        }

        private static string HwpxPoints(double value)
        {
            return CssNumber(value / 100.0) + "pt";
        }

        private static void AppendHwpxImageHtml(
            StringBuilder builder,
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            XElement element,
            ISet<string> renderedImages)
        {
            string imagePath = ResolveHwpxImagePath(element, binaryItems);
            if (string.IsNullOrWhiteSpace(imagePath) || !renderedImages.Add(imagePath))
            {
                return;
            }

            string? dataUri = TryReadImageDataUri(archive, imagePath);
            if (string.IsNullOrWhiteSpace(dataUri))
            {
                return;
            }

            var styles = new List<string>();
            if (TryReadHwpxSize(element, out double width, out double height) ||
                TryReadHwpxDimensions(element, "sz", out width, out height))
            {
                styles.Add("width:" + HwpxPoints(width));
                styles.Add("height:" + HwpxPoints(height));
            }

            string margin = BuildHwpxBoxSpacingStyle(element, "outMargin", "margin");
            if (!string.IsNullOrWhiteSpace(margin))
            {
                styles.Add(margin);
            }

            builder.Append("<span class=\"doc-image hwpx-image\"");
            AppendStyleAttribute(builder, string.Join(';', styles));
            builder.Append("><img src=\"")
                .Append(Html(dataUri))
                .Append("\" alt=\"\"></span>");
        }

        private static string GetHwpxTextStyle(XText textNode, IReadOnlyDictionary<string, string> characterStyles)
        {
            XElement? parent = textNode.Parent;
            while (parent != null)
            {
                if (parent.Name.LocalName == "run")
                {
                    string styleId = GetAttributeValue(parent, "charPrIDRef");
                    return characterStyles.TryGetValue(styleId, out string? style) ? style : string.Empty;
                }

                parent = parent.Parent;
            }

            return string.Empty;
        }

        private static string ResolveHwpxImagePath(XElement element, IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems)
        {
            foreach (string attributeName in new[] { "binaryItemIDRef", "binItemIDRef", "refID", "refId" })
            {
                string id = GetAttributeValue(element, attributeName);
                if (!string.IsNullOrWhiteSpace(id) && binaryItems.TryGetValue(id, out HwpxBinaryItem? item))
                {
                    return item.Path;
                }
            }

            foreach (string attributeName in new[] { "href", "path", "target" })
            {
                string path = GetAttributeValue(element, attributeName);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return NormalizeHwpxBinaryPath(path);
                }
            }

            foreach (XElement child in element.Descendants())
            {
                string childPath = ResolveHwpxImagePath(child, binaryItems);
                if (!string.IsNullOrWhiteSpace(childPath))
                {
                    return childPath;
                }
            }

            return string.Empty;
        }

        private static bool IsInsideNestedElement(XElement root, XNode node, string localName)
        {
            XElement? parent = node.Parent;
            while (parent != null && !ReferenceEquals(parent, root))
            {
                if (parent.Name.LocalName == localName)
                {
                    return true;
                }

                parent = parent.Parent;
            }

            return false;
        }

        private static bool IsInsideRenderedContainer(
            XElement root,
            XNode node,
            ISet<XElement> renderedContainers)
        {
            XElement? parent = node.Parent;
            while (parent != null && !ReferenceEquals(parent, root))
            {
                if (renderedContainers.Contains(parent))
                {
                    return true;
                }

                parent = parent.Parent;
            }

            return false;
        }

        private static string ReadPositiveIntegerAttribute(XElement element, string attributeName)
        {
            string value = GetAttributeValue(element, attributeName);
            if (string.IsNullOrWhiteSpace(value))
            {
                XElement? cellSpan = element.Elements().FirstOrDefault(e => e.Name.LocalName == "cellSpan");
                value = GetAttributeValue(cellSpan, attributeName);
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 1
                ? parsed.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static string GetAttributeValue(XElement? element, string localName)
        {
            return element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value ?? string.Empty;
        }

        private static void AppendStyledText(StringBuilder builder, string text, string style)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(style))
            {
                builder.Append(Html(text));
                return;
            }

            builder.Append("<span style=\"")
                .Append(Html(style))
                .Append("\">")
                .Append(Html(text))
                .Append("</span>");
        }

    }
}
