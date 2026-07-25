using System.Collections.Generic;
using System.Text.Json;

namespace TxtAIEditor.Core.Services
{
    public sealed class NotebookDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("cells")]
        public List<NotebookCell>? Cells { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nbformat")]
        public int? NbFormat { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nbformat_minor")]
        public int? NbFormatMinor { get; set; }
    }

    public sealed class NotebookCell
    {
        [System.Text.Json.Serialization.JsonPropertyName("cell_type")]
        public string? CellType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public JsonElement? Source { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("outputs")]
        public List<JsonElement>? Outputs { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("metadata")]
        public JsonElement? Metadata { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("execution_count")]
        public JsonElement? ExecutionCount { get; set; }
    }
}
