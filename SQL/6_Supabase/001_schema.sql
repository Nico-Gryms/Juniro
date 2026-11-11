-- =============================================================================
-- Juniro — schéma Supabase (projet « juniro », région Paris eu-west-3)
-- -----------------------------------------------------------------------------
-- Deux familles de tables :
--   1. Données copiées depuis la base locale Juniro_Evo par le service Windows (job
--      « supabase-sync ») : matches, predictions, match_stats, strategy_reports, signals,
--      sync_log. L'application les LIT seulement ; seul le service (utilisateur postgres)
--      les écrit.
--   2. Données saisies dans l'application : profiles (nom affiché) et user_bets (paris
--      réellement joués). Chacun écrit les siens, les deux comptes voient tout.
-- Vues et fonctions : ce que l'application affiche (matchs à venir, résultats des
-- prédictions, bilan des stratégies, paris avec gain calculé).
-- Documentation : DOCUMENTATION.md (racine du projet), chapitre 13.
-- =============================================================================

-- ----------------------------------------------------------------------------- 1. données copiées
create table if not exists public.matches (
    match_id        bigint primary key,
    league          text        not null,
    country         text,
    tier            smallint,
    season          text,
    kickoff_utc     timestamptz not null,
    round_number    integer,
    status          text        not null,          -- Scheduled / Finished / Postponed ...
    home_team       text        not null,
    away_team       text        not null,
    home_goals      smallint,
    away_goals      smallint,
    home_goals_ht   smallint,
    away_goals_ht   smallint,
    home_xg         numeric(5,2),
    away_xg         numeric(5,2),
    home_shots      smallint,
    away_shots      smallint,
    home_sot        smallint,
    away_sot        smallint,
    home_corners    smallint,
    away_corners    smallint,
    home_possession numeric(5,1),
    away_possession numeric(5,1),
    synced_at       timestamptz not null default now()
);
create index if not exists ix_matches_kickoff on public.matches (kickoff_utc);
create index if not exists ix_matches_league_kickoff on public.matches (league, kickoff_utc);

-- Une prédiction par match : la dernière faite AVANT le coup d'envoi (passage quotidien
-- « LIVE ») ou, à défaut, celle du dernier backtest (walk-forward, donc honnête).
create table if not exists public.predictions (
    match_id          bigint primary key,
    source            text    not null,            -- LIVE / BACKTEST
    model_run_id      integer not null,
    trained_until     date,
    exp_home_goals    numeric(5,2),
    exp_away_goals    numeric(5,2),
    exp_total_goals   numeric(5,2),
    most_likely_score text,
    p_home            double precision,
    p_draw            double precision,
    p_away            double precision,
    p_btts_yes        double precision,
    p_over15          double precision,
    p_over25          double precision,
    p_over35          double precision,
    mkt_p_home        double precision,            -- probabilités du marché, marge retirée
    mkt_p_draw        double precision,
    mkt_p_away        double precision,
    mkt_p_btts_yes    double precision,
    mkt_p_over25      double precision,
    odd_home          numeric(6,2),                -- dernière cote relevée avant le match
    odd_draw          numeric(6,2),
    odd_away          numeric(6,2),
    odd_btts_yes      numeric(6,2),
    odd_btts_no       numeric(6,2),
    odd_over25        numeric(6,2),
    odd_under25       numeric(6,2),
    odds_label        text,                        -- DAY / J-1 / J-2 / J-3 ...
    synced_at         timestamptz not null default now()
);

-- Statistiques des deux équipes AVANT le match (moyennes sur leurs derniers matchs).
create table if not exists public.match_stats (
    match_id          bigint primary key,
    home_elo          numeric(7,1),
    away_elo          numeric(7,1),
    home_elo_trend5   numeric(6,1),
    away_elo_trend5   numeric(6,1),
    home_rank         smallint,
    away_rank         smallint,
    home_played       smallint,
    away_played       smallint,
    home_ppg          numeric(4,2),                -- points par match sur la saison
    away_ppg          numeric(4,2),
    home_xppg         numeric(4,2),                -- points attendus par match sur la saison
    away_xppg         numeric(4,2),
    home_pts5         numeric(4,2),                -- points par match sur les 5 derniers
    away_pts5         numeric(4,2),
    home_gf5          numeric(4,2),
    home_ga5          numeric(4,2),
    away_gf5          numeric(4,2),
    away_ga5          numeric(4,2),
    home_xgf5         numeric(4,2),
    home_xga5         numeric(4,2),
    away_xgf5         numeric(4,2),
    away_xga5         numeric(4,2),
    home_btts10       numeric(4,2),                -- part de matchs BTTS sur les 10 derniers
    away_btts10       numeric(4,2),
    home_over25_10    numeric(4,2),
    away_over25_10    numeric(4,2),
    home_rest_days    numeric(5,1),
    away_rest_days    numeric(5,1),
    synced_at         timestamptz not null default now()
);

