// [ObservableProperty] on fields: partial properties need C# 13 and the project uses C# 12;
// the app isn't AOT-compiled, so this WinRT AOT warning doesn't apply.
#pragma warning disable MVVMTK0045

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SAM.App.Models;

namespace SAM.App.ViewModels;

/// <summary>An achievement whose progress depends on a stat (for the "Used by" section).</summary>
public sealed record StatUsage(string AchievementName, double TargetValue);

/// <summary>
/// One stat row. <see cref="EditValue"/> is what the user typed; it's validated and only
/// copied to <see cref="Value"/> (the value that gets stored) when valid, mirroring the
/// rules of the old manager: protected stats can't change, integer stats take whole numbers,
/// increment-only stats can't go below their original value, plus the schema's min/max and
/// max change. Average-rate stats are read-only (Steam can't set them through SetStat).
/// </summary>
public sealed partial class StatViewModel : ObservableObject
{
    private static readonly char[] Vowels = ['A', 'E', 'I', 'O', 'U', 'Y'];
    private readonly Action _changed;

    public StatViewModel(StatDefinition definition, double originalValue, Action changed)
    {
        this.Definition = definition;
        this.OriginalValue = originalValue;
        this._changed = changed;
        this._value = originalValue;
        this._editValue = originalValue;
        this.UsedBy.CollectionChanged += (_, _) => this.OnPropertyChanged(nameof(this.HasUsedBy));
    }

    public StatDefinition Definition { get; }

    public string Id => this.Definition.Id;

    /// <summary>The display name, humanized when the schema only repeats the ID.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(this.Definition.DisplayName) || this.Definition.DisplayName == this.Definition.Id
            ? Humanize(this.Definition.Id)
            : this.Definition.DisplayName;

    public bool IsInteger => this.Definition.Kind == StatKind.Integer;

    public bool IsProtected => this.Definition.IsProtected;

    public bool IsIncrementOnly => this.Definition.IncrementOnly;

    /// <summary>
    /// Average-rate stats are updated with UpdateAvgRateStat from the game's play sessions;
    /// writing them with SetStat(float) fails, so they're shown read-only.
    /// </summary>
    public bool IsAverageRate => this.Definition.Kind == StatKind.AverageRate;

    /// <summary>Protected or average-rate: shown but never written.</summary>
    public bool IsReadOnly => this.IsProtected || this.IsAverageRate;

    public string KindText => this.Definition.Kind switch
    {
        StatKind.Integer => "Integer",
        StatKind.Float => "Float",
        _ => "Average rate",
    };

