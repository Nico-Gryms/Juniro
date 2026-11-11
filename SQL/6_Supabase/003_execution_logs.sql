-- =============================================================================
-- Juniro — Supabase : journal d'exécution du service (à rejouer après 002_app_v2.sql)
-- -----------------------------------------------------------------------------
-- Copie des tables dbo.AppExecutionRuns / dbo.AppExecutionLogs du PC fixe (30 derniers
-- jours pour les exécutions, 14 jours pour les événements), envoyée chaque matin par le
-- job « supabase-sync » (vues app.vx_SupaExecutionRuns / app.vx_SupaExecutionLogs de A8).
-- L'application affiche l'état du dernier scrap dans l'en-tête et le détail dans « Journal ».
-- Déjà appliqué au projet « juniro » (migration execution_logs). Script rejouable.
-- =============================================================================

create table if not exists public.execution_runs (
    run_id            integer primary key,
    source            text,                 -- ScheduledTask (scrap du jour) / Backfill / SupabaseSync
    started_utc       timestamptz,
    finished_utc      timestamptz,
    duration_seconds  integer,
    status            text,                 -- Success / Warning / Failed / Interrupted / Running
    matches_found     integer,
    matches_saved     integer,
    matches_skipped   integer,
    matches_failed    integer,
    quality_degraded  boolean,
    error_count       integer,              -- lignes ERROR / FATAL du journal
    summary           text,
    error_message     text,
    synced_at         timestamptz not null default now()
);
create index if not exists ix_execution_runs_started on public.execution_runs (started_utc desc);

create table if not exists public.execution_logs (
    run_id       integer not null,
    seq          integer not null,          -- ordre de l'événement dans l'exécution
    created_utc  timestamptz,
    level        text,                      -- INFO / WARN / ERROR / FATAL
    sector       text,                      -- étape : Scraper.MatchProcessed, Supabase.Table...
    match_ident  text,
    message      text,
    exception    text,
    synced_at    timestamptz not null default now(),
    primary key (run_id, seq)
);

alter table public.execution_runs enable row level security;
alter table public.execution_logs enable row level security;

drop policy if exists "lecture connectee" on public.execution_runs;
drop policy if exists "lecture connectee" on public.execution_logs;
create policy "lecture connectee" on public.execution_runs for select to authenticated using (true);
create policy "lecture connectee" on public.execution_logs for select to authenticated using (true);
