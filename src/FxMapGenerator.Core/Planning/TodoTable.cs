namespace FxMapGenerator.Core.Planning;

/// <summary>
/// One row of the to-do table: a stage, or a part of one (children). Rows that are not needed stay in the table with
/// <see cref="Needed"/> false and a <see cref="Reason"/>, so changing the maps shows which rows came and went.
/// </summary>
/// <param name="Id">Stable id, e.g. <c>visit</c>, <c>visit.shot</c>, <c>cells.atlas-c-en</c>.</param>
/// <param name="Needed">False when none of the chosen maps needs the row.</param>
/// <param name="Reason">Why nothing is left: <c>notNeeded</c>, <c>done</c>, <c>noVisit</c>.</param>
/// <param name="Remaining">Units left to do.</param>
/// <param name="Unit"><c>block</c>, <c>cell</c>, <c>set</c>, <c>language</c>, <c>sheet</c>, <c>world</c>, <c>once</c> or <c>step</c> (a parent counting its child rows with work left).</param>
/// <param name="Land">Blocks left that are land (block rows only).</param>
/// <param name="Water">Blocks left that are water (block rows only).</param>
/// <param name="For">Ids of the maps that need this row.</param>
/// <param name="Low">Estimate, lower end, in seconds.</param>
/// <param name="High">Estimate, upper end, in seconds.</param>
/// <param name="UsesGame">Runs in the game (the visit): one at a time, the number of workers does not apply.</param>
/// <param name="ByHand">Time the user spends (the pre-check).</param>
/// <param name="Targets">Names of the blocks or cells left, for highlighting them on the map.</param>
public sealed record TodoRow(
    string Id,
    bool Needed,
    string? Reason,
    int Remaining,
    string Unit,
    int? Land,
    int? Water,
    IReadOnlyList<string> For,
    double Low,
    double High,
    bool UsesGame,
    bool ByHand,
    IReadOnlyList<string> Targets,
    IReadOnlyList<TodoRow> Children);

/// <summary>The whole table plus totals; the game part is the visit and the pre-check.</summary>
public sealed record TodoTable(
    IReadOnlyList<TodoRow> Rows,
    double Low,
    double High,
    double GameLow,
    double GameHigh,
    int Workers,
    int Processors,
    int RangeBlocks,
    int RangeLand,
    int RangeWater,
    int Cells);
