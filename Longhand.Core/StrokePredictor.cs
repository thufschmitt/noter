using System;
using System.Collections.Generic;
using System.Linq;

namespace Longhand.Services;

/// <summary>One sample from the pen: where it was (page pixels), how hard it pressed (0 to 1) and when (seconds, any origin).</summary>
public readonly record struct PenSample(double X, double Y, double Pressure, double Time);

/// <summary>
/// Guesses where the pen is going, so the wet ink can be drawn a little ahead of the last sample the system has delivered and the
/// stroke seems to stick to the nib. This is what Windows Ink's low-latency path does inside <c>InkCanvas</c>; done here, on plain
/// pointer samples, it works with any platform's raw stylus input.
/// <para>
/// Each axis is followed by a Kalman filter with position, velocity and acceleration (a constant-acceleration model whose noise is
/// a random jerk), fed the samples at their own timestamps, so uneven sampling and dropped or batched samples are fine. The guess
/// is the filter's state carried forward, with the acceleration's share tapering off further out (it is the least trustworthy
/// part). How far ahead it guesses shrinks when it has little to go on: the first samples of a stroke, a pen that is barely moving
/// (the gap it would close is too small to see, and the guess would only jitter), and a pen whose recent guesses missed (a sharp
/// turn, a stop). The guess never turns back on itself and never runs further than the pen's speed allows.
/// </para>
/// <para>
/// The guess is only for drawing: it is thrown away with every new sample and never becomes part of the stroke.
/// </para>
/// </summary>
public sealed class StrokePredictor
{
    /// <summary>How far ahead to guess, in seconds, at full confidence: about the time from the pen touching to the ink showing.</summary>
    public double Horizon { get; set; }

    /// <summary>The spacing in time of the guessed points, so a curving guess is drawn as a curve.</summary>
    public double Step { get; set; } = 0.004;

    // Tuning, on synthetic handwriting with digitizer noise (tools/CoreCheck/PredictorChecks.cs).
    /// <summary>Spectral density of the jerk the model allows (page pixels² per second⁵): how quickly the pen may change course.</summary>
    const double JerkNoise = 4e9;
    /// <summary>How far out (as a share of <see cref="Horizon"/>) the acceleration's share of the guess has halved.</summary>
    const double FadeAt = 2;
    /// <summary>The smoothed miss (as a share of the guess's length) that is still taken as ordinary; from there the guess shrinks.</summary>
    const double MissOk = 0.35;
    /// <summary>Variance of a sample's position (page pixels²): the digitizer's noise.</summary>
    const double SampleNoise = 0.04;
    /// <summary>Samples before a guess is at full length.</summary>
    const int FullAfter = 6;
    /// <summary>Below this speed (page pixels per second) nothing is guessed; above <see cref="FastSpeed"/> it is guessed in full.</summary>
    const double SlowSpeed = 40, FastSpeed = 110;
    /// <summary>Gaps longer than this (seconds) between samples start the filter afresh: the pen paused, or samples were lost.</summary>
    const double MaxGap = 0.1;

    /// <summary>The span (seconds) over which the pen's speed is measured for deciding whether to guess: long enough that the digitizer's noise doesn't look like motion.</summary>
    const double SpeedWindow = 0.03;

    readonly Axis _x = new(), _y = new();
    // The last samples, for the measured speed and for scoring past guesses against where the pen really went.
    readonly Queue<PenSample> _recent = new();
    double _time, _pressure;
    int _count;
    // Recent guesses, to see how good they turned out once the pen got there: when they were for, and where they said.
    readonly Queue<(double Time, double X, double Y, double Travel)> _checks = new();
    double _miss; // smoothed miss, as a fraction of the distance the guess covered
    double _scoredAt = double.NaN; // the sample whose guess was last put up for checking

    public StrokePredictor(double horizon = 0.025) => Horizon = horizon;

    /// <summary>How sure the last guess was, from 0 (nothing guessed) to 1 (guessed the full <see cref="Horizon"/>).</summary>
    public double Confidence { get; private set; }

    /// <summary>Forgets the stroke: the next sample starts a new one.</summary>
    public void Reset()
    {
        _count = 0;
        _recent.Clear();
        _checks.Clear();
        _miss = 0;
        _scoredAt = double.NaN;
        Confidence = 0;
    }

