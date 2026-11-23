namespace OcriaNano.DocumentExtraction.Imaging;

/// <summary>An 8-bit single-channel image, row-major, no padding.</summary>
internal sealed class GrayImage
{
    public GrayImage(int width, int height, byte[]? data = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height];
        if (Data.Length != width * height)
        {
            throw new ArgumentException("Pixel buffer doesn't match the size.", nameof(data));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Data { get; }

    public byte this[int x, int y]
    {
        get => Data[(y * Width) + x];
        set => Data[(y * Width) + x] = value;
    }

    public Span<byte> Row(int y) => Data.AsSpan(y * Width, Width);
}

/// <summary>An 8-bit RGB image (R, G, B per pixel), row-major, no padding.</summary>
internal sealed class RgbImage
{
    public RgbImage(int width, int height, byte[]? data = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 3];
        if (Data.Length != width * height * 3)
        {
            throw new ArgumentException("Pixel buffer doesn't match the size.", nameof(data));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Data { get; }

    /// <summary>
    /// Grey via OpenCV's fixed-point <c>RGB2GRAY</c> (the coefficients LiteParse's <c>img::to_gray</c> uses):
    /// <c>(R*4899 + G*9617 + B*1868 + 8192) &gt;&gt; 14</c>.
    /// </summary>
    public GrayImage ToGray()
    {
        GrayImage grey = new(Width, Height);
        byte[] src = Data;
        byte[] dst = grey.Data;
        for (int i = 0, j = 0; i < dst.Length; i++, j += 3)
        {
            dst[i] = (byte)(((src[j] * 4899) + (src[j + 1] * 9617) + (src[j + 2] * 1868) + 8192) >> 14);
        }

        return grey;
    }

    public static RgbImage FromGray(GrayImage grey)
    {
        RgbImage rgb = new(grey.Width, grey.Height);
        byte[] src = grey.Data;
        byte[] dst = rgb.Data;
        for (int i = 0, j = 0; i < src.Length; i++, j += 3)
        {
            dst[j] = dst[j + 1] = dst[j + 2] = src[i];
        }

        return rgb;
    }
}

/// <summary>A 32-bit B, G, R, A image as PDFium/SkiaSharp produce on little-endian machines, row-major, no padding.</summary>
internal sealed class Bgra32Image
{
    public Bgra32Image(int width, int height, byte[]? data = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height * 4];
        if (Data.Length != width * height * 4)
        {
            throw new ArgumentException("Pixel buffer doesn't match the size.", nameof(data));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Data { get; }

    /// <summary>Converts to RGB, compositing any transparency over white.</summary>
    public RgbImage ToRgb()
    {
        RgbImage rgb = new(Width, Height);
        byte[] src = Data;
        byte[] dst = rgb.Data;
        for (int i = 0, j = 0; j < dst.Length; i += 4, j += 3)
        {
            int a = src[i + 3];
            if (a == 255)
            {
                dst[j] = src[i + 2];
                dst[j + 1] = src[i + 1];
                dst[j + 2] = src[i];
            }
            else
            {
                int white = 255 * (255 - a);
                dst[j] = (byte)(((src[i + 2] * a) + white + 127) / 255);
                dst[j + 1] = (byte)(((src[i + 1] * a) + white + 127) / 255);
                dst[j + 2] = (byte)(((src[i] * a) + white + 127) / 255);
            }
        }

        return rgb;
    }
}
