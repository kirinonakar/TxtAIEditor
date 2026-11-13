using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxBinaryCatalog;
using static TxtAIEditor.Core.Services.OfficeHwpxRenderUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxTextHtmlRenderer;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficeHwpxImageHtmlRenderer
    {
        private readonly ZipArchive _archive;
        private readonly IReadOnlyDictionary<string, HwpxBinaryItem> _binaryItems;
        private readonly IReadOnlyDictionary<string, string> _characterStyles;

        internal OfficeHwpxImageHtmlRenderer(
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles)
        {
            _archive = archive;
            _binaryItems = binaryItems;
            _characterStyles = characterStyles;
        }

        internal void Append(
            StringBuilder builder,
            XElement element,
            ISet<string> renderedImages)
        {
            string imagePath = ResolveHwpxImagePath(element);
            if (string.IsNullOrWhiteSpace(imagePath) || !renderedImages.Add(imagePath))
            {
                return;
            }

            string? dataUri = TryReadImageDataUri(_archive, imagePath);
            if (string.IsNullOrWhiteSpace(dataUri))
            {
                return;
            }

            var styles = new List<string>();
            if (TryReadHwpxDimensions(element, "sz", out double width, out double height) ||
                TryReadHwpxSize(element, out width, out height))
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

        internal bool TryAppendLayered(
            StringBuilder builder,
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

            string imagePath = ResolveHwpxImagePath(picture);
            if (string.IsNullOrWhiteSpace(imagePath) || renderedImages.Contains(imagePath))
            {
                return false;
            }

            string? dataUri = TryReadImageDataUri(_archive, imagePath);
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
                        AppendStyledText(text, textNode.Value, GetHwpxTextStyle(textNode, _characterStyles));
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

        private string ResolveHwpxImagePath(XElement element)
        {
            foreach (string attributeName in new[] { "binaryItemIDRef", "binItemIDRef", "refID", "refId" })
            {
                string id = GetAttributeValue(element, attributeName);
                if (!string.IsNullOrWhiteSpace(id) && _binaryItems.TryGetValue(id, out HwpxBinaryItem? item))
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
                string childPath = ResolveHwpxImagePath(child);
                if (!string.IsNullOrWhiteSpace(childPath))
                {
                    return childPath;
                }
            }

            return string.Empty;
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
    }
}
