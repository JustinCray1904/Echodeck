using System;
using System.IO;
using System.Linq;
using System.Threading;
using EchoDeck;
using Xunit;

namespace EchoDeck.Tests;

/// <summary>Audio Mod's sound processing, checked with plain tones: the length, the pitch, what the filters let through, the echo, the reverb.</summary>
public class AudioModDspTests
{
    private const int Rate = 22050;   // a lower rate keeps the tests quick

    private static float[] Sine(double hz, double seconds, double amplitude = 0.4)
    {
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        return x;
    }

    /// <summary>The frequency of a clean tone, from the upward zero crossings in the middle of the sound.</summary>
    private static double Frequency(float[] x)
    {
        int begin = x.Length / 5, finish = x.Length * 4 / 5, crossings = 0;
        for (int i = begin + 1; i < finish; i++)
            if (x[i - 1] < 0 && x[i] >= 0) crossings++;
        return crossings / ((finish - begin) / (double)Rate);
    }

    private static float Peak(float[] x, int start = 0)
    {
        float peak = 0;
        for (int i = start; i < x.Length; i++) peak = Math.Max(peak, Math.Abs(x[i]));
        return peak;
    }

    private static double Rms(float[] x, int start)
    {
        double sum = 0;
        for (int i = start; i < x.Length; i++) sum += (double)x[i] * x[i];
        return Math.Sqrt(sum / (x.Length - start));
    }

    private static (float[] L, float[] R) Run(float[] tone, AudioModSettings settings) =>
        AudioModDsp.Process((float[])tone.Clone(), (float[])tone.Clone(), Rate, settings);

    [Fact]
    public void Untouched_settings_leave_the_sound_as_it_is()
    {
        var tone = Sine(440, 1);
        var (l, r) = Run(tone, new AudioModSettings());
        Assert.Equal(tone, l);
        Assert.Equal(tone, r);
    }

