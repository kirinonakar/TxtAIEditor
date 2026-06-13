using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TxtAIEditor.Core.Services
{
    internal sealed class SettingsAboutPanel : UserControl
    {
        private readonly TextBlock _titleText;
        private readonly TextBlock _descText;
        private readonly TextBlock _copyrightText;

        public SettingsAboutPanel(Func<string, string, string> getString)
        {
            var section = SettingsDialogUi.CreateSection();
            section.HorizontalAlignment = HorizontalAlignment.Stretch;
            section.Spacing = 6;
            section.Padding = new Thickness(16, 8, 16, 12);

            section.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/TxtAIEditor.png")),
                Width = 72,
                Height = 72,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 2)
            });

            _titleText = new TextBlock
            {
                Text = $"TxtAIEditor (v{SettingsAppVersionProvider.GetAppVersion()})",
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            section.Children.Add(_titleText);

            _descText = new TextBlock
            {
                Text = getString("SettingsAboutDescription", "강력하고 가벼운 텍스트 및 마크다운 에디터입니다.\n실시간 미리보기, 코드 및 수식 템플릿, 터미널 인터페이스, Git 통합, AI Assistant 등을 지원합니다."),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                Margin = new Thickness(0, 0, 0, 4)
            };
            section.Children.Add(_descText);

            section.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(20, 2, 20, 4)
            });

            section.Children.Add(new TextBlock
            {
                Text = getString("SettingsAboutProjectGitHub", "Project GitHub"),
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            section.Children.Add(new HyperlinkButton
            {
                Content = "https://github.com/kirinonakar/TxtAIEditor",
                NavigateUri = new Uri("https://github.com/kirinonakar/TxtAIEditor"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 0, 2)
            });
            section.Children.Add(CreateThirdPartyNoticesButton(getString));

            _copyrightText = new TextBlock
            {
                Text = "Copyright © 2026 kirinonakar. All rights reserved.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            section.Children.Add(_copyrightText);
            Content = section;
        }

        public void RestoreCustomFontSizes()
        {
            _titleText.FontSize = 17;
            _descText.FontSize = 10.5;
            _copyrightText.FontSize = 9.5;
        }

        private static Button CreateThirdPartyNoticesButton(Func<string, string, string> getString)
        {
            var button = new Button
            {
                Content = getString("SettingsAboutThirdPartyNotices", "오픈소스 라이선스 고지 (Third-Party Notices)"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };

            button.Flyout = new Flyout
            {
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = ThirdPartyNoticesText,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 10.5,
                        FontFamily = new FontFamily("Consolas")
                    },
                    MaxHeight = 280,
                    Width = 380
                }
            };

            return button;
        }

        private const string ThirdPartyNoticesText = @"This software includes Mermaid, xterm.js, and KaTeX.

Mermaid
License: MIT License
Copyright (c) 2014 - 2022 Knut Sveidqvist

xterm.js
License: MIT License
Copyright (c) 2017-2019, The xterm.js authors
Copyright (c) 2014-2016, SourceLair Private Company
Copyright (c) 2012-2013, Christopher Jeffrey

KaTeX
License: MIT License
Copyright (c) 2013-2020 Khan Academy and other contributors

The MIT License (MIT)

The MIT license text below applies to the third-party components listed above.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.";
    }
}
