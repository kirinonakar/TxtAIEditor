using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TxtAIEditor.Core.Services
{
    internal static class SettingsDialogStyler
    {
        private const double InputFontSize = 11;
        private const double BodyFontSize = 11.5;

        public static void ApplyCompactStyleToLogicalTree(object? element)
        {
            if (element == null)
            {
                return;
            }

            if (element is Control ctrl)
            {
                if (ctrl is not Pivot && ctrl is not PivotItem)
                {
                    if (ctrl is ListView)
                    {
                        ctrl.FontSize = InputFontSize;
                    }
                    else if (ctrl.Tag as string == "LlmModelCombo")
                    {
                        ctrl.FontSize = InputFontSize;
                    }
                    else if (IsInputControl(ctrl))
                    {
                        ctrl.FontSize = InputFontSize;
                        ctrl.Loaded -= ApplyInputControlVisualStyle;
                        ctrl.Loaded += ApplyInputControlVisualStyle;
                    }
                    else
                    {
                        ctrl.FontSize = BodyFontSize;
                    }
                }

                if (ctrl is DropDownButton ddb)
                {
                    ddb.MinHeight = 26;
                    ddb.Height = double.NaN;
                    ddb.Padding = new Thickness(6, 2, 6, 2);
                    ddb.VerticalAlignment = VerticalAlignment.Center;
                }
                else if (ctrl is ComboBox || ctrl is TextBox || ctrl is PasswordBox || ctrl is Button)
                {
                    ctrl.MinHeight = 26;
                    ctrl.Height = 26;
                    ctrl.Padding = ctrl.Tag as string == "LlmModelCombo"
                        ? new Thickness(4, 1, 4, 1)
                        : new Thickness(8, 2, 8, 2);
                    ctrl.VerticalAlignment = VerticalAlignment.Center;
                }
                else if (ctrl is CheckBox chk)
                {
                    chk.MinHeight = 22;
                    chk.Padding = new Thickness(8, 2, 0, 2);
                    chk.Margin = new Thickness(chk.Margin.Left, 1, chk.Margin.Right, 1);
                }
            }
            else if (element is TextBlock tb && tb.FontSize != 11 && tb.FontSize != 12)
            {
                tb.FontSize = BodyFontSize;
            }

            if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    ApplyCompactStyleToLogicalTree(child);
                }
            }
            else if (element is UserControl userControl)
            {
                ApplyCompactStyleToLogicalTree(userControl.Content);
            }
            else if (element is ContentControl cc)
            {
                ApplyCompactStyleToLogicalTree(cc.Content);
            }
            else if (element is ScrollViewer sv)
            {
                ApplyCompactStyleToLogicalTree(sv.Content);
            }
            else if (element is Pivot pivot)
            {
                foreach (var item in pivot.Items)
                {
                    ApplyCompactStyleToLogicalTree(item);
                }
            }
        }

        public static void ApplyCompactStyleToVisualTree(DependencyObject? element)
        {
            if (element == null)
            {
                return;
            }

            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is Control ctrl)
                {
                    ctrl.FontSize = 10.5;

                    if (ctrl is TextBox || ctrl is ComboBox || ctrl is Button)
                    {
                        ctrl.MinHeight = 22;
                        ctrl.Height = 22;
                        ctrl.Padding = new Thickness(4, 1, 4, 1);
                    }
                }
                else if (child is TextBlock tb)
                {
                    tb.FontSize = 10.5;
                }

                ApplyCompactStyleToVisualTree(child);
            }
        }

        public static void ApplyEditableComboBoxVisualStyles(DependencyObject? parent)
        {
            if (parent == null)
            {
                return;
            }

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox textBox)
                {
                    ApplyEditableTextBoxStyles(textBox);
                    textBox.GotFocus += (_, __) => ApplyEditableTextBoxStyles(textBox);
                }
                else
                {
                    ApplyEditableComboBoxVisualStyles(child);
                }
            }
        }

        private static void ApplyEditableTextBoxStyles(TextBox textBox)
        {
            textBox.FontSize = InputFontSize;
            textBox.MinHeight = 24;
            textBox.Height = 24;
            textBox.Padding = new Thickness(4, 1, 4, 1);
            textBox.VerticalAlignment = VerticalAlignment.Center;
            textBox.IsSpellCheckEnabled = false;
            textBox.IsTextPredictionEnabled = false;
        }

        private static bool IsInputControl(Control ctrl)
        {
            return ctrl is CheckBox ||
                ctrl is ComboBox ||
                ctrl is TextBox ||
                ctrl is PasswordBox ||
                ctrl is Button ||
                ctrl is DropDownButton ||
                ctrl is NumberBox;
        }

        private static void ApplyInputControlVisualStyle(object sender, RoutedEventArgs e)
        {
            if (sender is DependencyObject element)
            {
                ApplyInputTextStyleToVisualTree(element);
            }
        }

        private static void ApplyInputTextStyleToVisualTree(DependencyObject element)
        {
            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                if (child is TextBlock textBlock)
                {
                    textBlock.FontSize = InputFontSize;
                }

                ApplyInputTextStyleToVisualTree(child);
            }
        }
    }
}
