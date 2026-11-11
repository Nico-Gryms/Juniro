using System.Collections.ObjectModel;
using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;

namespace Juniro.App.ViewModels;

/// <summary>
/// Saisie ou modification d'un pari réellement joué : sélection, cote, mise, stratégie
/// suivie, bookmaker, note. Paramètres de navigation :
///   draft = BetDraft  -> nouveau pari (depuis la fiche du match)
///   betId = Guid      -> modification / suppression d'un pari existant (le sien seulement)
/// </summary>
public sealed class BetEditViewModel : BaseViewModel, IQueryAttributable
{
    private readonly DataService _data;
    private BetDraft? _draft;
    private Guid? _betId;
    private UserBetRow? _existing;
    private SelectionRow? _selection;
    private string _oddText = "";
    private string _stakeText = "";
    private StrategyOption? _strategy;
    private string _bookmaker = "Betclic";
    private string _note = "";
    private bool _isVoid;
    private DateTime? _placedDate = DateTime.Today;
    private string _title = "Nouveau pari";
    private string _matchText = "";
    private string _hintText = "";
    private bool _canEdit = true;

    public BetEditViewModel(DataService data)
    {
        _data = data;
        SaveCommand = new AsyncCommand(SaveAsync);
        DeleteCommand = new AsyncCommand(DeleteAsync);
    }

    public ObservableCollection<SelectionRow> SelectionOptions { get; } = new();
    public ObservableCollection<StrategyOption> StrategyOptions { get; } = new() { new StrategyOption(null) };
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string MatchText { get => _matchText; set => SetProperty(ref _matchText, value); }
    public string HintText { get => _hintText; set => SetProperty(ref _hintText, value); }
    public bool IsEdit => _betId != null;
    public bool CanEdit { get => _canEdit; set => SetProperty(ref _canEdit, value); }
    public bool CanDelete => IsEdit && CanEdit;

    public SelectionRow? Selection
    {
        get => _selection;
        set
        {
            if (!SetProperty(ref _selection, value) || value == null) return;
            // Cote proposée = dernière cote relevée, modifiable
            if (value.Odd != null && (!IsEdit || string.IsNullOrWhiteSpace(OddText))) OddText = Fmt.Odd(value.Odd);
            UpdateHint();
        }
    }

