using System;
using System.Text;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Core.Services
{
    public static class TextEncodingService
    {
        static TextEncodingService()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public static readonly string[] SupportedEncodingNames =
        {
            "Auto",
            "UTF-8",
            "UTF-8 BOM",
            "UTF-16 LE",
            "UTF-16 BE",
            "UTF-32 LE",
            "EUC-KR",
            "Shift-JIS",
            "Johab"
        };

        public static Encoding GetTextEncoding(byte[] bytes, string encodingName)
        {
            if (!string.IsNullOrWhiteSpace(encodingName) &&
                !encodingName.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                return GetEncodingByName(encodingName);
            }

            return DetectEncoding(bytes);
        }

        public static string GetDisplayName(Encoding encoding, bool hasBom = false)
        {
            if (encoding.CodePage == Encoding.UTF8.CodePage)
            {
                return hasBom ? "UTF-8 BOM" : "UTF-8";
            }

            return encoding.CodePage switch
            {
                1200 => "UTF-16 LE",
                1201 => "UTF-16 BE",
                12000 => "UTF-32 LE",
                949 => "EUC-KR",
                51949 => "EUC-KR",
                932 => "Shift-JIS",
                1361 => "Johab",
                _ => encoding.WebName.ToUpperInvariant()
            };
        }

        public static Encoding GetEncodingByName(string encodingName)
        {
            return encodingName switch
            {
                "UTF-8" => new UTF8Encoding(false),
                "UTF-8 BOM" => new UTF8Encoding(true),
                "UTF-16 LE" => Encoding.Unicode,
                "UTF-16 BE" => Encoding.BigEndianUnicode,
                "UTF-32 LE" => Encoding.UTF32,
                "EUC-KR" => Encoding.GetEncoding(949),
                "Shift-JIS" => Encoding.GetEncoding(932),
                "Johab" => Encoding.GetEncoding(1361),
                _ => new UTF8Encoding(false)
            };
        }

        public static Encoding DetectEncoding(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) return Encoding.UTF32;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;

            var htmlCharset = DetectHtmlCharset(bytes);
            if (htmlCharset != null) return htmlCharset;

            if (IsValidUtf8(bytes)) return new UTF8Encoding(false);

            int eucKrScore = GetEucKrScore(bytes);
            int sjisScore = GetSjisScore(bytes);
            int johabScore = GetJohabScore(bytes);

            if (sjisScore > eucKrScore && sjisScore > johabScore && sjisScore > 0) return Encoding.GetEncoding(932);
            if (eucKrScore > sjisScore && eucKrScore > johabScore && eucKrScore > 0) return Encoding.GetEncoding(949);
            if (johabScore > sjisScore && johabScore > eucKrScore && johabScore > 0) return Encoding.GetEncoding(1361);

            if (eucKrScore > 0 && eucKrScore >= sjisScore) return Encoding.GetEncoding(949);
            if (sjisScore > 0) return Encoding.GetEncoding(932);
            if (johabScore > 0 || ContainsJohabPattern(bytes)) return Encoding.GetEncoding(1361);

            return new UTF8Encoding(false);
        }

        public static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static int GetSjisScore(byte[] bytes)
        {
            int score = 0;
            int i = 0;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b < 0x80)
                {
                    i++;
                    continue;
                }

                if (b >= 0xA1 && b <= 0xDF)
                {
                    if (i + 1 < bytes.Length && bytes[i + 1] < 0x80) score += 1;
                    i++;
                    continue;
                }

                if (i + 1 >= bytes.Length) break;
                byte b2 = bytes[i + 1];
                if (((b >= 0x81 && b <= 0x9F) || (b >= 0xE0 && b <= 0xFC)) &&
                    ((b2 >= 0x40 && b2 <= 0x7E) || (b2 >= 0x80 && b2 <= 0xFC)))
                {
                    score += (b == 0x82 || b == 0x83) ? 5 : 1;
                    i += 2;
                    continue;
                }

                i++;
            }

            return score;
        }

        private static int GetEucKrScore(byte[] bytes)
        {
            int score = 0;
            int i = 0;
            while (i < bytes.Length)
            {
                byte b1 = bytes[i];
                if (b1 < 0x80)
                {
                    i++;
                    continue;
                }

                if (i + 1 >= bytes.Length) break;
                byte b2 = bytes[i + 1];
                if (b1 >= 0xB0 && b1 <= 0xC8 && b2 >= 0xA1 && b2 <= 0xFE)
                {
                    score += 2;
                    i += 2;
                    continue;
                }

                i++;
            }

            return score;
        }

        private static int GetJohabScore(byte[] bytes)
        {
            int score = 0;
            int i = 0;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b < 0x80)
                {
                    i++;
                    continue;
                }

                if (i + 1 >= bytes.Length) break;
                byte b2 = bytes[i + 1];
                if (b >= 0x84 && b <= 0xD3)
                {
                    if ((b2 >= 0x5B && b2 <= 0x60) || (b2 >= 0x7B && b2 <= 0x7E))
                    {
                        score += 3;
                        i += 2;
                        continue;
                    }

                    if ((b2 >= 0x41 && b2 <= 0x7E) || (b2 >= 0x81 && b2 <= 0xFE))
                    {
                        score += 1;
                        i += 2;
                        continue;
                    }
                }

                i++;
            }

            return score;
        }

        private static bool ContainsJohabPattern(byte[] bytes)
        {
            int johabOnlyPairCount = 0;
            int i = 0;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b < 0x80)
                {
                    i++;
                    continue;
                }

                if (i + 1 >= bytes.Length) break;
                byte b2 = bytes[i + 1];
                if (b >= 0x84 && b <= 0xD3)
                {
                    bool johabOnlySecond = (b2 >= 0x5B && b2 <= 0x60) || (b2 >= 0x7B && b2 <= 0x7E);
                    if (johabOnlySecond)
                    {
                        johabOnlyPairCount++;
                        i += 2;
                        if (johabOnlyPairCount >= 2) return true;
                        continue;
                    }
                }

                i++;
            }

            return false;
        }

        private static Encoding? DetectHtmlCharset(byte[] bytes)
        {
            try
            {
                int len = Math.Min(bytes.Length, 2048);
                string head = Encoding.ASCII.GetString(bytes, 0, len);

                var match = Regex.Match(head, @"<meta\s+charset=[""']?([a-zA-Z0-9-_]+)[""']?", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return GetEncodingFromCharset(match.Groups[1].Value);
                }

                match = Regex.Match(head, @"charset\s*=\s*([a-zA-Z0-9-_]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return GetEncodingFromCharset(match.Groups[1].Value);
                }
            }
            catch { }

            return null;
        }

        private static Encoding? GetEncodingFromCharset(string charset)
        {
            try
            {
                if (charset.Equals("shift_jis", StringComparison.OrdinalIgnoreCase) ||
                    charset.Equals("sjis", StringComparison.OrdinalIgnoreCase) ||
                    charset.Equals("x-sjis", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.GetEncoding(932);
                }

                if (charset.Equals("euc-kr", StringComparison.OrdinalIgnoreCase) ||
                    charset.Equals("ks_c_5601-1987", StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.GetEncoding(949);
                }

                return Encoding.GetEncoding(charset);
            }
            catch
            {
                return null;
            }
        }

        public static bool IsValidUtf8(byte[] bytes)
        {
            try
            {
                var decoder = Encoding.UTF8.GetDecoder();
                decoder.Fallback = new DecoderExceptionFallback();
                char[] chars = new char[decoder.GetCharCount(bytes, 0, bytes.Length)];
                decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
