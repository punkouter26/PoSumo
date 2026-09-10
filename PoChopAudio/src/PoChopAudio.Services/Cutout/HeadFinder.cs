namespace PoChopAudio.Services.Cutout;

/// <summary>Head-only crop rectangle, in pixels, within the source image.</summary>
public sealed record HeadBounds(int X, int Y, int Width, int Height, bool Empty);

/// <summary>
/// Finds the head inside a cutout mask.
///
/// u2netp is a saliency model, not a face model — it marks the whole person, so the alpha bounding
/// box always includes shoulders and torso. This narrows that to the head by reading the shape of
/// the mask: measure how wide the subject is on every row, and the shoulders announce themselves as
/// a sharp, sustained widening below the narrow band of the neck. Cut there.
///
/// It needs no second model and no face detector, which matters because the same logic has to run
/// in the browser, where photographs of a face must never be uploaded.
/// </summary>
public static class HeadFinder
{
    /// <summary>
    /// The neck must be at least this much narrower than the head to count as a neck.
    ///
    /// Was 0.75, which demanded the neck be a full 25% narrower than the head. That is true of a
    /// bare neck and false of almost every real head shot: a crew neck, a collar, a hood or long
    /// hair fills the gap, the mask never narrows that far, and detection fell off a cliff into the
    /// no-neck fallback -- which kept the entire torso. 0.90 catches a partially hidden neck while
    /// still needing a real narrowing rather than noise.
    /// </summary>
    private const double NeckNarrowing = 0.90;

    /// <summary>Below the neck, the mask must widen by this much again for it to be shoulders.</summary>
    private const double ShoulderFlare = 1.12;

    /// <summary>
    /// A head is roughly this many times taller than it is wide, hair included. Used only when no
    /// neck can be found at all, to tell a head-and-torso apart from a genuine head-only photo.
    /// </summary>
    private const double HeadAspect = 1.35;

    /// <summary>Fraction of the subject, from the top, that is crown no matter how it is framed.</summary>
    private const double CrownFraction = 0.12;

    /// <summary>A row wider than this multiple of the crown is shoulder, not head.</summary>
    private const double ShoulderRejection = 1.45;

    /// <summary>Alpha at or below this is background.</summary>
    private const byte Opaque = 8;

    /// <summary>
    /// Returns the head rectangle, padded and clamped to the image. Falls back to the full subject
    /// box when no shoulder line is detectable — a head-and-nothing-else photo is the common case.
    /// </summary>
    public static HeadBounds Find(byte[] rgba, int width, int height, int paddingPx) =>
        Find(rgba, width, height, paddingPx, cutBiasPercent: 0);

    /// <param name="cutBiasPercent">
    /// Shifts the neck cut up (negative) or down (positive) by this percentage of the subject's
    /// height. Zero uses the detected neck as-is.
    /// </param>
    /// <param name="faceChinRow">
    /// Chin row from a real face detection, when the host has one. Supplying it replaces the
    /// mask-shape inference entirely: a measured chin beats a guessed neck, and it is the only
    /// thing that is reliable when a collar hides the neck. Null falls back to the shape logic.
    /// </param>
    public static HeadBounds Find(
        byte[] rgba, int width, int height, int paddingPx, int cutBiasPercent, int? faceChinRow = null)
    {
        ArgumentNullException.ThrowIfNull(rgba);

        var widths = new int[height];
        var firstX = new int[height];
        var lastX = new int[height];

        var top = -1;
        var bottom = -1;
        var stride = width * CutoutLimits.AlphaChannels;

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var lo = -1;
            var hi = -1;
            var count = 0;

            for (var x = 0; x < width; x++)
            {
                if (rgba[row + (x * CutoutLimits.AlphaChannels) + 3] <= Opaque) continue;

                if (lo < 0) lo = x;
                hi = x;
                count++;
            }

            widths[y] = count;
            firstX[y] = lo;
            lastX[y] = hi;

            if (count <= 0) continue;
            if (top < 0) top = y;
            bottom = y;
        }

        if (top < 0)
        {
            return new HeadBounds(0, 0, width, height, Empty: true);
        }

        var cut = faceChinRow is { } chin
            ? Math.Clamp(chin, top, bottom)
            : NeckRow(widths, top, bottom);

        if (cutBiasPercent != 0)
        {
            var shift = (int)((bottom - top + 1) * (cutBiasPercent / 100.0));
            cut = Math.Clamp(cut + shift, top, bottom);
        }

        // Horizontal extent is measured across the head rows only. Using the whole subject would
        // drag the box out to shoulder width and defeat the point.
        var left = int.MaxValue;
        var right = int.MinValue;
        for (var y = top; y <= cut; y++)
        {
            if (widths[y] <= 0) continue;
            left = Math.Min(left, firstX[y]);
            right = Math.Max(right, lastX[y]);
        }

        if (left > right)
        {
            return new HeadBounds(0, 0, width, height, Empty: true);
        }