    public string OddText { get => _oddText; set { if (SetProperty(ref _oddText, value)) UpdateHint(); } }
    public string StakeText { get => _stakeText; set { if (SetProperty(ref _stakeText, value)) UpdateHint(); } }
    public StrategyOption? SelectedStrategy { get => _strategy; set => SetProperty(ref _strategy, value); }
    public string Bookmaker { get => _bookmaker; set => SetProperty(ref _bookmaker, value); }
    public string Note { get => _note; set => SetProperty(ref _note, value); }
    public bool IsVoid { get => _isVoid; set => SetProperty(ref _isVoid, value); }
    public DateTime? PlacedDate { get => _placedDate; set => SetProperty(ref _placedDate, value); }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("draft", out var d) && d is BetDraft draft) _draft = draft;
        if (query.TryGetValue("betId", out var b) && b is Guid id) _betId = id;
        OnPropertyChanged(nameof(IsEdit));
        OnPropertyChanged(nameof(CanDelete));
    }

    private bool _loaded;

    public Task OnAppearingAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        // Liste des stratégies proposées : paris fictifs actifs + familles simulées
        var paper = await _data.GetPaperSummaryAsync();
        var overview = await _data.GetStrategyOverviewAsync();
        foreach (var code in paper.Select(p => p.Strategy).Concat(overview.Select(o => o.Family)).Distinct())
            AddStrategy(code);
        SelectedStrategy = StrategyOptions[0];

        if (_betId is Guid id)
        {
            _existing = await _data.GetBetAsync(id);
            if (_existing == null) { Error = "Pari introuvable."; return; }
            Title = "Modifier le pari";
            CanEdit = _existing.UserId == _data.CurrentUserId;
            OnPropertyChanged(nameof(CanDelete));
            if (!CanEdit) HintText = $"Pari de {_existing.DisplayName} : lecture seule.";
            MatchText = $"{_existing.MatchText} · {_existing.League} · {_existing.KickoffText}";
            var prediction = await _data.GetPredictionAsync(_existing.MatchId);
            FillSelections(prediction?.Selections(_existing.HomeTeam, _existing.AwayTeam)
                           ?? DefaultSelections(_existing.HomeTeam, _existing.AwayTeam));
            OddText = Fmt.Odd(_existing.Odd);
            StakeText = Fmt.Num(_existing.Stake, 2);
            Selection = SelectionOptions.FirstOrDefault(s => s.Key == _existing.BetKey);
            SelectedStrategy = string.IsNullOrEmpty(_existing.Strategy) ? StrategyOptions[0] : AddStrategy(_existing.Strategy!);
            Bookmaker = _existing.Bookmaker ?? "";
            Note = _existing.Note ?? "";
            IsVoid = _existing.IsVoid;
            PlacedDate = _existing.PlacedAt.ToLocalTime().Date;
        }
        else if (_draft != null)
        {
            Title = "J'ai parié";
            MatchText = $"{_draft.HomeTeam} – {_draft.AwayTeam} · {_draft.League} · {Fmt.Kickoff(_draft.KickoffUtc)}";
            FillSelections(_draft.Selections);
            Selection = SelectionOptions.FirstOrDefault(s => s.Key == _draft.PreselectedKey) ?? SelectionOptions.FirstOrDefault();
            if (!string.IsNullOrEmpty(_draft.Strategy))
                SelectedStrategy = AddStrategy(_draft.Strategy);
        }
        _loaded = true;
    });

    /// <summary>Ajoute une stratégie à la liste si besoin et la renvoie.</summary>
    private StrategyOption AddStrategy(string code)
    {
        var found = StrategyOptions.FirstOrDefault(x => x.Code == code);
        if (found != null) return found;
        var option = new StrategyOption(code);
        StrategyOptions.Add(option);
        return option;
    }

    private void FillSelections(IEnumerable<SelectionRow> rows)
    {
        SelectionOptions.Clear();
        foreach (var r in rows) SelectionOptions.Add(r);
    }

    private static List<SelectionRow> DefaultSelections(string home, string away) =>
        BetKeys.All.Select(k => new SelectionRow { Key = k, Label = BetKeys.Label(k, home, away) }).ToList();

    /// <summary>Gain possible et avis du modèle sur ce pari.</summary>
    private void UpdateHint()
    {
        if (!CanEdit) return;
        if (!Fmt.TryParse(OddText, out var odd) || odd <= 1) { HintText = ""; return; }
        string gain = Fmt.TryParse(StakeText, out var stake) && stake > 0
            ? $"Gain possible : {Fmt.Num(stake * (odd - 1), 2)} (retour {Fmt.Num(stake * odd, 2)}). "
            : "";
        string model = "";
        if (Selection?.Model is double p)
        {
            double edge = p * odd - 1;
            model = $"Modèle : {Fmt.Pct(p)} (cote juste {Fmt.Odd(1 / p)}), valeur {Fmt.Signed(edge * 100, 0, " %")}.";
        }
        HintText = gain + model;
    }

    private Task SaveAsync() => RunAsync(async () =>
    {
        if (!CanEdit) return;
        if (Selection == null) { Error = "Choisissez le pari joué."; return; }
        if (!Fmt.TryParse(OddText, out var odd) || odd <= 1) { Error = "La cote doit être supérieure à 1 (ex. 1,85)."; return; }
        if (!Fmt.TryParse(StakeText, out var stake) || stake <= 0) { Error = "Indiquez la mise (ex. 10)."; return; }

        var placed = new DateTimeOffset((PlacedDate ?? DateTime.Today).Date.Add(DateTime.Now.TimeOfDay));
        var input = new UserBetInput
        {
            MatchId = _existing?.MatchId ?? _draft?.MatchId ?? 0,
            BetKey = Selection.Key,
            Odd = Math.Round(odd, 2),
            Stake = Math.Round(stake, 2),
            Strategy = SelectedStrategy?.Code,
            Bookmaker = string.IsNullOrWhiteSpace(Bookmaker) ? null : Bookmaker.Trim(),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
            IsVoid = IsVoid,
            PlacedAt = placed
        };
        if (input.MatchId == 0) { Error = "Match inconnu."; return; }

        if (_betId is Guid id) await _data.UpdateBetAsync(id, input);
        else await _data.AddBetAsync(input);
        await Shell.Current.GoToAsync("..");
    });

    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (_betId is not Guid id || !CanEdit) return;
        var page = Shell.Current.CurrentPage;
        bool ok = await page.DisplayAlert("Supprimer ce pari ?", "Il disparaîtra de l'historique et de la courbe.", "Supprimer", "Annuler");
        if (!ok) return;
        await _data.DeleteBetAsync(id);
        await Shell.Current.GoToAsync("..");
    });
}