-- Bilan des stratégies simulées (dernier backtest) : une ligne par famille, ligue et saison,
-- plus les totaux « Test » et « Total ».
create table if not exists public.strategy_reports (
    family          text    not null,
    league          text    not null,
    season          text    not null,              -- 2022-2023 ... / Test / Total
    strategy_name   text,
    period          text,                          -- réglage / test / en cours
    model_run_id    integer,
    bets            integer,
    stakes          numeric(10,2),
    profit          numeric(10,2),
    roi_pct         numeric(7,2),
    hit_pct         numeric(5,1),
    avg_odd         numeric(5,2),
    sort_order      smallint,
    synced_at       timestamptz not null default now(),
    primary key (family, league, season)
);

-- Paris fictifs des stratégies actives (config.json > live_strategies) avec leur résultat.
create table if not exists public.signals (
    match_id        bigint  not null,
    strategy        text    not null,
    market          text    not null,              -- 1X2 / DNB / OU25 / BTTS ...
    selection       text    not null,              -- HOME / DRAW / AWAY / OVER / YES ...
    line_value      numeric(4,2),
    probability     double precision,
    odd             numeric(6,2),
    odd_label       text,
    stake           numeric(8,2),
    result          text,                          -- PENDING / WIN / LOSE / PUSH
    profit          numeric(8,2),
    model_run_id    integer,
    synced_at       timestamptz not null default now(),
    primary key (match_id, strategy, market, selection)
);

create table if not exists public.sync_log (
    id           bigint generated always as identity primary key,
    started_at   timestamptz not null,
    finished_at  timestamptz,
    status       text,
    details      jsonb
);

-- ----------------------------------------------------------------------------- 2. données saisies
create table if not exists public.profiles (
    id            uuid primary key references auth.users (id) on delete cascade,
    display_name  text not null,
    created_at    timestamptz not null default now()
);

create table if not exists public.user_bets (
    id          uuid primary key default gen_random_uuid(),
    user_id     uuid not null default auth.uid() references auth.users (id) on delete cascade,
    match_id    bigint not null references public.matches (match_id),
    bet_key     text not null check (bet_key in (
                    '1X2_HOME','1X2_DRAW','1X2_AWAY','DNB_HOME','DNB_AWAY','DC_1X','DC_X2','DC_12',
                    'BTTS_YES','BTTS_NO','OU15_OVER','OU15_UNDER','OU25_OVER','OU25_UNDER',
                    'OU35_OVER','OU35_UNDER')),
    odd         numeric(6,2)  not null check (odd > 1),
    stake       numeric(10,2) not null check (stake > 0),
    strategy    text,                              -- stratégie suivie, ou « Perso »
    bookmaker   text,
    note        text,
    is_void     boolean not null default false,    -- pari annulé / remboursé par le bookmaker
    placed_at   timestamptz not null default now(),
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now()
);
create index if not exists ix_user_bets_user on public.user_bets (user_id, placed_at);
create index if not exists ix_user_bets_match on public.user_bets (match_id);

-- Profil créé automatiquement à la création d'un compte (nom = début de l'adresse e-mail)
create or replace function public.handle_new_user()
returns trigger language plpgsql security definer set search_path = '' as $$
begin
    insert into public.profiles (id, display_name)
    values (new.id, coalesce(new.raw_user_meta_data ->> 'display_name', split_part(new.email, '@', 1)))
    on conflict (id) do nothing;
    return new;
end $$;
drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created after insert on auth.users
    for each row execute function public.handle_new_user();

create or replace function public.touch_updated_at()
returns trigger language plpgsql set search_path = '' as $$
begin
    new.updated_at := now();
    return new;
