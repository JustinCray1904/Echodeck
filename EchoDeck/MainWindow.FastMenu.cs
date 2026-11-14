using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace EchoDeck;

// The right-click menu of the player. WPF's own ContextMenu fades in and out with a translucent window (a Windows setting that an
// app can't turn off), which showed the moving cover colours through the menu and looked like a glitch. This draws the same items in
// a popup that appears and goes away at once.
public partial class MainWindow
{
    private Popup? _fastMenu;

    private void CloseFastMenu()
    {
        if (_fastMenu is { IsOpen: true }) _fastMenu.IsOpen = false;
        _fastMenu = null;
    }

    /// <summary>Shows the menu's items in the instant popup. Returns false (nothing shown) for a menu it can't draw: use the normal one.</summary>
    private bool ShowFastMenu(ContextMenu source)
    {
        // only plain items and separators; a submenu or a check mark needs the real menu
        foreach (var item in source.Items)
        {
            if (item is Separator) continue;
            if (item is not MenuItem mi || mi.Items.Count > 0 || mi.IsCheckable) return false;
        }

        try
        {
            CloseFastMenu();

            var panel = new StackPanel { Margin = new Thickness(5) };
            var card = new Border
            {
                MinWidth = 210,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Child = panel,
            };
            TextElement.SetFontFamily(card, FontFamily);
            SkinResources(card.Resources);   // the cover's colours while a song is showing
            card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "SlateBrush");

            var popup = new Popup
            {
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,   // no fade: it is there at once
                StaysOpen = false,
                Placement = PlacementMode.MousePoint,
                PlacementTarget = this,
                Child = card,
            };

            foreach (var item in source.Items)
            {
                if (item is Separator)
                {
                    var line = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
                    line.SetResourceReference(Border.BackgroundProperty, "SlateBrush");
                    panel.Children.Add(line);
                    continue;
                }

                var entry = (MenuItem)item;
                panel.Children.Add(MakeFastRow(entry, popup));
            }

            _fastMenu = popup;
            popup.Closed += (_, _) => { if (ReferenceEquals(_fastMenu, popup)) _fastMenu = null; };
            popup.IsOpen = true;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Fast menu", ex);
            CloseFastMenu();
            return false;
        }
    }

    private UIElement MakeFastRow(MenuItem entry, Popup popup)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (entry.Icon is UIElement icon)
        {
            entry.Icon = null;   // an element can only live in one place
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(icon);
        }

        var label = new TextBlock
        {
            Text = entry.Header?.ToString() ?? "",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(entry.InputGestureText))
        {
            var gesture = new TextBlock
            {
                Text = entry.InputGestureText,
                FontSize = 11,
                Margin = new Thickness(28, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            gesture.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            Grid.SetColumn(gesture, 2);
            grid.Children.Add(gesture);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 7, 12, 7),
            Background = Brushes.Transparent,   // transparent but still clickable
            Cursor = Cursors.Hand,
            Child = grid,
            IsEnabled = entry.IsEnabled,
            Opacity = entry.IsEnabled ? 1 : 0.4,
        };

        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            popup.IsOpen = false;
            // run the action once the popup is gone, like a normal menu does
            Dispatcher.InvokeAsync(() => entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, entry)),
                System.Windows.Threading.DispatcherPriority.Input);
        };
        return row;
    }
}
