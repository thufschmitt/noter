using System;
using System.Drawing;
using System.Windows.Forms;

namespace Longhand.PenDemo;

/// <summary>
/// A window to try the pen predictor on a real device, outside UWP: write on it, and compare the ink with the guess and without
/// (P, or the box), see the guess in red, and change how far ahead it guesses. Reads the pen with Win32's pointer messages.
/// </summary>
static class Program
{
    [STAThread]
    static void Main()
    {
        // The mouse comes as pointer messages too, so the same code serves pen, touch and mouse. Must come before any window.
        Native.EnableMouseInPointer(true);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new DemoForm());
    }
}

sealed class DemoForm : Form
{
    public DemoForm()
    {
        Text = "Longhand pen demo";
        ClientSize = new Size(1100, 760);
        KeyPreview = true;

        var ink = new InkPanel { Dock = DockStyle.Fill };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 6), WrapContents = true };
        var predict = new CheckBox { Text = "Prediction (P)", Checked = true, AutoSize = true, Margin = new Padding(0, 6, 16, 0) };
        var show = new CheckBox { Text = "Show the guess in red (R)", AutoSize = true, Margin = new Padding(0, 6, 16, 0) };
        var horizonLabel = new Label { AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
        var horizon = new TrackBar { Minimum = 0, Maximum = 60, Value = 25, TickFrequency = 5, Width = 220, AutoSize = false, Height = 32 };
        var clear = new Button { Text = "Clear (C)", AutoSize = true };
        var status = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 26, Padding = new Padding(8, 4, 8, 4), Text = "Write with a pen, a finger or the mouse. The pen's eraser end, or the right mouse button, erases strokes." };
        var hint = new Label { AutoSize = true, Margin = new Padding(16, 8, 0, 0), ForeColor = SystemColors.GrayText, Text = "Horizon: how far ahead the ink is drawn. Too long wobbles at turns; too short trails." };

        void ShowHorizon() => horizonLabel.Text = $"Horizon {horizon.Value} ms";
        predict.CheckedChanged += (_, _) => ink.Predicting = predict.Checked;
        show.CheckedChanged += (_, _) => ink.ShowGuess = show.Checked;
        horizon.ValueChanged += (_, _) => { ink.HorizonMs = horizon.Value; ShowHorizon(); };
        clear.Click += (_, _) => ink.Clear();
        ink.Status += s => status.Text = s;
        ShowHorizon();
        ink.HorizonMs = horizon.Value;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.P) predict.Checked = !predict.Checked;
            else if (e.KeyCode == Keys.R) show.Checked = !show.Checked;
            else if (e.KeyCode == Keys.C) ink.Clear();
            else return;
            e.Handled = e.SuppressKeyPress = true;
        };

        bar.Controls.AddRange(new Control[] { predict, show, horizonLabel, horizon, clear, hint });
        Controls.Add(ink);
        Controls.Add(bar);
        Controls.Add(status);
    }
}
