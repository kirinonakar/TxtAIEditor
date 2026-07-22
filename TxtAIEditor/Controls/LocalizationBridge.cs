using System.ComponentModel;

namespace TxtAIEditor.Controls
{
    public sealed class LocalizationBridge : INotifyPropertyChanged
    {
        private string _snippetEditTooltip = "수정";
        private string _snippetDeleteTooltip = "삭제";
        private string _replaceOneTooltip = "이 항목만 바꾸기";
        private string _gitRestoreFileTooltip = "파일 복원";

        public string SnippetEditTooltip
        {
            get => _snippetEditTooltip;
            set => SetValue(ref _snippetEditTooltip, value, nameof(SnippetEditTooltip));
        }

        public string SnippetDeleteTooltip
        {
            get => _snippetDeleteTooltip;
            set => SetValue(ref _snippetDeleteTooltip, value, nameof(SnippetDeleteTooltip));
        }

        public string ReplaceOneTooltip
        {
            get => _replaceOneTooltip;
            set => SetValue(ref _replaceOneTooltip, value, nameof(ReplaceOneTooltip));
        }

        public string GitRestoreFileTooltip
        {
            get => _gitRestoreFileTooltip;
            set => SetValue(ref _gitRestoreFileTooltip, value, nameof(GitRestoreFileTooltip));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetValue(ref string field, string value, string propertyName)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
