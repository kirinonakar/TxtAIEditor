using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    public sealed class DocxTextExtractionService
    {
        public async Task<string> ExtractTextAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return string.Empty;
            }

            try
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
                var entry = archive.GetEntry("word/document.xml");
                if (entry == null)
                {
                    return string.Empty;
                }

                using var entryStream = entry.Open();
                var doc = await Task.Run(() => XDocument.Load(entryStream)).ConfigureAwait(false);
                
                var sb = new StringBuilder();
                var body = doc.Root?.Element(XName.Get("body", doc.Root.Name.NamespaceName));
                if (body == null)
                {
                    body = doc.Root;
                }
                
                if (body == null)
                {
                    return string.Empty;
                }

                foreach (var paragraph in body.Descendants())
                {
                    if (paragraph.Name.LocalName == "p")
                    {
                        var pSb = new StringBuilder();
                        foreach (var child in paragraph.Descendants())
                        {
                            if (child.Name.LocalName == "t")
                            {
                                pSb.Append(child.Value);
                            }
                            else if (child.Name.LocalName == "tab")
                            {
                                pSb.Append('\t');
                            }
                            else if (child.Name.LocalName == "br" || child.Name.LocalName == "cr")
                            {
                                pSb.Append('\n');
                            }
                        }
                        
                        string pText = pSb.ToString();
                        sb.AppendLine(pText);
                    }
                }

                return NormalizeExtractedText(sb.ToString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting text from docx: {ex.Message}");
                return string.Empty;
            }
        }

        private static string NormalizeExtractedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            
            // Normalize spaces: replace multiple spaces with a single space.
            normalized = Regex.Replace(normalized, @" {2,}", " ");
            
            // Normalize consecutive empty lines: replace 3 or more newlines with at most 2 newlines (i.e. one blank line in between paragraphs).
            normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
            
            return normalized.Trim();
        }
    }
}
