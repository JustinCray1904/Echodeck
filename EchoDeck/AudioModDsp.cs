namespace EchoDeck;

/// <summary>
/// The sound processing behind Audio Mod, on whole songs (two channels of float samples, -1..1). Pure functions with no window and no
/// file: speed and pitch (a WSOLA time stretch and a cubic resampler), low / high pass (two biquad sections each), distortion, echo and
/// a Freeverb-style reverb. Processing order: stretch + resample, filters, distortion, echo, reverb, then the level is brought down
/// if it would clip. The arrays passed in may be changed.
/// </summary>
internal static class AudioModDsp
{
    /// <summary>Runs the settings over a song. <paramref name="progress"/> gets 0..1 after every stage.</summary>
    public static (float[] Left, float[] Right) Process(float[] left, float[] right, int sampleRate, AudioModSettings settings,
        Action<double>? progress = null, CancellationToken ct = default)
    {
        var s = settings.Clamp();
        double tempo = s.SpeedPercent / 100.0;
        double pitch = Math.Pow(2, s.PitchSemitones / 12.0);
        double stretch = pitch / tempo;   // resampling by "pitch" changes the length by 1/pitch; this puts the length right (1/tempo)

        var (l, r) = (left, right);
        if (Math.Abs(stretch - 1) > 0.002) (l, r) = TimeStretch(l, r, sampleRate, stretch, ct);
        progress?.Invoke(0.25);
        ct.ThrowIfCancellationRequested();
        if (Math.Abs(pitch - 1) > 0.0005) (l, r) = Resample(l, r, pitch);
        progress?.Invoke(0.4);
        ct.ThrowIfCancellationRequested();

        if (s.LowPass > 0)
        {
            double hz = LowPassHz(s.LowPass, sampleRate);
            Filter(l, lowPass: true, hz, sampleRate);
            Filter(r, lowPass: true, hz, sampleRate);
        }
        if (s.HighPass > 0)
        {
            double hz = HighPassHz(s.HighPass, sampleRate);
            Filter(l, lowPass: false, hz, sampleRate);
            Filter(r, lowPass: false, hz, sampleRate);
        }
        progress?.Invoke(0.55);
        ct.ThrowIfCancellationRequested();

        if (s.Distort)
        {
            Distort(l);
            Distort(r);
        }

        // echoes and the reverb ring on after the song's last note: make room for them
        double echoSeconds = EchoSeconds(s.DelayTime);
        int tail = 0;
        if (s.DelayOn) tail = Math.Max(tail, (int)(Math.Min(6.0, echoSeconds * 6) * sampleRate));
        if (s.ReverbOn && s.Reverb > 0) tail = Math.Max(tail, 3 * sampleRate);
        if (tail > 0) (l, r) = Pad(l, r, tail);

        if (s.DelayOn)
        {
            double feedback = 0.15 + s.DelayIntensity * 0.055;
            double mix = 0.2 + s.DelayIntensity * 0.05;
            Echo(l, sampleRate, echoSeconds, feedback, mix);
            Echo(r, sampleRate, echoSeconds, feedback, mix);
        }
        progress?.Invoke(0.7);
        ct.ThrowIfCancellationRequested();

        if (s.ReverbOn && s.Reverb > 0) Reverb(l, r, sampleRate, s.Reverb / 100.0);
        progress?.Invoke(0.9);
        ct.ThrowIfCancellationRequested();

        KeepFromClipping(l, r);
        progress?.Invoke(1.0);
        return (l, r);
    }

    // ------------------------------------------------------------------ the numbers on the sliders, in Hz and seconds

    /// <summary>1 = barely muffled (18 kHz) ... 100 = very muffled (300 Hz), in even musical steps.</summary>
    internal static double LowPassHz(int value, int sampleRate) =>
        Math.Min(18000.0 * Math.Pow(300.0 / 18000.0, value / 100.0), sampleRate * 0.45);

    /// <summary>1 = barely thinner (about 23 Hz) ... 100 = thin (2 kHz).</summary>
    internal static double HighPassHz(int value, int sampleRate) =>
        Math.Min(20.0 * Math.Pow(2000.0 / 20.0, value / 100.0), sampleRate * 0.45);

    /// <summary>The echo's time knob (0..10) as seconds between the echoes.</summary>
    internal static double EchoSeconds(int knob) => 0.08 + knob * 0.09;

    // ------------------------------------------------------------------ speed and pitch

