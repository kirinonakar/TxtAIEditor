using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Services;
using Windows.Graphics.Imaging;

namespace TxtAIEditor.Controls
{
    public sealed class ImageConversionController
    {
        private const double MaxDimension = 100_000;

        private readonly Func<string, string, string> _getString;
        private readonly Func<ElementTheme> _getCurrentElementTheme;
        private readonly ImageConversionService _imageConversionService = new();
        private bool _isShowing;

        public ImageConversionController(
            Func<string, string, string> getString,
            Func<ElementTheme> getCurrentElementTheme)
        {
            _getString = getString;
            _getCurrentElementTheme = getCurrentElementTheme;
        }

        public async Task ShowAsync(
            string sourcePath,
            XamlRoot? xamlRoot,
            ElementTheme theme)
        {
            if (_isShowing || xamlRoot == null || string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            if (theme == ElementTheme.Default)
            {
                theme = _getCurrentElementTheme();
            }

            _isShowing = true;
            try
            {
                ImageConversionSourceInfo sourceInfo;
                try
                {
                    sourceInfo = await _imageConversionService.ReadSourceInfoAsync(sourcePath);
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync(
                        _getString("ImageConvertErrorTitle", "이미지 변환 오류"),
                        ex.Message,
                        xamlRoot,
                        theme);
                    return;
                }

                bool isAnimated = sourceInfo.FrameCount > 1 && IsAnimatedImagePath(sourcePath);
                ImageConversionDialogValues? values = await ShowOptionsDialogAsync(
                    sourcePath,
                    sourceInfo,
                    isAnimated,
                    xamlRoot,
                    theme);
                if (values == null)
                {
                    return;
                }

                IReadOnlyList<string> outputPaths = ImageConversionService.BuildOutputPaths(
                    sourcePath,
                    values.OutputFormat,
                    values.ExtractFrames,
                    sourceInfo.FrameCount);
                string[] existingPaths = outputPaths.Where(File.Exists).ToArray();
                if (existingPaths.Length > 0)
                {
                    string existingName = Path.GetFileName(existingPaths[0]);
                    string overwriteMessage = string.Format(
                        _getString(
                            "ImageConvertOverwriteMessage",
                            "출력 파일 {0}개가 이미 존재합니다. 덮어쓰시겠습니까?\n예: {1}"),
                        existingPaths.Length,
                        existingName);
                    ContentDialogResult overwriteResult = await ShowConfirmationAsync(
                        _getString("ImageConvertOverwriteTitle", "출력 파일 덮어쓰기"),
                        overwriteMessage,
                        _getString("ImageConvertOverwriteButton", "덮어쓰기"),
                        xamlRoot,
                        theme);
                    if (overwriteResult != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }

                try
                {
                    IReadOnlyList<string> convertedPaths = await _imageConversionService.ConvertAsync(
                        sourcePath,
                        new ImageConversionOptions
                        {
                            OutputFormat = values.OutputFormat,
                            Quality = values.Quality,
                            ResizeEnabled = values.ResizeEnabled,
                            TargetWidth = values.TargetWidth,
                            TargetHeight = values.TargetHeight,
                            KeepAspectRatio = values.KeepAspectRatio,
                            InterpolationMode = values.InterpolationMode,
                            ExtractFrames = values.ExtractFrames
                        });

                    string firstOutputName = Path.GetFileName(convertedPaths[0]);
                    string successMessage = string.Format(
                        _getString(
                            "ImageConvertSuccessMessage",
                            "이미지 변환이 완료되었습니다.\n{0}개 파일 저장\n예: {1}"),
                        convertedPaths.Count,
                        firstOutputName);
                    await ShowMessageAsync(
                        _getString("ImageConvertSuccessTitle", "이미지 변환 완료"),
                        successMessage,
                        xamlRoot,
                        theme);
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync(
                        _getString("ImageConvertErrorTitle", "이미지 변환 오류"),
                        ex.Message,
                        xamlRoot,
                        theme);
                }
            }
            finally
            {
                _isShowing = false;
            }
        }

        private async Task<ImageConversionDialogValues?> ShowOptionsDialogAsync(
            string sourcePath,
            ImageConversionSourceInfo sourceInfo,
            bool isAnimated,
            XamlRoot xamlRoot,
            ElementTheme theme)
        {
            var root = new StackPanel
            {
                Spacing = 10,
                MinWidth = 410,
                MaxWidth = 560
            };

            root.Children.Add(new TextBlock
            {
                Text = string.Format(
                    _getString(
                        "ImageConvertSourceInfo",
                        "원본: {0}\n크기: {1} × {2}px · 프레임: {3}"),
                    Path.GetFileName(sourcePath),
                    sourceInfo.Width,
                    sourceInfo.Height,
                    sourceInfo.FrameCount),
                TextWrapping = TextWrapping.Wrap
            });

            var formatCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0
            };
            formatCombo.Items.Add(CreateComboItem(
                _getString("ImageConvertFormatPng", "PNG"),
                ImageConversionOutputFormat.Png));
            formatCombo.Items.Add(CreateComboItem(
                _getString("ImageConvertFormatJpg", "JPG"),
                ImageConversionOutputFormat.Jpeg));
            root.Children.Add(CreateField(
                _getString("ImageConvertFormat", "출력 형식"),
                formatCombo));

            var qualityBox = new NumberBox
            {
                Value = 90,
                Minimum = 1,
                Maximum = 100,
                SmallChange = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var qualityField = new ContentControl
            {
                Content = CreateField(
                    _getString("ImageConvertQuality", "JPG 화질 (1-100)"),
                    qualityBox),
                IsEnabled = false
            };
            root.Children.Add(qualityField);

            formatCombo.SelectionChanged += (_, __) =>
            {
                qualityField.IsEnabled = GetSelectedFormat(formatCombo) == ImageConversionOutputFormat.Jpeg;
            };

            var resizeCheck = new CheckBox
            {
                Content = _getString("ImageConvertResize", "크기 조정"),
                IsChecked = false
            };
            root.Children.Add(resizeCheck);

            var widthBox = CreateDimensionBox(sourceInfo.Width);
            var heightBox = CreateDimensionBox(sourceInfo.Height);
            var dimensionGrid = new Grid { ColumnSpacing = 10 };
            dimensionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dimensionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dimensionGrid.Children.Add(CreateField(
                _getString("ImageConvertWidth", "너비 (px)"),
                widthBox));
            var heightField = CreateField(
                _getString("ImageConvertHeight", "높이 (px)"),
                heightBox);
            Grid.SetColumn(heightField, 1);
            dimensionGrid.Children.Add(heightField);

            var keepAspectCheck = new CheckBox
            {
                Content = _getString("ImageConvertKeepAspect", "비율 유지"),
                IsChecked = true
            };
            var interpolationCombo = CreateInterpolationComboBox();
            var resizeOptionsPanel = new StackPanel { Spacing = 8 };
            resizeOptionsPanel.Children.Add(dimensionGrid);
            resizeOptionsPanel.Children.Add(keepAspectCheck);
            resizeOptionsPanel.Children.Add(CreateField(
                _getString("ImageConvertInterpolation", "인터폴레이션"),
                interpolationCombo));
            var resizeOptions = new ContentControl
            {
                Content = resizeOptionsPanel,
                IsEnabled = false
            };
            root.Children.Add(resizeOptions);

            bool syncingAspectRatio = false;
            void SyncHeightFromWidth()
            {
                if (syncingAspectRatio || keepAspectCheck.IsChecked != true || !IsValidNumber(widthBox.Value))
                {
                    return;
                }

                syncingAspectRatio = true;
                heightBox.Value = Math.Max(
                    1,
                    Math.Round(widthBox.Value * sourceInfo.Height / sourceInfo.Width));
                syncingAspectRatio = false;
            }

            void SyncWidthFromHeight()
            {
                if (syncingAspectRatio || keepAspectCheck.IsChecked != true || !IsValidNumber(heightBox.Value))
                {
                    return;
                }

                syncingAspectRatio = true;
                widthBox.Value = Math.Max(
                    1,
                    Math.Round(heightBox.Value * sourceInfo.Width / sourceInfo.Height));
                syncingAspectRatio = false;
            }

            widthBox.ValueChanged += (_, __) => SyncHeightFromWidth();
            heightBox.ValueChanged += (_, __) => SyncWidthFromHeight();
            keepAspectCheck.Checked += (_, __) => SyncHeightFromWidth();
            resizeCheck.Checked += (_, __) => resizeOptions.IsEnabled = true;
            resizeCheck.Unchecked += (_, __) => resizeOptions.IsEnabled = false;

            CheckBox? extractFramesCheck = null;
            if (isAnimated)
            {
                extractFramesCheck = new CheckBox
                {
                    Content = string.Format(
                        _getString(
                            "ImageConvertExtractFrames",
                            "애니메이션 프레임별로 추출 ({0}개)"),
                        sourceInfo.FrameCount),
                    IsChecked = true
                };
                root.Children.Add(extractFramesCheck);
                root.Children.Add(new TextBlock
                {
                    Text = _getString(
                        "ImageConvertAnimatedHint",
                        "선택하면 각 프레임을 별도 파일로 저장합니다. 선택하지 않으면 첫 프레임만 변환합니다."),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                });
            }

            root.Children.Add(new TextBlock
            {
                Text = _getString(
                    "ImageConvertOutputSuffixHint",
                    "출력 파일은 원본과 같은 폴더에 _convert 접미사를 붙여 저장합니다."),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });

            var validationText = new TextBlock
            {
                Foreground = new SolidColorBrush(Colors.Red),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            root.Children.Add(validationText);

            bool TryReadDimension(NumberBox numberBox, out uint value)
            {
                value = 0;
                if (!IsValidNumber(numberBox.Value) ||
                    numberBox.Value < 1 ||
                    numberBox.Value > MaxDimension ||
                    Math.Abs(numberBox.Value - Math.Round(numberBox.Value)) > 0.0001)
                {
                    return false;
                }

                value = (uint)Math.Round(numberBox.Value);
                return true;
            }

            bool TryReadQuality(out int quality)
            {
                quality = 0;
                if (!IsValidNumber(qualityBox.Value) ||
                    qualityBox.Value < 1 ||
                    qualityBox.Value > 100 ||
                    Math.Abs(qualityBox.Value - Math.Round(qualityBox.Value)) > 0.0001)
                {
                    return false;
                }

                quality = (int)Math.Round(qualityBox.Value);
                return true;
            }

            bool Validate()
            {
                validationText.Visibility = Visibility.Collapsed;
                if (resizeCheck.IsChecked == true &&
                    (!TryReadDimension(widthBox, out _) || !TryReadDimension(heightBox, out _)))
                {
                    validationText.Text = _getString(
                        "ImageConvertInvalidSize",
                        "너비와 높이는 1 이상의 정수 픽셀 값이어야 합니다.");
                    validationText.Visibility = Visibility.Visible;
                    return false;
                }

                if (GetSelectedFormat(formatCombo) == ImageConversionOutputFormat.Jpeg &&
                    !TryReadQuality(out _))
                {
                    validationText.Text = _getString(
                        "ImageConvertInvalidQuality",
                        "JPG 화질은 1에서 100 사이의 정수여야 합니다.");
                    validationText.Visibility = Visibility.Visible;
                    return false;
                }

                return true;
            }

            var dialog = new ContentDialog
            {
                Title = string.Format(
                    _getString("ImageConvertDialogTitle", "이미지 변환 - {0}"),
                    Path.GetFileName(sourcePath)),
                Content = new ScrollViewer { Content = root },
                PrimaryButtonText = _getString("ImageConvertConvertButton", "변환"),
                CloseButtonText = _getString("ImageConvertCancelButton", "취소"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = theme
            };

            ImageConversionDialogValues? values = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!Validate())
                {
                    args.Cancel = true;
                    return;
                }

                uint? targetWidth = null;
                uint? targetHeight = null;
                if (resizeCheck.IsChecked == true)
                {
                    TryReadDimension(widthBox, out uint width);
                    TryReadDimension(heightBox, out uint height);
                    targetWidth = width;
                    targetHeight = height;
                }

                TryReadQuality(out int quality);
                values = new ImageConversionDialogValues(
                    GetSelectedFormat(formatCombo),
                    quality,
                    resizeCheck.IsChecked == true,
                    targetWidth,
                    targetHeight,
                    keepAspectCheck.IsChecked == true,
                    GetSelectedInterpolation(interpolationCombo),
                    extractFramesCheck?.IsChecked == true);
            };

            await dialog.ShowAsync();
            return values;
        }

        private async Task<ContentDialogResult> ShowConfirmationAsync(
            string title,
            string message,
            string primaryButtonText,
            XamlRoot xamlRoot,
            ElementTheme theme)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = _getString("ImageConvertCancelButton", "취소"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
                RequestedTheme = theme
            };
            return await dialog.ShowAsync();
        }

        private async Task ShowMessageAsync(
            string title,
            string message,
            XamlRoot xamlRoot,
            ElementTheme theme)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = _getString("ImageConvertCloseButton", "확인"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
                RequestedTheme = theme
            };
            await dialog.ShowAsync();
        }

