using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TxtAIEditor.Core.Services
{
    // Project the binary formats onto the parts consumed by the existing Office renderers.
    // These packages live only in memory and are never saved as user documents.
    internal static class OfficeLegacyPackage
    {
        internal static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        internal static readonly XNamespace RelationshipId = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        internal static async Task<ZipArchive> OpenAsync(string filePath, CancellationToken cancellationToken = default)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            Stream stream = extension is ".doc" or ".ppt" or ".xls"
                ? await Task.Run(() => ReadBinary(filePath, extension, cancellationToken), cancellationToken).ConfigureAwait(false)
                : OpenFile(filePath);
            try
            {
                return new ZipArchive(stream, ZipArchiveMode.Read);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static Stream ReadBinary(string filePath, string extension, CancellationToken cancellationToken)
        {
            using FileStream file = OpenFile(filePath);
            Span<byte> signature = stackalloc byte[8];
            file.ReadExactly(signature);
            file.Position = 0;
            var package = new MemoryStream();
            try
            {
                using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
                {
                    if (extension == ".xls" && !OfficeCompoundFile.HasSignature(signature))
                    {
                        OfficeBinaryWorkbook.Write(archive, OfficeBinaryWorkbook.ReadRaw(file), cancellationToken);
                    }
                    else
                    {
                        var compound = new OfficeCompoundFile(file, cancellationToken);
                        if (compound.Contains("EncryptionInfo") || compound.Contains("EncryptedPackage"))
                        {
                            throw Encrypted();
                        }

                        switch (extension)
                        {
                            case ".doc":
                                OfficeBinaryWord.Write(archive, compound, cancellationToken);
                                break;
                            case ".ppt":
                                OfficeBinaryPresentation.Write(archive, compound, cancellationToken);
                                break;
                            case ".xls":
                                OfficeBinaryWorkbook.Write(archive,
                                    compound.ReadStream(compound.Contains("Workbook") ? "Workbook" : "Book"), cancellationToken);
                                break;
                        }
                    }
                }

                package.Position = 0;
                return package;
            }
            catch
            {
                package.Dispose();
                throw;
            }
        }

        internal static IOException Encrypted() => new EncryptedDocumentException();

        internal static bool HasCompoundFileSignature(string filePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream stream = OpenFile(filePath);
            Span<byte> signature = stackalloc byte[8];
            return stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) == signature.Length &&
                OfficeCompoundFile.HasSignature(signature);
        }

        internal static void WriteXml(ZipArchive archive, string path, XElement element)
        {
            using Stream stream = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
            element.Save(stream, SaveOptions.DisableFormatting);
        }

        internal static XElement Relationship(string id, string target, string type) =>
            new(Relationships + "Relationship", new XAttribute("Id", id), new XAttribute("Target", target),
                new XAttribute("Type", RelationshipId.NamespaceName + "/" + type));

        private static FileStream OpenFile(string filePath) =>
            new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

        internal sealed class EncryptedDocumentException() : IOException("The Office document is password-encrypted.");
    }
}
