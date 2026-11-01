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
        private const double InputHeight = 32;

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

        public Task<bool> ShowAsync(
            string sourcePath,
            XamlRoot? xamlRoot,
            ElementTheme theme)
        {
            return ShowAsync(new[] { sourcePath }, xamlRoot, theme);
        }

        public async Task<bool> ShowAsync(
            IReadOnlyList<string> sourcePaths,
            XamlRoot? xamlRoot,
            ElementTheme theme)
        {
            if (_isShowing || xamlRoot == null || sourcePaths == null)
            {
                return false;
            }

            IReadOnlyList<string> paths = sourcePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0)
            {
                return false;
            }

            if (theme == ElementTheme.Default)
            {
                theme = _getCurrentElementTheme();
            }

            _isShowing = true;
            try
            {
                var sourceInfos = new Dictionary<string, ImageConversionSourceInfo>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string path in paths)
                    {
                        sourceInfos[path] = await _imageConversionService.ReadSourceInfoAsync(path);
                    }
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync(
                        _getString("ImageConvertErrorTitle", "이미지 변환 오류"),
                        ex.Message,
                        xamlRoot,
                        theme);
                    return false;
                }

                ImageConversionSourceInfo sourceInfo = sourceInfos[paths[0]];
                bool isAnimated = sourceInfos.Any(pair =>
                    pair.Value.FrameCount > 1 && IsAnimatedImagePath(pair.Key));
                ImageConversionDialogValues? values = await ShowOptionsDialogAsync(
                    paths[0],
                    sourceInfo,
                    isAnimated,
                    xamlRoot,
                    theme,
                    paths.Count);
                if (values == null)
                {
                    return false;
                }

                var outputPaths = new List<string>();
                foreach (string path in paths)
                {
                    outputPaths.AddRange(ImageConversionService.BuildOutputPaths(
                        path,
                        values.OutputFormat,
                        values.ExtractFrames,
                        sourceInfos[path].FrameCount));
                }

                if (outputPaths.Count != outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                {
                    await ShowMessageAsync(
                        _getString("ImageConvertErrorTitle", "이미지 변환 오류"),
                        _getString(
                            "ImageConvertBatchOutputConflict",
                            "선택한 이미지의 출력 파일 이름이 서로 겹칩니다. 파일 이름을 확인한 후 다시 시도해 주세요."),
                        xamlRoot,
                        theme);
                    return false;
                }

                string[] existingPaths = outputPaths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(File.Exists)
                    .ToArray();
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
                        return false;
                    }
                }

                try
                {
                    var convertedPaths = new List<string>();
                    var options = new ImageConversionOptions
                    {
                        OutputFormat = values.OutputFormat,
                        Quality = values.Quality,
                        WebpLossless = values.WebpLossless,
                        ResizeEnabled = values.ResizeEnabled,
                        TargetWidth = values.TargetWidth,
                        TargetHeight = values.TargetHeight,
                        KeepAspectRatio = values.KeepAspectRatio,
                        InterpolationMode = values.InterpolationMode,
                        ExtractFrames = values.ExtractFrames,
                        CropRegion = values.CropRegion
                    };

                    foreach (string path in paths)
                    {
                        IReadOnlyList<string> converted = await _imageConversionService.ConvertAsync(
                            path,
                            options);
                        convertedPaths.AddRange(converted);
                    }

                    string firstOutputName = convertedPaths.Count > 0
                        ? Path.GetFileName(convertedPaths[0])
                        : string.Empty;
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
                    return true;
                }
                catch (Exception ex)
                {
                    await ShowMessageAsync(
                        _getString("ImageConvertErrorTitle", "이미지 변환 오류"),
                        ex.Message,
                        xamlRoot,
                        theme);
                    return false;
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
            ElementTheme theme,
            int sourceCount)
        {
            string sourceInfoText = sourceCount > 1
                ? string.Format(
                    _getString(
                        "ImageConvertBatchSourceInfo",
                        "선택된 이미지: {0}개\n기준 이미지: {1}\n크기: {2} × {3}px · 프레임: {4}"),
                    sourceCount,
                    Path.GetFileName(sourcePath),
                    sourceInfo.Width,
                    sourceInfo.Height,
                    sourceInfo.FrameCount)
                : string.Format(
                    _getString(
                        "ImageConvertSourceInfo",
                        "원본: {0}\n크기: {1} × {2}px · 프레임: {3}"),
                    Path.GetFileName(sourcePath),
                    sourceInfo.Width,
                    sourceInfo.Height,
                    sourceInfo.FrameCount);

            var root = new StackPanel
            {
                Spacing = 10,
                // Keep format labels and compression hints from resizing the options view.
                Width = Math.Max(0, Math.Min(480, xamlRoot.Size.Width - 112))
            };

            root.Children.Add(new TextBlock
            {
                Text = sourceInfoText,
                TextWrapping = TextWrapping.Wrap
            });

            var formatCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = InputHeight,
                MinHeight = InputHeight,
                MaxHeight = InputHeight,
                VerticalContentAlignment = VerticalAlignment.Center,
                SelectedIndex = 0
            };
            formatCombo.Items.Add(CreateComboItem(
                _getString("ImageConvertFormatPng", "PNG"),
                ImageConversionOutputFormat.Png));
            formatCombo.Items.Add(CreateComboItem(
                _getString("ImageConvertFormatJpg", "JPG"),
                ImageConversionOutputFormat.Jpeg));
            formatCombo.Items.Add(CreateComboItem(
                _getString("ImageConvertFormatWebp", "WebP"),
                ImageConversionOutputFormat.Webp));
            root.Children.Add(CreateField(
                _getString("ImageConvertFormat", "출력 형식"),
                formatCombo));

            var losslessCheck = new CheckBox
            {
                Content = _getString("ImageConvertWebpLossless", "무손실"),
                IsChecked = false,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = InputHeight,
                MinHeight = InputHeight
            };

            var qualityBox = new NumberBox
            {
                Value = 90,
                Minimum = 1,
                Maximum = 100,
                SmallChange = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = InputHeight,
                MinHeight = InputHeight,
                MaxHeight = InputHeight,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var qualityLabel = new TextBlock();
            var qualityPanel = new StackPanel { Spacing = 4 };
            qualityPanel.Children.Add(qualityLabel);
            qualityPanel.Children.Add(qualityBox);
            var qualityField = new ContentControl
            {
                Content = qualityPanel
            };
            var qualityGrid = new Grid { ColumnSpacing = 10 };
            qualityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            qualityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            qualityGrid.Children.Add(qualityField);
            Grid.SetColumn(losslessCheck, 1);
            qualityGrid.Children.Add(losslessCheck);
            root.Children.Add(qualityGrid);

            bool IsWebpLossless() => GetSelectedFormat(formatCombo) == ImageConversionOutputFormat.Webp &&
                losslessCheck.IsChecked == true;
            bool UsesCompressionStrength() => GetSelectedFormat(formatCombo) == ImageConversionOutputFormat.Png || IsWebpLossless();

            void UpdateQualityOptions()
            {
                losslessCheck.Visibility = GetSelectedFormat(formatCombo) == ImageConversionOutputFormat.Webp
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                qualityLabel.Text = UsesCompressionStrength()
                    ? _getString("ImageConvertCompressionStrength", "압축 강도 (1-100)")
                    : _getString("ImageConvertQuality", "JPG / WebP 화질 (1-100)");
            }

            formatCombo.SelectionChanged += (_, __) => UpdateQualityOptions();
            losslessCheck.Checked += (_, __) => UpdateQualityOptions();
            losslessCheck.Unchecked += (_, __) => UpdateQualityOptions();
            UpdateQualityOptions();

            var cropCheck = new CheckBox
            {
                Content = _getString("ImageConvertCrop", "크롭"),
                IsChecked = false
            };
            using var cropSelector = new ImageCropSelector(sourceInfo, _getString, sourceCount > 1);
            Func<Task>? selectCropAsync = null;
            root.Children.Add(cropCheck);
            root.Children.Add(cropSelector);

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
            uint aspectWidth = sourceInfo.Width;
            uint aspectHeight = sourceInfo.Height;
            void SyncHeightFromWidth()
            {
                if (syncingAspectRatio || keepAspectCheck.IsChecked != true || !IsValidNumber(widthBox.Value))
                {
                    return;
                }

                syncingAspectRatio = true;
                heightBox.Value = Math.Max(
                    1,
                    Math.Round(widthBox.Value * aspectHeight / aspectWidth));
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
                    Math.Round(heightBox.Value * aspectWidth / aspectHeight));
                syncingAspectRatio = false;
            }

            widthBox.ValueChanged += (_, __) => SyncHeightFromWidth();
            heightBox.ValueChanged += (_, __) => SyncWidthFromHeight();
            keepAspectCheck.Checked += (_, __) => SyncHeightFromWidth();
            resizeCheck.Checked += (_, __) => resizeOptions.IsEnabled = true;
            resizeCheck.Unchecked += (_, __) => resizeOptions.IsEnabled = false;

            void UpdateCropDimensions()
            {
                ImageCropBounds bounds = (cropCheck.IsChecked == true ? cropSelector.Region : ImageCropRegion.FullImage)
                    .GetPixelBounds(sourceInfo.Width, sourceInfo.Height);
                aspectWidth = bounds.Width;
                aspectHeight = bounds.Height;
                if (resizeCheck.IsChecked != true)
                {
                    syncingAspectRatio = true;
                    widthBox.Value = aspectWidth;
                    heightBox.Value = aspectHeight;
                    syncingAspectRatio = false;
                }
                else
                {
                    SyncHeightFromWidth();
                }
            }

            cropSelector.SelectionChanged += (_, __) => UpdateCropDimensions();
            cropCheck.Checked += async (_, __) =>
            {
                cropSelector.Visibility = Visibility.Visible;
                if (selectCropAsync != null)
                {
                    await selectCropAsync();
                }
            };
            cropSelector.SelectionRequested += async (_, __) =>
            {
                if (selectCropAsync != null)
                {
                    await selectCropAsync();
                }
            };
            cropCheck.Unchecked += (_, __) =>
            {
                cropSelector.CancelSelection();
                cropSelector.Visibility = Visibility.Collapsed;
                UpdateCropDimensions();
            };

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
                if (cropCheck.IsChecked == true && !cropSelector.HasSelection)
                {
                    validationText.Text = _getString("ImageConvertCropNotReady", "미리보기를 불러온 뒤 영역 선택을 완료해 주세요.");
                    validationText.Visibility = Visibility.Visible;
                    return false;
                }

                if (resizeCheck.IsChecked == true &&
                    (!TryReadDimension(widthBox, out _) || !TryReadDimension(heightBox, out _)))
                {
                    validationText.Text = _getString(
                        "ImageConvertInvalidSize",
                        "너비와 높이는 1 이상의 정수 픽셀 값이어야 합니다.");
                    validationText.Visibility = Visibility.Visible;
                    return false;
                }

                if (!TryReadQuality(out _))
                {
                    validationText.Text = UsesCompressionStrength()
                        ? _getString("ImageConvertInvalidCompressionStrength", "압축 강도는 1에서 100 사이의 정수여야 합니다.")
                        : _getString("ImageConvertInvalidQuality", "JPG / WebP 화질은 1에서 100 사이의 정수여야 합니다.");
                    validationText.Visibility = Visibility.Visible;
                    return false;
                }

                return true;
            }

            var optionsContent = new ScrollViewer { Content = root };
            FrameworkElement selectionContent = cropSelector.SelectionContent;
            selectionContent.Visibility = Visibility.Collapsed;
            var dialogContent = new Grid();
            dialogContent.Children.Add(optionsContent);
            dialogContent.Children.Add(selectionContent);

            var dialog = new ContentDialog
            {
                Title = sourceCount > 1
                    ? string.Format(
                        _getString("ImageConvertBatchDialogTitle", "이미지 일괄 변환 - {0}개"),
                        sourceCount)
                    : string.Format(
                        _getString("ImageConvertDialogTitle", "이미지 변환 - {0}"),
                        Path.GetFileName(sourcePath)),
                Content = dialogContent,
                PrimaryButtonText = _getString("ImageConvertConvertButton", "변환"),
                CloseButtonText = _getString("ImageConvertCancelButton", "취소"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = theme
            };

            // Keep both views attached; changing Content during a button event can invalidate WinUI focus state.
            dialog.Resources["ContentDialogMaxWidth"] = Math.Max(240, xamlRoot.Size.Width - 32);
            dialog.Resources["ContentDialogMaxHeight"] = Math.Max(240, xamlRoot.Size.Height - 32);
            object optionsTitle = dialog.Title;
            bool selectingCrop = false;
            bool applyingCrop = false;
            bool returningToOptions = false;
            bool dialogClosed = false;
            int selectionRevision = 0;

            void ReturnToOptions(bool cancelSelection)
            {
                if (returningToOptions || dialogClosed)
                {
                    return;
                }
                returningToOptions = true;
                selectionRevision++;
                dialog.IsPrimaryButtonEnabled = false;
                // Let the current close/click event finish before changing focusable controls.
                if (!dialog.DispatcherQueue.TryEnqueue(() =>
                {
                    if (dialogClosed)
                    {
                        return;
                    }
                    try
                    {
                        if (cancelSelection)
                        {
                            cropSelector.RevertSelection();
                        }
                        if (cancelSelection || !cropSelector.HasSelection)
                        {
                            cropCheck.IsChecked = false;
                        }
                        selectionContent.Visibility = Visibility.Collapsed;
                        optionsContent.Visibility = Visibility.Visible;
                        selectingCrop = false;
                        dialog.Title = optionsTitle;
                        dialog.PrimaryButtonText = _getString("ImageConvertConvertButton", "변환");
                        dialog.IsPrimaryButtonEnabled = true;
                        UpdateCropDimensions();
                    }
                    finally
                    {
                        returningToOptions = false;
                    }
                }))
                {
                    returningToOptions = false;
                }
            }

            selectCropAsync = async () =>
            {
                if (selectingCrop || returningToOptions || dialogClosed)
                {
                    return;
                }
                selectingCrop = true;
                int revision = ++selectionRevision;
                validationText.Visibility = Visibility.Collapsed;
                cropSelector.BeginSelection(xamlRoot.Size);
                optionsContent.Visibility = Visibility.Collapsed;
                selectionContent.Visibility = Visibility.Visible;
                dialog.Title = _getString("ImageConvertCropTitle", "크롭 영역 선택");
                dialog.PrimaryButtonText = _getString("ImageConvertCropApply", "선택 완료");
                dialog.IsPrimaryButtonEnabled = false;
                await cropSelector.LoadAsync(sourcePath, _imageConversionService);
                if (selectingCrop && !returningToOptions && !dialogClosed && revision == selectionRevision)
                {
                    dialog.IsPrimaryButtonEnabled = cropSelector.IsReady;
                }
            };

            dialog.CloseButtonClick += (_, args) =>
            {
                if (selectingCrop)
                {
                    args.Cancel = true;
                    if (!applyingCrop)
                    {
                        ReturnToOptions(cancelSelection: true);
                    }
                }
            };
            dialog.Closing += (_, args) =>
            {
                if (selectingCrop)
                {
                    args.Cancel = true;
                    if (!applyingCrop)
                    {
                        ReturnToOptions(cancelSelection: true);
                    }
                }
            };

            ImageConversionDialogValues? values = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                if (selectingCrop)
                {
                    args.Cancel = true;
                    if (applyingCrop || returningToOptions || !cropSelector.IsReady || cropSelector.IsSelecting)
                    {
                        return;
                    }
                    var deferral = args.GetDeferral();
                    applyingCrop = true;
                    dialog.IsPrimaryButtonEnabled = false;
                    try
                    {
                        if (await cropSelector.ApplySelectionAsync())
                        {
                            ReturnToOptions(cancelSelection: false);
                        }
                    }
                    finally
                    {
                        applyingCrop = false;
                        if (!returningToOptions && !dialogClosed)
                        {
                            dialog.IsPrimaryButtonEnabled = true;
                        }
                        deferral.Complete();
                    }
                    return;
                }

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
                    IsWebpLossless(),
                    resizeCheck.IsChecked == true,
                    targetWidth,
                    targetHeight,
                    keepAspectCheck.IsChecked == true,
                    GetSelectedInterpolation(interpolationCombo),
                    extractFramesCheck?.IsChecked == true,
                    cropCheck.IsChecked == true ? cropSelector.Region : null);
            };

            void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
            {
                root.Width = Math.Max(0, Math.Min(480, xamlRoot.Size.Width - 112));
                dialog.Resources["ContentDialogMaxWidth"] = Math.Max(240, xamlRoot.Size.Width - 32);
                dialog.Resources["ContentDialogMaxHeight"] = Math.Max(240, xamlRoot.Size.Height - 32);
                if (selectingCrop)
                {
                    cropSelector.UpdateSelectionSize(xamlRoot.Size);
                }
            }

            xamlRoot.Changed += OnRootChanged;
            try
            {
                await dialog.ShowAsync();
                return values;
            }
            finally
            {
                dialogClosed = true;
                selectionRevision++;
                xamlRoot.Changed -= OnRootChanged;
            }
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
                Height = InputHeight,
                MinHeight = InputHeight,
                MaxHeight = InputHeight,
                VerticalContentAlignment = VerticalAlignment.Center,
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
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = InputHeight,
                MinHeight = InputHeight,
                MaxHeight = InputHeight,
                VerticalContentAlignment = VerticalAlignment.Center
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
            bool WebpLossless,
            bool ResizeEnabled,
            uint? TargetWidth,
            uint? TargetHeight,
            bool KeepAspectRatio,
            BitmapInterpolationMode InterpolationMode,
            bool ExtractFrames,
            ImageCropRegion? CropRegion);
    }
}