    public string FlagsText
    {
        get
        {
            var parts = new List<string> { this.KindText };
            if (this.IsIncrementOnly)
            {
                parts.Add("increment-only");
            }

            if (this.HasBounds)
            {
                parts.Add($"range {Format(this.Definition.MinValue)} – {Format(this.Definition.MaxValue)}");
            }

            if (this.Definition.MaxChange > 0)
            {
                parts.Add($"max change {Format(this.Definition.MaxChange)}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Why the stat can't be edited (shown when <see cref="IsReadOnly"/>).</summary>
    public string ProtectedText => this.IsProtected
        ? "Protected — only the game's servers can change this stat"
        : "Average rate — Steam only updates these from the game's play sessions, so they can't be edited here";

    /// <summary>The value Steam reported when loaded (or last committed).</summary>
    public double OriginalValue { get; private set; }

    public string OriginalValueText => "Was " + Format(this.OriginalValue);

    /// <summary>Last valid value; this is what gets stored.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private double _value;

    /// <summary>Bound to the editor; may be invalid.</summary>
    [ObservableProperty]
    private double _editValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => this.Error != null;

    /// <summary>Set from the "Allow editing" switch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isEditingAllowed;

    public bool CanEdit => this.IsEditingAllowed && this.IsReadOnly == false;

    public bool IsModified => this.IsInteger
        ? (long)this.Value != (long)this.OriginalValue
        : ((float)this.Value).Equals((float)this.OriginalValue) == false;

    /// <summary>
    /// Achievements that depend on this stat. Empty for now; filled once the schema exposes
    /// achievement progress links.
    /// </summary>
    public ObservableCollection<StatUsage> UsedBy { get; } = [];

    public bool HasUsedBy => this.UsedBy.Count > 0;

    private bool HasBounds => this.Definition.MaxValue > this.Definition.MinValue;

    partial void OnEditValueChanged(double value)
    {
        var error = this.Validate(value);
        this.Error = error;
        if (error == null && value.Equals(this.Value) == false)
        {
            this.Value = value;
        }

        this._changed();
    }

    partial void OnValueChanged(double value) => this._changed();

    /// <summary>
    /// Puts the original value back and clears any error. The original came from Steam, so
    /// it's always accepted, even if it's outside the schema's limits.
    /// </summary>
    [RelayCommand]
    public void ResetToOriginal()
    {
        this.Error = null;
        this.Value = this.OriginalValue;
        this.EditValue = this.OriginalValue; // Validate accepts the original unconditionally.
        this._changed();
    }

    /// <summary>
    /// Records that <see cref="Value"/> was stored on Steam, so the stat stops counting as
    /// modified (e.g. while the post-commit reload runs).
    /// </summary>
    public void MarkCommitted()
    {
        if (this.OriginalValue.Equals(this.Value))
        {
            return;
        }

        this.OriginalValue = this.Value;
        this.OnPropertyChanged(nameof(this.OriginalValue));
        this.OnPropertyChanged(nameof(this.OriginalValueText));
        this.OnPropertyChanged(nameof(this.IsModified));
    }

    private string? Validate(double value)
    {
        // The original value came from Steam; going back to it is always allowed.
        if (value.Equals(this.OriginalValue))
        {
            return null;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "Enter a number.";
        }

        if (this.IsProtected)
        {
            return "This stat is protected and can't be changed.";
        }

        if (this.IsAverageRate)
        {
            return "Average-rate stats can't be changed here.";
        }

        if (this.IsInteger)
        {
            if (Math.Floor(value) != value)
            {
                return "This stat takes whole numbers only.";
            }

            if (value < int.MinValue || value > int.MaxValue)
            {
                return "The value is out of range for an integer stat.";
            }
        }
        else if (value < float.MinValue || value > float.MaxValue)
        {
            return "The value is out of range.";
        }

        if (this.HasBounds)
        {
            if (value < this.Definition.MinValue)
            {
                return $"The minimum is {Format(this.Definition.MinValue)}.";
            }

            if (value > this.Definition.MaxValue)
            {
                return $"The maximum is {Format(this.Definition.MaxValue)}.";
            }
        }

        if (this.IsIncrementOnly && value < this.OriginalValue)
        {
            return $"This stat can only increase; it can't go below {Format(this.OriginalValue)}.";
        }

        // Steam rejects a store that changes a stat by more than its "maxchange" at once.
        if (this.Definition.MaxChange > 0 && Math.Abs(value - this.OriginalValue) > this.Definition.MaxChange)
        {
            return $"This stat can change by at most {Format(this.Definition.MaxChange)} at a time " +
                $"(from {Format(this.OriginalValue)}).";
        }

        return null;
    }

    private static string Format(double value)
    {
        if (value >= float.MaxValue || value == int.MaxValue)
        {
            return "max";
        }

        if (value <= float.MinValue || value == int.MinValue)
        {
            return "min";
        }

        return Math.Abs(value) >= 1e7 && Math.Floor(value) == value
            ? value.ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString("G7", CultureInfo.CurrentCulture);
    }

    /// <summary>"GFWL_MIGRATE" → "GFWL Migrate", "stat_kills_total" → "Stat Kills Total", "totalKills" → "Total Kills".</summary>
    public static string Humanize(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        // Split on separators and lower→upper camel-case boundaries.
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (c is '_' or '-' or ' ' or '.')
            {
                Flush();
                continue;
            }

            if (current.Length > 0 && char.IsUpper(c) && char.IsLower(id[i - 1]))
            {
                Flush();
            }

            current.Append(c);
        }

        Flush();

        var hasLower = id.Any(char.IsLower);
        var culture = CultureInfo.CurrentCulture;
        return string.Join(' ', words.Select(word =>
        {
            var allCaps = word.All(ch => char.IsUpper(ch) || char.IsDigit(ch));

            // Acronyms: vowel-less caps in an all-caps ID ("GFWL", "XP", "HP"), or any caps
            // word in a mixed-case ID ("totalXP").
            if (allCaps && (hasLower || word.IndexOfAny(Vowels) < 0))
            {
                return word;
            }

            return char.ToUpper(word[0], culture) + word[1..].ToLower(culture);
        }));

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
