-- =============================================================================
-- Juniro — Supabase, version 2 de l'application (à rejouer après 001_schema.sql)
-- -----------------------------------------------------------------------------
--   1. predictions : toutes les cotes (double chance, remboursé si nul, ±1,5, ±3,5)
--   2. v_matches       : tous les matchs d'une période (joués, en attente du score, à venir)
--   3. v_leagues       : ligues et saison en cours
--   4. league_table()  : classement d'une ligue (général, domicile, extérieur, 5 derniers)
--   5. v_league_stats  : moyennes de la saison par ligue (buts, +2,5, BTTS)
--   6. v_elo           : dernier Elo connu de chaque équipe + ses 10 dernières valeurs
-- Déjà appliqué au projet « juniro » (migration app_v2). Script rejouable.
-- Documentation : DOCUMENTATION.md, chapitre 13.
-- =============================================================================

-- ----------------------------------------------------------------------------- 1. cotes
alter table public.predictions
    add column if not exists odd_dc_1x    numeric(6,2),
    add column if not exists odd_dc_x2    numeric(6,2),
    add column if not exists odd_dnb_home numeric(6,2),
    add column if not exists odd_dnb_away numeric(6,2),
    add column if not exists odd_over15   numeric(6,2),
    add column if not exists odd_under15  numeric(6,2),
    add column if not exists odd_over35   numeric(6,2),
    add column if not exists odd_under35  numeric(6,2);

-- ----------------------------------------------------------------------------- 2. matchs
-- state : FINISHED (score connu), PLAYED (joué, score à la prochaine copie),
--         LIVE (commencé il y a moins de 2 h), UPCOMING, POSTPONED
create or replace view public.v_matches with (security_invoker = on) as
select m.match_id, m.league, m.country, m.tier, m.season, m.kickoff_utc, m.round_number, m.status,
       m.home_team, m.away_team, m.home_goals, m.away_goals, m.home_xg, m.away_xg,
       case when m.status = 'Finished' and m.home_goals is not null then 'FINISHED'
            when m.status in ('Postponed', 'Cancelled', 'Abandoned') then 'POSTPONED'
            when m.kickoff_utc <= now() - interval '2 hours' then 'PLAYED'
            when m.kickoff_utc <= now() then 'LIVE'
            else 'UPCOMING' end as state,
       p.source, p.exp_home_goals, p.exp_away_goals, p.exp_total_goals, p.most_likely_score,
       p.p_home, p.p_draw, p.p_away, p.p_btts_yes, p.p_over15, p.p_over25, p.p_over35,
       p.mkt_p_home, p.mkt_p_draw, p.mkt_p_away, p.mkt_p_btts_yes, p.mkt_p_over25,
       p.odd_home, p.odd_draw, p.odd_away, p.odd_dc_1x, p.odd_dc_x2, p.odd_dnb_home, p.odd_dnb_away,
       p.odd_btts_yes, p.odd_btts_no, p.odd_over15, p.odd_under15, p.odd_over25, p.odd_under25,
       p.odd_over35, p.odd_under35, p.odds_label,
       st.home_elo, st.away_elo, st.home_rank, st.away_rank,
       coalesce(sg.n, 0) as signal_count, sg.strategies
from public.matches m
left join public.predictions p  on p.match_id = m.match_id
left join public.match_stats st on st.match_id = m.match_id
left join lateral (
    select count(*) as n, string_agg(distinct x.strategy, ', ') as strategies
    from public.signals x where x.match_id = m.match_id
) sg on true;

-- ----------------------------------------------------------------------------- 3. ligues
create or replace view public.v_leagues with (security_invoker = on) as
select m.league, max(m.country) as country, max(m.tier) as tier, max(m.season) as season
from public.matches m
group by m.league;

-- ----------------------------------------------------------------------------- 4. classement
-- p_scope : all / home / away / last5 (classement sur les 5 derniers matchs de chaque équipe)
create or replace function public.league_table(p_league text, p_scope text default 'all', p_season text default null)
returns table (rank integer, team text, played integer, won integer, drawn integer, lost integer,
               gf integer, ga integer, gd integer, pts integer, form text)
