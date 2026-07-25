using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficePresentationRenderingUtilities;

namespace TxtAIEditor.Core.Services
{
    internal sealed class OfficePresentationDocumentHtmlRenderer
    {
        private const double PresentationBaseWidthPx = 960;

        public static async Task<string> BuildAsync(
            string filePath,
            Func<string, string, string> getString)
        {
            using ZipArchive archive =
                await OfficePresentationPackageReader.OpenArchiveAsync(filePath)
                    .ConfigureAwait(false);
            XDocument? presentation =
                await OfficePresentationPackageReader.TryLoadXmlEntryAsync(
                    archive,
                    "ppt/presentation.xml")
                    .ConfigureAwait(false);
            if (presentation == null)
            {
                return BuildErrorHtml(getString(
                    "OfficeViewerPptxStructureError",
                    "Could not read the PPTX presentation structure."));
            }

            (long slideWidth, long slideHeight) =
                OfficePresentationPackageReader.ReadSlideSize(presentation);
            IReadOnlyList<string> themeColors =
                await OfficePresentationPackageReader.LoadThemeColorsAsync(archive)
                    .ConfigureAwait(false);
            List<string> slidePaths =
                await OfficePresentationPackageReader.ReadSlidePathsAsync(
                    archive,
                    presentation)
                    .ConfigureAwait(false);
            if (slidePaths.Count == 0)
            {
                return BuildErrorHtml(getString(
                    "OfficeViewerNoSlides",
                    "No slides to display."));
            }

            var slides = new StringBuilder();
            for (int index = 0; index < slidePaths.Count; index++)
            {
                string? slideHtml =
                    await OfficePresentationSlideHtmlRenderer.BuildAsync(
                        archive,
                        slidePaths[index],
                        index + 1,
                        slidePaths.Count,
                        slideWidth,
                        slideHeight,
                        PresentationBaseWidthPx,
                        themeColors)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(slideHtml))
                {
                    slides.Append(slideHtml);
                }
            }

            if (slides.Length == 0)
            {
                return BuildErrorHtml(getString(
                    "OfficeViewerSlideRenderError",
                    "Could not render any slides."));
            }

            return BuildDocumentHtml(filePath, slides);
        }

        private static string BuildDocumentHtml(string filePath, StringBuilder slides)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{Html(Path.GetFileName(filePath))}}</title>
<style>
:root {
    color-scheme: light dark;
    --app-bg: #f3f4f6;
    --slide-shadow: 0 16px 44px rgba(15, 23, 42, .18);
    --text: #111827;
    --muted: #667085;
}
@media (prefers-color-scheme: dark) {
    :root {
        --app-bg: #17181c;
        --slide-shadow: 0 18px 44px rgba(0, 0, 0, .42);
        --text: #f3f4f6;
        --muted: #a6adbb;
    }
}
* { box-sizing: border-box; }
html, body { margin: 0; min-height: 100%; background: var(--app-bg); color: var(--text); font-family: "Segoe UI", Arial, sans-serif; }
body { padding: 28px 16px 40px; }
.deck { display: flex; flex-direction: column; align-items: center; gap: 26px; }
.slide {
    position: relative;
    width: min(1120px, calc(100vw - 32px));
    aspect-ratio: var(--slide-ratio);
    overflow: hidden;
    box-shadow: var(--slide-shadow);
    border: 1px solid rgba(148, 163, 184, .35);
}
.slide-canvas {
    position: absolute;
    inset: 0 auto auto 0;
    width: var(--base-width-px);
    height: var(--base-height-px);
    transform-origin: top left;
}
.slide-number {
    position: absolute;
    right: 12px;
    bottom: 9px;
    z-index: 10;
    color: var(--muted);
    font: 12px/1.2 "Segoe UI", Arial, sans-serif;
    background: rgba(255, 255, 255, .72);
    border-radius: 999px;
    padding: 4px 8px;
}
@media (prefers-color-scheme: dark) {
    .slide-number { background: rgba(17, 24, 39, .66); }
}
.ppt-shape, .ppt-image, .ppt-table, .ppt-chart {
    position: absolute;
    overflow: hidden;
    transform-origin: center center;
}
.ppt-shape {
    display: block;
    color: #111827;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    padding: 0;
    line-height: 1.16;
}
.ppt-text {
    display: block;
    width: 100%;
    transform-origin: top left;
}
.ppt-shape p { width: 100%; margin: 0 0 .24em; line-height: inherit; }
.ppt-shape p:last-child { margin-bottom: 0; }
.ppt-shape span { white-space: pre-wrap; }
.ppt-box { padding: 0; }
.ppt-image img {
    width: 100%;
    height: 100%;
    object-fit: fill;
    display: block;
}
.ppt-chart {
    background: #fff;
}
.ppt-chart svg {
    display: block;
    width: 100%;
    height: 100%;
}
.ppt-table table {
    width: 100%;
    height: 100%;
    border-collapse: collapse;
    table-layout: fixed;
    background: rgba(255, 255, 255, .88);
    color: #111827;
}
.ppt-table td {
    border: 1px solid rgba(31, 41, 55, .28);
    padding: .32em .45em;
    vertical-align: top;
    overflow-wrap: anywhere;
}
.ppt-table p {
    margin: 0 0 .2em;
    line-height: 1.16;
}
.ppt-table p:last-child { margin-bottom: 0; }
</style>
</head>
<body>
<main class="deck">
{{slides}}
</main>
<script>
function fitTextBox(box) {
    const text = box.querySelector(':scope > .ppt-text');
    if (!text) return;

    text.style.transform = '';
    text.style.width = '100%';
    text.style.height = 'auto';

    const availableWidth = Math.max(1, box.clientWidth);
    const availableHeight = Math.max(1, box.clientHeight);
    const neededWidth = Math.max(1, text.scrollWidth);
    const neededHeight = Math.max(1, text.scrollHeight);
    const scale = Math.min(1, availableWidth / neededWidth, availableHeight / neededHeight);

    if (scale < 0.995) {
        const fitScale = Math.max(0.45, scale * 0.985);
        text.style.width = `${100 / fitScale}%`;
        text.style.transform = `scale(${fitScale})`;
    }
}
function fitSlide(slide) {
    const canvas = slide.querySelector('.slide-canvas');
    if (!canvas) return;
    const baseWidth = parseFloat(getComputedStyle(slide).getPropertyValue('--base-width')) || 960;
    canvas.style.transform = `scale(${slide.clientWidth / baseWidth})`;
    slide.querySelectorAll('.ppt-shape').forEach(fitTextBox);
}
const observer = new ResizeObserver(entries => {
    for (const entry of entries) fitSlide(entry.target);
});
document.querySelectorAll('.slide').forEach(slide => {
    fitSlide(slide);
    observer.observe(slide);
});
if (document.fonts && document.fonts.ready) {
    document.fonts.ready.then(() => document.querySelectorAll('.slide').forEach(fitSlide));
}
</script>
</body>
</html>
""";
        }

        private static string BuildErrorHtml(string message)
        {
            return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
html, body { margin: 0; height: 100%; font-family: "Segoe UI", Arial, sans-serif; color-scheme: light dark; }
body { display: grid; place-items: center; background: Canvas; color: CanvasText; }
.message { max-width: 520px; padding: 24px; border: 1px solid color-mix(in srgb, CanvasText 18%, transparent); border-radius: 8px; }
</style>
</head>
<body><div class="message">{{Html(message)}}</div></body>
</html>
""";
        }
    }
}