        private ComboBox CreateInterpolationComboBox()
        {
            var comboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0
            };
            comboBox.Items.Add(CreateComboItem(
                _getString("ImageConvertInterpolationFant", "Fant (고품질)"),
                BitmapInterpolationMode.Fant));
            comboBox.Items.Add(CreateComboItem(
                _getString("ImageConvertInterpolationCubic", "Cubic (바이큐빅)"),
                BitmapInterpolationMode.Cubic));
            comboBox.Items.Add(CreateComboItem(
                _getString("ImageConvertInterpolationLinear", "Linear (바이리니어)"),
                BitmapInterpolationMode.Linear));
            comboBox.Items.Add(CreateComboItem(
                _getString("ImageConvertInterpolationNearest", "Nearest Neighbor (최근접)"),
                BitmapInterpolationMode.NearestNeighbor));
            return comboBox;
        }

        private static NumberBox CreateDimensionBox(uint value)
        {
            return new NumberBox
            {
                Value = value,
                Minimum = 1,
                Maximum = MaxDimension,
                SmallChange = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        private static StackPanel CreateField(string label, UIElement control)
        {
            var field = new StackPanel { Spacing = 4 };
            field.Children.Add(new TextBlock { Text = label });
            field.Children.Add(control);
            return field;
        }

        private static ComboBoxItem CreateComboItem(string text, object tag)
        {
            return new ComboBoxItem { Content = text, Tag = tag };
        }

        private static ImageConversionOutputFormat GetSelectedFormat(ComboBox comboBox)
        {
            return comboBox.SelectedItem is ComboBoxItem item &&
                   item.Tag is ImageConversionOutputFormat format
                ? format
                : ImageConversionOutputFormat.Png;
        }

        private static BitmapInterpolationMode GetSelectedInterpolation(ComboBox comboBox)
        {
            return comboBox.SelectedItem is ComboBoxItem item &&
                   item.Tag is BitmapInterpolationMode interpolation
                ? interpolation
                : BitmapInterpolationMode.Fant;
        }

        private static bool IsValidNumber(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsAnimatedImagePath(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record ImageConversionDialogValues(
            ImageConversionOutputFormat OutputFormat,
            int Quality,
            bool ResizeEnabled,
            uint? TargetWidth,
            uint? TargetHeight,
            bool KeepAspectRatio,
            BitmapInterpolationMode InterpolationMode,
            bool ExtractFrames);
    }
}