end $$;
drop trigger if exists trg_user_bets_updated on public.user_bets;
create trigger trg_user_bets_updated before update on public.user_bets
    for each row execute function public.touch_updated_at();

-- ----------------------------------------------------------------------------- 3. sécurité (RLS)
alter table public.matches          enable row level security;
alter table public.predictions      enable row level security;
alter table public.match_stats      enable row level security;
alter table public.strategy_reports enable row level security;
alter table public.signals          enable row level security;
alter table public.sync_log         enable row level security;
alter table public.profiles         enable row level security;
alter table public.user_bets        enable row level security;

-- Données copiées : lecture pour les comptes connectés, aucune écriture depuis l'application
create policy "lecture connectee" on public.matches          for select to authenticated using (true);
create policy "lecture connectee" on public.predictions      for select to authenticated using (true);
create policy "lecture connectee" on public.match_stats      for select to authenticated using (true);
create policy "lecture connectee" on public.strategy_reports for select to authenticated using (true);
create policy "lecture connectee" on public.signals          for select to authenticated using (true);
create policy "lecture connectee" on public.sync_log         for select to authenticated using (true);

-- Profils : tout le monde (connecté) les lit, chacun modifie le sien
create policy "lecture connectee" on public.profiles for select to authenticated using (true);
create policy "modifier son profil" on public.profiles for update to authenticated
    using (id = (select auth.uid())) with check (id = (select auth.uid()));

-- Paris : les deux comptes voient tout, chacun crée / modifie / supprime les siens
create policy "lecture connectee" on public.user_bets for select to authenticated using (true);
create policy "ajouter ses paris" on public.user_bets for insert to authenticated
    with check (user_id = (select auth.uid()));
create policy "modifier ses paris" on public.user_bets for update to authenticated
    using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));
create policy "supprimer ses paris" on public.user_bets for delete to authenticated
    using (user_id = (select auth.uid()));

-- ----------------------------------------------------------------------------- 4. calculs
-- Résultat d'un pari d'après le score final : WIN / LOSE / PUSH (remboursé) / PENDING
create or replace function public.settle_bet(p_key text, hg integer, ag integer)
returns text language sql immutable set search_path = '' as $$
    select case
        when hg is null or ag is null then 'PENDING'
        when p_key = '1X2_HOME'   then case when hg > ag then 'WIN' else 'LOSE' end
        when p_key = '1X2_DRAW'   then case when hg = ag then 'WIN' else 'LOSE' end
        when p_key = '1X2_AWAY'   then case when hg < ag then 'WIN' else 'LOSE' end
        when p_key = 'DNB_HOME'   then case when hg = ag then 'PUSH' when hg > ag then 'WIN' else 'LOSE' end
        when p_key = 'DNB_AWAY'   then case when hg = ag then 'PUSH' when hg < ag then 'WIN' else 'LOSE' end
        when p_key = 'DC_1X'      then case when hg >= ag then 'WIN' else 'LOSE' end
        when p_key = 'DC_X2'      then case when hg <= ag then 'WIN' else 'LOSE' end
        when p_key = 'DC_12'      then case when hg <> ag then 'WIN' else 'LOSE' end
        when p_key = 'BTTS_YES'   then case when hg > 0 and ag > 0 then 'WIN' else 'LOSE' end
        when p_key = 'BTTS_NO'    then case when hg = 0 or ag = 0 then 'WIN' else 'LOSE' end
        when p_key = 'OU15_OVER'  then case when hg + ag > 1 then 'WIN' else 'LOSE' end
        when p_key = 'OU15_UNDER' then case when hg + ag < 2 then 'WIN' else 'LOSE' end
        when p_key = 'OU25_OVER'  then case when hg + ag > 2 then 'WIN' else 'LOSE' end
        when p_key = 'OU25_UNDER' then case when hg + ag < 3 then 'WIN' else 'LOSE' end
        when p_key = 'OU35_OVER'  then case when hg + ag > 3 then 'WIN' else 'LOSE' end
        when p_key = 'OU35_UNDER' then case when hg + ag < 4 then 'WIN' else 'LOSE' end
    end
$$;

