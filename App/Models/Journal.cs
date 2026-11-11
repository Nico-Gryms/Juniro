using System.Text.Json.Serialization;
using Juniro.App.Services;

namespace Juniro.App.Models;

/// <summary>Une exécution du service sur le PC fixe (table Supabase execution_runs).</summary>
public sealed class ExecutionRun
{
    public int RunId { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Status { get; set; }
    public int? MatchesFound { get; set; }
    public int? MatchesSaved { get; set; }
    public int? MatchesSkipped { get; set; }
    public int? MatchesFailed { get; set; }
    public bool? QualityDegraded { get; set; }
    public int? ErrorCount { get; set; }
    public string? Summary { get; set; }
    public string? ErrorMessage { get; set; }

    [JsonIgnore] public bool IsScrape => Source is "ScheduledTask" or "Backfill";
    [JsonIgnore] public bool IsCopy => Source == "SupabaseSync";
    [JsonIgnore] public bool HasProblem => Status is "Warning" or "Failed" or "Interrupted" || (ErrorCount ?? 0) > 0 || QualityDegraded == true;

    [JsonIgnore] public string SourceText => Source switch
    {
        "ScheduledTask" => "Scrap quotidien",
        "Backfill" => "Rattrapage",
        "SupabaseSync" => "Copie vers Supabase",
        _ => Source ?? "Exécution"
    };
    [JsonIgnore] public string DateText => StartedUtc is { } s ? $"{Fmt.DayLong(s.ToLocalTime().DateTime)} · {s.ToLocalTime():HH:mm}" : "";
    [JsonIgnore] public string DurationText => Journal.Duration(DurationSeconds);
    [JsonIgnore] public string StatusText => Journal.StatusText(Status, Source);
    [JsonIgnore] public Color StatusColor => Journal.StatusColor(Status, Source);
    [JsonIgnore] public Color StatusBackground => Journal.StatusBackground(Status, Source);
    [JsonIgnore] public string CountsText
    {
        get
        {
            if (!IsScrape) return (ErrorCount ?? 0) > 0 ? $"{ErrorCount} erreur(s) dans le journal" : "";
            var parts = new List<string>();
            if (MatchesFound != null) parts.Add($"{MatchesFound} trouvés");
            if (MatchesSaved != null) parts.Add($"{MatchesSaved} enregistrés");
            if (MatchesFailed is > 0) parts.Add($"{MatchesFailed} en échec");
            if (MatchesSkipped is > 0) parts.Add($"{MatchesSkipped} ignorés");
            if (QualityDegraded == true) parts.Add("qualité en alerte");
            return string.Join(" · ", parts);
        }
    }
    [JsonIgnore] public bool HasCounts => !string.IsNullOrEmpty(CountsText);
    [JsonIgnore] public string SummaryText => string.IsNullOrWhiteSpace(ErrorMessage) ? Summary ?? "" : $"{ErrorMessage}\n{Summary}".Trim();
    [JsonIgnore] public bool HasSummary => !string.IsNullOrWhiteSpace(SummaryText);
}

/// <summary>Un événement du journal d'une exécution (table Supabase execution_logs).</summary>
public sealed class ExecutionLog
{
    public int RunId { get; set; }
    public int Seq { get; set; }
    public DateTimeOffset? CreatedUtc { get; set; }
    public string? Level { get; set; }
    public string? Sector { get; set; }
    public string? MatchIdent { get; set; }
    public string? Message { get; set; }
    public string? Exception { get; set; }

    [JsonIgnore] public bool IsProblem => Level is "WARN" or "ERROR" or "FATAL";
    [JsonIgnore] public bool IsMatch => Sector?.EndsWith(".MatchProcessed") == true || Sector?.EndsWith(".MatchError") == true;
    [JsonIgnore] public string TimeText => CreatedUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "";
    [JsonIgnore] public string LevelText => Level ?? "";
    [JsonIgnore] public Color LevelColor => Level switch
    {
        "ERROR" or "FATAL" => Palette.Lose,
        "WARN" => Palette.Even,
        _ => Palette.TextMuted
    };
    [JsonIgnore] public Color LevelBackground => Level switch
    {
        "ERROR" or "FATAL" => Palette.LoseSoft,
        "WARN" => Color.FromArgb("#3A3122"),
        _ => Palette.CardAlt
    };
    [JsonIgnore] public string HeaderText => string.IsNullOrEmpty(MatchIdent) ? Sector ?? "" : $"{Sector} · {MatchIdent}";
    [JsonIgnore] public string ExceptionText => string.IsNullOrEmpty(Exception) ? "" : Exception.Length > 600 ? Exception[..600] + "…" : Exception;
    [JsonIgnore] public bool HasException => !string.IsNullOrEmpty(Exception);
}

/// <summary>
/// État du service affiché dans l'en-tête de l'onglet Matchs : dernier scrap et dernière copie.
/// Level : ok, warn, bad, unknown.
/// </summary>
public sealed class ServiceHealth
{
    public string Level { get; init; } = "unknown";
    public string Text { get; init; } = "État inconnu";
    public string Detail { get; init; } = "";

