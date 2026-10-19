using System.Globalization;

namespace Swivel.Core.Files;

/// <summary>One page of an organised PDF: which source file, which page of it, and extra rotation.</summary>
public readonly record struct PlannedPage(int Source, int Page, int Rotation);

/// <summary>
/// The page organiser's answer, carried as a job parameter: <c>"0:2:90;0:0:0;1:4:0"</c> means
/// source 0 page 3 turned right, then source 0 page 1, then source 1 page 5 (all zero-based).
/// Pages added from other PDFs travel in a separate "sources" parameter.
/// </summary>
public static class PagePlan
{
    public static string Format(IEnumerable<PlannedPage> pages) =>
        string.Join(';', pages.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Source}:{p.Page}:{Normalise(p.Rotation)}")));

    public static IReadOnlyList<PlannedPage> Parse(string plan)
    {
        var pages = new List<PlannedPage>();
        foreach (string item in plan.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(':');
            if (parts.Length != 3
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int source)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rotation)
                || source < 0 || page < 0)
                throw new FormatException($"Bad page plan entry '{item}'.");
            pages.Add(new PlannedPage(source, page, Normalise(rotation)));
        }
        return pages;
    }

    /// <summary>Rotation in quarter turns clockwise, as 0, 90, 180 or 270.</summary>
    public static int Normalise(int degrees) => ((degrees / 90 * 90 % 360) + 360) % 360;
}
