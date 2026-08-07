using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using static TxtAIEditor.Core.Services.MediaMetadata.MediaMetadataUtilities;

namespace TxtAIEditor.Core.Services.MediaMetadata
{
    internal sealed class MediaAlbumArtService : IMediaAlbumArtService
    {
        private const uint AlbumArtMaxDimension = 600;
        private const int AlbumArtMaxInlineBytes = 512 * 1024;

        public async Task<string?> ReadAsync(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            // Prefer deterministic artwork sources before the Windows thumbnail
            // provider. The provider can finish asynchronously and a short timeout
            // here caused the first open to miss artwork that appeared after reload.
            // 1. Try Embedded Tag Picture Parsing (MP3 ID3v2 APIC/PIC, FLAC picture block, M4A/MP4 covr atom)
            try
            {
                string? embedded = ExtractEmbeddedPicture(filePath);
                if (!string.IsNullOrEmpty(embedded))
                {
                    return await NormalizeAlbumArtDataUriAsync(embedded);
                }
            }
            catch
            {
            }

            // 2. Try Same-Folder Artwork Image Files
            try
            {
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
                    string[] candidateNames = { fileNameNoExt, "cover", "folder", "album", "front", "art", "artwork", "albumart", "albumartsmall" };
                    string[] candidateExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

                    foreach (var cand in candidateNames)
                    {
                        foreach (var ext in candidateExtensions)
                        {
                            string candidatePath = Path.Combine(directory, cand + ext);
                            if (File.Exists(candidatePath))
                            {
                                byte[] imgBytes = File.ReadAllBytes(candidatePath);
                                if (imgBytes.Length > 0)
                                {
                                    string mime = GetImageMimeType(imgBytes, ext == ".png" ? "image/png" : "image/jpeg");
                                    return await CreateAlbumArtDataUriAsync(imgBytes, mime);
                                }
                            }
                        }
                    }

                    // Fallback: If folder has 1..4 image files, pick the first one as cover art
                    var imageFiles = Directory.GetFiles(directory)
                        .Where(f => {
                            string ext = Path.GetExtension(f).ToLowerInvariant();
                            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".bmp";
                        }).ToArray();

                    if (imageFiles.Length > 0 && imageFiles.Length <= 4)
                    {
                        byte[] imgBytes = File.ReadAllBytes(imageFiles[0]);
                        if (imgBytes.Length > 0)
                        {
                            string mime = GetImageMimeType(imgBytes, "image/jpeg");
                            return await CreateAlbumArtDataUriAsync(imgBytes, mime);
                        }
                    }
                }
            }
            catch
            {
            }

            // 3. Try Windows Storage API Thumbnail (MusicView mode). This is kept
            // as the final fallback for formats whose artwork is only exposed by
            // the Windows media property handler.
            try
            {
                return await ReadStorageThumbnailAsync(filePath);
            }
            catch
            {
            }

            return null;
        }

