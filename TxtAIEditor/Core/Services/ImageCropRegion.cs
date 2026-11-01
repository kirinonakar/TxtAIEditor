using System;

namespace TxtAIEditor.Core.Services
{
    // Relative coordinates let a selection apply to images of different sizes in a batch.
    public readonly record struct ImageCropRegion(double Left, double Top, double Right, double Bottom)
    {
        public static ImageCropRegion FullImage => new(0, 0, 1, 1);

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

            uint x = Math.Min(width - 1, (uint)Math.Floor(Left * width));
            uint y = Math.Min(height - 1, (uint)Math.Floor(Top * height));
            uint right = Math.Clamp((uint)Math.Ceiling(Right * width), x + 1, width);
            uint bottom = Math.Clamp((uint)Math.Ceiling(Bottom * height), y + 1, height);
            return new ImageCropBounds(x, y, right - x, bottom - y);
        }
    }

    public readonly record struct ImageCropBounds(uint X, uint Y, uint Width, uint Height);
}
