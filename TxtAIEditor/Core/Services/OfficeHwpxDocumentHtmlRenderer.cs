using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxBinaryCatalog;
using static TxtAIEditor.Core.Services.OfficeHwpxPageLayout;
using static TxtAIEditor.Core.Services.OfficeHwpxRenderUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxStyleCatalog;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeHwpxDocumentHtmlRenderer
    {
        public static async Task<string> BuildAsync(string filePath, Func<string, string, string> getString)
        {
            using ZipArchive archive = await OpenArchiveAsync(filePath).ConfigureAwait(false);
            IReadOnlyDictionary<string, HwpxBinaryItem> binaryItems = await LoadHwpxBinaryItemsAsync(archive).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> characterStyles = await LoadHwpxCharacterStylesAsync(archive).ConfigureAwait(false);
            var (paragraphStyles, pageBreakBeforeStyles) = await LoadHwpxParagraphStylesAsync(archive).ConfigureAwait(false);
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

            var blockRenderer = new OfficeHwpxBlockHtmlRenderer(
                archive, binaryItems, characterStyles, paragraphStyles, borderFillStyles);
            var content = new StringBuilder();
            string? pageWidth = null;
            string? pagePadding = null;
            string? pageHeight = null;
            bool hasSavedPagination = false;
            foreach (ZipArchiveEntry sectionEntry in sectionEntries)
            {
                XDocument section = await LoadHwpxSectionXmlAsync(sectionEntry).ConfigureAwait(false);
                bool hasPageLayout = TryReadHwpxPageLayout(section, out string sectionPageWidth,
                    out string sectionPagePadding, out string? sectionPageHeight, out string? printLayout);
                if (string.IsNullOrWhiteSpace(pageWidth) && hasPageLayout)
                {
                    pageWidth = sectionPageWidth;
                    pagePadding = sectionPagePadding;
                    pageHeight = sectionPageHeight;
                }

                bool singleColumn = IsHwpxSingleColumn(section);
                double? previousVerticalPosition = null;
                int sectionStart = content.Length;
                XElement[] blocks = section.Root?.Elements().ToArray() ?? Array.Empty<XElement>();
                if (printLayout != null)
                {
                    content.Append("<div class=\"hwpx-section\" data-hwpx-print-layout=\"")
                        .Append(printLayout).Append("\">");
                }
                int sectionContentStart = content.Length;
                double coverHeight = 0;
                double[]? coverOffsets = sectionStart == 0 && sectionEntries.Count > 1 && singleColumn
                    ? ReadHwpxCoverOffsets(blocks, pageBreakBeforeStyles, out coverHeight)
                    : null;
                if (coverOffsets != null)
                {
                    content.Append("<div class=\"hwpx-positioned-cover\" style=\"--hwpx-cover-height:")
                        .Append(HwpxPoints(coverHeight)).Append("\">");
                }

                for (int index = 0; index < blocks.Length; index++)
                {
                    XElement block = blocks[index];
                    Queue<int>? pageBreaks = null;
                    if (block.Name.LocalName == "p")
                    {
                        pageBreaks = ReadHwpxPageBreaks(
                            block, pageBreakBeforeStyles, singleColumn, ref previousVerticalPosition, ref hasSavedPagination);
                        if (pageBreaks.TryPeek(out int offset) && offset == 0)
                        {
                            pageBreaks.Dequeue();
                            if (content.Length > sectionContentStart)
                            {
                                content.Append(PageBreakHtml);
                            }
                        }
                    }

                    if (coverOffsets != null)
                    {
                        content.Append("<div class=\"hwpx-cover-block\" style=\"--hwpx-cover-offset:")
                            .Append(HwpxPoints(coverOffsets[index])).Append("\">");
                    }
                    blockRenderer.AppendBlock(content, block, pageBreaks);
                    if (coverOffsets != null)
                    {
                        content.Append("</div>");
                    }
                }
                if (coverOffsets != null)
                {
                    content.Append("</div>");
                }

                if (content.Length == sectionContentStart)
                {
                    content.Length = sectionStart;
                    continue;
                }
                if (printLayout != null)
                {
                    content.Append("</div>");
                }
                if (sectionStart > 0)
                {
                    content.Insert(sectionStart, PageBreakHtml);
                }
            }

            if (content.Length == 0)
            {
                return BuildErrorHtml(getString("OfficeViewerNoContent", "No content to display."));
            }

            return BuildDocumentHtml(Path.GetFileName(filePath), content.ToString(),
                pageWidth, pagePadding, pageHeight, hasSavedPagination);
        }

        private static async Task<XDocument> LoadHwpxSectionXmlAsync(ZipArchiveEntry entry)
        {
            using Stream stream = entry.Open();
            return await Task.Run(() =>
                XDocument.Load(stream, LoadOptions.PreserveWhitespace)).ConfigureAwait(false);
        }
    }
}
