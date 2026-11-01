using System;

namespace TxtAIEditor.Core.Services
{
    public enum ImageCropCorner
    {
        TopLeft,
        TopRight,
        BottomRight,
        BottomLeft
    }

    // Relative coordinates let a selection apply to images of different sizes in a batch.
    public readonly record struct ImageCropRegion(double Left, double Top, double Right, double Bottom)
    {
        public static ImageCropRegion FullImage => new(0, 0, 1, 1);

        public ImageCropRegion Move(double offsetX, double offsetY, uint imageWidth, uint imageHeight)
        {
            ImageCropBounds bounds = GetPixelBounds(imageWidth, imageHeight);
            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            {
                throw new ArgumentOutOfRangeException(nameof(offsetX));
            }
            // Move whole pixels so rounding the selection cannot change its saved dimensions.
            double x = Math.Clamp(Math.Round(bounds.X + offsetX * imageWidth), 0, imageWidth - bounds.Width);
            double y = Math.Clamp(Math.Round(bounds.Y + offsetY * imageHeight), 0, imageHeight - bounds.Height);
            return FromPixelEdges(x, y, x + bounds.Width, y + bounds.Height, imageWidth, imageHeight);
        }

        public ImageCropRegion ResizeCorner(ImageCropCorner corner, double offsetX, double offsetY, uint imageWidth, uint imageHeight)
        {
            ImageCropBounds bounds = GetPixelBounds(imageWidth, imageHeight);
            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            {
                throw new ArgumentOutOfRangeException(nameof(offsetX));
            }
            // Keep the opposite corner fixed and prevent the selected area from inverting.
            double left = bounds.X, top = bounds.Y;
            double right = bounds.X + bounds.Width, bottom = bounds.Y + bounds.Height;
            return corner switch
            {
                ImageCropCorner.TopLeft => FromPixelEdges(Math.Clamp(Math.Round(left + offsetX * imageWidth), 0, right - 1),
                    Math.Clamp(Math.Round(top + offsetY * imageHeight), 0, bottom - 1), right, bottom, imageWidth, imageHeight),
                ImageCropCorner.TopRight => FromPixelEdges(left, Math.Clamp(Math.Round(top + offsetY * imageHeight), 0, bottom - 1),
                    Math.Clamp(Math.Round(right + offsetX * imageWidth), left + 1, imageWidth), bottom, imageWidth, imageHeight),
                ImageCropCorner.BottomRight => FromPixelEdges(left, top, Math.Clamp(Math.Round(right + offsetX * imageWidth), left + 1, imageWidth),
                    Math.Clamp(Math.Round(bottom + offsetY * imageHeight), top + 1, imageHeight), imageWidth, imageHeight),
                ImageCropCorner.BottomLeft => FromPixelEdges(Math.Clamp(Math.Round(left + offsetX * imageWidth), 0, right - 1), top,
                    right, Math.Clamp(Math.Round(bottom + offsetY * imageHeight), top + 1, imageHeight), imageWidth, imageHeight),
                _ => throw new ArgumentOutOfRangeException(nameof(corner))
            };
        }

        public ImageCropBounds GetPixelBounds(uint width, uint height)
        {
            if (width == 0 || height == 0 ||
                !double.IsFinite(Left) || !double.IsFinite(Top) ||
                !double.IsFinite(Right) || !double.IsFinite(Bottom) ||
                Left < 0 || Top < 0 || Right > 1 || Bottom > 1 ||
                Right <= Left || Bottom <= Top)
            {
                throw new ArgumentOutOfRangeException(nameof(ImageCropRegion));
            }

            uint x = Math.Min(width - 1, (uint)Math.Floor(SnapPixelEdge(Left * width)));
            uint y = Math.Min(height - 1, (uint)Math.Floor(SnapPixelEdge(Top * height)));
            uint right = Math.Clamp((uint)Math.Ceiling(SnapPixelEdge(Right * width)), x + 1, width);
            uint bottom = Math.Clamp((uint)Math.Ceiling(SnapPixelEdge(Bottom * height)), y + 1, height);
            return new ImageCropBounds(x, y, right - x, bottom - y);
        }

        private static ImageCropRegion FromPixelEdges(double left, double top, double right, double bottom, uint width, uint height) =>
            new(left / width, top / height, right / width, bottom / height);

        private static double SnapPixelEdge(double value)
        {
            double rounded = Math.Round(value);
            // Normalizing an integer pixel coordinate and scaling it back can introduce floating-point noise.
            return Math.Abs(value - rounded) < 0.0000001 ? rounded : value;
        }
    }

    public readonly record struct ImageCropBounds(uint X, uint Y, uint Width, uint Height);
}
