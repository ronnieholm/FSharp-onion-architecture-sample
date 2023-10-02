-- SQLite

-- $ cd ~/git/FSharp-clean-architecture-sample
-- $ sqlite3 scrum_web.sqlite < src/Scrum/Sql/20230315-initial.sql
-- $ sqlite3 scrum_test.sqlite < src/Scrum/Sql/20230315-initial.sql

create table stories(
    id text primary key,
    title text not null,
    description text null,
    created_at text not null,
    updated_at text null
) strict;

create table tasks(
    id text primary key,
    story_id text not null, -- TODO: add index of foreign key
    title text not null,
    description text null,
    created_at text not null,
    updated_at text null    
) strict;

create table domain_events(
    id text primary key,
    aggregate_type text not null,
    aggregate_id text not null, -- TODO: add index
    event_type text not null,                    
    event_payload text not null,
    created_at text not null -- TODO: add index                          
) strict;