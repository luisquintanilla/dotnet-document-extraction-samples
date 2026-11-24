// Port of LiteParse crates/liteparse/src/table_model/units.rs (Unit), Apache-2.0.
namespace OcriaNano.DocumentExtraction.Layout;

/// <summary>A box as (x0, y0, x1, y1), LiteParse's <c>[f64; 4]</c>. Indexable so ported code reads like the original.</summary>
internal readonly record struct Box4(double X0, double Y0, double X1, double Y1)
{
    public double this[int i] => i switch
    {
        0 => X0,
        1 => Y0,
        2 => X1,
        3 => Y1,
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };

    public double Width => X1 - X0;

    public double Height => Y1 - Y0;

    public static Box4 Empty { get; } = new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public Box4 Union(Box4 o) => new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
}

/// <summary>
/// A text unit LayoutNet sees: a run of characters on one line, split at cell-sized gaps (LiteParse's <c>Unit</c>).
/// Boxes are in PDF points with a top-left origin, on the page as rendered (after <c>/Rotate</c>).
/// </summary>
internal sealed class Unit
{
    /// <summary>Box in the unit's reading frame (LiteParse's <c>to_frame</c>).</summary>
    public Box4 Bx { get; set; }

    /// <summary>Box on the rendered page.</summary>
    public Box4 PBox { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Line height in points.</summary>
    public double H { get; set; }

    /// <summary>Font size in points (0 for OCR units).</summary>
    public double Size { get; set; }

    public bool? Bold { get; set; }

    /// <summary>Reading direction on the page: right, up, left, down = 0..3.</summary>
    public byte Q { get; set; }

    /// <summary>Runs against the page's main direction (a sidebar, a rotated label).</summary>
    public bool Minor { get; set; }

    /// <summary>Words as (x0, x1, text) in the reading frame.</summary>
    public List<(double X0, double X1, string Text)> Words { get; set; } = [];

    /// <summary>Where the unit came from: native PDF text, or OCR (for confidence and source polygons).</summary>
    public bool FromOcr { get; set; }

    public Unit Clone()
    {
        Unit u = (Unit)MemberwiseClone();
        u.Words = [.. Words];
        return u;
    }
}
