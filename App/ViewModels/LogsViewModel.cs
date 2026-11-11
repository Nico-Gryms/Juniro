using System.Collections.ObjectModel;
using System.Windows.Input;
using Juniro.App.Models;
using Juniro.App.Services;
using Juniro.App.Views;

namespace Juniro.App.ViewModels;

/// <summary>
/// Journal du service (PC fixe) : état du dernier scrap et de la dernière copie, puis la liste
/// des exécutions récentes (scrap quotidien, rattrapages, copies vers Supabase).
/// Filtres : Tout / Scrap / Copie / Problèmes. Toucher une exécution ouvre son journal détaillé.
/// </summary>
public sealed class LogsViewModel : BaseViewModel
{
    private readonly DataService _data;
    private List<ExecutionRun> _runs = new();
    private string _filter = "all";
    private ServiceHealth _health = new();
    private ExecutionRun? _lastScrape;
    private SyncInfo? _lastSync;
    private bool _journalMissing;
    private bool _isRefreshing;
    private ExecutionRun? _selectedRun;

    public LogsViewModel(DataService data)
    {
        _data = data;
        RefreshCommand = new AsyncCommand(async () => { await LoadAsync(); IsRefreshing = false; });
        var pick = new AsyncCommand(p => { Pick(p as string); return Task.CompletedTask; });
        Filters = new() { new("Tout", "all", pick), new("Scrap", "scrape", pick), new("Copie", "copy", pick), new("Problèmes", "problems", pick) };
        foreach (var f in Filters) f.IsSelected = f.Value == _filter;
    }

    public ICommand RefreshCommand { get; }
    public ObservableCollection<FilterChip> Filters { get; }
    public ObservableCollection<ExecutionRun> Runs { get; } = new();
    public ObservableCollection<SyncInfo> Syncs { get; } = new();

    public bool IsRefreshing { get => _isRefreshing; set => SetProperty(ref _isRefreshing, value); }
    public ServiceHealth Health { get => _health; set => SetProperty(ref _health, value); }
    public ExecutionRun? LastScrape { get => _lastScrape; set { if (SetProperty(ref _lastScrape, value)) OnPropertyChanged(nameof(HasLastScrape)); } }
    public SyncInfo? LastSync { get => _lastSync; set { if (SetProperty(ref _lastSync, value)) OnPropertyChanged(nameof(HasLastSync)); } }
    public bool HasLastScrape => LastScrape != null;
    public bool HasLastSync => LastSync != null;
    public bool JournalMissing { get => _journalMissing; set => SetProperty(ref _journalMissing, value); }
    public bool ShowSyncs => _filter == "copy" && Syncs.Count > 0;

    /// <summary>Toucher une exécution ouvre son journal détaillé.</summary>
    public ExecutionRun? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (!SetProperty(ref _selectedRun, value) || value == null) return;
            int id = value.RunId;
            SelectedRun = null;
            _ = GoToAsync(nameof(RunLogsPage), new Dictionary<string, object> { ["runId"] = id });
        }
    }

    public Task OnAppearingAsync() => _runs.Count == 0 ? LoadAsync() : Task.CompletedTask;

    private Task LoadAsync() => RunAsync(async () =>
    {
        var syncs = await _data.GetSyncsAsync(20);
        Syncs.Clear();
        foreach (var s in syncs) Syncs.Add(s);
        LastSync = syncs.FirstOrDefault();
        try
        {
            _runs = await _data.GetRunsAsync(80);
            JournalMissing = false;
        }
        catch (InvalidOperationException)
        {
            // Tables execution_runs / execution_logs pas encore créées ou pas encore remplies
            _runs = new List<ExecutionRun>();
            JournalMissing = true;
        }
        LastScrape = _runs.FirstOrDefault(r => r.Source == "ScheduledTask");
        Health = Journal.Health(LastScrape, LastSync, !JournalMissing);
        Apply();
    });

    private void Pick(string? value)
    {
        if (value == null) return;
        _filter = value;
        foreach (var f in Filters) f.IsSelected = f.Value == value;
        Apply();
    }

    private void Apply()
    {
        IEnumerable<ExecutionRun> q = _filter switch
        {
            "scrape" => _runs.Where(r => r.IsScrape),
            "copy" => _runs.Where(r => r.IsCopy),
            "problems" => _runs.Where(r => r.HasProblem),
            _ => _runs
        };
        Runs.Clear();
        foreach (var r in q) Runs.Add(r);
        OnPropertyChanged(nameof(ShowSyncs));
    }
}

/// <summary>
/// Journal détaillé d'une exécution : bilan, puis les événements (alertes et erreurs par défaut,
/// ou tout, ou seulement les matchs traités). Paramètre de navigation : runId.
/// </summary>
public sealed class RunLogsViewModel : BaseViewModel, IQueryAttributable
{
    private readonly DataService _data;
    private int _runId;
    private List<ExecutionLog> _logs = new();
    private string _filter = "problems";
    private ExecutionRun? _run;
    private string _countText = "";

    public RunLogsViewModel(DataService data)
    {
        _data = data;
        var pick = new AsyncCommand(p => { Pick(p as string); return Task.CompletedTask; });
        Filters = new() { new("Alertes et erreurs", "problems", pick), new("Tout", "all", pick), new("Matchs", "matches", pick) };
        foreach (var f in Filters) f.IsSelected = f.Value == _filter;
    }

    public ObservableCollection<FilterChip> Filters { get; }
    public ObservableCollection<ExecutionLog> Logs { get; } = new();
    public ExecutionRun? Run { get => _run; set { if (SetProperty(ref _run, value)) OnPropertyChanged(nameof(Title)); } }
    public string Title => Run == null ? "Journal" : $"{Run.SourceText} n° {Run.RunId}";
    public string CountText { get => _countText; set => SetProperty(ref _countText, value); }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("runId", out var v) && v is int id) _runId = id;
    }

    public Task OnAppearingAsync() => RunAsync(async () =>
    {
        if (_runId == 0) return;
        var runTask = _data.GetRunAsync(_runId);
        var logsTask = _data.GetRunLogsAsync(_runId);
        await Task.WhenAll(runTask, logsTask);
        Run = runTask.Result;
        _logs = logsTask.Result;
        Apply();
    });

    private void Pick(string? value)
    {
        if (value == null) return;
        _filter = value;
        foreach (var f in Filters) f.IsSelected = f.Value == value;
        Apply();
    }

    private void Apply()
    {
        IEnumerable<ExecutionLog> q = _filter switch
        {
            "problems" => _logs.Where(l => l.IsProblem || l.Sector?.EndsWith(".Finished") == true),
            "matches" => _logs.Where(l => l.IsMatch),
            _ => _logs
        };
        var list = q.ToList();
        Logs.Clear();
        foreach (var l in list) Logs.Add(l);
        int problems = _logs.Count(l => l.IsProblem);
        CountText = _logs.Count == 0
            ? "Aucun événement copié pour cette exécution (journal limité aux 14 derniers jours)."
            : $"{list.Count} affiché(s) sur {_logs.Count} événements · {problems} alerte(s) ou erreur(s)";
    }
}
