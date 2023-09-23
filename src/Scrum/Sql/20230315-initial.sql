-- SQLite

-- sqlite3 /home/rh/Downloads/scrumfs.sqlite < src/Scrum/Sql/20230315-initial.sql

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

-- TODO: extend with more field from actual solution
create table domain_events(
    id text primary key,
    payload text not null
) strict;