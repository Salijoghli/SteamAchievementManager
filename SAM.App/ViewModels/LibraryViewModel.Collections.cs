using Microsoft.UI.Xaml.Controls;
using SAM.App.Models;
using SAM.App.Services;

namespace SAM.App.ViewModels;

// Collections (Steam's and SAM's own), hidden games and installed state.
public sealed partial class LibraryViewModel
{
    private IReadOnlySet<uint> _hidden = new HashSet<uint>();
    private IReadOnlyDictionary<uint, InstalledGame> _installed = new Dictionary<uint, InstalledGame>();
    private int _steamCollectionsVersion;

    /// <summary>Raised after <see cref="SteamCollections"/> or <see cref="CustomCollections"/> change.</summary>
    public event EventHandler? CollectionsChanged;

    /// <summary>Creates a card with the current favorite, hidden and installed state.</summary>
    private GameCardViewModel CreateCard(GameEntry entry) => new(this, entry, this.IsFavorite(entry.AppId))
    {
        IsHidden = this._hidden.Contains(entry.AppId),
        Installed = this._installed.TryGetValue(entry.AppId, out var installed) ? installed : null,
    };

    private GameCollection? FindCollection(string filterKey) =>
        this.CustomCollections.FirstOrDefault(c => c.FilterKey == filterKey) ??
        this.SteamCollections.FirstOrDefault(c => c.FilterKey == filterKey);

    /// <summary>Re-reads Steam's collections off the UI thread (they only change in Steam).</summary>
    private async void ReloadSteamCollections()
    {
        var version = ++this._steamCollectionsVersion;
        var collections = await Task.Run(() =>
        {
            try
            {
                return this._services.Collections.LoadSteamCollections();
            }
            catch (Exception e)
            {
                this.LogOnce("SteamCollections", e);
                return null;
            }
        });

        if (collections == null || version != this._steamCollectionsVersion || this._isShutDown)
        {
            return;
        }

        this.SteamCollections = collections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        this.OnCollectionsUpdated();
    }

    /// <summary>Re-reads SAM's own collections and the hidden-games list (in memory, so on the UI thread).</summary>
    private void ReloadCustomCollections()
    {
        try
        {
            this.CustomCollections = this._services.Collections.GetCustomCollections()
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception e)
        {
            this.LogOnce("CustomCollections", e);
        }

        try
        {
            this._hidden = this._services.Collections.HiddenAppIds;
        }
        catch (Exception e)
        {
            this.LogOnce("HiddenAppIds", e);
        }

        foreach (var card in this._master.Values)
        {
            card.IsHidden = this._hidden.Contains(card.AppId);
        }

        this.OnCollectionsUpdated();
    }

    private void OnCollectionsUpdated()
    {
        this.OnPropertyChanged(nameof(this.CollectionFilterName));
        this.CollectionsChanged?.Invoke(this, EventArgs.Empty);
        if (this.CollectionFilter != null || this.ShowHidden == false)
        {
            this.ApplyFilter();
        }
    }

    private void OnCollectionsStoreChanged(object? sender, EventArgs e)
    {
        if (this._dispatcher.HasThreadAccess)
        {
            this.ReloadCustomCollections();
        }
        else
        {
            this._dispatcher.TryEnqueue(this.ReloadCustomCollections);
        }
    }

    /// <summary>True if the game is in the SAM collection with this ID.</summary>
    public bool IsInCollection(GameCardViewModel card, string collectionId) =>
        this.CustomCollections.FirstOrDefault(c => c.Id == collectionId)?.AppIds.Contains(card.AppId) == true;

    /// <summary>Adds the game to, or removes it from, a SAM collection.</summary>
    public void SetInCollection(GameCardViewModel card, string collectionId, bool included)
    {
        try
        {
            this._services.Collections.SetInCustomCollection(collectionId, card.AppId, included);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't update a collection", e);
            this.ShowNotice(Strings.Get("Collections_UpdateFailed"), e.Message, InfoBarSeverity.Error);
        }

        this.ReloadCustomCollections();
    }

    /// <summary>Creates a SAM collection, optionally with one game in it. Returns null on failure.</summary>
    public GameCollection? CreateCollection(string name, GameCardViewModel? withCard = null)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        GameCollection? collection = null;
        try
        {
            collection = this._services.Collections.CreateCustomCollection(name);
            if (withCard != null)
            {
                this._services.Collections.SetInCustomCollection(collection.Id, withCard.AppId, true);
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't create a collection", e);
            this.ShowNotice(Strings.Get("Collections_CreateFailed"), e.Message, InfoBarSeverity.Error);
        }

        this.ReloadCustomCollections();
        return collection;
    }

    public void RenameCollection(string collectionId, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }

        try
        {
            this._services.Collections.RenameCustomCollection(collectionId, name);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't rename a collection", e);
            this.ShowNotice(Strings.Get("Collections_RenameFailed"), e.Message, InfoBarSeverity.Error);
        }

        this.ReloadCustomCollections();
    }

    public void DeleteCollection(string collectionId)
    {
        try
        {
            this._services.Collections.DeleteCustomCollection(collectionId);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't delete a collection", e);
            this.ShowNotice(Strings.Get("Collections_DeleteFailed"), e.Message, InfoBarSeverity.Error);
            return;
        }

        var key = this.CustomCollections.FirstOrDefault(c => c.Id == collectionId)?.FilterKey;
        if (key != null && this.CollectionFilter == key)
        {
            this.CollectionFilter = null;
        }

        this.ReloadCustomCollections();
    }

    /// <summary>Hides or unhides a game. Hidden games are left out unless "Show hidden games" is on.</summary>
    internal void SetHidden(GameCardViewModel card, bool hidden)
    {
        try
        {
            this._services.Collections.SetHidden(card.AppId, hidden);
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't update hidden games", e);
            this.ShowNotice(Strings.Get(hidden ? "Library_HideGameFailed" : "Library_UnhideGameFailed"), e.Message, InfoBarSeverity.Error);
            return;
        }

        this.ReloadCustomCollections();
        if (hidden && this.ShowHidden == false)
        {
            this.ShowNotice(
                Strings.Format("Library_GameHiddenTitle", card.Name),
                Strings.Get("Library_GameHiddenMessage"),
                InfoBarSeverity.Informational);
        }
    }
}
