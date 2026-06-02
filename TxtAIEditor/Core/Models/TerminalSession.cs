using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Core.Models
{
    public class TerminalSession : INotifyPropertyChanged
    {
        private int _number;

        public TerminalSession(string workingDirectory, TerminalShellProfile shellProfile)
        {
            _number = 1;
            WorkingDirectory = workingDirectory;
            ShellProfile = shellProfile;
            WindowTitle = $"TxtAIEditor_Console_{Process.GetCurrentProcess().Id}_{Guid.NewGuid():N}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public int Number
        {
            get => _number;
            private set
            {
                if (_number == value)
                {
                    return;
                }

                _number = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }

        public string WorkingDirectory { get; }
        public TerminalShellProfile ShellProfile { get; }
        public string WindowTitle { get; }
        public string DisplayTitle => $"{ShellProfile.ShortName}{Number}";
        public Process? Process { get; set; }
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;
        public bool IsNative { get; set; }
        public ConPtyTerminal? Terminal { get; set; }
        public int Columns { get; set; } = 80;
        public int Rows { get; set; } = 24;
        public StringBuilder Output { get; } = new StringBuilder();

        public void SetDisplayNumber(int number)
        {
            Number = Math.Max(1, number);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
