using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A scrolling line chart drawn directly as a uGUI mesh - no textures, no line
/// renderers, no extra cameras - so it lives on a world-space canvas and costs one
/// small mesh rebuild per sample on Quest.
///
/// Samples fill from the left; once the buffer is full the trace scrolls, oldest
/// value at the left edge, newest at the right. Callers push samples at a fixed rate
/// so the x axis is a fixed time window.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class TrendGraph : MaskableGraphic
{
    [Tooltip("Number of samples across the full width.")]
    public int capacity = 240;

    [Header("Vertical range")]
    public float minValue = 0f;
    public float maxValue = 1f;
    [Tooltip("Fit the range to the samples on screen instead of using min/max above.")]
    public bool autoRange;
    [Tooltip("Smallest span auto-range will zoom to, so a flat signal doesn't blow up noise.")]
    public float minAutoSpan = 1f;
    [Tooltip("Fraction of the span added above and below the data in auto-range mode.")]
    public float autoRangePadding = 0.12f;

    [Header("Look")]
    public float lineThickness = 5f;
    public Color gridColor = new Color(1f, 1f, 1f, 0.10f);
    [Tooltip("Horizontal grid lines, including top and bottom edges.")]
    public int gridLines = 4;

    float[] samples;
    int head;   // index the next sample is written to
    int count;

    /// <summary>The range the trace was last drawn with (useful for axis labels).</summary>
    public float DrawnMin { get; private set; }
    public float DrawnMax { get; private set; }

    public void AddSample(float value)
    {
        EnsureBuffer();
        samples[head] = value;
        head = (head + 1) % samples.Length;
        if (count < samples.Length) count++;
        SetVerticesDirty();
    }

    public void Clear()
    {
        count = 0;
        head = 0;
        SetVerticesDirty();
    }

    void EnsureBuffer()
    {
        int n = Mathf.Max(2, capacity);
        if (samples == null || samples.Length != n)
        {
            samples = new float[n];
            head = 0;
            count = 0;
        }
    }

    // i = 0 is the oldest sample currently held.
    float Sample(int i)
    {
        int start = (head - count + samples.Length) % samples.Length;
        return samples[(start + i) % samples.Length];
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = GetPixelAdjustedRect();

        Color32 grid = gridColor;
        int lines = Mathf.Max(2, gridLines);
        for (int i = 0; i < lines; i++)
        {
            float y = r.yMin + r.height * i / (lines - 1);
            AddSegment(vh, new Vector2(r.xMin, y), new Vector2(r.xMax, y), 2f, grid);
        }

        if (samples == null || count < 2)
            return;

        float lo = minValue, hi = maxValue;
        if (autoRange)
        {
            lo = float.MaxValue;
            hi = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float v = Sample(i);
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            float span = Mathf.Max(hi - lo, minAutoSpan);
            float mid = (hi + lo) * 0.5f;
            lo = mid - span * (0.5f + autoRangePadding);
            hi = mid + span * (0.5f + autoRangePadding);
        }
        if (hi - lo < 1e-5f) hi = lo + 1e-5f;
        DrawnMin = lo;
        DrawnMax = hi;

        Color32 line = color;
        float dx = r.width / (Mathf.Max(2, capacity) - 1);
        Vector2 prev = Point(r, 0, dx, Sample(0), lo, hi);
        for (int i = 1; i < count; i++)
        {
            Vector2 p = Point(r, i, dx, Sample(i), lo, hi);
            AddSegment(vh, prev, p, lineThickness, line);
            prev = p;
        }
    }

    static Vector2 Point(Rect r, int i, float dx, float v, float lo, float hi)
    {
        float t = Mathf.Clamp01((v - lo) / (hi - lo));
        return new Vector2(r.xMin + i * dx, r.yMin + t * r.height);
    }

    // A quad from a to b, extended by half the thickness at each end so consecutive
    // segments overlap at the joints instead of leaving notches on steep slopes.
    static void AddSegment(VertexHelper vh, Vector2 a, Vector2 b, float thickness, Color32 c)
    {
        Vector2 dir = b - a;
        if (dir.sqrMagnitude < 1e-8f)
            return;
        dir.Normalize();
        float h = thickness * 0.5f;
        a -= dir * h;
        b += dir * h;
        Vector2 n = new Vector2(-dir.y, dir.x) * h;

        int idx = vh.currentVertCount;
        vh.AddVert(a - n, c, Vector2.zero);
        vh.AddVert(a + n, c, Vector2.zero);
        vh.AddVert(b + n, c, Vector2.zero);
        vh.AddVert(b - n, c, Vector2.zero);
        vh.AddTriangle(idx, idx + 1, idx + 2);
        vh.AddTriangle(idx + 2, idx + 3, idx);
    }
}