    /// <summary>
    /// WSOLA: changes the length of the song by <paramref name="alpha"/> (output length / input length) without changing the pitch.
    /// Short overlapping pieces are laid next to each other at the new spacing, each one shifted a little to where it fits the previous
    /// piece best (the best match of every 4th sample, then refined).
    /// </summary>
    internal static (float[] Left, float[] Right) TimeStretch(float[] l, float[] r, int sampleRate, double alpha, CancellationToken ct = default)
    {
        int n = l.Length;
        int frame = Math.Max(256, (int)Math.Round(1024.0 * sampleRate / 44100 / 2) * 2);
        int hop = frame / 2, delta = frame / 4;
        int outLen = (int)(n * alpha);
        if (n < frame * 2 || outLen < frame) return (l, r);   // too short to stretch

        var win = new float[frame];
        for (int i = 0; i < frame; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / frame));

        var mono = new float[n];
        for (int i = 0; i < n; i++) mono[i] = (l[i] + r[i]) * 0.5f;

        var outL = new float[outLen + frame];
        var outR = new float[outLen + frame];
        var norm = new float[outLen + frame];
        double inputHop = hop / alpha;
        int prev = 0;

        for (int k = 0; ; k++)
        {
            int outPos = k * hop;
            int nominal = (int)Math.Round(k * inputHop);
            if (outPos > outLen || nominal + frame + delta >= n) break;

            int pos;
            if (k == 0)
            {
                pos = 0;
            }
            else
            {
                int refStart = prev + hop;
                int lo = Math.Max(0, nominal - delta);
                int hi = Math.Min(n - frame, nominal + delta);
                if (hi < lo)
                {
                    pos = Math.Clamp(nominal, 0, n - frame);
                }
                else
                {
                    int best = lo;
                    double bestValue = double.MinValue;
                    for (int c = lo; c <= hi; c += 2)
                    {
                        double v = Similarity(mono, c, refStart, hop);
                        if (v > bestValue) { bestValue = v; best = c; }
                    }
                    int around = best;
                    for (int c = around - 1; c <= around + 1; c += 2)
                    {
                        if (c < lo || c > hi) continue;
                        double v = Similarity(mono, c, refStart, hop);
                        if (v > bestValue) { bestValue = v; best = c; }
                    }
                    pos = best;
                }
            }
            if (pos + frame > n) break;

            for (int i = 0; i < frame; i++)
            {
                float w = win[i];
                outL[outPos + i] += l[pos + i] * w;
                outR[outPos + i] += r[pos + i] * w;
                norm[outPos + i] += w;
            }
            prev = pos;
            if ((k & 127) == 0) ct.ThrowIfCancellationRequested();
        }