    [Theory]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(70)]
    [InlineData(50)]
    public void Speed_changes_the_length_but_not_the_pitch(int percent)
    {
        var tone = Sine(440, 3);
        var (l, _) = Run(tone, new AudioModSettings { SpeedPercent = percent });

        Assert.InRange(l.Length / (double)tone.Length, 100.0 / percent * 0.98, 100.0 / percent * 1.02);
        Assert.InRange(Frequency(l), 435, 445);
    }

    [Theory]
    [InlineData(12, 880.0)]
    [InlineData(-12, 220.0)]
    [InlineData(4, 554.4)]
    [InlineData(-3, 370.0)]
    public void Pitch_moves_the_tone_but_not_the_length(int semitones, double expectedHz)
    {
        var tone = Sine(440, 3);
        var (l, _) = Run(tone, new AudioModSettings { PitchSemitones = semitones });

        Assert.InRange(l.Length / (double)tone.Length, 0.98, 1.02);
        Assert.InRange(Frequency(l), expectedHz * 0.985, expectedHz * 1.015);
    }

    [Fact]
    public void Speed_and_pitch_work_together_as_in_the_slowed_preset()
    {
        var tone = Sine(440, 3);
        var (l, _) = Run(tone, new AudioModSettings { SpeedPercent = 84, PitchSemitones = -2 });

        Assert.InRange(l.Length / (double)tone.Length, 1 / 0.84 * 0.98, 1 / 0.84 * 1.02);
        double expected = 440 * Math.Pow(2, -2 / 12.0);
        Assert.InRange(Frequency(l), expected * 0.985, expected * 1.015);
    }

    [Fact]
    public void Low_pass_removes_high_tones_and_keeps_low_ones()
    {
        var (high, _) = Run(Sine(4000, 1), new AudioModSettings { LowPass = 100 });   // 300 Hz cut-off
        var (low, _) = Run(Sine(100, 1), new AudioModSettings { LowPass = 100 });
        Assert.True(Peak(high, Rate / 4) < 0.02f, "4 kHz should be gone");
        Assert.True(Peak(low, Rate / 4) > 0.3f, "100 Hz should stay");
    }

    [Fact]
    public void High_pass_removes_low_tones_and_keeps_high_ones()
    {
        var (low, _) = Run(Sine(100, 1), new AudioModSettings { HighPass = 100 });    // 2 kHz cut-off
        var (high, _) = Run(Sine(6000, 1), new AudioModSettings { HighPass = 100 });
        Assert.True(Peak(low, Rate / 4) < 0.02f, "100 Hz should be gone");
        Assert.True(Peak(high, Rate / 4) > 0.3f, "6 kHz should stay");
    }

    [Fact]
    public void Slider_values_map_to_rising_cut_off_frequencies_inside_the_audible_range()
    {
        Assert.True(AudioModDsp.LowPassHz(1, 44100) > AudioModDsp.LowPassHz(50, 44100));
        Assert.True(AudioModDsp.LowPassHz(50, 44100) > AudioModDsp.LowPassHz(100, 44100));
        Assert.InRange(AudioModDsp.LowPassHz(100, 44100), 290, 310);
        Assert.True(AudioModDsp.HighPassHz(1, 44100) < AudioModDsp.HighPassHz(50, 44100));
        Assert.InRange(AudioModDsp.HighPassHz(100, 44100), 1990, 2010);
        Assert.True(AudioModDsp.LowPassHz(1, 8000) <= 8000 * 0.45 + 0.01);   // never above what the sample rate can carry
    }

    [Fact]
    public void Distortion_makes_quiet_sound_louder_and_rounds_the_peaks()
    {
        var (l, _) = Run(Sine(300, 1, amplitude: 0.2), new AudioModSettings { Distort = true });
        Assert.True(Peak(l) > 0.4f);
        Assert.True(Peak(l) <= 0.981f);
    }

    [Fact]
    public void Echo_repeats_the_sound_after_the_set_time_and_makes_room_for_it()
    {
        var impulse = new float[Rate * 2];
        impulse[0] = 0.5f;
        var settings = new AudioModSettings { DelayOn = true, DelayTime = 4, DelayIntensity = 2 };

        var (l, _) = AudioModDsp.Process((float[])impulse.Clone(), (float[])impulse.Clone(), Rate, settings);

        Assert.True(l.Length > impulse.Length, "the echoes need room after the last sample");
        int expected = (int)(AudioModDsp.EchoSeconds(4) * Rate);
        int found = 0;
        float best = 0;
        for (int i = 1000; i < l.Length; i++)
            if (Math.Abs(l[i]) > best) { best = Math.Abs(l[i]); found = i; }
        Assert.InRange(found, expected - 3, expected + 3);
        Assert.InRange(best, 0.1f, 0.2f);   // 0.5 x the echo level (0.2 + 2 x 0.05)
    }

    [Fact]
    public void Reverb_adds_a_tail_after_the_sound_and_keeps_the_sound()
    {
        var impulse = new float[Rate * 2];
        impulse[0] = 0.5f;

        var (l, _) = AudioModDsp.Process((float[])impulse.Clone(), (float[])impulse.Clone(), Rate,
            new AudioModSettings { ReverbOn = true, Reverb = 100 });

        Assert.Equal(impulse.Length + 3 * Rate, l.Length);
        Assert.InRange(l[0], 0.45f, 0.55f);                 // the sound itself is still there
        Assert.True(Peak(l, Rate / 10) > 0.005f);           // and the room rings on after it
    }

    [Fact]
    public void Full_reverb_is_about_as_loud_as_the_sound_it_is_added_to()
    {
        var rng = new Random(7);
        var noise = new float[Rate * 2];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)((rng.NextDouble() * 2 - 1) * 0.1);

        var (l, _) = AudioModDsp.Process((float[])noise.Clone(), (float[])noise.Clone(), Rate,
            new AudioModSettings { ReverbOn = true, Reverb = 100 });

        double ratio = Rms(l, Rate / 2) / Rms(noise, Rate / 2);
        Assert.InRange(ratio, 1.25, 1.6);   // dry + wet of the same level: about 1.41 times
    }

    [Fact]
    public void A_loud_result_is_turned_down_instead_of_clipping()
    {
        var (l, r) = Run(Sine(440, 1, amplitude: 1.5), new AudioModSettings());
        Assert.True(Peak(l) <= 0.981f);
        Assert.True(Peak(r) <= 0.981f);
    }

    [Fact]
    public void A_cancelled_job_stops()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tone = Sine(440, 3);

        Assert.Throws<OperationCanceledException>(() =>
            AudioModDsp.Process((float[])tone.Clone(), (float[])tone.Clone(), Rate, new AudioModSettings { SpeedPercent = 80 }, null, cts.Token));
    }

    [Fact]
    public void Progress_reports_every_stage_and_ends_at_one()
    {
        var seen = new System.Collections.Generic.List<double>();
        var tone = Sine(440, 1);

        AudioModDsp.Process((float[])tone.Clone(), (float[])tone.Clone(), Rate, new AudioModSettings { Distort = true }, seen.Add);

        Assert.NotEmpty(seen);
        Assert.Equal(seen.OrderBy(v => v), seen);
        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public void A_clip_too_short_to_stretch_is_left_alone_instead_of_failing()
    {
        var tone = Sine(440, 0.02);
        var (l, _) = Run(tone, new AudioModSettings { SpeedPercent = 80 });
        Assert.Equal(tone.Length, l.Length);
    }
}