-- Matchs à venir avec la prédiction, et les stratégies qui proposent un pari
create or replace view public.v_upcoming with (security_invoker = on) as
select m.match_id, m.league, m.country, m.season, m.kickoff_utc, m.round_number, m.status,
       m.home_team, m.away_team,
       p.source, p.exp_home_goals, p.exp_away_goals, p.exp_total_goals, p.most_likely_score,
       p.p_home, p.p_draw, p.p_away, p.p_btts_yes, p.p_over15, p.p_over25, p.p_over35,
       p.mkt_p_home, p.mkt_p_draw, p.mkt_p_away, p.mkt_p_btts_yes, p.mkt_p_over25,
       p.odd_home, p.odd_draw, p.odd_away, p.odd_btts_yes, p.odd_btts_no, p.odd_over25, p.odd_under25,
       p.odds_label,
       coalesce(s.n, 0) as signal_count, s.strategies
from public.matches m
join public.predictions p on p.match_id = m.match_id
left join lateral (
    select count(*) as n, string_agg(distinct x.strategy, ', ') as strategies
    from public.signals x where x.match_id = m.match_id
) s on true
where m.status <> 'Finished'
  and m.kickoff_utc >= now() - interval '3 hours';

-- Matchs terminés : prédiction et résultat côte à côte
create or replace view public.v_prediction_results with (security_invoker = on) as
select m.match_id, m.league, m.season, m.kickoff_utc, m.home_team, m.away_team,
       m.home_goals, m.away_goals, m.home_xg, m.away_xg,
       p.source, p.exp_home_goals, p.exp_away_goals, p.exp_total_goals, p.most_likely_score,
       p.p_home, p.p_draw, p.p_away, p.p_btts_yes, p.p_over25,
       p.mkt_p_home, p.mkt_p_draw, p.mkt_p_away, p.odd_home, p.odd_draw, p.odd_away,
       r.actual, r.predicted, (r.actual = r.predicted) as correct_1x2,
       (m.home_goals > 0 and m.away_goals > 0) as btts_actual,
       (m.home_goals + m.away_goals > 2) as over25_actual,
       ((p.p_btts_yes >= 0.5) = (m.home_goals > 0 and m.away_goals > 0)) as correct_btts,
       ((p.p_over25 >= 0.5) = (m.home_goals + m.away_goals > 2)) as correct_over25,
       (p.most_likely_score = m.home_goals || '-' || m.away_goals) as correct_score
from public.matches m
join public.predictions p on p.match_id = m.match_id
cross join lateral (
    select case when m.home_goals > m.away_goals then 'H' when m.home_goals = m.away_goals then 'D' else 'A' end as actual,
           case when p.p_home >= p.p_draw and p.p_home >= p.p_away then 'H'
                when p.p_away >= p.p_draw then 'A' else 'D' end as predicted
) r
where m.status = 'Finished' and m.home_goals is not null and m.away_goals is not null;

-- Indicateurs de précision des prédictions, par ligue et toutes ligues confondues
create or replace function public.prediction_kpis(p_from timestamptz default null, p_to timestamptz default null)
returns table (
    league text, matches bigint, pct_1x2 numeric, pct_btts numeric, pct_over25 numeric, pct_score numeric,
    logloss_model numeric, logloss_market numeric, matches_market bigint,
    avg_exp_goals numeric, avg_goals numeric
) language sql stable security invoker set search_path = '' as $$
    with r as (
        select v.*,
               case when v.mkt_p_home is not null then
                    -ln(greatest(case v.actual when 'H' then v.p_home when 'D' then v.p_draw else v.p_away end, 1e-6))
               end as ll_model,          -- perte du modèle, sur les matchs qui ont aussi la cote
               case when v.mkt_p_home is not null then
                    -ln(greatest(case v.actual when 'H' then v.mkt_p_home when 'D' then v.mkt_p_draw else v.mkt_p_away end, 1e-6))
               end as ll_market
        from public.v_prediction_results v
        where (p_from is null or v.kickoff_utc >= p_from) and (p_to is null or v.kickoff_utc < p_to)
    )
    select coalesce(r.league, 'Toutes ligues'),
           count(*),
           round(100.0 * avg(r.correct_1x2::int), 1),
           round(100.0 * avg(r.correct_btts::int), 1),
           round(100.0 * avg(r.correct_over25::int), 1),
           round(100.0 * avg(r.correct_score::int), 1),
           round(avg(r.ll_model)::numeric, 4),
           round(avg(r.ll_market)::numeric, 4),
           count(r.ll_market),
           round(avg(r.exp_total_goals), 2),
           round(avg(r.home_goals + r.away_goals), 2)
    from r
    group by grouping sets ((r.league), ())
    order by grouping(r.league) desc, r.league
