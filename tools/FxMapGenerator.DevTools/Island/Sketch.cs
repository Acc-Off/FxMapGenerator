namespace FxMapGenerator.DevTools.Island;

/// <summary>
/// The rough layout the land follows (a sketch of 743 x 551 px laid over the cell's width, its middle on the cell's
/// middle, y down): the coast with a headland in the north-west, the mountains to the north-east of a line, a river
/// from the east through a lake to the sea and another from the mountains along the headland's foot, the town west of
/// a line, the highway and its branch, the railway. Only the large shapes come from it; the algorithms make the rest.
/// </summary>
static class Sketch
{
    public const double Width = 743, Height = 551;
    /// <summary>Metres per sketch pixel (the sketch's width is the cell's).</summary>
    public const double Scale = 2250 / Width;

    /// <summary>A sketch point in the cell's local metres (x east, y north, from the cell's middle).</summary>
    public static (double X, double Y) Local((double X, double Y) p) => ((p.X - Width / 2) * Scale, (Height / 2 - p.Y) * Scale);

    public static (double X, double Y)[] Local(IEnumerable<(double X, double Y)> ps) => ps.Select(Local).ToArray();

    /// <summary>The sea: west of the coast, south of the headland (points beyond the sketch carry it to the cell's edges).</summary>
    public static readonly (double X, double Y)[] Sea =
    [
        (-120, 30), (0, 45), (40, 58), (100, 85), (150, 100), (178, 105), (188, 120), (188, 210), (200, 240), (215, 275), (228, 310), (233, 340),
        (232, 380), (222, 420), (210, 455), (200, 490), (195, 515), (180, 525), (172, 540), (170, 551), (165, 700), (-120, 700),
    ];

    /// <summary>The mountains: north-east of the line (closed round the top-right corner beyond the sketch).</summary>
    public static readonly (double X, double Y)[] Mountains =
    [
        (400, -150), (405, 0), (408, 25), (430, 45), (455, 65), (462, 100), (478, 120), (510, 140), (545, 165), (570, 195), (600, 225), (628, 255),
        (650, 270), (690, 295), (743, 325), (900, 360), (900, -150),
    ];

    /// <summary>The highest summit (the mountains' top-right).</summary>
    public static readonly (double X, double Y) Summit = (690, 40);

    /// <summary>The town: west of the line, south of the headland's foot.</summary>
    public static readonly (double X, double Y)[] Town =
    [
        (190, 155), (300, 154), (360, 155), (395, 160), (410, 175), (430, 200), (445, 225), (460, 260), (475, 295), (500, 320), (525, 340), (545, 360),
        (560, 390), (580, 430), (605, 455), (625, 480), (635, 510), (637, 551), (640, 700), (150, 700), (150, 155),
    ];

    /// <summary>The river from the east through the lake to the sea, and the one from the mountains along the headland's foot.</summary>
    public static readonly (double X, double Y)[] River =
    [
        (800, 150), (715, 183), (700, 195), (680, 210), (650, 235), (630, 258), (600, 275), (560, 295), (520, 320), (480, 340), (430, 360), (370, 385),
        (320, 405), (280, 425), (240, 440), (200, 445), (150, 448),
    ];

    public static readonly (double X, double Y)[] UpperRiver =
        [(600, -40), (570, 18), (520, 35), (470, 55), (420, 70), (270, 72), (210, 80), (170, 95), (140, 105), (110, 110)];

    /// <summary>The lake on the river: middle, half-lengths along and across (px), and its turn (degrees, anticlockwise from east).</summary>
    public static readonly (double X, double Y, double A, double B, double Turn) Lake = (595, 274, 62, 18, 28);

    /// <summary>The highway from the headland south through the town, and its branch east into the mountains.</summary>
    public static readonly (double X, double Y)[] Highway =
    [
        (-100, -60), (8, 0), (40, 20), (95, 40), (140, 65), (180, 95), (215, 110), (240, 125), (255, 145), (275, 195), (300, 255), (320, 315),
        (330, 375), (338, 440), (345, 505), (350, 551), (355, 700),
    ];

    public static readonly (double X, double Y)[] Branch = [(245, 128), (300, 128), (400, 127), (480, 122), (560, 117), (620, 112), (680, 110), (743, 128), (850, 150)];

    /// <summary>The avenue (not in the sketch; added): from the town's quay east, south of the lake, to the cell's east edge.</summary>
    public static readonly (double X, double Y)[] Avenue = [(245, 335), (330, 335), (450, 345), (560, 335), (650, 360), (743, 380), (850, 400)];

    public static readonly (double X, double Y)[] Railway =
    [
        (170, -60), (190, 0), (215, 15), (245, 40), (270, 60), (300, 100), (340, 140), (370, 170), (390, 195), (410, 230), (430, 275), (455, 320),
        (480, 360), (500, 390), (515, 420), (545, 428), (600, 440), (650, 455), (700, 460), (743, 462), (850, 465),
    ];
}