        var x0 = Math.Max(0, left - paddingPx);
        var y0 = Math.Max(0, top - paddingPx);
        var x1 = Math.Min(width - 1, right + paddingPx);
        var y1 = Math.Min(height - 1, cut + paddingPx);

        return new HeadBounds(x0, y0, x1 - x0 + 1, y1 - y0 + 1, Empty: false);
    }

    /// <summary>
    /// The last row that still belongs to the head.
    ///
    /// Finds the head's widest row (cheekbones), then the narrowest row below it (the neck), and
    /// confirms the mask widens again underneath (the shoulders). All three have to hold: a photo
    /// that is only a head has no neck minimum and no flare, and must keep its full height rather
    /// than be cut in half by a fixed proportion.
    /// </summary>
    private static int NeckRow(int[] widths, int top, int bottom)
    {
        // Phase 1 — ride the widening head up to its widest row, and stop once the mask has
        // clearly narrowed again. A global maximum would not do: shoulders are usually wider than
        // a head, so the widest row in the frame is normally the torso, not the face.
        var peakWidth = 0;
        var y = top;
        for (; y <= bottom; y++)
        {
            if (widths[y] <= 0) continue;

            if (widths[y] >= peakWidth)
            {
                peakWidth = widths[y];
                continue;
            }

            if (widths[y] < peakWidth * NeckNarrowing) break;
        }

        if (peakWidth <= 0 || y > bottom)
        {
            // Never narrowed. That used to be read as "a head on its own, keep all of it", which is
            // only half right -- it is equally what a head sitting on covered shoulders looks like,
            // and that reading returned the whole torso. Decide by proportion instead: measure the
            // head where it is unambiguous (the top of the subject) and see whether the subject is
            // taller than a head of that width could be.
            return ProportionalHeadCut(widths, top, bottom);
        }

        // Phase 2 — follow the narrowing down to the neck, then stop at the first row that flares
        // back out. That flare is the shoulder line.
        var troughWidth = widths[y] > 0 ? widths[y] : peakWidth;
        var troughRow = y;

        for (; y <= bottom; y++)
        {
            if (widths[y] <= 0) continue;

            if (widths[y] <= troughWidth)
            {
                troughWidth = widths[y];
                troughRow = y;
            }
            else if (widths[y] > troughWidth * ShoulderFlare)
            {
                return troughRow;
            }
        }

        return troughRow;
    }

    /// <summary>
    /// Last head row inferred from proportion alone, for masks with no detectable neck.
    ///
    /// The head's width is taken from the top <see cref="HeadTopFraction"/> of the subject, which is
    /// skull and hair in any framing and never shoulders. A head is about
    /// <see cref="HeadAspect"/> times taller than that width, so anything below that line is body.
    /// When the estimate reaches the bottom of the mask the subject really was head-only, and the
    /// full height is kept -- the same outcome the old fallback gave, but now for a checked reason.
    /// </summary>
    private static int ProportionalHeadCut(int[] widths, int top, int bottom)
    {
        var span = bottom - top + 1;

        // Pass 1 -- the crown. A narrow window right at the top of the subject is skull and hair in
        // any framing, so it gives a head width that cannot be contaminated by shoulders.
        var crownEnd = Math.Min(bottom, top + Math.Max(1, (int)(span * CrownFraction)) - 1);
        var crownWidth = 0;
        for (var y = top; y <= crownEnd; y++)
        {
            if (widths[y] > crownWidth) crownWidth = widths[y];
        }

        if (crownWidth <= 0)
        {
            return bottom;
        }

        // Pass 2 -- widen the estimate over the head's likely extent, but ignore any row that has
        // already flared well past the crown. Without that guard this window reaches the shoulders
        // whenever the subject is mostly torso, and a shoulder-width "head" projects a cut below
        // the bottom of the image -- which silently returned the entire body.
        var searchEnd = Math.Min(bottom, top + (int)(crownWidth * HeadAspect));
        var headWidth = crownWidth;
        for (var y = top; y <= searchEnd; y++)
        {
            if (widths[y] > headWidth && widths[y] <= crownWidth * ShoulderRejection)
            {
                headWidth = widths[y];
            }
        }

        var estimated = top + (int)(headWidth * HeadAspect);
        return estimated >= bottom ? bottom : estimated;
    }

    /// <summary>Copies the head rectangle out of an RGBA buffer.</summary>
    public static byte[] Crop(byte[] rgba, int width, HeadBounds box)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(box);

        var channels = CutoutLimits.AlphaChannels;
        var stride = width * channels;
        var outStride = box.Width * channels;
        var cropped = new byte[outStride * box.Height];

        for (var y = 0; y < box.Height; y++)
        {
            Buffer.BlockCopy(
                rgba,
                ((box.Y + y) * stride) + (box.X * channels),
                cropped,
                y * outStride,
                outStride);
        }

        return cropped;
    }
}
