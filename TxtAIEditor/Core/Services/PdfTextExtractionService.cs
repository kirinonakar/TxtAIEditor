using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TxtAIEditor.Core.Services
{
    public sealed class PdfTextExtractionService
    {
        private const int MaxDecodedStreamBytes = 1024 * 1024;
        private const int MaxRawStreamBytesToDecode = 2 * 1024 * 1024;
        private const int MaxStreamsToInspect = 300;
        private const int MaxLooseFallbackChars = 256 * 1024;

        public async Task<string> ExtractTextAsync(string filePath, int maxChars, IProgress<int>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || maxChars <= 0)
            {
                return string.Empty;
            }

            progress?.Report(1);
            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            progress?.Report(5);
            string raw = Encoding.Latin1.GetString(bytes);
            var parts = new List<string>();
            var streams = new List<PdfStreamInfo>();

            foreach (var stream in EnumeratePdfStreams(raw, progress))
            {
                streams.Add(stream);
            }

            var streamFontUnicodeMaps = BuildContentStreamFontUnicodeMaps(raw, streams);
            var fallbackFontUnicodeMaps = BuildUnambiguousFontUnicodeMaps(raw, streams);

            foreach (var stream in streams)
            {
                if (!LooksLikeTextContentStream(stream.DecodedText))
                {
                    continue;
                }

                Dictionary<string, ToUnicodeCMap> fontUnicodeMaps = fallbackFontUnicodeMaps;
                if (stream.ObjectNumber.HasValue &&
                    streamFontUnicodeMaps.TryGetValue(stream.ObjectNumber.Value, out Dictionary<string, ToUnicodeCMap>? localFontUnicodeMaps))
                {
                    fontUnicodeMaps = localFontUnicodeMaps;
                }

                string extracted = ExtractTextFromContentStream(stream.DecodedText, fontUnicodeMaps);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    parts.Add(extracted);
                    if (TotalLength(parts) >= maxChars)
                    {
                        break;
                    }
                }
            }

            if (parts.Count == 0)
            {
                progress?.Report(90);
                string fallback = ExtractLooseLiteralText(raw);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    parts.Add(fallback);
                }
            }

            progress?.Report(95);
            string text = NormalizeExtractedText(string.Join("\n", parts));
            progress?.Report(100);
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }

        private sealed class PdfStreamInfo
        {
            public PdfStreamInfo(int? objectNumber, string dictionary, string decodedText)
            {
                ObjectNumber = objectNumber;
                Dictionary = dictionary;
                DecodedText = decodedText;
            }

            public int? ObjectNumber { get; }

            public string Dictionary { get; }

            public string DecodedText { get; }
        }

        private sealed class ToUnicodeCMap
        {
            private readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public int MaxCodeBytes { get; private set; } = 1;

            public int Count => _map.Count;

            public void Add(byte[] sourceCode, string unicodeText)
            {
                if (sourceCode.Length == 0 || string.IsNullOrEmpty(unicodeText))
                {
                    return;
                }

                string key = BytesToHexKey(sourceCode, 0, sourceCode.Length);
                _map[key] = unicodeText;
                MaxCodeBytes = Math.Max(MaxCodeBytes, sourceCode.Length);
            }

            public string? Decode(byte[] bytes)
            {
                if (bytes.Length == 0 || _map.Count == 0)
                {
                    return null;
                }

                var builder = new StringBuilder();
                int mapped = 0;
                int i = 0;
                while (i < bytes.Length)
                {
                    string? unicode = null;
                    int matchedLength = 0;
                    int maxLength = Math.Min(MaxCodeBytes, bytes.Length - i);

                    for (int length = maxLength; length >= 1; length--)
                    {
                        string key = BytesToHexKey(bytes, i, length);
                        if (_map.TryGetValue(key, out unicode))
                        {
                            matchedLength = length;
                            break;
                        }
                    }

                    if (unicode != null)
                    {
                        builder.Append(unicode);
                        mapped++;
                        i += matchedLength;
                        continue;
                    }

                    // Keep simple ASCII operators/punctuation readable when the map is partial.
                    if (bytes[i] == '\r' || bytes[i] == '\n' || bytes[i] == '\t' || (bytes[i] >= 0x20 && bytes[i] <= 0x7E))
                    {
                        builder.Append((char)bytes[i]);
                    }

                    i++;
                }

                return mapped > 0 ? builder.ToString() : null;
            }
        }

        private static IEnumerable<PdfStreamInfo> EnumeratePdfStreams(string raw, IProgress<int>? progress)
        {
            int searchStart = 0;
            int inspectedStreams = 0;
            int lastReportedPercent = 5;
            while (searchStart < raw.Length)
            {
                if (inspectedStreams >= MaxStreamsToInspect)
                {
                    yield break;
                }

                int streamIndex = raw.IndexOf("stream", searchStart, StringComparison.Ordinal);
                if (streamIndex < 0)
                {
                    yield break;
                }

                int dataStart = streamIndex + "stream".Length;
                if (dataStart < raw.Length && raw[dataStart] == '\r')
                {
                    dataStart++;
                }
                if (dataStart < raw.Length && raw[dataStart] == '\n')
                {
                    dataStart++;
                }

                int endIndex = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
                if (endIndex < 0)
                {
                    yield break;
                }

                string dictionary = GetStreamDictionary(raw, streamIndex);
                string streamData = raw.Substring(dataStart, Math.Max(0, endIndex - dataStart));
                inspectedStreams++;
                if (ShouldSkipStream(dictionary, streamData.Length))
                {
                    searchStart = endIndex + "endstream".Length;
                    continue;
                }

                string? decoded = DecodeStream(streamData, dictionary);
                if (!string.IsNullOrEmpty(decoded))
                {
                    yield return new PdfStreamInfo(GetObjectNumberBefore(raw, streamIndex), dictionary, decoded);
                }

                searchStart = endIndex + "endstream".Length;
                int percent = 5 + (int)Math.Min(84, (searchStart / (double)Math.Max(1, raw.Length)) * 84);
                if (percent >= lastReportedPercent + 5)
                {
                    lastReportedPercent = percent;
                    progress?.Report(percent);
                }
            }
        }

        private static IEnumerable<string> EnumerateDecodedStreams(string raw, IProgress<int>? progress)
        {
            foreach (var stream in EnumeratePdfStreams(raw, progress))
            {
                if (LooksLikeTextContentStream(stream.DecodedText))
                {
                    yield return stream.DecodedText;
                }
            }
        }

        private static int? GetObjectNumberBefore(string raw, int streamIndex)
        {
            int searchStart = Math.Max(0, streamIndex - 4096);
            string prefix = raw.Substring(searchStart, streamIndex - searchStart);
            MatchCollection matches = Regex.Matches(prefix, @"(?<obj>\d+)\s+\d+\s+obj\b", RegexOptions.NonBacktracking);
            if (matches.Count == 0)
            {
                return null;
            }

            Match match = matches[matches.Count - 1];
            if (int.TryParse(match.Groups["obj"].Value, out int objectNumber))
            {
                return objectNumber;
            }

            return null;
        }

        private sealed class PdfObjectInfo
        {
            public PdfObjectInfo(int objectNumber, string body)
            {
                ObjectNumber = objectNumber;
                Body = body;
            }

            public int ObjectNumber { get; }

            public string Body { get; }
        }

        private static Dictionary<int, Dictionary<string, ToUnicodeCMap>> BuildContentStreamFontUnicodeMaps(string raw, List<PdfStreamInfo> streams)
        {
            var result = new Dictionary<int, Dictionary<string, ToUnicodeCMap>>();
            var toUnicodeByObjectNumber = BuildToUnicodeMapsByObjectNumber(streams);
            var fontObjectToCMap = BuildFontObjectToUnicodeMaps(raw, toUnicodeByObjectNumber);
            var objects = ParsePdfObjects(raw);
            var objectBodies = new Dictionary<int, string>();
            foreach (var obj in objects)
            {
                objectBodies[obj.ObjectNumber] = obj.Body;
            }

            foreach (var obj in objects)
            {
                string body = obj.Body;
                if (!body.Contains("/Type /Page", StringComparison.Ordinal) ||
                    body.Contains("/Type /Pages", StringComparison.Ordinal))
                {
                    continue;
                }

                string? resourceBody = GetPageResourceBody(body, objectBodies);
                if (string.IsNullOrEmpty(resourceBody))
                {
                    continue;
                }

                Dictionary<string, ToUnicodeCMap> pageFontMaps = BuildFontUnicodeMapsFromResourceText(resourceBody, fontObjectToCMap);
                if (pageFontMaps.Count == 0)
                {
                    continue;
                }

                foreach (int contentObject in ExtractContentObjectNumbers(body))
                {
                    result[contentObject] = pageFontMaps;
                }
            }

            return result;
        }

        private static Dictionary<string, ToUnicodeCMap> BuildUnambiguousFontUnicodeMaps(string raw, List<PdfStreamInfo> streams)
        {
            var toUnicodeByObjectNumber = BuildToUnicodeMapsByObjectNumber(streams);
            var fontObjectToCMap = BuildFontObjectToUnicodeMaps(raw, toUnicodeByObjectNumber);
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);

            foreach (string fontBlock in EnumerateFontResourceBlocks(raw))
            {
                foreach (Match entry in Regex.Matches(fontBlock, @"/(?<name>[A-Za-z0-9_.+\-#]+)\s+(?<obj>\d+)\s+\d+\s+R", RegexOptions.NonBacktracking))
                {
                    string name = DecodePdfName(entry.Groups["name"].Value);
                    if (!int.TryParse(entry.Groups["obj"].Value, out int fontObject) || !fontObjectToCMap.ContainsKey(fontObject))
                    {
                        continue;
                    }

                    if (byName.TryGetValue(name, out int existingFontObject) && existingFontObject != fontObject)
                    {
                        ambiguous.Add(name);
                    }
                    else
                    {
                        byName[name] = fontObject;
                    }
                }
            }

            var result = new Dictionary<string, ToUnicodeCMap>(StringComparer.Ordinal);
            foreach (var entry in byName)
            {
                if (!ambiguous.Contains(entry.Key) && fontObjectToCMap.TryGetValue(entry.Value, out ToUnicodeCMap? cmap))
                {
                    result[entry.Key] = cmap;
                }
            }

            return result;
        }

        private static Dictionary<int, ToUnicodeCMap> BuildToUnicodeMapsByObjectNumber(List<PdfStreamInfo> streams)
        {
            var result = new Dictionary<int, ToUnicodeCMap>();
            foreach (var stream in streams)
            {
                if (!stream.ObjectNumber.HasValue || !LooksLikeToUnicodeCMap(stream.DecodedText))
                {
                    continue;
                }

                ToUnicodeCMap cmap = ParseToUnicodeCMap(stream.DecodedText);
                if (cmap.Count > 0)
                {
                    result[stream.ObjectNumber.Value] = cmap;
                }
            }

            return result;
        }

        private static Dictionary<int, ToUnicodeCMap> BuildFontObjectToUnicodeMaps(string raw, Dictionary<int, ToUnicodeCMap> toUnicodeByObjectNumber)
        {
            var result = new Dictionary<int, ToUnicodeCMap>();
            foreach (Match match in Regex.Matches(raw, @"(?<obj>\d+)\s+\d+\s+obj(?<body>.*?)endobj", RegexOptions.Singleline | RegexOptions.NonBacktracking))
            {
                string body = match.Groups["body"].Value;
                if (!body.Contains("/ToUnicode", StringComparison.Ordinal))
                {
                    continue;
                }

                Match toUnicodeMatch = Regex.Match(body, @"/ToUnicode\s+(?<obj>\d+)\s+\d+\s+R", RegexOptions.NonBacktracking);
                if (!toUnicodeMatch.Success || !int.TryParse(toUnicodeMatch.Groups["obj"].Value, out int toUnicodeObject))
                {
                    continue;
                }

                if (!toUnicodeByObjectNumber.TryGetValue(toUnicodeObject, out ToUnicodeCMap? cmap))
                {
                    continue;
                }

                if (int.TryParse(match.Groups["obj"].Value, out int fontObject))
                {
                    result[fontObject] = cmap;
                }
            }

            return result;
        }

        private static List<PdfObjectInfo> ParsePdfObjects(string raw)
        {
            var objects = new List<PdfObjectInfo>();
            foreach (Match match in Regex.Matches(raw, @"(?<obj>\d+)\s+\d+\s+obj\b(?<body>.*?)endobj", RegexOptions.Singleline | RegexOptions.NonBacktracking))
            {
                if (int.TryParse(match.Groups["obj"].Value, out int objectNumber))
                {
                    objects.Add(new PdfObjectInfo(objectNumber, match.Groups["body"].Value));
                }
            }

            return objects;
        }

        private static IEnumerable<int> ExtractContentObjectNumbers(string pageBody)
        {
            Match contents = Regex.Match(pageBody, @"/Contents\s+(?<value>\[(?<array>.*?)\]|(?<obj>\d+)\s+\d+\s+R)", RegexOptions.Singleline | RegexOptions.NonBacktracking);
            if (!contents.Success)
            {
                yield break;
            }

            if (contents.Groups["obj"].Success && int.TryParse(contents.Groups["obj"].Value, out int singleObject))
            {
                yield return singleObject;
                yield break;
            }

            foreach (Match item in Regex.Matches(contents.Groups["array"].Value, @"(?<obj>\d+)\s+\d+\s+R", RegexOptions.NonBacktracking))
            {
                if (int.TryParse(item.Groups["obj"].Value, out int objectNumber))
                {
                    yield return objectNumber;
                }
            }
        }

        private static string? GetPageResourceBody(string pageBody, Dictionary<int, string> objectBodies)
        {
            Match indirect = Regex.Match(pageBody, @"/Resources\s+(?<obj>\d+)\s+\d+\s+R", RegexOptions.NonBacktracking);
            if (indirect.Success && int.TryParse(indirect.Groups["obj"].Value, out int resourceObject) &&
                objectBodies.TryGetValue(resourceObject, out string? resourceBody))
            {
                return resourceBody;
            }

            int resourcesIndex = pageBody.IndexOf("/Resources", StringComparison.Ordinal);
            if (resourcesIndex < 0)
            {
                return null;
            }

            int dictStart = pageBody.IndexOf("<<", resourcesIndex, StringComparison.Ordinal);
            if (dictStart < 0)
            {
                return null;
            }

            int dictEnd = FindMatchingDictionaryEnd(pageBody, dictStart);
            if (dictEnd < 0)
            {
                return null;
            }

            return pageBody.Substring(dictStart, dictEnd - dictStart + 2);
        }

        private static Dictionary<string, ToUnicodeCMap> BuildFontUnicodeMapsFromResourceText(string resourceText, Dictionary<int, ToUnicodeCMap> fontObjectToCMap)
        {
            var result = new Dictionary<string, ToUnicodeCMap>(StringComparer.Ordinal);
            foreach (string fontBlock in EnumerateFontResourceBlocks(resourceText))
            {
                foreach (Match entry in Regex.Matches(fontBlock, @"/(?<name>[A-Za-z0-9_.+\-#]+)\s+(?<obj>\d+)\s+\d+\s+R", RegexOptions.NonBacktracking))
                {
                    if (!int.TryParse(entry.Groups["obj"].Value, out int fontObject))
                    {
                        continue;
                    }

                    if (fontObjectToCMap.TryGetValue(fontObject, out ToUnicodeCMap? cmap))
                    {
                        result[DecodePdfName(entry.Groups["name"].Value)] = cmap;
                    }
                }
            }

            return result;
        }

        private static Dictionary<string, ToUnicodeCMap> BuildFontUnicodeMaps(string raw, List<PdfStreamInfo> streams)
        {
            var toUnicodeByObjectNumber = new Dictionary<int, ToUnicodeCMap>();
            foreach (var stream in streams)
            {
                if (!stream.ObjectNumber.HasValue || !LooksLikeToUnicodeCMap(stream.DecodedText))
                {
                    continue;
                }

                ToUnicodeCMap cmap = ParseToUnicodeCMap(stream.DecodedText);
                if (cmap.Count > 0)
                {
                    toUnicodeByObjectNumber[stream.ObjectNumber.Value] = cmap;
                }
            }

            var fontObjectToCMap = new Dictionary<int, ToUnicodeCMap>();
            foreach (Match match in Regex.Matches(raw, @"(?<obj>\d+)\s+\d+\s+obj(?<body>.*?)endobj", RegexOptions.Singleline | RegexOptions.NonBacktracking))
            {
                string body = match.Groups["body"].Value;
                if (!body.Contains("/ToUnicode", StringComparison.Ordinal))
                {
                    continue;
                }

                Match toUnicodeMatch = Regex.Match(body, @"/ToUnicode\s+(?<obj>\d+)\s+\d+\s+R", RegexOptions.NonBacktracking);
                if (!toUnicodeMatch.Success || !int.TryParse(toUnicodeMatch.Groups["obj"].Value, out int toUnicodeObject))
                {
                    continue;
                }

                if (!toUnicodeByObjectNumber.TryGetValue(toUnicodeObject, out ToUnicodeCMap? cmap))
                {
                    continue;
                }

                if (int.TryParse(match.Groups["obj"].Value, out int fontObject))
                {
                    fontObjectToCMap[fontObject] = cmap;
                }
            }

            var byResourceName = new Dictionary<string, ToUnicodeCMap>(StringComparer.Ordinal);
            foreach (string fontBlock in EnumerateFontResourceBlocks(raw))
            {
                foreach (Match entry in Regex.Matches(fontBlock, @"/(?<name>[A-Za-z0-9_.+\-#]+)\s+(?<obj>\d+)\s+\d+\s+R", RegexOptions.NonBacktracking))
                {
                    if (!int.TryParse(entry.Groups["obj"].Value, out int fontObject))
                    {
                        continue;
                    }

                    if (fontObjectToCMap.TryGetValue(fontObject, out ToUnicodeCMap? cmap))
                    {
                        byResourceName[DecodePdfName(entry.Groups["name"].Value)] = cmap;
                    }
                }
            }

            return byResourceName;
        }

        private static bool LooksLikeToUnicodeCMap(string text)
        {
            return text.Contains("begincmap", StringComparison.Ordinal) &&
                (text.Contains("beginbfchar", StringComparison.Ordinal) || text.Contains("beginbfrange", StringComparison.Ordinal));
        }

        private static IEnumerable<string> EnumerateFontResourceBlocks(string raw)
        {
            int searchStart = 0;
            while (searchStart < raw.Length)
            {
                int fontIndex = raw.IndexOf("/Font", searchStart, StringComparison.Ordinal);
                if (fontIndex < 0)
                {
                    yield break;
                }

                searchStart = fontIndex + 5;
                if (fontIndex + 5 < raw.Length && IsPdfNameCharacter(raw[fontIndex + 5]))
                {
                    continue;
                }

                int dictStart = fontIndex + 5;
                while (dictStart < raw.Length && char.IsWhiteSpace(raw[dictStart]))
                {
                    dictStart++;
                }

                if (dictStart + 1 >= raw.Length || raw[dictStart] != '<' || raw[dictStart + 1] != '<')
                {
                    continue;
                }

                int dictEnd = FindMatchingDictionaryEnd(raw, dictStart);
                if (dictEnd < 0)
                {
                    continue;
                }

                yield return raw.Substring(dictStart + 2, dictEnd - dictStart - 2);
                searchStart = dictEnd + 2;
            }
        }

        private static int FindMatchingDictionaryEnd(string raw, int dictStart)
        {
            int depth = 0;
            for (int i = dictStart; i + 1 < raw.Length; i++)
            {
                if (raw[i] == '<' && raw[i + 1] == '<')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (raw[i] == '>' && raw[i + 1] == '>')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    i++;
                }
            }

            return -1;
        }

        private static ToUnicodeCMap ParseToUnicodeCMap(string cmapText)
        {
            var cmap = new ToUnicodeCMap();

            foreach (Match section in Regex.Matches(cmapText, @"beginbfchar(?<body>.*?)endbfchar", RegexOptions.Singleline | RegexOptions.NonBacktracking))
            {
                foreach (Match entry in Regex.Matches(section.Groups["body"].Value, @"<(?<src>[0-9A-Fa-f\s]+)>\s*<(?<dst>[0-9A-Fa-f\s]+)>", RegexOptions.NonBacktracking))
                {
                    byte[] source = ParseHexStringBytes(entry.Groups["src"].Value);
                    string target = DecodeUnicodeHexString(entry.Groups["dst"].Value);
                    cmap.Add(source, target);
                }
            }

            foreach (Match section in Regex.Matches(cmapText, @"beginbfrange(?<body>.*?)endbfrange", RegexOptions.Singleline | RegexOptions.NonBacktracking))
            {
                string body = section.Groups["body"].Value;

                foreach (Match entry in Regex.Matches(body, @"<(?<start>[0-9A-Fa-f\s]+)>\s*<(?<end>[0-9A-Fa-f\s]+)>\s*\[(?<array>.*?)\]", RegexOptions.Singleline | RegexOptions.NonBacktracking))
                {
                    AddCMapArrayRange(cmap, entry.Groups["start"].Value, entry.Groups["end"].Value, entry.Groups["array"].Value);
                }

                string bodyWithoutArrays = Regex.Replace(body, @"<(?<start>[0-9A-Fa-f\s]+)>\s*<(?<end>[0-9A-Fa-f\s]+)>\s*\[(?<array>.*?)\]", string.Empty, RegexOptions.Singleline | RegexOptions.NonBacktracking);
                foreach (Match entry in Regex.Matches(bodyWithoutArrays, @"<(?<start>[0-9A-Fa-f\s]+)>\s*<(?<end>[0-9A-Fa-f\s]+)>\s*<(?<dst>[0-9A-Fa-f\s]+)>", RegexOptions.NonBacktracking))
                {
                    AddCMapSequentialRange(cmap, entry.Groups["start"].Value, entry.Groups["end"].Value, entry.Groups["dst"].Value);
                }
            }

            return cmap;
        }

        private static void AddCMapArrayRange(ToUnicodeCMap cmap, string startHex, string endHex, string arrayText)
        {
            byte[] startBytes = ParseHexStringBytes(startHex);
            byte[] endBytes = ParseHexStringBytes(endHex);
            if (startBytes.Length == 0 || startBytes.Length != endBytes.Length)
            {
                return;
            }

            int current = HexBytesToInt(startBytes);
            int end = HexBytesToInt(endBytes);
            if (end < current || end - current > 4096)
            {
                return;
            }

            foreach (Match item in Regex.Matches(arrayText, @"<(?<dst>[0-9A-Fa-f\s]+)>", RegexOptions.NonBacktracking))
            {
                if (current > end)
                {
                    break;
                }

                cmap.Add(IntToBigEndianBytes(current, startBytes.Length), DecodeUnicodeHexString(item.Groups["dst"].Value));
                current++;
            }
        }

        private static void AddCMapSequentialRange(ToUnicodeCMap cmap, string startHex, string endHex, string destinationStartHex)
        {
            byte[] startBytes = ParseHexStringBytes(startHex);
            byte[] endBytes = ParseHexStringBytes(endHex);
            byte[] destinationBytes = ParseHexStringBytes(destinationStartHex);
            if (startBytes.Length == 0 || startBytes.Length != endBytes.Length || destinationBytes.Length < 2 || destinationBytes.Length % 2 != 0)
            {
                return;
            }

            int start = HexBytesToInt(startBytes);
            int end = HexBytesToInt(endBytes);
            if (end < start || end - start > 4096)
            {
                return;
            }

            int destinationLastCodeUnit = (destinationBytes[destinationBytes.Length - 2] << 8) | destinationBytes[destinationBytes.Length - 1];
            byte[] prefix = new byte[destinationBytes.Length - 2];
            Array.Copy(destinationBytes, prefix, prefix.Length);

            for (int code = start; code <= end; code++)
            {
                int offset = code - start;
                byte[] target = new byte[destinationBytes.Length];
                Array.Copy(prefix, target, prefix.Length);
                int codeUnit = destinationLastCodeUnit + offset;
                target[target.Length - 2] = (byte)((codeUnit >> 8) & 0xFF);
                target[target.Length - 1] = (byte)(codeUnit & 0xFF);
                cmap.Add(IntToBigEndianBytes(code, startBytes.Length), Encoding.BigEndianUnicode.GetString(target));
            }
        }

        private static string GetStreamDictionary(string raw, int streamIndex)
        {
            int objStart = raw.LastIndexOf("obj", streamIndex, StringComparison.Ordinal);
            int dictStart = raw.LastIndexOf("<<", streamIndex, StringComparison.Ordinal);
            if (dictStart < 0 || (objStart >= 0 && dictStart < objStart))
            {
                return string.Empty;
            }

            return raw.Substring(dictStart, streamIndex - dictStart);
        }

        private static bool ShouldSkipStream(string dictionary, int rawStreamLength)
        {
            if (string.IsNullOrWhiteSpace(dictionary))
            {
                return rawStreamLength > MaxRawStreamBytesToDecode;
            }

            string compact = Regex.Replace(dictionary, @"\s+", " ", RegexOptions.NonBacktracking);
            string[] binaryMarkers =
            {
                "/Subtype /Image",
                "/Subtype/Image",
                "/ImageMask",
                "/DCTDecode",
                "/JPXDecode",
                "/CCITTFaxDecode",
                "/JBIG2Decode",
                "/BitsPerComponent",
                "/ColorSpace",
                "/Width",
                "/Height",
                "/FontFile",
                "/FontFile2",
                "/FontFile3"
            };

            foreach (string marker in binaryMarkers)
            {
                if (compact.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return rawStreamLength > MaxRawStreamBytesToDecode;
        }

        private static bool LooksLikeTextContentStream(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            return content.Contains("BT", StringComparison.Ordinal) &&
                (content.Contains("Tj", StringComparison.Ordinal) ||
                 content.Contains("TJ", StringComparison.Ordinal) ||
                 content.Contains("'", StringComparison.Ordinal) ||
                 content.Contains("\"", StringComparison.Ordinal));
        }

        private static string? DecodeStream(string streamData, string dictionary)
        {
            byte[] streamBytes = Encoding.Latin1.GetBytes(streamData);
            if (dictionary.Contains("/FlateDecode", StringComparison.Ordinal) ||
                dictionary.Contains("/Fl", StringComparison.Ordinal))
            {
                string? inflated = TryInflate(streamBytes);
                return inflated;
            }

            return Encoding.Latin1.GetString(streamBytes);
        }

        private static string? TryInflate(byte[] bytes)
        {
            try
            {
                using var input = new MemoryStream(bytes);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                return ReadLimitedText(zlib);
            }
            catch
            {
            }

            try
            {
                using var input = new MemoryStream(bytes);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                return ReadLimitedText(deflate);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadLimitedText(Stream stream)
        {
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0 && output.Length < MaxDecodedStreamBytes)
            {
                int allowed = (int)Math.Min(read, MaxDecodedStreamBytes - output.Length);
                output.Write(buffer, 0, allowed);
            }

            return Encoding.Latin1.GetString(output.ToArray());
        }

        private static string ExtractTextFromContentStream(string content, Dictionary<string, ToUnicodeCMap> fontUnicodeMaps)
        {
            var builder = new StringBuilder();
            ToUnicodeCMap? currentFontMap = null;

            foreach (Match match in Regex.Matches(content, @"/(?<font>[A-Za-z0-9_.+\-#]+)\s+[-+]?\d*\.?\d+\s+Tf|\[(?<array>(?:\\.|[^\]\\])*)\]\s*TJ|(?:(?:[-+]?\d*\.?\d+\s+){0,2})(?<text>\((?:\\.|[^\\)])*\)|<(?<hex>[0-9A-Fa-f\s]+)>)\s*(?:Tj|'|"")|(?<newline>T\*|Td|TD)", RegexOptions.Singleline | RegexOptions.NonBacktracking))
            {
                if (match.Groups["font"].Success)
                {
                    string fontName = DecodePdfName(match.Groups["font"].Value);
                    fontUnicodeMaps.TryGetValue(fontName, out currentFontMap);
                }
                else if (match.Groups["array"].Success)
                {
                    AppendArrayText(builder, match.Groups["array"].Value, currentFontMap);
                }
                else if (match.Groups["text"].Success)
                {
                    AppendTokenText(builder, match.Groups["text"].Value, currentFontMap);
                }
                else if (match.Groups["newline"].Success)
                {
                    AppendLineBreak(builder);
                }
            }

            return builder.ToString();
        }

        private static void AppendArrayText(StringBuilder builder, string arrayText, ToUnicodeCMap? currentFontMap)
        {
            foreach (Match token in Regex.Matches(arrayText, @"\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]+>|-?\d+(?:\.\d+)?", RegexOptions.Singleline | RegexOptions.NonBacktracking))
            {
                string value = token.Value;
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double spacing))
                {
                    if (Math.Abs(spacing) >= 120)
                    {
                        AppendSpace(builder);
                    }
                    continue;
                }

                AppendTokenText(builder, value, currentFontMap);
            }
        }

        private static void AppendTokenText(StringBuilder builder, string token, ToUnicodeCMap? currentFontMap)
        {
            byte[] bytes = token.StartsWith("(", StringComparison.Ordinal) && token.EndsWith(")", StringComparison.Ordinal)
                ? DecodeLiteralStringBytes(token.Substring(1, token.Length - 2))
                : ParseHexStringBytes(token.Trim('<', '>'));

            string? mappedText = currentFontMap?.Decode(bytes);
            string text = !string.IsNullOrEmpty(mappedText)
                ? mappedText
                : DecodePdfStringBytes(bytes);

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            builder.Append(text);
        }

        private static string DecodeLiteralString(string value)
        {
            return DecodePdfStringBytes(DecodeLiteralStringBytes(value));
        }

        private static byte[] DecodeLiteralStringBytes(string value)
        {
            using var bytes = new MemoryStream(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length)
                {
                    bytes.WriteByte(ch <= 0xFF ? (byte)ch : (byte)'?');
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case 'n': bytes.WriteByte((byte)'\n'); break;
                    case 'r': bytes.WriteByte((byte)'\r'); break;
                    case 't': bytes.WriteByte((byte)'\t'); break;
                    case 'b': bytes.WriteByte((byte)'\b'); break;
                    case 'f': bytes.WriteByte((byte)'\f'); break;
                    case '(':
                    case ')':
                    case '\\':
                        bytes.WriteByte((byte)next);
                        break;
                    case '\r':
                        if (i + 1 < value.Length && value[i + 1] == '\n')
                        {
                            i++;
                        }
                        break;
                    case '\n':
                        break;
                    default:
                        if (next >= '0' && next <= '7')
                        {
                            int octal = next - '0';
                            int count = 1;
                            while (count < 3 && i + 1 < value.Length && value[i + 1] >= '0' && value[i + 1] <= '7')
                            {
                                octal = (octal * 8) + (value[++i] - '0');
                                count++;
                            }
                            bytes.WriteByte((byte)(octal & 0xFF));
                        }
                        else
                        {
                            bytes.WriteByte(next <= 0xFF ? (byte)next : (byte)'?');
                        }
                        break;
                }
            }

            return bytes.ToArray();
        }

        private static string DecodeHexString(string hex)
        {
            return DecodePdfStringBytes(ParseHexStringBytes(hex));
        }

        private static byte[] ParseHexStringBytes(string hex)
        {
            string compact = Regex.Replace(hex, @"\s+", string.Empty, RegexOptions.NonBacktracking);
            if (compact.Length == 0)
            {
                return Array.Empty<byte>();
            }

            if (compact.Length % 2 == 1)
            {
                compact += "0";
            }

            var bytes = new byte[compact.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        private static string DecodePdfStringBytes(byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            // Try UTF-8 for CJK/Korean text without BOM
            if (bytes.Length >= 3)
            {
                string utf8 = Encoding.UTF8.GetString(bytes);
                if (utf8.IndexOf('\uFFFD') < 0 && ContainsCjkCharacters(utf8))
                {
                    return utf8;
                }
            }

            // Try UTF-16BE for CJK/Korean text without BOM
            // (conservative: skip plain ASCII)
            if (bytes.Length >= 2 && bytes.Length % 2 == 0 && !LooksLikePlainAsciiBytes(bytes))
            {
                string utf16be = Encoding.BigEndianUnicode.GetString(bytes);
                if (LooksLikeRealCjkText(utf16be))
                {
                    return utf16be;
                }
            }

            bool looksUtf16 = bytes.Length > 2 && bytes.Length % 2 == 0 && bytes[0] == 0;
            return looksUtf16 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        }

        private static string DecodeUnicodeHexString(string hex)
        {
            byte[] bytes = ParseHexStringBytes(hex);
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            return bytes.Length % 2 == 0
                ? Encoding.BigEndianUnicode.GetString(bytes)
                : Encoding.Latin1.GetString(bytes);
        }

        private static int HexBytesToInt(byte[] bytes)
        {
            int value = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                value = (value << 8) | bytes[i];
            }

            return value;
        }

        private static byte[] IntToBigEndianBytes(int value, int byteCount)
        {
            byte[] bytes = new byte[byteCount];
            for (int i = byteCount - 1; i >= 0; i--)
            {
                bytes[i] = (byte)(value & 0xFF);
                value >>= 8;
            }

            return bytes;
        }

        private static string BytesToHexKey(byte[] bytes, int offset, int length)
        {
            var builder = new StringBuilder(length * 2);
            for (int i = 0; i < length; i++)
            {
                builder.Append(bytes[offset + i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static string DecodePdfName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('#') < 0)
            {
                return name;
            }

            var builder = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] == '#' && i + 2 < name.Length && IsHexDigit(name[i + 1]) && IsHexDigit(name[i + 2]))
                {
                    builder.Append((char)Convert.ToByte(name.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                {
                    builder.Append(name[i]);
                }
            }

            return builder.ToString();
        }

        private static bool IsPdfNameCharacter(char ch)
        {
            return ch > 0x20 && ch != '/' && ch != '<' && ch != '>' && ch != '[' && ch != ']' && ch != '(' && ch != ')' && ch != '{' && ch != '}' && ch != '%';
        }

        private static bool IsHexDigit(char ch)
        {
            return (ch >= '0' && ch <= '9') ||
                (ch >= 'A' && ch <= 'F') ||
                (ch >= 'a' && ch <= 'f');
        }

        private static string DecodeUtf16BeIfBomPresent(string text)
        {
            if (text.Length >= 3 && text[0] == '\u00FE' && text[1] == '\u00FF')
            {
                return DecodeUtf16FromStringBytes(text, 2, bigEndian: true);
            }

            if (text.Length >= 3 && text[0] == '\u00FF' && text[1] == '\u00FE')
            {
                return DecodeUtf16FromStringBytes(text, 2, bigEndian: false);
            }

            // Try UTF-16BE without BOM for CJK/Korean text
            // (conservative: skip plain ASCII)
            if (text.Length >= 2 && text.Length % 2 == 0 && !LooksLikePlainAsciiText(text))
            {
                string utf16be = DecodeUtf16FromStringBytes(text, 0, bigEndian: true);
                if (LooksLikeRealCjkText(utf16be))
                {
                    return utf16be;
                }
            }

            return text;
        }

        private static string DecodeUtf16FromStringBytes(string text, int offset, bool bigEndian)
        {
            byte[] bytes = new byte[text.Length - offset];
            for (int i = offset; i < text.Length; i++)
            {
                bytes[i - offset] = text[i] <= 0xFF ? (byte)text[i] : (byte)'?';
            }
            return bigEndian
                ? Encoding.BigEndianUnicode.GetString(bytes)
                : Encoding.Unicode.GetString(bytes);
        }

        private static bool ContainsCjkCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (char ch in text)
            {
                if (IsCjkCharacter(ch))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsCjkCharacter(char ch)
        {
            // Hangul Syllables (Korean)
            if (ch >= 0xAC00 && ch <= 0xD7AF) return true;
            // Hangul Jamo
            if (ch >= 0x1100 && ch <= 0x11FF) return true;
            // Hangul Compatibility Jamo
            if (ch >= 0x3130 && ch <= 0x318F) return true;
            // CJK Unified Ideographs
            if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
            // CJK Extension A
            if (ch >= 0x3400 && ch <= 0x4DBF) return true;
            // Hiragana
            if (ch >= 0x3040 && ch <= 0x309F) return true;
            // Katakana
            if (ch >= 0x30A0 && ch <= 0x30FF) return true;
            // Fullwidth Forms
            if (ch >= 0xFF00 && ch <= 0xFFEF) return true;
            return false;
        }

        private static bool LooksLikePlainAsciiText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int printable = 0;
            int total = 0;
            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n' || ch == '\t')
                {
                    printable++;
                    total++;
                }
                else if (ch >= 0x20 && ch <= 0x7E)
                {
                    printable++;
                    total++;
                }
                else
                {
                    total++;
                }
            }

            return total > 0 && printable * 100 / total >= 85;
        }

        private static bool LooksLikePlainAsciiBytes(byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return false;
            }

            int printable = 0;
            foreach (byte b in bytes)
            {
                if (b == '\r' || b == '\n' || b == '\t' || (b >= 0x20 && b <= 0x7E))
                {
                    printable++;
                }
            }

            return printable * 100 / bytes.Length >= 85;
        }

        private static bool LooksLikeRealCjkText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int cjk = 0;
            int bad = 0;
            int meaningful = 0;

            foreach (char ch in text)
            {
                if (char.IsControl(ch) && ch != '\r' && ch != '\n' && ch != '\t')
                {
                    bad++;
                    continue;
                }

                if (!char.IsWhiteSpace(ch))
                {
                    meaningful++;
                }

                if (IsCjkCharacter(ch))
                {
                    cjk++;
                }
            }

            if (meaningful == 0)
            {
                return false;
            }

            // Reject if there are garbled control characters
            if (bad > 0)
            {
                return false;
            }

            // Pass only when CJK is at least 2 chars and a meaningful ratio
            return cjk >= 2 && cjk * 100 / meaningful >= 30;
        }

        private static string ExtractLooseLiteralText(string raw)
        {
            if (raw.Length > MaxLooseFallbackChars)
            {
                raw = raw.Substring(0, MaxLooseFallbackChars);
            }

            var builder = new StringBuilder();
            foreach (Match match in Regex.Matches(raw, @"\((?:\\.|[^\\)]){3,}\)", RegexOptions.NonBacktracking))
            {
                string text = DecodeLiteralString(match.Value.Substring(1, match.Value.Length - 2));
                if (LooksLikeText(text))
                {
                    builder.AppendLine(text);
                }
            }

            return builder.ToString();
        }

        private static bool LooksLikeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            int printable = 0;
            foreach (char ch in text)
            {
                if (!char.IsControl(ch) || ch == '\n' || ch == '\r' || ch == '\t')
                {
                    printable++;
                }
            }

            return printable >= Math.Max(3, text.Length / 2);
        }

        private static void AppendSpace(StringBuilder builder)
        {
            if (builder.Length > 0 && builder[builder.Length - 1] != ' ' && builder[builder.Length - 1] != '\n')
            {
                builder.Append(' ');
            }
        }

        private static void AppendLineBreak(StringBuilder builder)
        {
            if (builder.Length > 0 && builder[builder.Length - 1] != '\n')
            {
                builder.Append('\n');
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
            normalized = Regex.Replace(normalized, @"[ \t\f\v]+", " ", RegexOptions.NonBacktracking);
            normalized = Regex.Replace(normalized, @" *\n *", "\n", RegexOptions.NonBacktracking);
            normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n", RegexOptions.NonBacktracking);
            return normalized.Trim();
        }

        private static int TotalLength(IEnumerable<string> parts)
        {
            int total = 0;
            foreach (string part in parts)
            {
                total += part.Length;
            }

            return total;
        }
    }
}
