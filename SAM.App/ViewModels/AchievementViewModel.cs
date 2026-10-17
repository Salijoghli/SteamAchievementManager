// [ObservableProperty] on fields: partial properties need C# 13 and the project uses C# 12;
// the app isn't AOT-compiled, so this WinRT AOT warning doesn't apply.
#pragma warning disable MVVMTK0045

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels;

/// <summary>Rarity tier from the global unlock percentage.</summary>
public enum AchievementRarity
{
    Unknown,
    Common,
    Uncommon,
    Rare,
    Epic,
    Legendary,
}

/// <summary>One achievement row: the state Steam reports and the state the user wants.</summary>
public sealed partial class AchievementViewModel : ObservableObject
{
    private readonly ImageCache _images;
    private readonly uint _appId;
    private readonly Action _changed;
    private bool _iconRequested;

    public AchievementViewModel(
        AchievementDefinition definition,
        int schemaIndex,
        bool isAchieved,
        DateTimeOffset? unlockTime,
        uint appId,
        ImageCache images,
        Action changed)
    {
        this.Definition = definition;
        this.SchemaIndex = schemaIndex;
        this.IsAchieved = isAchieved;
        this.UnlockTime = unlockTime;
        this._appId = appId;
        this._images = images;
        this._changed = changed;
        this._isChecked = isAchieved;
    }

    public AchievementDefinition Definition { get; }

    /// <summary>Position in the schema, for the default sort.</summary>
    public int SchemaIndex { get; }

    public string Id => this.Definition.Id;

    public string Name => string.IsNullOrWhiteSpace(this.Definition.Name) ? this.Definition.Id : this.Definition.Name;

    /// <summary>Screen readers announce list rows by their item's ToString.</summary>
    public override string ToString() => $"{this.Name}, {(this.IsChecked ? "unlocked" : "locked")}";

    public string Description => this.Definition.Description;

    public bool IsHidden => this.Definition.IsHidden;

    public bool IsProtected => this.Definition.IsProtected;

    public bool CanChange => this.IsProtected == false;

    /// <summary>State reported by Steam when loaded (or last committed).</summary>
    public bool IsAchieved { get; private set; }

    public DateTimeOffset? UnlockTime { get; }

