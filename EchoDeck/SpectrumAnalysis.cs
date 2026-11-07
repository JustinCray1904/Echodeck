namespace EchoDeck;

/// <summary>The song's frequency content over time: <see cref="Bands"/> log-spaced bands (bass .. treble), 0..255, one frame every 1/<see cref="FramesPerSecond"/> s.</summary>
internal sealed class SpectrumData
{
    public const int Bands = 48;
    public const int FramesPerSecond = 30;

    public SpectrumData(byte[] values, int frames)
    {
        Values = values;
        Frames = frames;
    }

    public byte[] Values { get; }
    public int Frames { get; }
}

/// <summary>
/// Turns decoded mono samples into <see cref="SpectrumData"/>: a Hann-windowed FFT every 1/30 s, grouped into log-spaced bands,
/// tilted to compensate for music's falling spectrum, then scaled per song so quiet and loud tracks both fill the visualiser.
/// (A small FFT of our own: nothing to download, no extra native files.)
/// </summary>
internal sealed class SpectrumBuilder
{
    private const int N = 2048;                 // window: ~46 ms at 44.1 kHz
    private const double LowHz = 40, HighHz = 14000;

    private readonly int _sampleRate;
    private readonly int _hop;
    private readonly float[] _ring = new float[N];
    private readonly float[] _window = new float[N];
    private readonly double[] _re = new double[N];
    private readonly double[] _im = new double[N];
    private readonly int[] _bandLo = new int[SpectrumData.Bands];
    private readonly int[] _bandHi = new int[SpectrumData.Bands];
    private readonly double[] _tilt = new double[SpectrumData.Bands];
    private readonly List<float> _db = new();
    private int _ringPos;
    private long _monoCount;
    private int _frames;

    public SpectrumBuilder(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _hop = Math.Max(1, _sampleRate / SpectrumData.FramesPerSecond);

        for (int i = 0; i < N; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)));

        double binHz = (double)_sampleRate / N;
        double top = Math.Min(HighHz, _sampleRate / 2.0 - binHz);
        for (int b = 0; b < SpectrumData.Bands; b++)
        {
            double f0 = LowHz * Math.Pow(top / LowHz, (double)b / SpectrumData.Bands);
            double f1 = LowHz * Math.Pow(top / LowHz, (double)(b + 1) / SpectrumData.Bands);
            int lo = Math.Clamp((int)Math.Round(f0 / binHz), 1, N / 2 - 1);
            int hi = Math.Clamp((int)Math.Round(f1 / binHz), lo, N / 2 - 1);
            _bandLo[b] = lo;
            _bandHi[b] = hi;
            _tilt[b] = 3.5 * Math.Log2(Math.Sqrt(f0 * f1) / 500.0);   // +3.5 dB per octave above 500 Hz
        }
    }

    /// <summary>Feeds one mono sample; every hop a frame is analysed.</summary>
    public void Push(float sample)
    {
        _ring[_ringPos] = sample;
        _ringPos = (_ringPos + 1) % N;
        _monoCount++;
        if (_monoCount >= N && _monoCount % _hop == 0) AnalyseFrame();
    }

    private void AnalyseFrame()
    {
        for (int i = 0; i < N; i++)
        {
            _re[i] = _ring[(_ringPos + i) % N] * _window[i];   // oldest sample first
            _im[i] = 0;
        }
        Fft(_re, _im);

        for (int b = 0; b < SpectrumData.Bands; b++)
        {
            double sum = 0;
            for (int k = _bandLo[b]; k <= _bandHi[b]; k++) sum += _re[k] * _re[k] + _im[k] * _im[k];
            double mag = Math.Sqrt(sum / (_bandHi[b] - _bandLo[b] + 1)) / (N / 4.0);
            _db.Add((float)(20 * Math.Log10(mag + 1e-7) + _tilt[b]));
        }
        _frames++;
    }

    /// <summary>Finished: scales every band to 0..255 using this song's own loud / quiet levels.</summary>
    public SpectrumData? Build()
    {
        if (_frames < 10) return null;

        var sorted = _db.ToArray();
        // percentiles from an evenly spaced sample (cheap even for a long mix)
        int step = Math.Max(1, sorted.Length / 20000);
        var sample = new List<float>(sorted.Length / step + 1);
        for (int i = 0; i < sorted.Length; i += step) sample.Add(sorted[i]);
        sample.Sort();
        double floor = sample[(int)(sample.Count * 0.08)];
        double ceil = sample[Math.Min(sample.Count - 1, (int)(sample.Count * 0.985))];
        if (ceil - floor < 6) ceil = floor + 6;

        var bytes = new byte[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            double v = (sorted[i] - floor) / (ceil - floor);
            bytes[i] = (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        }
        return new SpectrumData(bytes, _frames);
    }

    /// <summary>In-place radix-2 FFT (length must be a power of two).</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tRe = re[b] * curRe - im[b] * curIm;
                    double tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    double nRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nRe;
                }
            }
        }
    }
}