public class AudioModSettingsTests
{
    [Fact]
    public void Values_are_kept_in_range()
    {
        var c = new AudioModSettings
        {
            SpeedPercent = 999, PitchSemitones = -99, Reverb = 500, DelayTime = -4, DelayIntensity = 77, LowPass = 1000, HighPass = -1
        }.Clamp();

        Assert.Equal(200, c.SpeedPercent);
        Assert.Equal(-12, c.PitchSemitones);
        Assert.Equal(100, c.Reverb);
        Assert.Equal(0, c.DelayTime);
        Assert.Equal(10, c.DelayIntensity);
        Assert.Equal(100, c.LowPass);
        Assert.Equal(0, c.HighPass);
    }

    [Fact]
    public void Untouched_settings_are_neutral_and_any_change_is_not()
    {
        Assert.True(new AudioModSettings().IsNeutral);
        Assert.True(new AudioModSettings { ReverbOn = true, Reverb = 0 }.IsNeutral);          // reverb on but none of it
        Assert.True(new AudioModSettings { Reverb = 80, DelayTime = 9 }.IsNeutral);           // amounts of things that are off
        Assert.False(new AudioModSettings { SpeedPercent = 99 }.IsNeutral);
        Assert.False(new AudioModSettings { PitchSemitones = 1 }.IsNeutral);
        Assert.False(new AudioModSettings { Distort = true }.IsNeutral);
        Assert.False(new AudioModSettings { DelayOn = true }.IsNeutral);
        Assert.False(new AudioModSettings { LowPass = 1 }.IsNeutral);
        Assert.False(new AudioModSettings { HighPass = 1 }.IsNeutral);
    }

    [Fact]
    public void The_key_ignores_settings_that_are_switched_off()
    {
        Assert.Equal(new AudioModSettings { Reverb = 10 }.Key(), new AudioModSettings { Reverb = 90 }.Key());
        Assert.Equal(new AudioModSettings { DelayTime = 1 }.Key(), new AudioModSettings { DelayTime = 8 }.Key());
        Assert.NotEqual(new AudioModSettings { ReverbOn = true, Reverb = 10 }.Key(), new AudioModSettings { ReverbOn = true, Reverb = 90 }.Key());
        Assert.NotEqual(new AudioModSettings { SpeedPercent = 90 }.Key(), new AudioModSettings { SpeedPercent = 91 }.Key());
    }

