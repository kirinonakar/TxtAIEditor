using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    public sealed class MainWindowShellInteractionController
    {
        private readonly UIElement _root;
        private readonly UIElement _dragOverlay;
        private readonly UIElement _leftSplitter;
        private readonly UIElement _rightSplitter;
        private readonly FileOpenDropController _fileOpenDropController;
        private readonly ShellPanelLayoutService _shellPanelLayoutService;
        private readonly RootKeyboardShortcutController _rootKeyboardShortcutController;
        private readonly KeyboardAccelerator _wordWrapKeyboardAccelerator;

        public MainWindowShellInteractionController(
            UIElement root,
            UIElement dragOverlay,
            UIElement leftSplitter,
            UIElement rightSplitter,
            FileOpenDropController fileOpenDropController,
            ShellPanelLayoutService shellPanelLayoutService,
            RootKeyboardShortcutController rootKeyboardShortcutController)
        {
            _root = root;
            _dragOverlay = dragOverlay;
            _leftSplitter = leftSplitter;
            _rightSplitter = rightSplitter;
            _fileOpenDropController = fileOpenDropController;
            _shellPanelLayoutService = shellPanelLayoutService;
            _rootKeyboardShortcutController = rootKeyboardShortcutController;
            _wordWrapKeyboardAccelerator = new KeyboardAccelerator
            {
                Key = Windows.System.VirtualKey.Z,
                Modifiers = Windows.System.VirtualKeyModifiers.Menu
            };

            WireEvents();
        }

        private void WireEvents()
        {
            _root.DragOver += OnRootDragOver;
            _root.DragLeave += OnRootDragLeave;
            _root.Drop += OnRootDrop;
            _root.KeyDown += OnRootKeyDown;
            _wordWrapKeyboardAccelerator.Invoked += OnWordWrapKeyboardAcceleratorInvoked;
            _root.KeyboardAccelerators.Add(_wordWrapKeyboardAccelerator);

            _dragOverlay.DragOver += OnDragOverlayOver;
            _dragOverlay.Drop += OnDragOverlayDrop;
            _dragOverlay.DragLeave += OnDragOverlayLeave;
            _dragOverlay.PointerPressed += OnDragOverlayPointerInput;
            _dragOverlay.PointerWheelChanged += OnDragOverlayPointerInput;

            _leftSplitter.PointerPressed += OnLeftSplitterPointerPressed;
            _leftSplitter.PointerMoved += OnLeftSplitterPointerMoved;
            _leftSplitter.PointerReleased += OnLeftSplitterPointerReleased;

            _rightSplitter.PointerPressed += OnRightSplitterPointerPressed;
            _rightSplitter.PointerMoved += OnRightSplitterPointerMoved;
            _rightSplitter.PointerReleased += OnRightSplitterPointerReleased;
        }

        private void OnRootDragOver(object sender, DragEventArgs e)
        {
            _fileOpenDropController.HandleRootDragOver(e);
        }

        private async void OnRootDrop(object sender, DragEventArgs e)
        {
            await _fileOpenDropController.HandleRootDropAsync(e);
        }

        private void OnRootDragLeave(object sender, DragEventArgs e)
        {
            _fileOpenDropController.HandleDragOverlayLeave();
        }

        private void OnDragOverlayOver(object sender, DragEventArgs e)
        {
            _fileOpenDropController.HandleDragOverlayOver(e);
        }

        private async void OnDragOverlayDrop(object sender, DragEventArgs e)
        {
            await _fileOpenDropController.HandleDragOverlayDropAsync(e);
        }

        private void OnDragOverlayLeave(object sender, DragEventArgs e)
        {
            _fileOpenDropController.HandleDragOverlayLeave();
        }

        private void OnDragOverlayPointerInput(object sender, PointerRoutedEventArgs e)
        {
            _fileOpenDropController.HandleDragOverlayLeave();
        }

        private void OnLeftSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _shellPanelLayoutService.OnLeftSplitterPointerPressed(sender, e);
        }

        private void OnLeftSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _shellPanelLayoutService.OnLeftSplitterPointerMoved(sender, e);
        }

        private void OnLeftSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _shellPanelLayoutService.OnLeftSplitterPointerReleased(sender, e);
        }

        private void OnRightSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _shellPanelLayoutService.OnRightSplitterPointerPressed(sender, e);
        }

        private void OnRightSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            _shellPanelLayoutService.OnRightSplitterPointerMoved(sender, e);
        }

        private void OnRightSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _shellPanelLayoutService.OnRightSplitterPointerReleased(sender, e);
        }

        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            _rootKeyboardShortcutController.HandleKeyDown(e);
        }

        private void OnWordWrapKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        {
            _rootKeyboardShortcutController.HandleWordWrapKeyboardAccelerator(e);
        }
    }
}
