using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxPageLayout;
using static TxtAIEditor.Core.Services.OfficeHwpxRenderUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxStyleCatalog;
using static TxtAIEditor.Core.Services.OfficeHwpxTextHtmlRenderer;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficeHwpxParagraphHtmlRenderer
    {
        private readonly OfficeHwpxImageHtmlRenderer _images;
        private readonly IReadOnlyDictionary<string, string> _characterStyles;
        private readonly IReadOnlyDictionary<string, string> _paragraphStyles;

        internal OfficeHwpxParagraphHtmlRenderer(
            OfficeHwpxImageHtmlRenderer images,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles)
        {
            _images = images;
            _characterStyles = characterStyles;
            _paragraphStyles = paragraphStyles;
        }

        internal string BuildHtml(XElement paragraph, Queue<int>? pageBreaks)
        {
            var content = new StringBuilder();
            var renderedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var renderedContainers = new HashSet<XElement>();
            bool hasInlinePageBreaks = pageBreaks is { Count: > 0 };
            IReadOnlyDictionary<XText, string> textValues = GetHwpxRenderedTextValues(paragraph, !hasInlinePageBreaks);
            int textOffset = 0;
            foreach (XNode node in paragraph.DescendantNodes())
            {
                if (IsInsideNestedElement(paragraph, node, "tbl") ||
                    IsInsideRenderedContainer(paragraph, node, renderedContainers))
                {
                    continue;
                }

                if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                {
                    string style = GetHwpxTextStyle(textNode, _characterStyles);
                    string text = textValues.TryGetValue(textNode, out string? renderedText)
                        ? renderedText
                        : textNode.Value;
                    int textEnd = textOffset + textNode.Value.Length;
                    int start = 0;
                    while (pageBreaks != null && pageBreaks.TryPeek(out int offset) && offset < textEnd)
                    {
                        int split = Math.Max(0, pageBreaks.Dequeue() - textOffset);
                        if (split > 0 && char.IsLowSurrogate(textNode.Value[split]))
                        {
                            split--;
                        }
                        AppendStyledText(content, textNode.Value[start..split], style);
                        content.Append(PageBreakHtml);
                        start = split;
                    }
                    AppendStyledText(content, start == 0 ? text : textNode.Value[start..], style);
                    textOffset = textEnd;
                    continue;
                }

                if (node is not XElement element)
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "tab":
                        textOffset += 8;
                        content.Append('\t');
                        break;
                    case "lineBreak":
                    case "br":
                    case "cr":
                        textOffset++;
                        content.Append("<br>");
                        break;
                    case "nbSpace":
                        textOffset++;
                        content.Append(' ');
                        break;
                    case "fwSpace":
                        textOffset++;
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
                        _images.Append(content, element, renderedImages);
                        break;
                    case "container":
                        if (_images.TryAppendLayered(content, element, renderedImages) ||
                            OfficeHwpxShapeHtmlRenderer.TryAppend(content, _characterStyles, _paragraphStyles, element))
                        {
                            renderedContainers.Add(element);
                        }
                        break;
                }
            }

            string paragraphStyle = GetHwpxParagraphStyle(paragraph, _paragraphStyles);
            // The browser's paragraph strut must use the document's font size too.
            // A smaller styled run alone cannot reduce the height of its line box.
            XElement? firstRun = paragraph.Elements().FirstOrDefault(element => element.Name.LocalName == "run");
            if (_characterStyles.TryGetValue(GetAttributeValue(firstRun, "charPrIDRef"), out string? runStyle))
            {
                string? fontSize = runStyle.Split(';').FirstOrDefault(declaration =>
                    declaration.StartsWith("font-size:", StringComparison.Ordinal));
                if (fontSize != null)
                {
                    paragraphStyle += ";--hwpx-paragraph-" + fontSize;
                }
            }
            XElement? savedLine = GetHwpxSingleSavedLine(paragraph);
            double lineHeight = ReadHwpxDoubleAttribute(savedLine, "vertsize");
            double lineAdvance = lineHeight + ReadHwpxDoubleAttribute(savedLine, "spacing");
            if (lineHeight > 0 && paragraph.Parent?.Name.LocalName == "subList")
            {
                double height = paragraph.ElementsAfterSelf().Any(element => element.Name.LocalName == "p") && lineAdvance > 0
                    ? lineAdvance
                    : lineHeight;
                paragraphStyle += ";--hwpx-saved-line-height:" + HwpxPoints(height);
            }
            string styleAttribute = string.IsNullOrWhiteSpace(paragraphStyle)
                ? string.Empty
                : " style=\"" + Html(paragraphStyle) + "\"";
            string classes = "doc-paragraph hwpx-paragraph";
            if (savedLine != null)
            {
                classes += " hwpx-saved-single-line";
            }
            if (lineHeight > 0 && lineAdvance <= 0 && paragraph.Parent?.Parent == null)
            {
                classes += " hwpx-zero-advance";
            }
            if (content.Length == 0)
            {
                classes += " empty-paragraph";
                if (paragraph.Descendants().Any(element => element.Name.LocalName == "tbl" &&
                    GetAttributeValue(element.Elements().FirstOrDefault(child => child.Name.LocalName == "pos"), "treatAsChar") == "1"))
                {
                    classes += " hwpx-inline-table-anchor";
                }
            }
            return "<p class=\"" + classes + "\"" + styleAttribute + ">" + content + "</p>";
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
    }
}
