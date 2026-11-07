using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services.LLM;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TxtAIEditor.Controls
{
    internal static class AgentMcpToolResultFormatter
    {
        public static async Task<string> FormatAsync(
            string alias,
            JsonElement result,
            Action<LlmMessageAttachment>? addImageAttachment)
        {
            if (result.ValueKind != JsonValueKind.Object)
            {
                return "MCP tool returned an invalid result.";
            }

            bool isError = result.TryGetProperty("isError", out var isErrorProp) && isErrorProp.ValueKind == JsonValueKind.True;
            var builder = new StringBuilder();
            builder.AppendLine(isError
                ? $"MCP tool returned an error: {alias}"
                : $"MCP tool result: {alias}");

            // Each call needs distinct names: the run context replaces attachments with the same name.
            string callId = Guid.NewGuid().ToString("N");
            int imageIndex = 0;
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    string type = GetStringProperty(item, "type");
                    if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = GetStringProperty(item, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            builder.AppendLine(text);
                        }
                    }
                    else if (type.Equals("image", StringComparison.OrdinalIgnoreCase))
                    {
                        imageIndex++;
                        string mimeType = GetStringProperty(item, "mimeType").Trim();
                        string data = GetStringProperty(item, "data");
                        if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                            mimeType.Length <= "image/".Length || string.IsNullOrWhiteSpace(data))
                        {
                            builder.AppendLine("[image not attached: missing image MIME type or Base64 data]");
                            continue;
                        }

                        if (addImageAttachment == null)
                        {
                            builder.AppendLine("[image not attached: image attachment handler unavailable]");
                            continue;
                        }

                        byte[] bytes;
                        try
                        {
                            bytes = Convert.FromBase64String(data);
                        }
                        catch (FormatException)
                        {
                            builder.AppendLine("[image not attached: invalid Base64 data]");
                            continue;
                        }

                        var attachment = new LlmMessageAttachment
                        {
                            DisplayName = $"MCP {alias} {callId} image {imageIndex}",
                            MimeType = mimeType,
                            Base64Data = data
                        };
                        await PopulateDimensionsAsync(attachment, bytes);
                        addImageAttachment(attachment);
                        builder.AppendLine($"[image attached: {attachment.DisplayName}; mime: {mimeType}; bytes: {bytes.Length}; " +
                            $"dimensions: {attachment.Width}x{attachment.Height}; original image without resizing or re-encoding; " +
                            "available to the next model call]");
                    }
                    else if (!string.IsNullOrWhiteSpace(type))
                    {
                        builder.AppendLine($"[{type} content omitted]");
                    }
                }
            }

            if (result.TryGetProperty("structuredContent", out var structuredContent))
            {
                builder.AppendLine("[structuredContent]");
                builder.AppendLine(structuredContent.GetRawText());
            }

            return builder.ToString().TrimEnd();
        }

        private static async Task PopulateDimensionsAsync(LlmMessageAttachment attachment, byte[] bytes)
        {
            try
            {
                using var memory = new MemoryStream(bytes, writable: false);
                using IRandomAccessStream input = memory.AsRandomAccessStream();
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                int width = checked((int)decoder.PixelWidth);
                int height = checked((int)decoder.PixelHeight);
                attachment.Width = width;
                attachment.Height = height;
                double tiles = Math.Ceiling(width / 512.0) * Math.Ceiling(height / 512.0);
                attachment.EstimatedTokens = (int)Math.Min(int.MaxValue, 85 + tiles * 170);
            }
            catch (Exception)
            {
                // Metadata is optional: the model may support codecs unavailable on this machine.
                // Always preserve the server's MIME type and original bytes.
            }
        }

        private static string GetStringProperty(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                    ? property.GetString() ?? string.Empty
                    : string.Empty;
        }
    }
}