    /// <summary>Adds a sample of the stroke, in the order they were taken. A sample older than the last is ignored.</summary>
    public void Add(PenSample s)
    {
        if (_count > 0 && s.Time <= _time)
        {
            if (s.Time < _time) return;
            // Same timestamp (some digitizers report two at once): take it as a correction of the last one.
            _x.Correct(s.X);
            _y.Correct(s.Y);
            _pressure = s.Pressure;
            return;
        }
        if (_count == 0 || s.Time - _time > MaxGap)
        {
            _x.Start(s.X);
            _y.Start(s.Y);
            _recent.Clear();
            _checks.Clear();
            _miss = 0;
            _count = 1;
        }
        else
        {
            double dt = s.Time - _time;
            _x.Predict(dt);
            _y.Predict(dt);
            _x.Correct(s.X);
            _y.Correct(s.Y);
            _count++;
        }
        var previous = _recent.Count > 0 ? _recent.Last() : s;
        _recent.Enqueue(s);
        while (_recent.Count > 2 && s.Time - _recent.ElementAt(1).Time >= SpeedWindow) _recent.Dequeue();
        _time = s.Time;
        _pressure = s.Pressure;
        Score(previous, s);
    }

    /// <summary>How fast the pen has gone over the last <see cref="SpeedWindow"/>, from the samples themselves (page pixels per second).</summary>
    double MeasuredSpeed()
    {
        var first = _recent.Peek();
        var last = _recent.Last();
        double dt = last.Time - first.Time;
        if (dt <= 0) return 0;
        return Math.Sqrt((last.X - first.X) * (last.X - first.X) + (last.Y - first.Y) * (last.Y - first.Y)) / dt;
    }

    /// <summary>
    /// Compares the guesses whose time has come with where the pen really was. The miss is relative to how far the guess reached,
    /// so a slow pen and a fast one are judged alike.
    /// </summary>
    void Score(PenSample a, PenSample b)
    {
        while (_checks.Count > 0 && _checks.Peek().Time <= b.Time)
        {
            var c = _checks.Dequeue();
            if (c.Travel < 1) continue; // too short a guess to judge
            // Where the pen was at the guess's time, between the two samples around it.
            double f = b.Time > a.Time ? Math.Clamp((c.Time - a.Time) / (b.Time - a.Time), 0, 1) : 1;
            double x = a.X + f * (b.X - a.X), y = a.Y + f * (b.Y - a.Y);
            double miss = Math.Min(2, Math.Sqrt((c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y)) / c.Travel);
            _miss += 0.3 * (miss - _miss);
        }
    }

    /// <summary>
    /// The guessed points beyond the last sample, in time order, each <see cref="Step"/> apart; empty when there is nothing worth
    /// guessing. Draw them after the stroke's real points, and draw them again (not add to them) after the next sample.
    /// </summary>
    public IReadOnlyList<PenSample> Predict()
    {
        Confidence = 0;
        if (_count < 3 || Horizon <= 0) return Array.Empty<PenSample>();

        double vx = _x.V, vy = _y.V;
        double speed = Math.Sqrt(vx * vx + vy * vy);
        if (_scoredAt != _time)
        {
            // What the model says at the full horizon is checked against where the pen gets to, whether or not it is drawn, so
            // that the guess can come back once the pen is predictable again.
            _scoredAt = _time;
            var (ex, ey) = At(Horizon);
            double reach = Math.Sqrt((ex - _x.P) * (ex - _x.P) + (ey - _y.P) * (ey - _y.P));
            _checks.Enqueue((_time + Horizon, ex, ey, reach));
            while (_checks.Count > 64) _checks.Dequeue();
        }
        double samples = Math.Min(1, (_count - 2) / (double)(FullAfter - 2));
        double moving = Math.Clamp((MeasuredSpeed() - SlowSpeed) / (FastSpeed - SlowSpeed), 0, 1);
        // A miss of a third of the guess's length is ordinary at a turn; beyond, the guess shrinks, and by one length it is gone.
        double accurate = Math.Clamp(1 - (_miss - MissOk) / (1 - MissOk), 0, 1);
        double confidence = samples * moving * accurate;
        double horizon = Horizon * confidence;
        // A guess shorter than half a pixel can't be seen; it would only make the end of the line tremble.
        if (speed * horizon < 0.5) return Array.Empty<PenSample>();
        Confidence = confidence;
        int steps = Math.Max(1, (int)Math.Ceiling(horizon / Step));
        double maxTravel = 1.5 * speed * horizon + 1;
        var points = new List<PenSample>(steps);
        double x0 = _x.P, y0 = _y.P, px = x0, py = y0, travel = 0;
        for (int i = 1; i <= steps; i++)
        {
            double t = horizon * i / steps;
            var (nx, ny) = At(t);
            // Never back on itself: once the guessed motion turns against the pen's present direction, stop there.
            double fade = Fade(t), dvx = vx + _x.A * fade * t, dvy = vy + _y.A * fade * t;
            if (dvx * vx + dvy * vy <= 0) break;
            double seg = Math.Sqrt((nx - px) * (nx - px) + (ny - py) * (ny - py));
            if (travel + seg > maxTravel) break;
            travel += seg;
            px = nx;
            py = ny;
            points.Add(new PenSample(nx, ny, _pressure, _time + t));
        }
        return points;
    }

