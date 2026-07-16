using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TxtAIEditor.Editor
{
    internal static class EditorProtocol
    {
        public const int CurrentVersion = 1;

        private static readonly HashSet<string> MutationMessageTypes = new(StringComparer.Ordinal)
        {
            "lineChanged",
            "lineEdit",
            "rangeEdit",
            "insertLine",
            "splitLine",
            "mergeLineWithPrevious",
            "deleteLine",
            "hexEdit",
            "replaceAll"
        };

        public static bool TryAcceptIncoming(
            JsonElement root,
            string expectedDocumentId,
            string expectedViewId,
            ref long lastSequence,
            out string type)
        {
            type = string.Empty;
            if (!root.TryGetProperty("type", out JsonElement typeProperty) ||
                typeProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            type = typeProperty.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            if (string.Equals(type, "ready", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.IsNullOrEmpty(expectedDocumentId))
            {
                return false;
            }

            if (!root.TryGetProperty("protocolVersion", out JsonElement protocolProperty) ||
                !protocolProperty.TryGetInt32(out int protocolVersion) ||
                protocolVersion != CurrentVersion ||
                !HasExpectedString(root, "documentId", expectedDocumentId) ||
                !HasExpectedString(root, "viewId", expectedViewId) ||
                !root.TryGetProperty("documentVersion", out JsonElement versionProperty) ||
                !versionProperty.TryGetInt64(out long documentVersion) ||
                documentVersion < 0 ||
                !root.TryGetProperty("sequence", out JsonElement sequenceProperty) ||
                !sequenceProperty.TryGetInt64(out long sequence) ||
                sequence <= lastSequence)
            {
                return false;
            }

            if (MutationMessageTypes.Contains(type) &&
                (!root.TryGetProperty("baseVersion", out JsonElement baseVersionProperty) ||
                 !baseVersionProperty.TryGetInt64(out long baseVersion) ||
                 baseVersion < 0))
            {
                return false;
            }

            lastSequence = sequence;
            return true;
        }

        private static bool HasExpectedString(JsonElement root, string propertyName, string expectedValue)
        {
            return root.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String &&
                string.Equals(property.GetString(), expectedValue, StringComparison.Ordinal);
        }
    }
}