    [Fact]
    public void Settings_survive_being_saved_as_json()
    {
        var s = new AudioModSettings { SpeedPercent = 84, PitchSemitones = -2, ReverbOn = true, Reverb = 24, LowPass = 7 };
        Assert.Equal(s, AudioModSettings.FromJson(s.ToJson()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void Unreadable_saved_settings_give_the_untouched_ones(string? json) =>
        Assert.True(AudioModSettings.FromJson(json).IsNeutral);

    [Fact]
    public void Every_preset_does_something_has_its_own_name_and_is_found_again()
    {
        Assert.Equal(AudioModPresets.All.Length, AudioModPresets.All.Select(p => p.Name).Distinct().Count());
        foreach (var preset in AudioModPresets.All)
        {
            Assert.False(preset.Settings.IsNeutral, preset.Name);
            Assert.Equal(preset, AudioModPresets.Match(preset.Settings));
        }
        Assert.Null(AudioModPresets.Match(new AudioModSettings()));
    }

    [Fact]
    public void The_presets_in_the_screenshots_have_their_values()
    {
        var slowedReverb = AudioModPresets.All.First(p => p.Name == "Slowed & Reverb").Settings;
        Assert.Equal(84, slowedReverb.SpeedPercent);
        Assert.Equal(-2, slowedReverb.PitchSemitones);
        Assert.True(slowedReverb.ReverbOn);
        Assert.Equal(24, slowedReverb.Reverb);
        Assert.Equal(7, slowedReverb.LowPass);

        Assert.Equal(71, AudioModPresets.All.First(p => p.Name == "Muffled").Settings.LowPass);
    }
}

public class AudioModFileTests
{
    [Fact]
    public void A_wav_file_has_the_right_header_and_size()
    {
        var left = new float[] { 0f, 0.5f, -0.5f, 2f };    // 2 is clipped to full scale
        var right = new float[] { 0f, 0.25f, -1f, -2f };
        using var stream = new MemoryStream();

        AudioModRenderer.WriteWav(stream, left, right, 44100);
        var bytes = stream.ToArray();

        Assert.Equal(44 + 4 * 4, bytes.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
        Assert.Equal(44100, BitConverter.ToInt32(bytes, 24));       // sample rate
        Assert.Equal(2, BitConverter.ToInt16(bytes, 22));           // channels
        Assert.Equal(16, BitConverter.ToInt16(bytes, 34));          // bits
        Assert.Equal(16, BitConverter.ToInt32(bytes, 40));          // data bytes
        Assert.Equal(36 + 16, BitConverter.ToInt32(bytes, 4));      // RIFF size
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(bytes, 44 + 3 * 4));        // 2.0 clipped, left
        Assert.Equal(short.MinValue, BitConverter.ToInt16(bytes, 44 + 3 * 4 + 2));    // -2.0 clipped, right
        Assert.Equal((short)16384, BitConverter.ToInt16(bytes, 44 + 1 * 4));          // 0.5 on the left (16383.5 rounds to even)
    }

    [Fact]
    public void The_modded_copy_has_its_own_file_per_song_and_settings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tempo-mod-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string song = Path.Combine(dir, "song.mp3");
            File.WriteAllBytes(song, new byte[] { 1, 2, 3 });

            var a = AudioModRenderer.FileFor(song, new AudioModSettings { SpeedPercent = 80 });
            var b = AudioModRenderer.FileFor(song, new AudioModSettings { SpeedPercent = 90 });
            Assert.NotNull(a);
            Assert.EndsWith(".wav", a);
            Assert.NotEqual(a, b);
            Assert.Equal(a, AudioModRenderer.FileFor(song, new AudioModSettings { SpeedPercent = 80 }));
            Assert.Null(AudioModRenderer.FileFor(Path.Combine(dir, "missing.mp3"), new AudioModSettings()));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temp folder */ } }
    }
}
