using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TxtAIEditor.Controls
{
    internal sealed class RightSidebarPresetMenuController
    {
        private readonly ResourceDictionary _resources;
        private readonly StackPanel _presetListPanel;
        private readonly Flyout _presetFlyout;

        private List<string> _presetNames = new();
        private Action? _onAddPresetClick;
        private Action<string>? _onPresetSelected;
        private Action<string>? _onPresetEdited;
        private Action<string>? _onPresetDeleted;
        private Action? _onExportPresetClick;
        private Action? _onImportPresetClick;

        public RightSidebarPresetMenuController(
            ResourceDictionary resources,
            StackPanel presetListPanel,
            Flyout presetFlyout)
        {
            _resources = resources;
            _presetListPanel = presetListPanel;
            _presetFlyout = presetFlyout;
        }

        public void UpdatePresetsMenu(
            List<string> presetNames,
            Action onAddPresetClick,
            Action<string> onPresetSelected,
            Action<string> onPresetEdited,
            Action<string> onPresetDeleted,
            Action onExportPresetClick,
            Action onImportPresetClick)
        {
            _presetNames = presetNames;
            _onAddPresetClick = onAddPresetClick;
            _onPresetSelected = onPresetSelected;
            _onPresetEdited = onPresetEdited;
            _onPresetDeleted = onPresetDeleted;
            _onExportPresetClick = onExportPresetClick;
            _onImportPresetClick = onImportPresetClick;

            RebuildPresetsMenu();
        }

        public void AddPreset()
        {
            _onAddPresetClick?.Invoke();
            _presetFlyout.Hide();
        }

        public void ExportPresets()
        {
            _onExportPresetClick?.Invoke();
            _presetFlyout.Hide();
        }

        public void ImportPresets()
        {
            _onImportPresetClick?.Invoke();
            _presetFlyout.Hide();
        }

        public void RebuildPresetsMenu()
        {
            _presetListPanel.Children.Clear();

            var buttonStyle = _resources["RightSidebarButtonStyle"] as Style;

            foreach (var presetName in _presetNames)
            {
                string currentName = presetName;
                var rowGrid = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 10, 2) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var selectButton = new Button
                {
                    Content = presetName,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 28,
                    FontSize = 11,
                    Padding = new Thickness(8, 0, 8, 0),
                    Style = buttonStyle
                };
                selectButton.Click += (_, _) =>
                {
                    _onPresetSelected?.Invoke(currentName);
                    _presetFlyout.Hide();
                };
                Grid.SetColumn(selectButton, 0);
                rowGrid.Children.Add(selectButton);

                var editButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE70F", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                editButton.Click += (_, _) =>
                {
                    _onPresetEdited?.Invoke(currentName);
                    _presetFlyout.Hide();
                };
                Grid.SetColumn(editButton, 1);
                rowGrid.Children.Add(editButton);

                var deleteButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Style = buttonStyle
                };
                deleteButton.Click += (_, _) => _onPresetDeleted?.Invoke(currentName);
                Grid.SetColumn(deleteButton, 2);
                rowGrid.Children.Add(deleteButton);

                _presetListPanel.Children.Add(rowGrid);
            }
        }
    }
}
