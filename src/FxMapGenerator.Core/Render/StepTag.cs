using FxMapGenerator.Core.Styles;

namespace FxMapGenerator.Core.Render;

/// <summary>
/// What one step of a cell's drawing paints, for telling where the colour of a pixel comes from
/// (<see cref="CellPainter.Pick"/>): its kind (<c>background</c>, <c>ground</c> (the ground picture), <c>groundLayer</c>,
/// <c>shade</c>, <c>canopy</c>, <c>water</c>, <c>sea</c>, <c>building</c>, <c>contour</c>, <c>rail</c>, <c>tunnel</c>,
/// <c>track</c>, <c>casing</c>, <c>road</c>, <c>postal</c>, <c>poi</c>, <c>zone</c>, <c>street</c>), the colour it paints
/// and what tells it from its kind's others: the road class (0 road, 1 highway), the paint (a ground paint, a building
/// colour), the sea band and bed (<c>sand</c> / <c>rock</c>), a label's text.
/// </summary>
public sealed record StepTag(string Kind, Rgb? Color = null, int Class = -1, string? Paint = null, int Band = -1, string? Bed = null, string? Text = null);
