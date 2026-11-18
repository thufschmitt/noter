using System;
using System.Collections.Generic;
using System.Linq;
using Longhand.Services;

/// <summary>
/// Checks for the pen predictor that draws wet ink ahead of the last sample (<see cref="StrokePredictor"/>), on synthetic strokes
/// sampled the way a digitizer does: at 240 Hz with jitter in the timing and noise in the position.
/// </summary>
static class PredictorChecks
{
    const double Latency = 0.025;

    /// <summary>A path the nib follows: its position at a time, in seconds from the start of the stroke.</summary>
    delegate (double X, double Y) Path(double t);

    /// <summary>Samples a path as a pen digitizer would, from 0 to <paramref name="duration"/>.</summary>
    static List<PenSample> Sample(Path path, double duration, int seed, double rate = 240, double noise = 0.15, double jitter = 0.0004)
    {
        var random = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
        var samples = new List<PenSample>();
        for (int i = 0; ; i++)
        {
            double t = i / rate + (i == 0 ? 0 : jitter * (random.NextDouble() - 0.5));
            if (t > duration) break;
            var (x, y) = path(t);
            samples.Add(new PenSample(x + noise * Gauss(), y + noise * Gauss(), 0.5, t));
        }
        return samples;
    }

    /// <summary>Cursive-like writing: loops along a line, at the speeds of ordinary handwriting (up to about 300 px/s).</summary>
    static Path Cursive(double f1, double f2, double phase) => t =>
        (55 * t + 9 * Math.Sin(2 * Math.PI * f1 * t + phase), 10 * Math.Sin(2 * Math.PI * f2 * t));

