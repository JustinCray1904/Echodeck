using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EchoDeck;

/// <summary>
/// One-click setup for local AI lyrics: pick a model (with an honest description of speed and accuracy) and Tempo downloads
/// it, and the whisper.cpp program if it isn't installed yet, into %LOCALAPPDATA%\Tempo\whisper. Online only while this runs.
/// </summary>
public partial class WhisperSetupWindow : Window
{
    private sealed record ModelInfo(string File, string Name, string Size, string Speed, string Accuracy, string About, bool Recommended = false);

    private static readonly ModelInfo[] Models =
    {
        new("ggml-tiny.bin", "Tiny", "≈ 75 MB", "Very fast", "★☆☆☆☆",
            "Runs on almost anything. Fine for clear speech, but it misses and invents a lot of words in music."),
        new("ggml-base.bin", "Base", "≈ 142 MB", "Fast", "★★☆☆☆",
            "A good first try on an older or low-power laptop. Understands clear vocals, struggles with busy mixes."),
        new("ggml-small.bin", "Small", "≈ 466 MB", "Medium", "★★★☆☆",
            "Balanced for speech. For singing it still drops lines and lyrics can drift, so don't expect perfect results."),
        new("ggml-medium-q5_0.bin", "Medium (compressed)", "≈ 539 MB", "Slow", "★★★★☆",
            "Clearly more accurate than Small on songs. Compressed (q5) so it needs less memory. Expect a few times the length of the song."),
        new("ggml-large-v3-turbo-q5_0.bin", "Large v3 Turbo (compressed)", "≈ 574 MB", "Slow", "★★★★★",
            "The most accurate choice here, close to the full Large model but about the size of Medium. Best for music if your PC has a recent 6+ core processor and you can wait.",
            Recommended: true),
    };

    /// <summary>True once a model has been downloaded and selected.</summary>
    public bool Installed { get; private set; }