$$;

-- Bilan des stratégies simulées : une ligne par famille et par ligue, avec un verdict
create or replace view public.v_strategy_overview with (security_invoker = on) as
select s.family, s.league,
       max(s.strategy_name) filter (where s.season = 'Test')  as strategy_name,
       max(s.roi_pct)       filter (where s.season = 'Test')  as roi_test,
       max(s.bets)          filter (where s.season = 'Test')  as bets_test,
       max(s.profit)        filter (where s.season = 'Test')  as profit_test,
       max(s.roi_pct)       filter (where s.season = 'Total') as roi_total,
       max(s.bets)          filter (where s.season = 'Total') as bets_total,
       count(*) filter (where s.sort_order = 1 and s.roi_pct > 0) as seasons_positive,
       count(*) filter (where s.sort_order = 1)                   as seasons,
       case when max(s.bets) filter (where s.season = 'Test') is null then 'Sans test'
            when max(s.roi_pct) filter (where s.season = 'Test') >=  3 then 'Gagnante'
            when max(s.roi_pct) filter (where s.season = 'Test') <= -3 then 'Perdante'
            else 'Équilibrée' end as verdict,
       max(s.model_run_id) as model_run_id
from public.strategy_reports s
group by s.family, s.league;

-- Paris fictifs avec le match
create or replace view public.v_signals with (security_invoker = on) as
select x.*, m.league, m.kickoff_utc, m.home_team, m.away_team, m.home_goals, m.away_goals, m.status as match_status
from public.signals x
join public.matches m on m.match_id = x.match_id;

-- Bilan des paris fictifs par stratégie, avec un verdict
create or replace view public.v_paper_summary with (security_invoker = on) as
select x.strategy, min(m.league) as league,
       count(*) as bets,
       count(*) filter (where x.result = 'PENDING') as pending,
       count(*) filter (where x.result = 'WIN')     as won,
       count(*) filter (where x.result = 'LOSE')    as lost,
       sum(x.stake)  filter (where x.result <> 'PENDING') as stakes,
       sum(x.profit) as profit,
       round(100 * sum(x.profit) / nullif(sum(x.stake) filter (where x.result <> 'PENDING'), 0), 1) as roi_pct,
       case when count(*) filter (where x.result <> 'PENDING') < 20 then 'Trop tôt'
            when 100 * sum(x.profit) / nullif(sum(x.stake) filter (where x.result <> 'PENDING'), 0) >=  3 then 'Gagnante'
            when 100 * sum(x.profit) / nullif(sum(x.stake) filter (where x.result <> 'PENDING'), 0) <= -3 then 'Perdante'
            else 'Équilibrée' end as verdict
from public.signals x
join public.matches m on m.match_id = x.match_id
group by x.strategy;

-- Paris saisis, avec le match, le résultat et le gain
create or replace view public.v_user_bets with (security_invoker = on) as
select b.id, b.user_id, pr.display_name, b.match_id, b.bet_key, b.odd, b.stake, b.strategy, b.bookmaker,
       b.note, b.is_void, b.placed_at, b.created_at,
       m.league, m.season, m.kickoff_utc, m.home_team, m.away_team, m.home_goals, m.away_goals,
       m.status as match_status,
       r.result,
       case r.result when 'WIN' then round(b.stake * (b.odd - 1), 2)
                     when 'LOSE' then -b.stake
                     when 'PENDING' then null
                     else 0 end as profit
from public.user_bets b
join public.matches m on m.match_id = b.match_id
left join public.profiles pr on pr.id = b.user_id
cross join lateral (
    select case when b.is_void then 'VOID'
                when m.status <> 'Finished' then 'PENDING'
                else public.settle_bet(b.bet_key, m.home_goals, m.away_goals) end as result
) r;

revoke execute on function public.prediction_kpis(timestamptz, timestamptz) from anon, public;
grant execute on function public.prediction_kpis(timestamptz, timestamptz) to authenticated;
revoke execute on function public.handle_new_user() from anon, authenticated, public;
