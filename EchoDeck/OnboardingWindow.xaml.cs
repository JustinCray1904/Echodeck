using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace EchoDeck;

/// <summary>
/// First-launch tour, laid out like the Figma onboarding frames: welcome + media folder, theme and colour,
/// a walk through every control, Anonymous Mode passcode setup, and the keyboard shortcuts.
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int Steps = 5;

    private readonly AppSettings _cfg = AppSettings.Current;
    private readonly UIElement[] _pages;
    private readonly System.Windows.Shapes.Ellipse[] _dots;
    private int _step;
    private bool _ready;

    public OnboardingWindow()
    {
        InitializeComponent();
        WindowHelper.AttachChrome(this);

        _pages = new UIElement[] { Page0, Page1, Page2, Page3, Page4 };
        _dots = new[] { Dot0, Dot1, Dot2, Dot3, Dot4 };

        // Reflect whatever is already stored (first run: dark + Sky).
        FolderBox.Text = !string.IsNullOrWhiteSpace(_cfg.MediaFolder)
            ? _cfg.MediaFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        ThemeLight.IsChecked = _cfg.Theme == ThemeMode.Light;
        ThemeDark.IsChecked = _cfg.Theme == ThemeMode.Dark;
        ThemeSystem.IsChecked = _cfg.Theme == ThemeMode.System;
        var swatches = new[] { Accent0, Accent1, Accent2, Accent3, Accent4, Accent5, Accent6, Accent7, Accent8 };
        int accent = Math.Clamp(_cfg.AccentIndex, 0, swatches.Length - 1);
        swatches[accent].IsChecked = true;
        MenuSwitch.IsChecked = _cfg.ShowAnonymousInMenu;

        _ready = true;
        ShowStep(0);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing early (X) counts as skipping: the tour is only shown on first launch.
        _cfg.OnboardingDone = true;
        _cfg.Save();
        PassBox.Clear();
        PassConfirmBox.Clear();
        base.OnClosed(e);
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, Steps - 1);
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _dots.Length; i++)
            _dots[i].SetResourceReference(System.Windows.Shapes.Shape.FillProperty, i == _step ? "AccentBrush" : "SlateBrush");

        BackButton.Visibility = _step == 0 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = _step == Steps - 1 ? "Finish" : "Next";
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0)
        {
            var folder = FolderBox.Text.Trim();
            if (folder.Length > 0 && Directory.Exists(folder)) _cfg.MediaFolder = folder;
            _cfg.Save();
        }
        else if (_step == 3 && !TrySavePasscode())
        {
            return; // stay here until the passcode is valid (or both fields are left empty)
        }

        if (_step == Steps - 1)
        {
            Close();
            return;
        }
        ShowStep(_step + 1);
    }

    private bool TrySavePasscode()
    {
        PassError.Text = "";
        string pass = PassBox.Password, confirm = PassConfirmBox.Password;

        _cfg.ShowAnonymousInMenu = MenuSwitch.IsChecked == true;
        _cfg.Save();

        if (pass.Length == 0 && confirm.Length == 0) return true; // set it up later

        var error = PasscodeForm.TrySet(null, pass, confirm);
        if (error != null)
        {
            PassError.Text = error;
            return false;
        }
        return true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose your media folder" };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _cfg.Theme = ThemeLight.IsChecked == true ? ThemeMode.Light
                   : ThemeSystem.IsChecked == true ? ThemeMode.System
                   : ThemeMode.Dark;
        ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex);
        _cfg.Save(notify: false);
    }

    private void Accent_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int index))
        {
            _cfg.AccentIndex = index;
            ThemeManager.Apply(_cfg.Theme, _cfg.AccentIndex);
            _cfg.Save(notify: false);
        }
    }

    private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c is >= '0' and <= '9');
}