        private static async Task<string?> ReadStorageThumbnailAsync(string filePath)
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.MusicView,
                600,
                ThumbnailOptions.UseCurrentScale);

            if (thumbnail == null || thumbnail.Size <= 0)
            {
                return null;
            }

            using var stream = thumbnail.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            byte[] bytes = ms.ToArray();
            if (bytes.Length <= 100)
            {
                return null;
            }

            string rawContentType = thumbnail.ContentType;
            string fallbackMime = string.IsNullOrEmpty(rawContentType) || rawContentType.Contains("win-bitmap")
                ? "image/jpeg"
                : rawContentType;
            string mime = GetImageMimeType(bytes, fallbackMime);
            return await CreateAlbumArtDataUriAsync(bytes, mime);
        }

        private static async Task<string?> NormalizeAlbumArtDataUriAsync(string dataUri)
        {
            if (dataUri.Length <= AlbumArtMaxInlineBytes * 2 &&
                !dataUri.Contains("image/bmp", StringComparison.OrdinalIgnoreCase))
            {
                return dataUri;
            }

            int commaIndex = dataUri.IndexOf(',');
            if (commaIndex <= "data:".Length)
            {
                return null;
            }

            string header = dataUri[..commaIndex];
            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return dataUri;
            }

            string mime = header["data:".Length..];
            int semicolonIndex = mime.IndexOf(';');
            if (semicolonIndex >= 0)
            {
                mime = mime[..semicolonIndex];
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(dataUri[(commaIndex + 1)..]);
                return await CreateAlbumArtDataUriAsync(bytes, mime);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> CreateAlbumArtDataUriAsync(byte[] bytes, string fallbackMime)
        {
            if (bytes.Length == 0)
            {
                return null;
            }

            string mime = GetImageMimeType(bytes, fallbackMime);
            bool needsResize = bytes.Length > AlbumArtMaxInlineBytes ||
                               string.Equals(mime, "image/bmp", StringComparison.OrdinalIgnoreCase);
            if (!needsResize)
            {
                return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            }

            try
            {
                using var input = new InMemoryRandomAccessStream();
                using (var output = input.GetOutputStreamAt(0))
                using (var writer = new DataWriter(output))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }

                input.Seek(0);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                uint sourceWidth = decoder.PixelWidth;
                uint sourceHeight = decoder.PixelHeight;
                if (sourceWidth == 0 || sourceHeight == 0)
                {
                    return null;
                }

                double scale = Math.Min(
                    1d,
                    (double)AlbumArtMaxDimension / Math.Max(sourceWidth, sourceHeight));
                uint targetWidth = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
                uint targetHeight = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));

                using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
                using var encoded = new InMemoryRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.JpegEncoderId,
                    encoded);
                encoder.BitmapTransform.ScaledWidth = targetWidth;
                encoder.BitmapTransform.ScaledHeight = targetHeight;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();

                encoded.Seek(0);
                using var encodedStream = encoded.AsStreamForRead();
                using var managedStream = new MemoryStream();
                await encodedStream.CopyToAsync(managedStream);
                byte[] normalizedBytes = managedStream.ToArray();
                if (normalizedBytes.Length == 0)
                {
                    return null;
                }

                return $"data:image/jpeg;base64,{Convert.ToBase64String(normalizedBytes)}";
            }
            catch
            {
                // A broken cover must not prevent the audio page from loading.
                return null;
            }
        }

        private static string? ExtractEmbeddedPicture(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractId3v2Picture(filePath);
            }
            else if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractFlacPicture(filePath);
            }
            else if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".3gp", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractM4aPicture(filePath);
            }
            return null;
        }

        private static string? ExtractM4aPicture(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                int maxRead = (int)Math.Min(fs.Length, 12 * 1024 * 1024);
                byte[] buffer = new byte[maxRead];
                int read = fs.Read(buffer, 0, maxRead);
                if (read < 16) return null;

                byte[] covrTag = Encoding.ASCII.GetBytes("covr");
                int covrIndex = IndexOfBytes(buffer, covrTag, 0, read);
                if (covrIndex < 0) return null;

                byte[] dataTag = Encoding.ASCII.GetBytes("data");
                int dataIndex = IndexOfBytes(buffer, dataTag, covrIndex, read - covrIndex);
                if (dataIndex < 4) return null;

                int boxLen = ReadBigEndianInt32(buffer, dataIndex - 4);
                int typeFlags = dataIndex + 8 < read ? ReadBigEndianInt32(buffer, dataIndex + 4) : 0;
                int dataOffset = dataIndex + 12;

                int imgLen = boxLen - 16;
                if (imgLen > 0 && dataOffset + imgLen <= read)
                {
                    byte[] imgBytes = new byte[imgLen];
                    System.Buffer.BlockCopy(buffer, dataOffset, imgBytes, 0, imgLen);
                    string fallbackMime = typeFlags == 14 ? "image/png" : "image/jpeg";
                    string mime = GetImageMimeType(imgBytes, fallbackMime);
                    return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                }
            }
            catch
            {
            }
            return null;
        }

        private static int IndexOfBytes(byte[] array, byte[] pattern, int startIndex, int count)
        {
            int endIndex = Math.Min(array.Length, startIndex + count) - pattern.Length;
            for (int i = startIndex; i <= endIndex; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (array[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static string? ExtractId3v2Picture(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 10) return null;

            byte[] header = new byte[10];
            if (fs.Read(header, 0, 10) < 10) return null;
            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return null;

            byte majorVersion = header[3];
            int tagSize = SyncSafeToInt(header, 6);
            if (tagSize <= 0 || tagSize > fs.Length - 10) return null;

            byte[] tagData = new byte[tagSize];
            if (fs.Read(tagData, 0, tagSize) < tagSize) return null;

            int offset = 0;
            while (offset < tagSize - 10)
            {
                if (majorVersion == 2)
                {
                    string frameId = Encoding.ASCII.GetString(tagData, offset, 3);
                    int frameSize = (tagData[offset + 3] << 16) | (tagData[offset + 4] << 8) | tagData[offset + 5];
                    offset += 6;
                    if (frameSize <= 0 || offset + frameSize > tagSize) break;

                    if (frameId == "PIC")
                    {
                        string format = Encoding.ASCII.GetString(tagData, offset + 1, 3).ToLowerInvariant();
                        string fallbackMime = format switch { "png" => "image/png", "jpg" => "image/jpeg", "jpeg" => "image/jpeg", _ => "image/jpeg" };
                        int imgStart = offset + 5;
                        while (imgStart < offset + frameSize && tagData[imgStart] != 0) imgStart++;
                        imgStart++;
                        int imgLen = (offset + frameSize) - imgStart;
                        if (imgLen > 0 && imgStart + imgLen <= tagData.Length)
                        {
                            byte[] imgBytes = new byte[imgLen];
                            System.Buffer.BlockCopy(tagData, imgStart, imgBytes, 0, imgLen);
                            string mime = GetImageMimeType(imgBytes, fallbackMime);
                            return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                        }
                    }
                    offset += frameSize;
                }
                else
                {
                    if (tagData[offset] == 0) break;
                    string frameId = Encoding.ASCII.GetString(tagData, offset, 4);
                    int frameSize = majorVersion == 4
                        ? SyncSafeToInt(tagData, offset + 4)
                        : ReadBigEndianInt32(tagData, offset + 4);
                    offset += 10;
                    if (frameSize <= 0 || offset + frameSize > tagSize) break;

                    if (frameId == "APIC")
                    {
                        byte encoding = tagData[offset];
                        int mimeEnd = offset + 1;
                        while (mimeEnd < offset + frameSize && tagData[mimeEnd] != 0) mimeEnd++;
                        string mimeStr = Encoding.ASCII.GetString(tagData, offset + 1, mimeEnd - (offset + 1));
                        if (string.IsNullOrEmpty(mimeStr)) mimeStr = "image/jpeg";

                        int picTypeOffset = mimeEnd + 1;
                        if (picTypeOffset < offset + frameSize)
                        {
                            int descStart = picTypeOffset + 1;
                            int descEnd = descStart;
                            if (encoding == 1 || encoding == 2)
                            {
                                while (descEnd + 1 < offset + frameSize && (tagData[descEnd] != 0 || tagData[descEnd + 1] != 0)) descEnd += 2;
                                descEnd += 2;
                            }
                            else
                            {
                                while (descEnd < offset + frameSize && tagData[descEnd] != 0) descEnd++;
                                descEnd += 1;
                            }

                            int imgStart = descEnd;
                            int imgLen = (offset + frameSize) - imgStart;
                            if (imgLen > 0 && imgStart + imgLen <= tagData.Length)
                            {
                                byte[] imgBytes = new byte[imgLen];
                                System.Buffer.BlockCopy(tagData, imgStart, imgBytes, 0, imgLen);
                                string mime = GetImageMimeType(imgBytes, mimeStr);
                                return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                            }
                        }
                    }
                    offset += frameSize;
                }
            }
            return null;
        }

        private static string? ExtractFlacPicture(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 4) return null;

            byte[] marker = new byte[4];
            if (fs.Read(marker, 0, 4) < 4) return null;
            if (marker[0] != 'f' || marker[1] != 'L' || marker[2] != 'a' || marker[3] != 'C') return null;

            while (fs.Position + 4 <= fs.Length)
            {
                byte[] blockHeader = new byte[4];
                if (fs.Read(blockHeader, 0, 4) < 4) break;

                bool isLast = (blockHeader[0] & 0x80) != 0;
                int blockType = blockHeader[0] & 0x7F;
                int blockLength = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

                if (blockType == 6)
                {
                    if (blockLength <= 0 || fs.Position + blockLength > fs.Length) break;
                    byte[] pictureData = new byte[blockLength];
                    if (fs.Read(pictureData, 0, blockLength) < blockLength) break;

                    int pos = 4;
                    if (pos + 4 > blockLength) break;
                    int mimeLen = ReadBigEndianInt32(pictureData, pos);
                    pos += 4;
                    if (pos + mimeLen > blockLength) break;
                    string mimeStr = Encoding.ASCII.GetString(pictureData, pos, mimeLen);
                    pos += mimeLen;

                    if (pos + 4 > blockLength) break;
                    int descLen = ReadBigEndianInt32(pictureData, pos);
                    pos += 4;
                    pos += descLen;

                    pos += 16;

                    if (pos + 4 > blockLength) break;
                    int dataLen = ReadBigEndianInt32(pictureData, pos);
                    pos += 4;

                    if (dataLen > 0 && pos + dataLen <= blockLength)
                    {
                        byte[] imgBytes = new byte[dataLen];
                        System.Buffer.BlockCopy(pictureData, pos, imgBytes, 0, dataLen);
                        string mime = GetImageMimeType(imgBytes, string.IsNullOrEmpty(mimeStr) ? "image/jpeg" : mimeStr);
                        return $"data:{mime};base64,{Convert.ToBase64String(imgBytes)}";
                    }
                }
                else
                {
                    fs.Seek(blockLength, SeekOrigin.Current);
                }

                if (isLast) break;
            }
            return null;
        }
    }
}
