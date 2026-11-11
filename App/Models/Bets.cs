using System.Text.Json.Serialization;
using Juniro.App.Services;

namespace Juniro.App.Models;

/// <summary>Pari saisi, avec le match et le résultat calculé (vue Supabase v_user_bets).</summary>
public sealed class UserBetRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? DisplayName { get; set; }
    public long MatchId { get; set; }
    public string BetKey { get; set; } = "";
    public double Odd { get; set; }
    public double Stake { get; set; }
    public string? Strategy { get; set; }
    public string? Bookmaker { get; set; }
    public string? Note { get; set; }
    public bool IsVoid { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
    public string League { get; set; } = "";
    public string? Season { get; set; }
    public DateTimeOffset KickoffUtc { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }
    public string? MatchStatus { get; set; }
    public string? Result { get; set; }
    public double? Profit { get; set; }

    [JsonIgnore] public bool IsSettled => Result is "WIN" or "LOSE" or "PUSH" or "VOID";
    [JsonIgnore] public string MatchText => $"{HomeTeam} – {AwayTeam}";
    [JsonIgnore] public string BetText => $"{BetKeys.Label(BetKey, HomeTeam, AwayTeam)} @ {Fmt.Odd(Odd)}";
    [JsonIgnore] public string KickoffText => Fmt.Kickoff(KickoffUtc);
    [JsonIgnore] public string StrategyText => BetKeys.Strategy(Strategy);
    [JsonIgnore] public string WhoText => DisplayName ?? "?";
    [JsonIgnore] public string StakeText => $"Mise {Fmt.Num(Stake, 2)}";
    [JsonIgnore] public string ScoreText => HomeGoals is null ? "" : $"{HomeGoals} – {AwayGoals}";
    [JsonIgnore] public string ResultText => Result switch
    {
        "WIN" => Fmt.Signed(Profit, 2),
        "LOSE" => Fmt.Signed(Profit, 2),
        "PUSH" => "Remboursé",
        "VOID" => "Annulé",
        _ => "En attente"
    };
    [JsonIgnore] public Color ResultColor => Palette.ForResult(Result);
}

/// <summary>Ce qui est envoyé à Supabase pour créer ou modifier un pari (table user_bets).</summary>
public sealed class UserBetInput
{
    public long MatchId { get; set; }
    public string BetKey { get; set; } = "";
    public double Odd { get; set; }
    public double Stake { get; set; }
    public string? Strategy { get; set; }
    public string? Bookmaker { get; set; }
    public string? Note { get; set; }
    public bool IsVoid { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
}

/// <summary>Ce dont l'écran de saisie a besoin pour un nouveau pari (passé depuis la fiche du match).</summary>
public sealed class BetDraft
{
    public long MatchId { get; init; }
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
    public string League { get; init; } = "";
    public DateTimeOffset KickoffUtc { get; init; }
    public List<SelectionRow> Selections { get; init; } = new();
    public string? PreselectedKey { get; init; }
    public string? Strategy { get; init; }
}

/// <summary>Stratégie proposée dans la saisie d'un pari : code enregistré, nom affiché.</summary>
public sealed class StrategyOption
{
    public StrategyOption(string? code)
    {
        Code = code;
        Name = code == null ? "Perso (sans stratégie)" : BetKeys.Strategy(code);
    }

    /// <summary>Code enregistré dans user_bets.strategy (null = perso).</summary>
    public string? Code { get; }
    public string Name { get; }
    public override string ToString() => Name;
}
