-- SQLite Scrum schema

-- To manually create database with schema:
-- $ sqlite3 scrum_web.sqlite < src/Scrum/Sql/20230315-initial.sql
-- $ sqlite3 scrum_test.sqlite < src/Scrum/Sql/20230315-initial.sql

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
    updated_at integer null    
) strict;

create index tasks_id_asc_story_id_asc_index on tasks (id asc, story_id asc);

create table domain_events(
    id text primary key,
    aggregate_type text not null,
    aggregate_id text not null, -- TODO: add index
    event_type text not null,                    
    event_payload text not null,
    created_at integer not null -- TODO: add index                          
) strict;

create index domain_events_aggregate_id_asc_created_at_asc_index on domain_events (aggregate_id asc, created_at asc);