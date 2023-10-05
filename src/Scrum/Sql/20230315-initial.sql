-- SQLite Scrum schema

-- To manually create database with schema:
-- $ sqlite3 scrum_web.sqlite < src/Scrum/Sql/20230315-initial.sql
-- $ sqlite3 scrum_test.sqlite < src/Scrum/Sql/20230315-initial.sql

-- When an application connects to SQLite, it must issue
-- pragma foreign_keys = ON
-- Or foreign key cascade delete has no effect.

-- Every script, except for the initial one, must be idempotent.

create table stories(
    id text primary key,
    title text not null,
    description text null,
    created_at integer not null,
    updated_at integer null
) strict;

create table tasks(
    id text primary key,
    story_id text not null,
    title text not null,
    description text null,
    created_at integer not null,
    updated_at integer null,
    foreign key (story_id) references stories(id) on delete cascade
) strict;

create index tasks_id_asc_story_id_asc_index on tasks (id asc, story_id asc);

create table domain_events(
    id text primary key,
    aggregate_type text not null,
    aggregate_id text not null,
    event_type text not null,                    
    event_payload text not null,
    created_at integer not null                          
) strict;

create index domain_events_aggregate_id_asc_created_at_asc_index on domain_events (aggregate_id asc, created_at asc);