    private readonly AppSettings _cfg = AppSettings.Current;
    private readonly List<(ModelInfo Info, Border Card)> _cards = new();
    private ModelInfo _selected;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public WhisperSetupWindow()
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);

        _selected = Models.First(m => m.Recommended);
        foreach (var m in Models)
        {
            var card = BuildCard(m);
            _cards.Add((m, card));
            ModelList.Children.Add(card);
        }
        RefreshSelection();

        bool haveProgram = WhisperService.Resolve(_cfg).Exe != null && !WhisperService.IsDeprecatedStub(WhisperService.Resolve(_cfg).Exe);
        ProgramSwitch.IsChecked = !haveProgram;
        ProgramSwitch.IsEnabled = true;
        ProgramHint.Text = haveProgram
            ? "A working whisper.cpp program was found, so this is optional. Turn it on to replace it with the latest release."
            : "The program that runs the model (a small download from the whisper.cpp project on GitHub). Needed once.";

        Closing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                StatusText.Text = "A download is running. Press Cancel to stop it first.";
            }
        };
    }

    private Border BuildCard(ModelInfo m)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = m.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (m.Recommended)
        {
            var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(10, 0, 0, 0) };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            var label = new TextBlock { Text = "Recommended", FontSize = 11, FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
            badge.Child = label;
            title.Children.Add(badge);
        }

        var facts = new TextBlock { FontSize = 12, Margin = new Thickness(0, 3, 0, 0), Text = $"{m.Size}   ·   Speed: {m.Speed}   ·   Accuracy: {m.Accuracy}" };
        facts.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        var about = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Text = m.About };
        about.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(facts);
        stack.Children.Add(about);

        var card = new Border
        {
            Child = stack,
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (_busy) return;
            _selected = m;
            RefreshSelection();
        };
        return card;
    }

    private void RefreshSelection()
    {
        foreach (var (info, card) in _cards)
        {
            bool on = ReferenceEquals(info, _selected);
            card.SetResourceReference(Border.BorderBrushProperty, on ? "AccentBrush" : "SlateBrush");
            card.SetResourceReference(Border.BackgroundProperty, on ? "AccentSoftBrush" : "CardBrush");
        }
        DownloadButton.Content = "Download " + _selected.Name;
    }

    // ------------------------------------------------------------------ download

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<(double Fraction, string Text)>(p => ShowProgress(p.Fraction, p.Text));

        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tempo/3.0");

            Directory.CreateDirectory(WhisperService.DefaultFolder);

            if (ProgramSwitch.IsChecked == true)
            {
                var exe = await DownloadProgramAsync(http, progress, ct);
                _cfg.WhisperExePath = exe;
            }

            var modelDir = Path.Combine(WhisperService.DefaultFolder, "models");
            Directory.CreateDirectory(modelDir);
            var modelPath = Path.Combine(modelDir, _selected.File);

            if (WhisperService.CheckModel(modelPath) != null)   // not there yet, or incomplete
                await DownloadFileAsync(http, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + _selected.File,
                    modelPath, _selected.Name + " model", 0.0, 1.0, progress, ct);

            var problem = WhisperService.CheckModel(modelPath);
            if (problem != null) throw new InvalidOperationException("The downloaded model isn't valid (" + problem + "). Try again.");

            _cfg.WhisperModelPath = modelPath;
            _cfg.Save();

            ShowProgress(1, "Done. " + _selected.Name + " is ready: open a song, press Lyrics, then Generate with AI.");
            Installed = true;
            SetBusy(false);
            DownloadButton.Content = "Done";
            DownloadButton.IsEnabled = false;
        }
        catch (OperationCanceledException)
        {
            ShowProgress(0, "Cancelled.");
            SetBusy(false);
        }
        catch (Exception ex)
        {
            AppLog.Write("Whisper download", ex);
            ShowProgress(0, "Download failed: " + ex.Message + " (check your connection and try again).");
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DownloadButton.Content = busy ? "Cancel" : "Download " + _selected.Name;
        DownloadButton.IsEnabled = true;
        ProgramSwitch.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        ProgressTrack.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowProgress(double fraction, string text)
    {
        StatusText.Text = text;
        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * Math.Clamp(fraction, 0, 1));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static async Task<string> DownloadProgramAsync(HttpClient http, IProgress<(double Fraction, string Text)> progress, CancellationToken ct)
    {
        progress.Report((0, "Looking up the latest whisper.cpp release…"));
        var json = await http.GetStringAsync("https://api.github.com/repos/ggml-org/whisper.cpp/releases/latest", ct);

        string? url = null;
        using (var doc = JsonDocument.Parse(json))
        {
            var assets = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .ToList();

            // the plain 64-bit CPU build: it runs on every PC (the BLAS / CUDA builds need extra libraries or hardware)
            url = assets.FirstOrDefault(a => a.Name.Equals("whisper-bin-x64.zip", StringComparison.OrdinalIgnoreCase)).Url;
            url ??= assets.FirstOrDefault(a => a.Name.Contains("bin-x64", StringComparison.OrdinalIgnoreCase) &&
                                               a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                               !a.Name.Contains("blas", StringComparison.OrdinalIgnoreCase) &&
                                               !a.Name.Contains("cuda", StringComparison.OrdinalIgnoreCase) &&
                                               !a.Name.Contains("cublas", StringComparison.OrdinalIgnoreCase) &&
                                               !a.Name.Contains("vulkan", StringComparison.OrdinalIgnoreCase)).Url;
        }
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("The latest whisper.cpp release has no Windows 64-bit download I recognise.");

        var zip = Path.Combine(Path.GetTempPath(), "Tempo-whisper-bin-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await DownloadFileAsync(http, url, zip, "whisper.cpp program", 0.0, 0.0, progress, ct, scaleToTotal: false);

            var binDir = Path.Combine(WhisperService.DefaultFolder, "bin");
            if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true);
            progress.Report((0, "Unpacking…"));
            ZipFile.ExtractToDirectory(zip, binDir, overwriteFiles: true);

            var exe = Directory.EnumerateFiles(binDir, "whisper-cli.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null) throw new InvalidOperationException("The download didn't contain whisper-cli.exe.");
            return exe;
        }
        finally
        {
            try { File.Delete(zip); } catch { /* temp file */ }
        }
    }

    /// <param name="from">Progress range this file covers: <paramref name="from"/> .. <paramref name="to"/> of the whole bar.</param>
    private static async Task DownloadFileAsync(HttpClient http, string url, string dest, string label, double from, double to,
        IProgress<(double Fraction, string Text)> progress, CancellationToken ct, bool scaleToTotal = true)
    {
        var part = dest + ".part";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                var lastReport = Environment.TickCount64;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;

                    if (Environment.TickCount64 - lastReport > 150)
                    {
                        lastReport = Environment.TickCount64;
                        double f = total > 0 ? (double)read / total : 0;
                        string sizes = total > 0 ? $"{read / 1048576.0:0} / {total / 1048576.0:0} MB" : $"{read / 1048576.0:0} MB";
                        progress.Report((scaleToTotal ? from + (to - from) * f : f, $"Downloading the {label}…  {sizes}"));
                    }
                }
            }

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* leftover partial file */ }
        }
    }
}
