'use strict';
// Guesses where the pen is going, so the stroke being written can be drawn a little ahead of the last sample and seem to stick to
// the nib. A line-for-line port of Longhand.Core/StrokePredictor.cs (read there for the why): a Kalman filter per axis (position,
// velocity, acceleration, a random jerk as its noise), fed every sample at its own timestamp; the guess is the filter carried
// forward, shortened for the first samples, a slow pen and recent misses. tools/CoreCheck checks that this file and the C# give the
// same guesses on the same samples, so change both together.
//
// It allocates nothing per sample: the recent samples and the guesses to check live in ring buffers, and predict() writes the
// guess into gx, gy (and gt, its times) and returns how many points it has.

const JERK_NOISE = 4e9, FADE_AT = 2, MISS_OK = 0.35, SAMPLE_NOISE = 0.04, FULL_AFTER = 6;
const SLOW_SPEED = 40, FAST_SPEED = 110, MAX_GAP = 0.1, SPEED_WINDOW = 0.03, MAX_STEPS = 64;

class PredictorAxis {
  constructor() { this.p = this.v = this.a = 0; this.pp = this.pv = this.pa = this.vv = this.va = this.aa = 0; }

  start(position) {
    this.p = position; this.v = 0; this.a = 0;
    this.pp = SAMPLE_NOISE; this.pv = 0; this.pa = 0; this.vv = 1e6; this.va = 0; this.aa = 1e9;
  }

  predict(dt) {
    const dt2 = dt * dt, dt3 = dt2 * dt, h = 0.5 * dt2;
    this.p += this.v * dt + 0.5 * this.a * dt2;
    this.v += this.a * dt;
    const fpP = this.pp + dt * this.pv + h * this.pa, fpV = this.pv + dt * this.vv + h * this.va, fpA = this.pa + dt * this.va + h * this.aa;
    const fvV = this.vv + dt * this.va, fvA = this.va + dt * this.aa;
    const pp = fpP + dt * fpV + h * fpA, pv = fpV + dt * fpA, pa = fpA, vv = fvV + dt * fvA, va = fvA, aa = this.aa;
    const q = JERK_NOISE;
    this.pp = pp + q * dt2 * dt3 / 20;
    this.pv = pv + q * dt2 * dt2 / 8;
    this.pa = pa + q * dt3 / 6;
    this.vv = vv + q * dt3 / 3;
    this.va = va + q * dt2 / 2;
    this.aa = aa + q * dt;
  }

  correct(measured) {
    const s = this.pp + SAMPLE_NOISE, kp = this.pp / s, kv = this.pv / s, ka = this.pa / s, r = measured - this.p;
    this.p += kp * r; this.v += kv * r; this.a += ka * r;
    const pp = this.pp, pv = this.pv, pa = this.pa;
    this.pp = pp - kp * pp; this.pv = pv - kp * pv; this.pa = pa - kp * pa;
    this.vv -= kv * pv; this.va -= kv * pa; this.aa -= ka * pa;
  }
}

class StrokePredictor {
  // horizon: how far ahead to guess at full confidence, in seconds. step: the spacing in time of the guessed points.
  constructor(horizon = 0.025, step = 0.004) {
    this.horizon = horizon;
    this.step = step;
    this.x = new PredictorAxis();
    this.y = new PredictorAxis();
    // The recent samples (x, y, time), a ring: for the measured speed, and the one before the newest for scoring.
    this.rx = new Float64Array(64); this.ry = new Float64Array(64); this.rt = new Float64Array(64);
    this.rStart = 0; this.rCount = 0;
    // The guesses waiting to be checked (time, x, y, reach), a ring.
    this.ct = new Float64Array(64); this.cx = new Float64Array(64); this.cy = new Float64Array(64); this.cr = new Float64Array(64);
    this.cStart = 0; this.cCount = 0;
    // The last guess.
    this.gx = new Float64Array(MAX_STEPS); this.gy = new Float64Array(MAX_STEPS); this.gt = new Float64Array(MAX_STEPS);
    this.reset();
  }

  reset() {
    this.count = 0; this.rCount = 0; this.cCount = 0; this.miss = 0; this.scoredAt = NaN; this.confidence = 0;
    this.time = 0; this.pressure = 0;
  }

