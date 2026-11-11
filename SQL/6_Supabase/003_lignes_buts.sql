-- =============================================================================
-- Juniro — Supabase, prédictions Plus/Moins 1,5 et 3,5 buts (à rejouer après 002_app_v2.sql)
-- -----------------------------------------------------------------------------
--   1. predictions          : probabilité du marché ±1,5 et ±3,5 (mkt_p_over15, mkt_p_over35)
--   2. v_matches            : ces deux colonnes en fin de vue
--   3. v_prediction_results : ±1,5 et ±3,5 réalisés et bien prévus
--   4. prediction_kpis()    : % de ±1,5 / ±3,5 bien prévus, perte log modèle / marché par ligne
-- p_over15 / p_over35 existent déjà : depuis le 10/11/2026 ce sont les probabilités finales
-- (modèle de score + classifieur dédié), comme p_over25.
-- Déjà appliqué au projet « juniro » (migration lignes_buts). Script rejouable.
-- Documentation : DOCUMENTATION.md, chapitre 13.
-- =============================================================================

-- ----------------------------------------------------------------------------- 1. colonnes
alter table public.predictions
    add column if not exists mkt_p_over15 double precision,
    add column if not exists mkt_p_over35 double precision;

-- ----------------------------------------------------------------------------- 2. matchs
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
       coalesce(sg.n, 0) as signal_count, sg.strategies,
       p.mkt_p_over15, p.mkt_p_over35
from public.matches m
left join public.predictions p  on p.match_id = m.match_id
left join public.match_stats st on st.match_id = m.match_id
left join lateral (
    select count(*) as n, string_agg(distinct x.strategy, ', ') as strategies
    from public.signals x where x.match_id = m.match_id
) sg on true;

