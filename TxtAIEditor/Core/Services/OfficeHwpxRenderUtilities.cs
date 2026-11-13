using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeHwpxRenderUtilities
    {
        internal static string GetAttributeValue(XElement? element, string localName)
        {
            return element?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value ?? string.Empty;
        }

        internal static double ReadHwpxDoubleAttribute(XElement? element, string attributeName)
        {
            return double.TryParse(
                GetAttributeValue(element, attributeName),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
                ? value
                : 0;
        }

        internal static bool TryReadHwpxSize(XElement element, out double width, out double height)
        {
            return TryReadHwpxDimensions(element, "curSz", out width, out height);
        }

        internal static bool TryReadHwpxDimensions(
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

        internal static bool IsInsideNestedElement(XElement root, XNode node, string localName)
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

        internal static string CssNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        internal static string CssPercent(double value)
        {
            return CssNumber(value) + "%";
        }

        internal static string HwpxPoints(double value)
        {
            return CssNumber(value / 100.0) + "pt";
        }

        internal static string BuildHwpxBoxSpacingStyle(
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

        internal static void AppendStyleAttribute(StringBuilder builder, string style)
        {
            if (!string.IsNullOrWhiteSpace(style))
            {
                builder.Append(" style=\"").Append(Html(style)).Append('"');
            }
        }
    }
}
