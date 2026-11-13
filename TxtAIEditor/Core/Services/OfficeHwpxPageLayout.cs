using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeHwpxRenderUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeHwpxPageLayout
    {
        internal static bool TryReadHwpxPageLayout(
            XDocument section,
            out string pageWidth,
            out string pagePadding,
            out string? pageHeight,
            out string? printLayout)
        {
            pageWidth = string.Empty;
            pagePadding = string.Empty;
            pageHeight = null;
            printLayout = null;

            XElement? pageProperties = section.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "pagePr");
            double width = ReadHwpxDoubleAttribute(pageProperties, "width");
            if (width <= 0)
            {
                return false;
            }

            double height = ReadHwpxDoubleAttribute(pageProperties, "height");
            if (height > width && GetAttributeValue(pageProperties, "landscape") == "NARROWLY")
            {
                (width, height) = (height, width);
            }
            pageWidth = HwpxPoints(width);
            if (height > 0)
            {
                pageHeight = HwpxPoints(height);
            }
            XElement? margin = pageProperties?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "margin");
            if (margin != null)
            {
                double top = ReadHwpxDoubleAttribute(margin, "top");
                double right = ReadHwpxDoubleAttribute(margin, "right");
                double bottom = ReadHwpxDoubleAttribute(margin, "bottom");
                double left = ReadHwpxDoubleAttribute(margin, "left");
                double header = ReadHwpxDoubleAttribute(margin, "header");
                double footer = ReadHwpxDoubleAttribute(margin, "footer");
                pagePadding = string.Join(
                    ' ',
                    HwpxPoints(top), HwpxPoints(right), HwpxPoints(bottom), HwpxPoints(left));
                if (pageHeight != null)
                {
                    printLayout = string.Join(' ', pageWidth, pageHeight, HwpxPoints(top + header),
                        HwpxPoints(right), HwpxPoints(bottom + footer), HwpxPoints(left), HwpxPoints(header));
                }
            }

            return true;
        }

        internal static bool IsHwpxSingleColumn(XDocument section)
        {
            // A drop in the saved vertical position can also mean a new column.
            return !section.Descendants().Any(element =>
                element.Name.LocalName == "colPr" && ReadHwpxDoubleAttribute(element, "colCount") > 1);
        }

        internal static double[]? ReadHwpxCoverOffsets(
            XElement[] blocks, IReadOnlySet<string> pageBreakBeforeStyles, out double height)
        {
            // A standalone cover with complete one-line anchors can be positioned
            // without inferring the layout of reflowed body paragraphs.
            height = 0;
            XElement? firstParagraph = blocks.FirstOrDefault();
            if (firstParagraph == null || !blocks.SelectMany(block => block.Descendants()).Any(element =>
                element.Name.LocalName == "pageHiding" && GetAttributeValue(element, "hidePageNum") is "1" or "true"))
            {
                return null;
            }

            XElement? page = firstParagraph.Descendants().FirstOrDefault(element => element.Name.LocalName == "pagePr");
            XElement? margin = page?.Elements().FirstOrDefault(element => element.Name.LocalName == "margin");
            double header = ReadHwpxDoubleAttribute(margin, "header");
            double availableHeight = ReadHwpxDoubleAttribute(page, "height") -
                ReadHwpxDoubleAttribute(margin, "top") - ReadHwpxDoubleAttribute(margin, "bottom");
            var offsets = new double[blocks.Length];
            for (int i = 0; i < blocks.Length; i++)
            {
                XElement paragraph = blocks[i];
                XElement? line = GetHwpxSingleSavedLine(paragraph);
                if (paragraph.Name.LocalName != "p" ||
                    GetAttributeValue(paragraph, "pageBreak") is "1" or "true" ||
                    pageBreakBeforeStyles.Contains(GetAttributeValue(paragraph, "paraPrIDRef")) ||
                    !double.TryParse(GetAttributeValue(line, "vertpos"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out double position) ||
                    position < 0 || (i > 0 && position + header <= offsets[i - 1]))
                {
                    return null;
                }

                offsets[i] = position + header;
                height = Math.Max(height, offsets[i] + ReadHwpxDoubleAttribute(line, "vertsize"));
            }

            return height > 0 && height <= availableHeight ? offsets : null;
        }

        internal static XElement? GetHwpxSingleSavedLine(XElement paragraph)
        {
            XElement? array = paragraph.Elements().FirstOrDefault(element => element.Name.LocalName == "linesegarray");
            XElement? first = array?.Elements().FirstOrDefault();
            return first != null && !first.ElementsAfterSelf().Any() ? first : null;
        }

        internal static Queue<int> ReadHwpxPageBreaks(
            XElement paragraph, IReadOnlySet<string> pageBreakBeforeStyles, bool singleColumn,
            ref double? previousVerticalPosition, ref bool hasSavedPagination)
        {
            var breaks = new Queue<int>();
            if (GetAttributeValue(paragraph, "pageBreak") is "1" or "true" ||
                pageBreakBeforeStyles.Contains(GetAttributeValue(paragraph, "paraPrIDRef")))
            {
                breaks.Enqueue(0);
            }

            int explicitBreakCount = breaks.Count;
            AppendHwpxSavedPageBreaks(paragraph, singleColumn, ref previousVerticalPosition, breaks);
            hasSavedPagination |= breaks.Count > explicitBreakCount;
            return breaks;
        }

        internal static void AppendHwpxSavedPageBreaks(
            XElement paragraph, bool useVerticalReset, ref double? previousVerticalPosition, Queue<int> breaks)
        {
            XElement? lineArray = paragraph.Elements().FirstOrDefault(element => element.Name.LocalName == "linesegarray");
            foreach (XElement line in lineArray?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (!double.TryParse(GetAttributeValue(line, "vertpos"), NumberStyles.Integer, CultureInfo.InvariantCulture, out double verticalPosition))
                {
                    continue;
                }

                // Hancom files may omit the first-line-of-page flag even when the vertical position resets.
                bool firstLineOfPage = uint.TryParse(GetAttributeValue(line, "flags"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out uint flags) && (flags & 1) != 0;
                if (previousVerticalPosition.HasValue &&
                    (firstLineOfPage || (useVerticalReset && verticalPosition < previousVerticalPosition.Value)) &&
                    int.TryParse(GetAttributeValue(line, "textpos"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset) &&
                    offset >= 0 && (breaks.Count == 0 || offset > breaks.Last()))
                {
                    breaks.Enqueue(offset);
                }

                previousVerticalPosition = verticalPosition;
            }
        }
    }
}
