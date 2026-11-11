using Juniro.App.Services;

namespace Juniro.App.Models;

/// <summary>
/// Un pari de l'historique, qu'il soit saisi par nous (user_bets) ou fictif (stratégies, signals),
/// sous une forme commune pour les bilans, la courbe et l'historique de l'onglet Mes paris.
/// </summary>
public sealed class BetLine
{
    public bool IsPaper { get; init; }
    public Guid? BetId { get; init; }
    public Guid? UserId { get; init; }
    public long MatchId { get; init; }
    public string Who { get; init; } = "";
    public string League { get; init; } = "";
    public string StrategyText { get; init; } = "";
    public DateTimeOffset KickoffUtc { get; init; }
    public DateTimeOffset PlacedAt { get; init; }
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
    public int? HomeGoals { get; init; }
    public int? AwayGoals { get; init; }
    public string BetKey { get; init; } = "";
    public double Odd { get; init; }
    public double Stake { get; init; }
    public string Result { get; init; } = "PENDING";
    public double? Profit { get; init; }

    public static BetLine From(UserBetRow b) => new()
    {
        IsPaper = false,
        BetId = b.Id,
        UserId = b.UserId,
        MatchId = b.MatchId,
        Who = b.WhoText,
        League = b.League,
        StrategyText = b.StrategyText,
        KickoffUtc = b.KickoffUtc,
        PlacedAt = b.PlacedAt,
        HomeTeam = b.HomeTeam,
        AwayTeam = b.AwayTeam,
        HomeGoals = b.HomeGoals,
        AwayGoals = b.AwayGoals,
        BetKey = b.BetKey,
        Odd = b.Odd,
        Stake = b.Stake,
        Result = b.Result ?? "PENDING",
        Profit = b.Profit
    };

    public static BetLine From(SignalRow s) => new()
    {
        IsPaper = true,
        MatchId = s.MatchId,
        Who = "Paris fictifs",
        League = s.League,
        StrategyText = s.StrategyText,
        KickoffUtc = s.KickoffUtc,
        PlacedAt = s.KickoffUtc,
        HomeTeam = s.HomeTeam,
        AwayTeam = s.AwayTeam,
        HomeGoals = s.HomeGoals,
        AwayGoals = s.AwayGoals,
        BetKey = s.BetKey,
        Odd = s.Odd ?? 0,
        Stake = s.Stake ?? 0,
        Result = string.IsNullOrEmpty(s.Result) ? "PENDING" : s.Result,
        Profit = s.Profit
    };

    public bool IsSettled => Result is "WIN" or "LOSE" or "PUSH" or "VOID";
    public bool IsDecided => Result is "WIN" or "LOSE";
    public DateTime Day => KickoffUtc.ToLocalTime().Date;

    public string MatchText => $"{HomeTeam} – {AwayTeam}";
    public string BetText => $"{BetKeys.Label(BetKey, HomeTeam, AwayTeam)} @ {Fmt.Odd(Odd)}";
    public string SubText => IsPaper
        ? $"{StrategyText} · {KickoffUtc.ToLocalTime():HH:mm} · mise {Fmt.Num(Stake, 2)}"
        : $"{Who} · {StrategyText} · mise {Fmt.Num(Stake, 2)}";
    public string ScoreText => HomeGoals is null ? "" : $"{HomeGoals} – {AwayGoals}";
    public bool HasScore => HomeGoals != null;
    public string ResultText => Result switch
    {
        "WIN" or "LOSE" => Fmt.Signed(Profit, 2),
        "PUSH" => "Remboursé",
        "VOID" => "Annulé",
        _ => "En attente"
    };
    public Color ResultColor => Palette.ForResult(Result);
    public string ResultBadge => Result switch { "WIN" => "Gagné", "LOSE" => "Perdu", "PUSH" => "Remb.", "VOID" => "Annulé", _ => "En cours" };
    public Color BadgeBackground => Result switch { "WIN" => Palette.WinSoft, "LOSE" => Palette.LoseSoft, _ => Palette.CardAlt };
}

/// <summary>Bilan d'une période (« Hier », « 3 derniers jours », « Total »).</summary>
public sealed class RecapCard
{
    public RecapCard(string title, string subtitle, IReadOnlyCollection<BetLine> lines)
    {
        Title = title;
        Subtitle = subtitle;
        var settled = lines.Where(l => l.IsSettled).ToList();
        int won = lines.Count(l => l.Result == "WIN"), lost = lines.Count(l => l.Result == "LOSE");
        int pending = lines.Count - settled.Count;
        double stakes = settled.Where(l => l.Result != "VOID").Sum(l => l.Stake);
        double profit = settled.Sum(l => l.Profit ?? 0);
        double? roi = stakes > 0 ? 100 * profit / stakes : null;

        BetsText = lines.Count == 0 ? "Aucun pari" : lines.Count == 1 ? "1 pari" : $"{lines.Count} paris";
        ProfitText = settled.Count == 0 ? "–" : Fmt.Signed(profit, 2);
        ProfitColor = settled.Count == 0 ? Palette.TextMuted : Palette.ForSign(profit);
        RoiText = roi is null ? "ROI –" : $"ROI {Fmt.Signed(roi, 1, " %")}";
        RecordText = pending == 0 ? $"{won} G · {lost} P" : $"{won} G · {lost} P · {pending} en cours";
        HitText = won + lost == 0 ? "" : $"Réussite {Fmt.Pct((double)won / (won + lost), 0)}";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string BetsText { get; }
    public string ProfitText { get; }
    public Color ProfitColor { get; }
    public string RoiText { get; }
    public string RecordText { get; }
    public string HitText { get; }
}

/// <summary>Les paris d'une journée dans l'historique, avec le bénéfice du jour.</summary>
public sealed class BetDay : List<BetLine>
{
    public BetDay(DateTime day, IEnumerable<BetLine> lines) : base(lines)
    {
        Title = Fmt.DayTitle(day);
        var settled = this.Where(l => l.IsSettled).ToList();
        double profit = settled.Sum(l => l.Profit ?? 0);
        int pending = Count - settled.Count;
        ProfitText = settled.Count == 0 ? (pending == 1 ? "1 en cours" : $"{pending} en cours") : Fmt.Signed(profit, 2);
        ProfitColor = settled.Count == 0 ? Palette.TextMuted : Palette.ForSign(profit);
    }

    public string Title { get; }
    public string ProfitText { get; }
    public Color ProfitColor { get; }
}
