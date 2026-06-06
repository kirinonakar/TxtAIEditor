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
        private const int MaxDecodedStreamBytes = 16 * 1024 * 1024;

        public async Task<string> ExtractTextAsync(string filePath, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || maxChars <= 0)
            {
                return string.Empty;
            }

            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            string raw = Encoding.Latin1.GetString(bytes);
            var parts = new List<string>();

            foreach (var streamText in EnumerateDecodedStreams(raw))
            {
                string extracted = ExtractTextFromContentStream(streamText);
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
                string fallback = ExtractLooseLiteralText(raw);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    parts.Add(fallback);
                }
            }

            string text = NormalizeExtractedText(string.Join("\n", parts));
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }

        private static IEnumerable<string> EnumerateDecodedStreams(string raw)
        {
            int searchStart = 0;
            while (searchStart < raw.Length)
            {
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
                string? decoded = DecodeStream(streamData, dictionary);
                if (!string.IsNullOrEmpty(decoded))
                {
                    yield return decoded;
                }

                searchStart = endIndex + "endstream".Length;
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

        private static string ExtractTextFromContentStream(string content)
        {
            var builder = new StringBuilder();

            foreach (Match match in Regex.Matches(content, @"\[(?<array>(?:\\.|[^\]])*)\]\s*TJ|(?<text>\((?:\\.|[^\\)])*\)|<(?<hex>[0-9A-Fa-f\s]+)>)\s*(?:Tj|'|"")|(?<newline>T\*|Td|TD)", RegexOptions.Singleline))
            {
                if (match.Groups["array"].Success)
                {
                    AppendArrayText(builder, match.Groups["array"].Value);
                }
                else if (match.Groups["text"].Success)
                {
                    AppendTokenText(builder, match.Groups["text"].Value);
                }
                else if (match.Groups["newline"].Success)
                {
                    AppendLineBreak(builder);
                }
            }

            return builder.ToString();
        }

        private static void AppendArrayText(StringBuilder builder, string arrayText)
        {
            foreach (Match token in Regex.Matches(arrayText, @"\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]+>|-?\d+(?:\.\d+)?", RegexOptions.Singleline))
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

                AppendTokenText(builder, value);
            }
        }

        private static void AppendTokenText(StringBuilder builder, string token)
        {
            string text = token.StartsWith("(", StringComparison.Ordinal) && token.EndsWith(")", StringComparison.Ordinal)
                ? DecodeLiteralString(token.Substring(1, token.Length - 2))
                : DecodeHexString(token.Trim('<', '>'));

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            builder.Append(text);
        }

        private static string DecodeLiteralString(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(ch);
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case '(':
                    case ')':
                    case '\\':
                        builder.Append(next);
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
                            builder.Append((char)octal);
                        }
                        else
                        {
                            builder.Append(next);
                        }
                        break;
                }
            }

            return builder.ToString();
        }

        private static string DecodeHexString(string hex)
        {
            string compact = Regex.Replace(hex, @"\s+", string.Empty);
            if (compact.Length == 0)
            {
                return string.Empty;
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

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            bool looksUtf16 = bytes.Length > 2 && bytes.Length % 2 == 0 && bytes[0] == 0;
            return looksUtf16 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        }

        private static string ExtractLooseLiteralText(string raw)
        {
            var builder = new StringBuilder();
            foreach (Match match in Regex.Matches(raw, @"\((?:\\.|[^\\)]){3,}\)"))
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
            normalized = Regex.Replace(normalized, @"[ \t\f\v]+", " ");
            normalized = Regex.Replace(normalized, @" *\n *", "\n");
            normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
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
