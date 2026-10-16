using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SAM.App.ViewModels;

namespace SAM.App.Views.Library;

/// <summary>
/// A game tile in the library grid. View-only concerns live here: the image fade-in once the
/// cached file is decoded, and the hover lift. Image downloads are started by the window when the
/// container is realized (<see cref="LibraryViewModel.RequestImage"/>).
/// </summary>
public sealed partial class GameCard : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card),
        typeof(GameCardViewModel),
        typeof(GameCard),
        new PropertyMetadata(null, (d, e) => ((GameCard)d).OnCardChanged((GameCardViewModel?)e.OldValue)));

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(250);

    private readonly ScalarTransition _fade = new() { Duration = FadeDuration };
    private GameCardViewModel? _subscribed;
    private string? _shownPath;

    public GameCard()
    {
        this.InitializeComponent();
        this.HeaderImage.OpacityTransition = this._fade;
        this.CardRoot.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        this.HoverBorder.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) };
        this.Loaded += (_, _) => this.Subscribe(this.Card);
        this.Unloaded += (_, _) => this.Subscribe(null);
    }

    public GameCardViewModel? Card
    {
        get => (GameCardViewModel?)this.GetValue(CardProperty);
        set => this.SetValue(CardProperty, value);
    }

    private void OnCardChanged(GameCardViewModel? oldCard)
    {
        if (this.IsLoaded)
        {
            this.Subscribe(this.Card);
        }

        this.SetHover(false);
        this.ShowImage(animate: false);
    }

    private void Subscribe(GameCardViewModel? card)
    {
        if (this._subscribed == card)
        {
            return;
        }

        if (this._subscribed != null)
        {
            this._subscribed.PropertyChanged -= this.OnCardPropertyChanged;
        }

        this._subscribed = card;
        if (card != null)
        {
            card.PropertyChanged += this.OnCardPropertyChanged;
            this.ShowImage(animate: false);
        }
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameCardViewModel.ImagePath) or nameof(GameCardViewModel.ImageFailed))
        {
            this.ShowImage(animate: true);
        }
    }

    /// <summary>Syncs the image area with the card: skeleton while loading, image once decoded, or fallback.</summary>
    private void ShowImage(bool animate)
    {
        var card = this.Card;
        var failed = card?.ImageFailed == true;
        var path = failed ? null : card?.ImagePath;
        if (path != null && path == this._shownPath)
        {
            return;
        }

        this._shownPath = path;
        this.SetImageOpacity(0, animate: false);

        if (path != null)
        {
            this.ImageSkeleton.Visibility = Visibility.Visible;
            this.ImageFallback.Visibility = Visibility.Collapsed;
            this.HeaderImage.Source = new BitmapImage
            {
                DecodePixelWidth = 460,
                DecodePixelType = DecodePixelType.Logical,
                UriSource = new Uri(path),
            };
        }
        else
        {
            this.HeaderImage.Source = null;
            this.ImageSkeleton.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
            this.ImageFallback.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        this.SetImageOpacity(1, animate: true);
        this.ImageSkeleton.Visibility = Visibility.Collapsed;
    }

    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // The cached file can't be decoded (corrupt or not really an image): drop it from the
        // cache so the next launch downloads it again instead of re-serving it forever.
        if (this._shownPath != null)
        {
            App.Services?.Images.Invalidate(this._shownPath);
        }

        // Remember it so the card doesn't retry the broken file every time it's realized.
        if (this.Card is { } card)
        {
            card.ImageFailed = true;
            return;
        }

        this.HeaderImage.Source = null;
        this.ImageSkeleton.Visibility = Visibility.Collapsed;
        this.ImageFallback.Visibility = Visibility.Visible;
    }

    private void SetImageOpacity(double value, bool animate)
    {
        this.HeaderImage.OpacityTransition = animate ? this._fade : null;
        this.HeaderImage.Opacity = value;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => this.SetHover(true);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => this.SetHover(false);

    private void SetHover(bool hover)
    {
        this.CardRoot.Translation = hover ? new Vector3(0, -3, 0) : Vector3.Zero;
        this.HoverBorder.Opacity = hover ? 1 : 0;
    }
}
