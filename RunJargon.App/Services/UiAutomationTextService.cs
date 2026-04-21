using System.Windows.Automation;
using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class UiAutomationTextService
{
    private static readonly HashSet<int> SupportedControlTypeIds =
    [
        ControlType.Button.Id,
        ControlType.CheckBox.Id,
        ControlType.ComboBox.Id,
        ControlType.DataItem.Id,
        ControlType.Edit.Id,
        ControlType.Header.Id,
        ControlType.HeaderItem.Id,
        ControlType.Hyperlink.Id,
        ControlType.ListItem.Id,
        ControlType.MenuItem.Id,
        ControlType.RadioButton.Id,
        ControlType.TabItem.Id,
        ControlType.Text.Id,
        ControlType.TreeItem.Id
    ];

    public IReadOnlyList<LayoutTextSegment> GetSegments(ScreenRegion captureRegion)
    {
        try
        {
            var windowHandles = ResolveCandidateWindowHandles(captureRegion);
            if (windowHandles.Count == 0)
            {
                return Array.Empty<LayoutTextSegment>();
            }

            var collected = new List<LayoutTextSegment>();

            foreach (var windowHandle in windowHandles)
            {
                AutomationElement window;
                try
                {
                    window = AutomationElement.FromHandle(windowHandle);
                }
                catch
                {
                    continue;
                }

                if (!TryGetBoundingRectangle(window, out var windowBounds)
                    || !Intersects(captureRegion, windowBounds))
                {
                    continue;
                }

                var descendants = window.FindAll(TreeScope.Subtree, Condition.TrueCondition);
                foreach (AutomationElement element in descendants)
                {
                    if (!TryCreateSegment(element, captureRegion, windowHandle, out var segment))
                    {
                        continue;
                    }

                    collected.Add(segment);
                }
            }

            return Deduplicate(collected);
        }
        catch
        {
            return Array.Empty<LayoutTextSegment>();
        }
    }

    private static bool TryCreateSegment(
        AutomationElement element,
        ScreenRegion captureRegion,
        IntPtr ownerWindowHandle,
        out LayoutTextSegment segment)
    {
        segment = default!;

        var name = TextRegionIntelligence.NormalizeWhitespace(element.Current.Name);
        if (!IsUsefulAutomationName(name))
        {
            return false;
        }

        var controlType = element.Current.ControlType;
        if (controlType is null || !SupportedControlTypeIds.Contains(controlType.Id))
        {
            return false;
        }

        if (element.Current.IsOffscreen)
        {
            return false;
        }

        if (!TryGetBoundingRectangle(element, out var absoluteBounds))
        {
            return false;
        }

        var intersection = Intersect(captureRegion, absoluteBounds);
        if (intersection is null)
        {
            return false;
        }

        var elementCoverage = Coverage(absoluteBounds, intersection.Value);
        if (!ContainsCenter(captureRegion, absoluteBounds) && elementCoverage < 0.42)
        {
            return false;
        }

        if (!BelongsToTopmostOwner(intersection.Value, ownerWindowHandle))
        {
            return false;
        }

        var relativeBounds = new ScreenRegion(
            intersection.Value.Left - captureRegion.Left,
            intersection.Value.Top - captureRegion.Top,
            intersection.Value.Width,
            intersection.Value.Height);
        if (relativeBounds.IsEmpty)
        {
            return false;
        }

        var line = new OcrLineRegion(name, relativeBounds);
        segment = new LayoutTextSegment(
            name,
            relativeBounds,
            [line],
            TextLayoutKind.UiLabel);
        return true;
    }

    private static bool TryGetBoundingRectangle(AutomationElement element, out ScreenRegion bounds)
    {
        bounds = default;
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || rect.Width <= 1 || rect.Height <= 1)
        {
            return false;
        }

        bounds = new ScreenRegion(rect.Left, rect.Top, rect.Width, rect.Height);
        return true;
    }

    private static bool IsUsefulAutomationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 48)
        {
            return false;
        }

        var wordCount = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 6)
        {
            return false;
        }

        if (!name.Any(char.IsLetter))
        {
            return false;
        }

        return !name.All(ch => ch is '-' or '_' or '|' or '·' or ' ');
    }

    private static IReadOnlyList<IntPtr> ResolveCandidateWindowHandles(ScreenRegion captureRegion)
    {
        var handleFrequency = new Dictionary<IntPtr, int>();

        foreach (var point in EnumerateSamplePoints(captureRegion))
        {
            var window = NativeMethods.WindowFromPoint(new NativeMethods.POINT(
                (int)Math.Round(point.X),
                (int)Math.Round(point.Y)));
            if (window == IntPtr.Zero)
            {
                continue;
            }

            var root = NativeMethods.GetAncestor(window, NativeMethods.GaRoot);
            var key = root == IntPtr.Zero ? window : root;
            handleFrequency[key] = handleFrequency.TryGetValue(key, out var count)
                ? count + 1
                : 1;
        }

        return handleFrequency
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Select(pair => pair.Key)
            .ToArray();
    }

    private static IEnumerable<(double X, double Y)> EnumerateSamplePoints(ScreenRegion captureRegion)
    {
        var minX = captureRegion.Left + Math.Max(1, captureRegion.Width * 0.18);
        var maxX = captureRegion.Right - Math.Max(1, captureRegion.Width * 0.18);
        var minY = captureRegion.Top + Math.Max(1, captureRegion.Height * 0.18);
        var maxY = captureRegion.Bottom - Math.Max(1, captureRegion.Height * 0.18);
        var centerX = captureRegion.Left + (captureRegion.Width / 2);
        var centerY = captureRegion.Top + (captureRegion.Height / 2);

        yield return (centerX, centerY);
        yield return (minX, minY);
        yield return (maxX, minY);
        yield return (minX, maxY);
        yield return (maxX, maxY);
    }

    private static bool BelongsToTopmostOwner(ScreenRegion visibleBounds, IntPtr ownerWindowHandle)
    {
        var centerX = visibleBounds.Left + (visibleBounds.Width / 2);
        var centerY = visibleBounds.Top + (visibleBounds.Height / 2);
        var window = NativeMethods.WindowFromPoint(new NativeMethods.POINT(
            (int)Math.Round(centerX),
            (int)Math.Round(centerY)));
        if (window == IntPtr.Zero)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(window, NativeMethods.GaRoot);
        if (root != IntPtr.Zero)
        {
            window = root;
        }

        return window == ownerWindowHandle;
    }

    private static IReadOnlyList<LayoutTextSegment> Deduplicate(IReadOnlyList<LayoutTextSegment> segments)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        var ordered = segments
            .OrderBy(segment => segment.Bounds.Width * segment.Bounds.Height)
            .ThenBy(segment => segment.Bounds.Top)
            .ThenBy(segment => segment.Bounds.Left)
            .ToList();

        var deduplicated = new List<LayoutTextSegment>();
        foreach (var candidate in ordered)
        {
            var duplicate = deduplicated.Any(existing =>
                string.Equals(existing.Text, candidate.Text, StringComparison.OrdinalIgnoreCase)
                && Coverage(existing.Bounds, candidate.Bounds) >= 0.72);
            if (duplicate)
            {
                continue;
            }

            deduplicated.Add(candidate);
        }

        return deduplicated
            .OrderBy(segment => segment.Bounds.Top)
            .ThenBy(segment => segment.Bounds.Left)
            .ToArray();
    }

    private static bool Intersects(ScreenRegion first, ScreenRegion second)
    {
        return !(first.Right <= second.Left
                 || second.Right <= first.Left
                 || first.Bottom <= second.Top
                 || second.Bottom <= first.Top);
    }

    private static bool ContainsCenter(ScreenRegion outer, ScreenRegion inner)
    {
        var centerX = inner.Left + (inner.Width / 2);
        var centerY = inner.Top + (inner.Height / 2);
        return centerX >= outer.Left
               && centerX <= outer.Right
               && centerY >= outer.Top
               && centerY <= outer.Bottom;
    }

    private static ScreenRegion? Intersect(ScreenRegion first, ScreenRegion second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);

        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    private static double Coverage(ScreenRegion target, ScreenRegion candidate)
    {
        var intersection = Intersect(target, candidate);
        if (intersection is null)
        {
            return 0;
        }

        var targetArea = Math.Max(1, target.Width * target.Height);
        return (intersection.Value.Width * intersection.Value.Height) / targetArea;
    }
}