  // A sample of the stroke: page pixels, pressure 0 to 1, time in seconds. In the order they were taken; older ones are ignored.
  add(x, y, pressure, time) {
    if (this.count > 0 && time <= this.time) {
      if (time < this.time) return;
      this.x.correct(x); this.y.correct(y); this.pressure = pressure;
      return;
    }
    if (this.count === 0 || time - this.time > MAX_GAP) {
      this.x.start(x); this.y.start(y);
      this.rCount = 0; this.cCount = 0; this.miss = 0; this.count = 1;
    } else {
      const dt = time - this.time;
      this.x.predict(dt); this.y.predict(dt);
      this.x.correct(x); this.y.correct(y);
      this.count++;
    }
    let ax = x, ay = y, at = time;
    if (this.rCount > 0) {
      const last = (this.rStart + this.rCount - 1) & 63;
      ax = this.rx[last]; ay = this.ry[last]; at = this.rt[last];
    }
    if (this.rCount === 64) { this.rStart = (this.rStart + 1) & 63; this.rCount--; }
    const slot = (this.rStart + this.rCount) & 63;
    this.rx[slot] = x; this.ry[slot] = y; this.rt[slot] = time; this.rCount++;
    while (this.rCount > 2 && time - this.rt[(this.rStart + 1) & 63] >= SPEED_WINDOW) { this.rStart = (this.rStart + 1) & 63; this.rCount--; }
    this.time = time;
    this.pressure = pressure;
    this.score(ax, ay, at, x, y, time);
  }

  measuredSpeed() {
    const first = this.rStart, last = (this.rStart + this.rCount - 1) & 63, dt = this.rt[last] - this.rt[first];
    if (dt <= 0) return 0;
    return Math.hypot(this.rx[last] - this.rx[first], this.ry[last] - this.ry[first]) / dt;
  }

  score(ax, ay, at, bx, by, bt) {
    while (this.cCount > 0 && this.ct[this.cStart] <= bt) {
      const i = this.cStart;
      this.cStart = (this.cStart + 1) & 63; this.cCount--;
      const reach = this.cr[i];
      if (reach < 1) continue;
      const f = bt > at ? Math.min(1, Math.max(0, (this.ct[i] - at) / (bt - at))) : 1;
      const x = ax + f * (bx - ax), y = ay + f * (by - ay);
      const miss = Math.min(2, Math.hypot(this.cx[i] - x, this.cy[i] - y) / reach);
      this.miss += 0.3 * (miss - this.miss);
    }
  }

  fade(t) { return 1 / (1 + t / (FADE_AT * this.horizon)); }

  // The guess beyond the last sample, written into gx, gy, gt; returns how many points. Draw it after the stroke's real points,
  // and draw it again (not add to it) after the next sample.
  predict() {
    this.confidence = 0;
    if (this.count < 3 || this.horizon <= 0) return 0;
    const X = this.x, Y = this.y, vx = X.v, vy = Y.v, speed = Math.hypot(vx, vy);
    if (this.scoredAt !== this.time) {
      this.scoredAt = this.time;
      const k = 0.5 * this.fade(this.horizon) * this.horizon * this.horizon;
      const ex = X.p + X.v * this.horizon + X.a * k, ey = Y.p + Y.v * this.horizon + Y.a * k;
      if (this.cCount === 64) { this.cStart = (this.cStart + 1) & 63; this.cCount--; }
      const slot = (this.cStart + this.cCount) & 63;
      this.ct[slot] = this.time + this.horizon; this.cx[slot] = ex; this.cy[slot] = ey; this.cr[slot] = Math.hypot(ex - X.p, ey - Y.p);
      this.cCount++;
    }
    const samples = Math.min(1, (this.count - 2) / (FULL_AFTER - 2));
    const moving = Math.min(1, Math.max(0, (this.measuredSpeed() - SLOW_SPEED) / (FAST_SPEED - SLOW_SPEED)));
    const accurate = Math.min(1, Math.max(0, 1 - (this.miss - MISS_OK) / (1 - MISS_OK)));
    const confidence = samples * moving * accurate, horizon = this.horizon * confidence;
    if (speed * horizon < 0.5) return 0;
    this.confidence = confidence;
    const steps = Math.min(MAX_STEPS, Math.max(1, Math.ceil(horizon / this.step)));
    const maxTravel = 1.5 * speed * horizon + 1;
    let px = X.p, py = Y.p, travel = 0, n = 0;
    for (let i = 1; i <= steps; i++) {
      const t = horizon * i / steps, f = this.fade(t), k = 0.5 * f * t * t;
      const nx = X.p + vx * t + X.a * k, ny = Y.p + vy * t + Y.a * k;
      const dvx = vx + X.a * f * t, dvy = vy + Y.a * f * t;
      if (dvx * vx + dvy * vy <= 0) break;
      const seg = Math.hypot(nx - px, ny - py);
      if (travel + seg > maxTravel) break;
      travel += seg; px = nx; py = ny;
      this.gx[n] = nx; this.gy[n] = ny; this.gt[n] = this.time + t; n++;
    }
    return n;
  }
}

if (typeof module !== 'undefined') module.exports = { StrokePredictor };