language sql stable security invoker set search_path = '' as $$
    with s as (
        select coalesce(p_season, (select max(m.season) from public.matches m where m.league = p_league)) as season
    ), g as (
        select m.home_team as team, true as is_home, m.kickoff_utc, m.home_goals as f, m.away_goals as a
        from public.matches m cross join s
        where m.league = p_league and m.season = s.season and m.status = 'Finished'
          and m.home_goals is not null and m.away_goals is not null
        union all
        select m.away_team, false, m.kickoff_utc, m.away_goals, m.home_goals
        from public.matches m cross join s
        where m.league = p_league and m.season = s.season and m.status = 'Finished'
          and m.home_goals is not null and m.away_goals is not null
    ), teams as (
        select distinct g.team from g
    ), k as (
        select g.*, row_number() over (partition by g.team order by g.kickoff_utc desc) as rn
        from g
        where p_scope in ('all', 'last5') or (p_scope = 'home' and g.is_home) or (p_scope = 'away' and not g.is_home)
    ), kept as (
        select * from k where p_scope <> 'last5' or k.rn <= 5
    ), agg as (
        select t.team,
               count(x.team)::int                        as played,
               count(*) filter (where x.f > x.a)::int    as won,
               count(*) filter (where x.f = x.a)::int    as drawn,
               count(*) filter (where x.f < x.a)::int    as lost,
               coalesce(sum(x.f), 0)::int                as gf,
               coalesce(sum(x.a), 0)::int                as ga,
               string_agg(case when x.f > x.a then 'V' when x.f = x.a then 'N' else 'D' end, ''
                          order by x.kickoff_utc) filter (where x.rn <= 5) as form
        from teams t
        left join kept x on x.team = t.team
        group by t.team
    )
    select (row_number() over (order by 3 * a.won + a.drawn desc, a.gf - a.ga desc, a.gf desc, a.team))::int,
           a.team, a.played, a.won, a.drawn, a.lost, a.gf, a.ga, a.gf - a.ga, 3 * a.won + a.drawn,
           coalesce(a.form, '')
    from agg a
    order by 1
$$;

-- ----------------------------------------------------------------------------- 5. moyennes de ligue
create or replace view public.v_league_stats with (security_invoker = on) as
select m.league, m.season,
       count(*)                                                                 as matches,
       round(avg(m.home_goals + m.away_goals), 2)                               as avg_goals,
       round(100.0 * avg((m.home_goals + m.away_goals > 2)::int), 1)            as pct_over25,
       round(100.0 * avg((m.home_goals > 0 and m.away_goals > 0)::int), 1)      as pct_btts,
       round(100.0 * avg((m.home_goals > m.away_goals)::int), 1)                as pct_home_win
from public.matches m
join public.v_leagues l on l.league = m.league and l.season = m.season
where m.status = 'Finished' and m.home_goals is not null and m.away_goals is not null
group by m.league, m.season;

-- ----------------------------------------------------------------------------- 6. Elo
-- Elo = valeur avant le dernier match connu de l'équipe (match à venir compris, donc à jour
-- dès que le prochain match est relevé). history = 10 dernières valeurs, de la plus ancienne
-- à la plus récente ; delta10 = évolution sur ces 10 valeurs. Seules les équipes qui jouent
-- la saison en cours d'une des ligues suivies sont gardées (pas les relégués partis plus bas).
create or replace view public.v_elo with (security_invoker = on) as
with t as (
    select m.home_team as team, m.league, m.country, m.tier, m.season, m.kickoff_utc, s.home_elo as elo
    from public.matches m join public.match_stats s on s.match_id = m.match_id
    where s.home_elo is not null
    union all
    select m.away_team, m.league, m.country, m.tier, m.season, m.kickoff_utc, s.away_elo
    from public.matches m join public.match_stats s on s.match_id = m.match_id
    where s.away_elo is not null
), r as (
    select t.*, row_number() over (partition by t.team order by t.kickoff_utc desc) as rn from t
), h as (
    select r.team, array_agg(r.elo::float8 order by r.kickoff_utc) as history,
           (array_agg(r.elo order by r.kickoff_utc))[1] as first_elo
    from r where r.rn <= 10 group by r.team
), cur as (
    select l.team, l.league, l.country, l.tier, l.elo, l.kickoff_utc
    from r l join public.v_leagues lg on lg.league = l.league and lg.season = l.season
    where l.rn = 1
)
select c.team, c.league, c.country, c.tier, c.elo, c.kickoff_utc as elo_date,
       h.history, round(c.elo - h.first_elo, 1) as delta10,
       (rank() over (order by c.elo desc))::int as rank_all
from cur c
join h on h.team = c.team;

-- ----------------------------------------------------------------------------- droits
revoke execute on function public.league_table(text, text, text) from anon, public;
grant execute on function public.league_table(text, text, text) to authenticated;