    public string UnlockTimeText => this.UnlockTime is { } time
        ? "Unlocked " + time.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)
        : this.IsAchieved ? "Unlocked" : "";

    public bool HasUnlockTimeText => this.UnlockTimeText.Length > 0;

    /// <summary>Desired state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(PendingText))]
    private bool _isChecked;

    public bool IsPending => this.IsChecked != this.IsAchieved;

    public string PendingText => this.IsPending == false ? "" : this.IsChecked ? "Will unlock" : "Will lock";

    partial void OnIsCheckedChanged(bool value) => this._changed();

    /// <summary>
    /// Records that the desired state was stored on Steam, so the row stops counting as
    /// pending (e.g. while the post-commit reload runs). Doesn't notify the owner.
    /// </summary>
    public void MarkCommitted()
    {
        if (this.IsAchieved == this.IsChecked)
        {
            return;
        }

        this.IsAchieved = this.IsChecked;
        this.OnPropertyChanged(nameof(this.IsAchieved));
        this.OnPropertyChanged(nameof(this.IsPending));
        this.OnPropertyChanged(nameof(this.PendingText));
    }

    #region Hidden description
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayDescription))]
    [NotifyPropertyChangedFor(nameof(IsDescriptionConcealed))]
    [NotifyPropertyChangedFor(nameof(IsSpoilerMasked))]
    private bool _isRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayDescription))]
    [NotifyPropertyChangedFor(nameof(IsDescriptionConcealed))]
    [NotifyPropertyChangedFor(nameof(IsSpoilerMasked))]
    private bool _isHovered;

    /// <summary>
    /// Spoiler protection (from the settings): hidden descriptions are masked until clicked
    /// and hovering no longer reveals them.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayDescription))]
    [NotifyPropertyChangedFor(nameof(IsDescriptionConcealed))]
    [NotifyPropertyChangedFor(nameof(IsSpoilerMasked))]
    private bool _isSpoilerProtectionOn;

    public bool IsDescriptionConcealed =>
        this.IsHidden && this.IsRevealed == false && (this.IsSpoilerProtectionOn || this.IsHovered == false);

    /// <summary>True when the description is replaced by the spoiler mask (instead of plain placeholder text).</summary>
    public bool IsSpoilerMasked => this.IsDescriptionConcealed && this.IsSpoilerProtectionOn;

    public string DisplayDescription => this.IsDescriptionConcealed
        ? (this.IsSpoilerProtectionOn
            ? "Hidden achievement — click to reveal"
            : "Hidden achievement — hover or click to reveal")
        : string.IsNullOrEmpty(this.Description) ? (this.IsHidden ? "Hidden achievement" : "") : this.Description;
    #endregion

    #region Row actions and note
    /// <summary>True while the pointer is over the row; reveals the row's action buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionOpacity))]
    private bool _isRowHovered;

    /// <summary>The user's personal note, or null if none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    [NotifyPropertyChangedFor(nameof(HasNoNote))]
    [NotifyPropertyChangedFor(nameof(NotePreview))]
    private string? _note;

    public bool HasNote => string.IsNullOrWhiteSpace(this.Note) == false;

    public bool HasNoNote => this.HasNote == false;

    /// <summary>The note's first non-empty line, for the one-line preview under the description.</summary>
    public string NotePreview => this.HasNote
        ? this.Note!.Split('\n').Select(line => line.Trim()).First(line => line.Length > 0)
        : "";

    /// <summary>Hover-revealed buttons (guides, and the note button while there's no note).</summary>
    public double ActionOpacity => this.IsRowHovered ? 1.0 : 0.0;
    #endregion

    #region Rarity
    /// <summary>True until the global percentages have arrived (or failed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRarity))]
    private bool _isRarityLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRarity))]
    [NotifyPropertyChangedFor(nameof(Rarity))]
    [NotifyPropertyChangedFor(nameof(RarityText))]
    [NotifyPropertyChangedFor(nameof(RarityTierText))]
    private double? _globalPercent;

    public bool HasRarity => this.IsRarityLoading == false && this.GlobalPercent.HasValue;

    public AchievementRarity Rarity => GetRarity(this.GlobalPercent);

    public string RarityText => this.GlobalPercent is { } p
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.#}% of players", p)
        : "";

    public string RarityTierText => this.Rarity == AchievementRarity.Unknown ? "" : this.Rarity.ToString();

    public static AchievementRarity GetRarity(double? percent) => percent switch
    {
        null => AchievementRarity.Unknown,
        >= 50 => AchievementRarity.Common,
        >= 20 => AchievementRarity.Uncommon,
        >= 10 => AchievementRarity.Rare,
        >= 5 => AchievementRarity.Epic,
        _ => AchievementRarity.Legendary,
    };
    #endregion

    #region Icon
    [ObservableProperty]
    private ImageSource? _iconSource;

    [ObservableProperty]
    private bool _isIconLoaded;

    /// <summary>True when there's no icon to show (none in the schema, or download failed).</summary>
    [ObservableProperty]
    private bool _isIconMissing;

    /// <summary>Starts loading the icon the first time the row is shown. Call on the UI thread.</summary>
    public async void EnsureIcon()
    {
        if (this._iconRequested)
        {
            return;
        }

        this._iconRequested = true;

        var file = this.IsAchieved
            ? this.Definition.IconNormal
            : (string.IsNullOrEmpty(this.Definition.IconLocked) ? this.Definition.IconNormal : this.Definition.IconLocked);
        if (string.IsNullOrEmpty(file))
        {
            this.IsIconMissing = true;
            this.IsIconLoaded = true;
            return;
        }

        string? path = null;
        try
        {
            path = await this._images.GetAsync([GameArt.GetAchievementIconUrl(this._appId, file)], CancellationToken.None);
        }
        catch (Exception e)
        {
            Diagnostics.Log($"Couldn't load icon for achievement {this.Id}", e);
        }

        if (path == null)
        {
            this.IsIconMissing = true;
        }
        else
        {
            this.IconSource = new BitmapImage(new Uri(path));
        }

        this.IsIconLoaded = true;
    }
    #endregion

    /// <summary>Text the search box matches against (doesn't spoil concealed descriptions).</summary>
    public bool Matches(string query) =>
        this.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        this.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (this.Note?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        ((this.IsHidden == false || this.IsRevealed) &&
         this.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase));
}