    public Color DotColor => Level switch { "ok" => Palette.Win, "warn" => Palette.Even, "bad" => Palette.Lose, _ => Palette.TextMuted };
    public Color Background => Level switch { "ok" => Palette.WinSoft, "warn" => Color.FromArgb("#3A3122"), "bad" => Palette.LoseSoft, _ => Palette.CardAlt };
    public Brush Stroke => new SolidColorBrush(DotColor.WithAlpha(0.45f));
}

/// <summary>Textes et couleurs communs du journal.</summary>
public static class Journal
{
    public static string StatusText(string? status, string? source) => status switch
    {
        "Success" => "OK",
        "Warning" => "Alertes",
        "Failed" => "Échec",
        "Interrupted" => "Interrompue",
        "Running" => source == "SupabaseSync" ? "En cours (copie)" : "En cours",
        _ => status ?? "?"
    };

    public static Color StatusColor(string? status, string? source) => status switch
    {
        "Success" => Palette.Win,
        "Warning" => Palette.Even,
        "Failed" or "Interrupted" => Palette.Lose,
        "Running" => source == "SupabaseSync" ? Palette.TextMuted : Palette.Even,
        _ => Palette.TextMuted
    };

    public static Color StatusBackground(string? status, string? source) => status switch
    {
        "Success" => Palette.WinSoft,
        "Warning" => Color.FromArgb("#3A3122"),
        "Failed" or "Interrupted" => Palette.LoseSoft,
        _ => Palette.CardAlt
    };

    public static string Duration(int? seconds)
    {
        if (seconds is not int s || s < 0) return "";
        if (s < 60) return $"{s} s";
        if (s < 3600) return $"{s / 60} min {s % 60:00} s";
        return $"{s / 3600} h {s % 3600 / 60:00} min";
    }

    /// <summary>
    /// État du service : le pire entre le dernier scrap quotidien (statut, ancienneté) et la
    /// dernière copie vers Supabase (ancienneté). Le scrap passe à 03h00, la copie à 06h15.
    /// </summary>
    public static ServiceHealth Health(ExecutionRun? scrape, SyncInfo? sync, bool journalAvailable)
    {
        var now = DateTimeOffset.Now;
        if (sync?.FinishedAt is not { } copied)
            return new ServiceHealth { Level = "bad", Text = "Aucune copie", Detail = "Les données n'ont jamais été envoyées vers Supabase." };

        double copyAge = (now - copied).TotalHours;
        string copyText = $"copie du {Fmt.DayLong(copied.ToLocalTime().DateTime).ToLowerInvariant()} à {copied.ToLocalTime():HH:mm}";
        if (copyAge > 54)
            return new ServiceHealth { Level = "bad", Text = "Données anciennes", Detail = $"Dernière {copyText} : le PC fixe ou le service ne tourne plus ?" };

        if (!journalAvailable || scrape?.StartedUtc is not { } started)
            return new ServiceHealth
            {
                Level = copyAge > 30 ? "warn" : "unknown",
                Text = copyAge > 30 ? "Copie d'hier" : "Scrap : pas de journal",
                Detail = $"Dernière {copyText}. Le journal du scrap n'est pas encore envoyé (rejouer A8 et mettre à jour le service)."
            };

        string at = started.ToLocalTime().ToString("HH:mm");
        string day = Fmt.DayTitle(started.ToLocalTime().Date).ToLowerInvariant();
        if ((now - started).TotalHours > 30)
            return new ServiceHealth { Level = "bad", Text = $"Pas de scrap depuis {day}", Detail = $"Dernier scrap {day} à {at} ; dernière {copyText}." };

        var (level, text) = scrape.Status switch
        {
            "Success" when scrape.QualityDegraded == true => ("warn", "Scrap avec alertes"),
            "Success" => ("ok", "Scrap OK"),
            "Warning" => ("warn", "Scrap avec alertes"),
            "Failed" => ("bad", "Scrap en échec"),
            "Interrupted" => ("bad", "Scrap interrompu"),
            "Running" => ("warn", "Scrap en cours"),
            _ => ("unknown", "Scrap : état inconnu")
        };
        if (level == "ok" && copyAge > 30) (level, text) = ("warn", "Copie d'hier");
        return new ServiceHealth
        {
            Level = level,
            Text = $"{text} · {at}",
            Detail = $"Scrap {day} à {at} ({Journal.StatusText(scrape.Status, scrape.Source)}) ; dernière {copyText}."
        };
    }
}