-- ----------------------------------------------------------------------------- 3. résultats
create or replace view public.v_prediction_results with (security_invoker = on) as
select m.match_id, m.league, m.season, m.kickoff_utc, m.home_team, m.away_team,
       m.home_goals, m.away_goals, m.home_xg, m.away_xg,
       p.source, p.exp_home_goals, p.exp_away_goals, p.exp_total_goals, p.most_likely_score,
       p.p_home, p.p_draw, p.p_away, p.p_btts_yes, p.p_over25,
       p.mkt_p_home, p.mkt_p_draw, p.mkt_p_away, p.odd_home, p.odd_draw, p.odd_away,
       r.actual, r.predicted, r.actual = r.predicted as correct_1x2,
       m.home_goals > 0 and m.away_goals > 0 as btts_actual,
       m.home_goals + m.away_goals > 2 as over25_actual,
       (p.p_btts_yes >= 0.5) = (m.home_goals > 0 and m.away_goals > 0) as correct_btts,
       (p.p_over25 >= 0.5) = (m.home_goals + m.away_goals > 2) as correct_over25,
       p.most_likely_score = m.home_goals || '-' || m.away_goals as correct_score,
       -- ajouts du 10/11/2026 (en fin de vue : create or replace n'accepte que des colonnes en plus)
       p.p_over15, p.p_over35, p.mkt_p_btts_yes, p.mkt_p_over15, p.mkt_p_over25, p.mkt_p_over35,
       m.home_goals + m.away_goals > 1 as over15_actual,
       m.home_goals + m.away_goals > 3 as over35_actual,
       (p.p_over15 >= 0.5) = (m.home_goals + m.away_goals > 1) as correct_over15,
       (p.p_over35 >= 0.5) = (m.home_goals + m.away_goals > 3) as correct_over35
from public.matches m
join public.predictions p on p.match_id = m.match_id
cross join lateral (
    select case when m.home_goals > m.away_goals then 'H' when m.home_goals = m.away_goals then 'D' else 'A' end as actual,
           case when p.p_home >= p.p_draw and p.p_home >= p.p_away then 'H'
                when p.p_away >= p.p_draw then 'A' else 'D' end as predicted
) r
where m.status = 'Finished' and m.home_goals is not null and m.away_goals is not null;

-- ----------------------------------------------------------------------------- 4. indicateurs
-- Le type de retour change : la fonction est supprimée puis recréée (l'application lit les
-- colonnes par leur nom, les anciennes sont conservées à l'identique).
drop function if exists public.prediction_kpis(timestamptz, timestamptz);

create function public.prediction_kpis(p_from timestamptz default null, p_to timestamptz default null)
returns table (league text, matches bigint, pct_1x2 numeric, pct_btts numeric, pct_over25 numeric,
               pct_score numeric, logloss_model numeric, logloss_market numeric, matches_market bigint,
               avg_exp_goals numeric, avg_goals numeric,
               pct_over15 numeric, pct_over35 numeric,
               ll_over15_model numeric, ll_over15_market numeric,
               ll_over25_model numeric, ll_over25_market numeric,
               ll_over35_model numeric, ll_over35_market numeric)
language sql stable security invoker set search_path = '' as $$
    with r as (
        select v.*,
               case when v.mkt_p_home is not null then
                    -ln(greatest(case v.actual when 'H' then v.p_home when 'D' then v.p_draw else v.p_away end, 1e-6))
               end as ll_model,
               case when v.mkt_p_home is not null then
                    -ln(greatest(case v.actual when 'H' then v.mkt_p_home when 'D' then v.mkt_p_draw else v.mkt_p_away end, 1e-6))
               end as ll_market,
               -- Plus/Moins : perte log du modèle et du marché, sur les seuls matchs où le marché est connu
               case when v.mkt_p_over15 is not null then -ln(greatest(case when v.over15_actual then v.p_over15 else 1 - v.p_over15 end, 1e-6)) end as l15m,
               case when v.mkt_p_over15 is not null then -ln(greatest(case when v.over15_actual then v.mkt_p_over15 else 1 - v.mkt_p_over15 end, 1e-6)) end as l15k,
               case when v.mkt_p_over25 is not null then -ln(greatest(case when v.over25_actual then v.p_over25 else 1 - v.p_over25 end, 1e-6)) end as l25m,
               case when v.mkt_p_over25 is not null then -ln(greatest(case when v.over25_actual then v.mkt_p_over25 else 1 - v.mkt_p_over25 end, 1e-6)) end as l25k,
               case when v.mkt_p_over35 is not null then -ln(greatest(case when v.over35_actual then v.p_over35 else 1 - v.p_over35 end, 1e-6)) end as l35m,
               case when v.mkt_p_over35 is not null then -ln(greatest(case when v.over35_actual then v.mkt_p_over35 else 1 - v.mkt_p_over35 end, 1e-6)) end as l35k
        from public.v_prediction_results v
        where (p_from is null or v.kickoff_utc >= p_from) and (p_to is null or v.kickoff_utc < p_to)
    )
    select coalesce(r.league, 'Toutes ligues'), count(*),
           round(100.0 * avg(r.correct_1x2::int), 1), round(100.0 * avg(r.correct_btts::int), 1),
           round(100.0 * avg(r.correct_over25::int), 1), round(100.0 * avg(r.correct_score::int), 1),
           round(avg(r.ll_model)::numeric, 4), round(avg(r.ll_market)::numeric, 4), count(r.ll_market),
           round(avg(r.exp_total_goals), 2), round(avg(r.home_goals + r.away_goals), 2),
           round(100.0 * avg(r.correct_over15::int), 1), round(100.0 * avg(r.correct_over35::int), 1),
           round(avg(r.l15m)::numeric, 4), round(avg(r.l15k)::numeric, 4),
           round(avg(r.l25m)::numeric, 4), round(avg(r.l25k)::numeric, 4),
           round(avg(r.l35m)::numeric, 4), round(avg(r.l35k)::numeric, 4)
    from r
    group by grouping sets ((r.league), ())
    order by grouping(r.league) desc, r.league
$$;

revoke execute on function public.prediction_kpis(timestamptz, timestamptz) from anon, public;
grant execute on function public.prediction_kpis(timestamptz, timestamptz) to authenticated;
