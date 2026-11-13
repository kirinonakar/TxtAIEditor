using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeHwpxRenderUtilities;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficeHwpxBlockHtmlRenderer
    {
        private readonly OfficeHwpxParagraphHtmlRenderer _paragraphs;
        private readonly OfficeHwpxTableHtmlRenderer _tables;

        internal OfficeHwpxBlockHtmlRenderer(
            ZipArchive archive,
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems,
            IReadOnlyDictionary<string, string> characterStyles,
            IReadOnlyDictionary<string, string> paragraphStyles,
            IReadOnlyDictionary<string, string> borderFillStyles)
        {
            var images = new OfficeHwpxImageHtmlRenderer(archive, binaryItems, characterStyles);
            _paragraphs = new OfficeHwpxParagraphHtmlRenderer(images, characterStyles, paragraphStyles);
            _tables = new OfficeHwpxTableHtmlRenderer(paragraphStyles, borderFillStyles, AppendBlock);
        }

        internal void AppendBlock(StringBuilder builder, XElement block, Queue<int>? pageBreaks = null)
        {
            switch (block.Name.LocalName)
            {
                case "p":
                    builder.Append(_paragraphs.BuildHtml(block, pageBreaks));
                    foreach (XElement table in block.Descendants().Where(element =>
                        element.Name.LocalName == "tbl" &&
                        !IsInsideNestedElement(block, element, "tbl")))
                    {
                        builder.Append(_tables.BuildHtml(table, block));
                    }

                    break;
                case "tbl":
                    builder.Append(_tables.BuildHtml(block));
                    break;
                default:
                    foreach (XElement child in block.Elements())
                    {
                        AppendBlock(builder, child);
                    }

                    break;
            }
        }
    }
}
