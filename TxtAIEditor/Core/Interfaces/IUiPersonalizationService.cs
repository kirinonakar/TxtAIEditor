using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Interfaces
{
    public interface IUiPersonalizationService
    {
        void Apply(
            EditorSettings settings,
            AppWindow appWindow,
            FrameworkElement? rootElement,
            Action<Windows.UI.Color> applyMarkdownToolbarBackground);
    }
}