    /// <summary>
    /// Plays the samples through the predictor and measures, at each one, the gap between where the ink ends (the last sample, or the
    /// end of the guess) and where the nib really is <see cref="Latency"/> later, when that frame reaches the screen.
    /// </summary>
    static (double Plain, double Predicted, double Worst) Gaps(Path path, List<PenSample> samples, double horizon = Latency, int skip = 3)
    {
        var predictor = new StrokePredictor(horizon);
        double plain = 0, predicted = 0, worst = 0;
        int n = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            predictor.Add(s);
            var guess = predictor.Predict();
            if (i < skip) continue;
            var (tx, ty) = path(s.Time + Latency);
            var end = guess.Count > 0 ? guess[^1] : s;
            double g = Math.Sqrt((end.X - tx) * (end.X - tx) + (end.Y - ty) * (end.Y - ty));
            plain += Math.Sqrt((s.X - tx) * (s.X - tx) + (s.Y - ty) * (s.Y - ty));
            predicted += g;
            worst = Math.Max(worst, g);
            n++;
        }
        return (plain / n, predicted / n, worst);
    }

    public static void Run(Action<string, bool> check)
    {
        // Ordinary handwriting: the guess closes most of the gap the latency leaves.
        double plainSum = 0, predictedSum = 0;
        var writing = new[] { (4.0, 5.0, 0.0), (5.0, 6.0, 1.0), (3.5, 4.5, 2.0), (6.0, 5.0, 0.5) };
        for (int k = 0; k < writing.Length; k++)
        {
            var (f1, f2, ph) = writing[k];
            var path = Cursive(f1, f2, ph);
            var (plain, predicted, _) = Gaps(path, Sample(path, 1.5, seed: k));
            plainSum += plain;
            predictedSum += predicted;
        }
        Console.WriteLine($"      handwriting: ink {plainSum / writing.Length:F2} px behind the nib without prediction, {predictedSum / writing.Length:F2} px with it");
        check("predictor: on handwriting, the ink ends less than half as far from the nib as without prediction", predictedSum < 0.5 * plainSum);

        // A fast straight line: almost all of the gap is closed.
        Path line = t => (500 * t, 200 * t);
        var (linePlain, linePredicted, _) = Gaps(line, Sample(line, 0.4, seed: 10));
        Console.WriteLine($"      straight line: {linePlain:F2} px → {linePredicted:F2} px");
        check("predictor: on a fast straight line, the ink ends within a fifth of the gap", linePredicted < 0.2 * linePlain);

        // A circle, drawn quickly.
        Path circle = t => (100 + 40 * Math.Cos(2 * Math.PI * 2 * t), 100 + 40 * Math.Sin(2 * Math.PI * 2 * t));
        var (circlePlain, circlePredicted, _) = Gaps(circle, Sample(circle, 1, seed: 11));
        Console.WriteLine($"      circle: {circlePlain:F2} px → {circlePredicted:F2} px");
        check("predictor: on a circle, the ink ends within a third of the gap", circlePredicted < circlePlain / 3);

        // A pen that stops dead: the guess may run past the stop for a moment, but not far, and is soon gone.
        Path stop = t => t < 0.2 ? (600 * t, 0) : (120, 0);
        var stopSamples = Sample(stop, 0.5, seed: 12);
        var predictor = new StrokePredictor(Latency);
        double overshoot = 0;
        bool goneAfter = true;
        foreach (var s in stopSamples)
        {
            predictor.Add(s);
            var guess = predictor.Predict();
            if (s.Time < 0.2) continue;
            foreach (var p in guess) overshoot = Math.Max(overshoot, p.X - 120);
            if (s.Time > 0.25 && guess.Count > 0) goneAfter = false;
        }
        Console.WriteLine($"      sudden stop at 600 px/s: the guess runs at most {overshoot:F1} px past it");
        check("predictor: when the pen stops dead, the guess runs past the stop by less than the latency's worth of travel", overshoot < 600 * Latency);
        check("predictor: 50 ms after the pen stopped, nothing is guessed", goneAfter);

        // A pen at rest, with only the digitizer's noise: no guess, so the ink end doesn't tremble.
        Path rest = _ => (50, 50);
        var still = new StrokePredictor(Latency);
        int guesses = 0;
        foreach (var s in Sample(rest, 0.5, seed: 13))
        {
            still.Add(s);
            if (still.Predict().Count > 0) guesses++;
        }
        check("predictor: a pen at rest guesses nothing", guesses == 0);

        // A slow pen: there is little to gain, and nothing guessed.
        Path slow = t => (20 * t, 10 * t);
        var crawl = new StrokePredictor(Latency);
        int slowGuesses = 0;
        foreach (var s in Sample(slow, 1, seed: 14))
        {
            crawl.Add(s);
            if (crawl.Predict().Count > 0) slowGuesses++;
        }
        check("predictor: a pen crawling at 20 px/s guesses nothing", slowGuesses == 0);

        // A sharp corner (a V): the guess overshoots the corner a little, then follows the new direction.
        Path vee = t => t < 0.15 ? (400 * t, 400 * t) : (60 + 400 * (t - 0.15), 60 - 400 * (t - 0.15));
        var (veePlain, veePredicted, veeWorst) = Gaps(vee, Sample(vee, 0.3, seed: 15));
        Console.WriteLine($"      sharp corner: {veePlain:F2} px → {veePredicted:F2} px, at worst {veeWorst:F1} px");
        check("predictor: through a sharp corner the ink is closer to the nib on average", veePredicted < veePlain);
        check("predictor: through a sharp corner the guess never strays further than twice the plain gap", veeWorst < 2 * 400 * Math.Sqrt(2) * Latency);

        var back = new StrokePredictor(Latency);
        bool guessingAgain = false;
        foreach (var s in Sample(vee, 0.3, seed: 15))
        {
            back.Add(s);
            if (back.Predict().Count > 0 && s.Time > 0.25) guessingAgain = true;
        }
        check("predictor: after the corner, once the pen is predictable again, it guesses again", guessingAgain);

        // The first samples of a stroke: nothing is guessed from fewer than three.
        var fresh = new StrokePredictor(Latency);
        fresh.Add(new PenSample(0, 0, 0.5, 0));
        fresh.Add(new PenSample(2, 0, 0.5, 0.004));
        check("predictor: nothing is guessed from two samples", fresh.Predict().Count == 0);

        // Reset starts a new stroke: nothing carries over from the last.
        var reused = new StrokePredictor(Latency);
        foreach (var s in Sample(line, 0.2, seed: 16)) reused.Add(s);
        bool guessedBefore = reused.Predict().Count > 0;
        reused.Reset();
        reused.Add(new PenSample(1000, 1000, 0.5, 5));
        check("predictor: after Reset, a new stroke starts with no guess", guessedBefore && reused.Predict().Count == 0);

        // Samples out of order or repeated don't break it.
        var messy = new StrokePredictor(Latency);
        var ms = Sample(line, 0.2, seed: 17);
        foreach (var s in ms) { messy.Add(s); messy.Add(s with { Time = s.Time - 0.01 }); messy.Add(s); }
        var mg = messy.Predict();
        check("predictor: old and repeated samples are ignored", mg.Count > 0 && mg.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)) && mg[^1].X > ms[^1].X);

        // Uneven sampling (a slower digitizer, batched delivery): it still helps.
        var uneven = Sample(line, 0.4, seed: 18, rate: 133, jitter: 0.004);
        var (unevenPlain, unevenPredicted, _) = Gaps(line, uneven);
        check("predictor: with uneven, slower sampling the ink still ends within a fifth of the gap", unevenPredicted < 0.2 * unevenPlain);

        // A pause in the middle of a stroke starts the filter afresh rather than guessing from before the pause.
        var paused = new StrokePredictor(Latency);
        foreach (var s in Sample(line, 0.2, seed: 19)) paused.Add(s);
        paused.Add(new PenSample(100, 40, 0.5, 0.6));
        paused.Add(new PenSample(100, 40.1, 0.5, 0.604));
        paused.Add(new PenSample(100, 40.2, 0.5, 0.608));
        check("predictor: after a long pause, the old speed is forgotten", paused.Predict().Count == 0);

        // The eraser: a pass that crosses a stroke, or comes within reach of it, takes it; one that passes by doesn't.
        var word = new List<(double X, double Y)> { (0, 0), (10, 5), (20, 0), (30, 5) };
        check("eraser: a pass across a stroke takes it", EraserHit.Touches(word, (15, -10), (15, 10)));
        check("eraser: a pass that comes within reach takes it", EraserHit.Touches(word, (33, 8), (45, 8)));
        check("eraser: a pass that keeps its distance doesn't", !EraserHit.Touches(word, (0, 20), (30, 20)));
        check("eraser: a still eraser on a dot takes it", EraserHit.Touches(new List<(double X, double Y)> { (5, 5) }, (7, 6), (7, 6)));
        check("eraser: a still eraser beside a dot doesn't", !EraserHit.Touches(new List<(double X, double Y)> { (5, 5) }, (20, 5), (20, 5)));

        // The guess's pressure is the pen's last.
        var pressed = new StrokePredictor(Latency);
        foreach (var s in Sample(line, 0.2, seed: 20)) pressed.Add(s with { Pressure = 0.8 });
        check("predictor: the guess keeps the last pressure", pressed.Predict() is { Count: > 0 } pg && pg.All(p => p.Pressure == 0.8));
    }
}
