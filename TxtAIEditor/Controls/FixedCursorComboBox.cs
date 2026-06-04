using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace TxtAIEditor.Controls
{
    public class FixedCursorComboBox : ComboBox
    {
        private static readonly InputSystemCursor ArrowCursor =
            InputSystemCursor.Create(InputSystemCursorShape.Arrow);

        public FixedCursorComboBox()
        {
            Loaded += OnLoaded;
            PointerEntered += OnPointerCursorEvent;
            PointerMoved += OnPointerCursorEvent;
            PointerPressed += OnPointerCursorEvent;
            DropDownOpened += OnDropDownStateChanged;
            DropDownClosed += OnDropDownStateChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UseArrowCursor();
        }

        private void OnPointerCursorEvent(object sender, PointerRoutedEventArgs e)
        {
            UseArrowCursor();
        }

        private void OnDropDownStateChanged(object? sender, object e)
        {
            UseArrowCursor();
        }

        private void UseArrowCursor()
        {
            ProtectedCursor = ArrowCursor;
        }
    }
}