    /// <summary>
    /// The acceleration's share of the guess at t seconds out. Near, it bends the guess along the curve; far, a curve carried on at
    /// full acceleration overshoots into a hook.
    /// </summary>
    double Fade(double t) => 1 / (1 + t / (FadeAt * Horizon));

    /// <summary>Where the filter's state, carried forward, puts the pen t seconds after the last sample.</summary>
    (double X, double Y) At(double t)
    {
        double k = 0.5 * Fade(t) * t * t;
        return (_x.P + _x.V * t + _x.A * k, _y.P + _y.V * t + _y.A * k);
    }

    /// <summary>A Kalman filter for one coordinate: position, velocity and acceleration, with their covariance.</summary>
    sealed class Axis
    {
        public double P, V, A;
        // Covariance, symmetric: [pp pv pa; pv vv va; pa va aa].
        double _pp, _pv, _pa, _vv, _va, _aa;

        public void Start(double position)
        {
            P = position;
            V = 0;
            A = 0;
            // Know the position; the velocity and acceleration could be anything a hand does.
            _pp = SampleNoise;
            _pv = 0;
            _pa = 0;
            _vv = 1e6;
            _va = 0;
            _aa = 1e9;
        }

        public void Predict(double dt)
        {
            double dt2 = dt * dt, dt3 = dt2 * dt;
            P += V * dt + 0.5 * A * dt2;
            V += A * dt;

            // C = F C Fᵀ, with F = [1 dt dt²/2; 0 1 dt; 0 0 1].
            double h = 0.5 * dt2;
            double fp_p = _pp + dt * _pv + h * _pa, fp_v = _pv + dt * _vv + h * _va, fp_a = _pa + dt * _va + h * _aa;
            double fv_v = _vv + dt * _va, fv_a = _va + dt * _aa;
            double pp = fp_p + dt * fp_v + h * fp_a;
            double pv = fp_v + dt * fp_a;
            double pa = fp_a;
            double vv = fv_v + dt * fv_a;
            double va = fv_a;
            double aa = _aa;

            // Plus the noise of a random jerk over dt (continuous white-noise jerk model).
            double q = JerkNoise;
            _pp = pp + q * dt2 * dt3 / 20;
            _pv = pv + q * dt2 * dt2 / 8;
            _pa = pa + q * dt3 / 6;
            _vv = vv + q * dt3 / 3;
            _va = va + q * dt2 / 2;
            _aa = aa + q * dt;
        }

        public void Correct(double measured)
        {
            double s = _pp + SampleNoise;
            double kp = _pp / s, kv = _pv / s, ka = _pa / s;
            double r = measured - P;
            P += kp * r;
            V += kv * r;
            A += ka * r;
            // C = (I - K H) C, H = [1 0 0].
            double pp = _pp, pv = _pv, pa = _pa;
            _pp = pp - kp * pp;
            _pv = pv - kp * pv;
            _pa = pa - kp * pa;
            _vv -= kv * pv;
            _va -= kv * pa;
            _aa -= ka * pa;
        }
    }
}
