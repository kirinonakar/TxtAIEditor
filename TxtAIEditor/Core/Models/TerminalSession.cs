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
        private const int MaximumOutputLength = 1_000_000;
        private const int RetainedOutputLength = 750_000;

        private readonly object _outputLock = new object();
        private readonly StringBuilder _output = new StringBuilder();
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

        public string WorkingDirectory { get; private set; }
        public TerminalShellProfile ShellProfile { get; }
        public string WindowTitle { get; }
        public string DisplayTitle => $"{ShellProfile.ShortName}{Number}";
        public Process? Process { get; set; }
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;
        public bool IsNative { get; set; }
        public ConPtyTerminal? Terminal { get; set; }
        public int Columns { get; set; } = 80;
        public int Rows { get; set; } = 24;
        public void SetDisplayNumber(int number)
        {
            Number = Math.Max(1, number);
        }

        public void SetWorkingDirectory(string workingDirectory)
        {
            if (string.Equals(WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            WorkingDirectory = workingDirectory;
            OnPropertyChanged(nameof(WorkingDirectory));
        }

        public void AppendOutput(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_outputLock)
            {
                _output.Append(text);
                if (_output.Length > MaximumOutputLength)
                {
                    _output.Remove(0, _output.Length - RetainedOutputLength);
                }
            }
        }

        public string GetOutputSnapshot()
        {
            lock (_outputLock)
            {
                return _output.ToString();
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
