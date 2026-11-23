// Ported from LiteParse crates/liteparse/src/ocr/ocria/img.rs, Apache-2.0.
namespace OcriaNano.DocumentExtraction.Imaging;

/// <summary>An 8-bit image with <see cref="Channels"/> interleaved channels per pixel (1 = grey, 3 = RGB).</summary>
internal sealed class Img
{
    public Img(int width, int height, int channels)
        : this(width, height, channels, new byte[width * height * channels])
    {
    }

    public Img(int width, int height, int channels, byte[] data)
    {
        Width = width;
        Height = height;
        Channels = channels;
        Data = data;
    }

    public int Width { get; }

    public int Height { get; }

    public int Channels { get; }

    public byte[] Data { get; }

    public static Img FromRgb(RgbImage image) => new(image.Width, image.Height, 3, image.Data);

    public byte At(int x, int y, int c) => Data[(((y * Width) + x) * Channels) + c];

    public Img Clone() => new(Width, Height, Channels, (byte[])Data.Clone());

    /// <summary><c>cv2.cvtColor(.., COLOR_RGB2GRAY)</c>: 14-bit fixed-point BT.601 weights.</summary>
    public Img ToGray()
    {
        if (Channels == 1)
        {
            return Clone();
        }

        Img output = new(Width, Height, 1);
        for (int i = 0, j = 0; i < output.Data.Length; i++, j += Channels)
        {
            uint v = (uint)((Data[j] * 4899u) + (Data[j + 1] * 9617u) + (Data[j + 2] * 1868u) + (1u << 13));
            output.Data[i] = (byte)(v >> 14);
        }

        return output;
    }

    /// <summary><c>img.mean(axis=2).astype(np.uint8)</c>: the channel mean, truncated.</summary>
    public Img MeanGray()
    {
        if (Channels == 1)
        {
            return Clone();
        }

        Img output = new(Width, Height, 1);
        for (int i = 0, j = 0; i < output.Data.Length; i++, j += Channels)
        {
            uint sum = 0;
            for (int c = 0; c < Channels; c++)
            {
                sum += Data[j + c];
            }

            output.Data[i] = (byte)(sum / (uint)Channels);
        }

        return output;
    }

    /// <summary><c>np.rot90(img, k)</c>: k quarter turns counter-clockwise.</summary>
    public Img Rot90(int k)
    {
        switch (((k % 4) + 4) % 4)
        {
            case 0:
                return Clone();
            case 1:
            {
                // out[i][j] = in[j][w - 1 - i]; out is h x w turned
                Img output = new(Height, Width, Channels);
                for (int i = 0; i < output.Height; i++)
                {
                    for (int j = 0; j < output.Width; j++)
                    {
                        int sx = Width - 1 - i, sy = j;
                        for (int c = 0; c < Channels; c++)
                        {
                            output.Data[(((i * output.Width) + j) * Channels) + c] = At(sx, sy, c);
                        }
                    }
                }

                return output;
            }

            case 2:
            {
                Img output = new(Width, Height, Channels);
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        for (int c = 0; c < Channels; c++)
                        {
                            output.Data[(((y * Width) + x) * Channels) + c] = At(Width - 1 - x, Height - 1 - y, c);
                        }
                    }
                }

                return output;
            }

            default:
                return Rot90(1).Rot90(2);
        }
    }
}