        var resultL = new float[outLen];
        var resultR = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            float w = norm[i];
            if (w > 1e-3f) { resultL[i] = outL[i] / w; resultR[i] = outR[i] / w; }
        }
        return (resultL, resultR);
    }

    /// <summary>How alike two pieces of the song are (cross-correlation over every 4th sample).</summary>
    private static double Similarity(float[] x, int a, int b, int length)
    {
        int limit = Math.Min(length, x.Length - Math.Max(a, b));
        double sum = 0;
        for (int i = 0; i < limit; i += 4) sum += (double)x[a + i] * x[b + i];
        return sum;
    }

    /// <summary>Reads the song <paramref name="ratio"/> times as fast (cubic interpolation): the pitch is multiplied by it, the length divided.</summary>
    internal static (float[] Left, float[] Right) Resample(float[] l, float[] r, double ratio)
    {
        int n = l.Length;
        int m = (int)((n - 1) / ratio);
        if (m < 1) return (l, r);

        var outL = new float[m];
        var outR = new float[m];
        for (int j = 0; j < m; j++)
        {
            double position = j * ratio;
            int i = (int)position;
            float f = (float)(position - i);
            outL[j] = Cubic(l, i, f, n);
            outR[j] = Cubic(r, i, f, n);
        }
        return (outL, outR);
    }

    private static float Cubic(float[] x, int i, float f, int n)
    {
        float p0 = x[Math.Max(i - 1, 0)];
        float p1 = x[Math.Min(i, n - 1)];
        float p2 = x[Math.Min(i + 1, n - 1)];
        float p3 = x[Math.Min(i + 2, n - 1)];
        return p1 + 0.5f * f * (p2 - p0 + f * (2 * p0 - 5 * p1 + 4 * p2 - p3 + f * (3 * (p1 - p2) + p3 - p0)));
    }

    // ------------------------------------------------------------------ filters, distortion, echo, reverb

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public Biquad(bool lowPass, double hz, int sampleRate, double q)
        {
            double w0 = 2 * Math.PI * hz / sampleRate;
            double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
            double b0, b1, b2;
            if (lowPass) { b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2; }
            else { b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2; }
            double a0 = 1 + alpha;
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0;
            _a1 = -2 * cos / a0; _a2 = (1 - alpha) / a0;
        }

        public float Process(float x)
        {
            double o = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * o + _z2;
            _z2 = _b2 * x - _a2 * o;
            return (float)o;
        }
    }

    /// <summary>Two 12 dB-per-octave sections one after the other (24 dB per octave).</summary>
    internal static void Filter(float[] x, bool lowPass, double hz, int sampleRate)
    {
        var first = new Biquad(lowPass, hz, sampleRate, 0.7071);
        var second = new Biquad(lowPass, hz, sampleRate, 0.7071);
        for (int i = 0; i < x.Length; i++) x[i] = second.Process(first.Process(x[i]));
    }

    /// <summary>Soft clipping (tanh): quiet sound is made louder and the peaks are rounded off.</summary>
    internal static void Distort(float[] x)
    {
        const double drive = 3.0;
        double scale = 1.0 / Math.Tanh(drive);
        for (int i = 0; i < x.Length; i++) x[i] = (float)(Math.Tanh(drive * x[i]) * scale);
    }

    private static (float[], float[]) Pad(float[] l, float[] r, int extra)
    {
        var outL = new float[l.Length + extra];
        var outR = new float[r.Length + extra];
        Array.Copy(l, outL, l.Length);
        Array.Copy(r, outR, r.Length);
        return (outL, outR);
    }

    /// <summary>A feedback delay: every echo is quieter by <paramref name="feedback"/>, and <paramref name="mix"/> sets how loud they are against the song.</summary>
    internal static void Echo(float[] x, int sampleRate, double seconds, double feedback, double mix)
    {
        int length = Math.Max(1, (int)(seconds * sampleRate));
        var buffer = new float[length];
        int index = 0;
        float fb = (float)feedback, wet = (float)mix;
        for (int i = 0; i < x.Length; i++)
        {
            float echo = buffer[index];
            buffer[index] = x[i] + fb * echo;
            x[i] += wet * echo;
            if (++index >= length) index = 0;
        }
    }

    private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllPassTuning = { 556, 441, 341, 225 };

    /// <summary>
    /// Freeverb: eight damped comb filters and four all-pass filters per channel (the right channel slightly longer, for width).
    /// The reverb is added to the song; at <paramref name="amount"/> 1 it is about as loud as the song itself for sound that is the same
    /// in both channels (centred voices, bass), and a little quieter for wide stereo.
    /// </summary>
    internal static void Reverb(float[] l, float[] r, int sampleRate, double amount)
    {
        int n = l.Length;
        var input = new float[n];
        // 0.0411: measured with the same noise in both channels, which makes the reverb as loud as that noise at amount 1
        for (int i = 0; i < n; i++) input[i] = (l[i] + r[i]) * 0.5f * 0.0411f;

        var wetL = ReverbChannel(input, sampleRate, 0);
        var wetR = ReverbChannel(input, sampleRate, 23);
        float gain = (float)amount;
        for (int i = 0; i < n; i++)
        {
            l[i] += wetL[i] * gain;
            r[i] += wetR[i] * gain;
        }
    }

    private static float[] ReverbChannel(float[] input, int sampleRate, int spread)
    {
        int n = input.Length;
        double scale = sampleRate / 44100.0;
        const float feedback = 0.7f * 0.28f + 0.7f, damp = 0.5f * 0.4f;
        var sum = new float[n];

        foreach (int tuning in CombTuning)
        {
            int length = Math.Max(1, (int)Math.Round((tuning + spread) * scale));
            var buffer = new float[length];
            int index = 0;
            float store = 0f;
            for (int i = 0; i < n; i++)
            {
                float v = buffer[index];
                store = v * (1 - damp) + store * damp;
                buffer[index] = input[i] + store * feedback;
                sum[i] += v;
                if (++index >= length) index = 0;
            }
        }

        foreach (int tuning in AllPassTuning)
        {
            int length = Math.Max(1, (int)Math.Round((tuning + spread) * scale));
            var buffer = new float[length];
            int index = 0;
            for (int i = 0; i < n; i++)
            {
                float stored = buffer[index];
                float v = sum[i];
                sum[i] = -v + stored;
                buffer[index] = v + stored * 0.5f;
                if (++index >= length) index = 0;
            }
        }
        return sum;
    }

    /// <summary>If the loudest sample is over 0.98, everything is turned down together so that it is not (no clipping, no pumping).</summary>
    internal static void KeepFromClipping(float[] l, float[] r)
    {
        float peak = 0f;
        for (int i = 0; i < l.Length; i++)
        {
            float a = Math.Abs(l[i]), b = Math.Abs(r[i]);
            if (a > peak) peak = a;
            if (b > peak) peak = b;
        }
        if (peak <= 0.98f) return;

        float scale = 0.98f / peak;
        for (int i = 0; i < l.Length; i++)
        {
            l[i] *= scale;
            r[i] *= scale;
        }
    }
}
