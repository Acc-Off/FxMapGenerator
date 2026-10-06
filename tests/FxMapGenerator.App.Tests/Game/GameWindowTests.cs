using System.Drawing;
using System.Windows.Forms;
using FxMapGenerator.App.Game;
using FxMapGenerator.Core.Capture;

namespace FxMapGenerator.App.Tests.Game;

public sealed class GameWindowTests
{
    /// <summary>
    /// PrintWindow as the capture uses it, on a small window of a known colour that shows for a moment in the corner of the
    /// screen without taking the focus (a window off the screen is not composed, so it would come out black; the game
    /// window is taken the same way while other windows cover it). Skipped without a desktop.
    /// </summary>
    [SkippableFact]
    public void AWindowIsTakenWithItsDrawingAreaAsRgba()
    {
        Skip.IfNot(Environment.UserInteractive, "no desktop to create a window on");
        Frame? frame = null;
        (int, int)? size = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new QuietForm
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(8, 8),
                    ClientSize = new Size(160, 90),   // not below the smallest window Windows allows (136 px wide)
                    BackColor = Color.FromArgb(30, 160, 90),
                };
                form.Show();
                form.Refresh();
                // let it be drawn and composed before taking it
                for (int i = 0; i < 20; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(10);
                }
                size = GameWindow.ClientSizeOf(form.Handle);
                frame = GameWindow.CaptureWindow(form.Handle);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
        Assert.Equal((160, 90), size);
        Assert.NotNull(frame);
        Assert.Equal((160, 90), (frame.Width, frame.Height));
        Assert.Equal((30, 160, 90), ((int, int, int))frame.Pixel(80, 45));
        Assert.Equal(255, frame.Rgba[(45 * 160 + 80) * 4 + 3]);
    }

    sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [SkippableFact]
    public void WithoutTheGameThereIsNoWindow()
    {
        Skip.If(GameWindow.Find() != IntPtr.Zero, "FiveM is running on this PC");
        Assert.Null(GameWindow.Instance.ClientSize());
        Assert.Null(GameWindow.Instance.Capture());
    }
}
