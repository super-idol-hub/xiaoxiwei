-- 小曦薇远程消息通道
-- 在 Supabase Dashboard -> SQL Editor 中完整执行一次。

create extension if not exists pgcrypto with schema extensions;

create table if not exists public.pet_devices (
  id uuid primary key,
  name text not null check (char_length(name) between 1 and 40),
  secret_hash text not null,
  last_seen_at timestamptz,
  created_at timestamptz not null default now()
);

create table if not exists public.pet_messages (
  id bigint generated always as identity primary key,
  client_message_id uuid not null unique,
  device_id uuid not null references public.pet_devices(id) on delete cascade,
  content text not null check (char_length(content) between 1 and 300),
  status text not null default 'queued'
    check (status in ('queued', 'leased', 'delivered')),
  lease_until timestamptz,
  created_at timestamptz not null default now(),
  delivered_at timestamptz,
  expires_at timestamptz not null default (now() + interval '1 day')
);

create index if not exists pet_messages_device_queue_idx
  on public.pet_messages (device_id, status, created_at);

alter table public.pet_devices enable row level security;
alter table public.pet_messages enable row level security;

revoke all on public.pet_devices from anon, authenticated;
revoke all on public.pet_messages from anon, authenticated;

create or replace function public.pet_secret_hash(p_secret text)
returns text
language sql
immutable
security invoker
set search_path = ''
as $$
  select encode(extensions.digest(convert_to(p_secret, 'UTF8'), 'sha256'), 'hex');
$$;

create or replace function public.pet_device_authorized(p_device_id uuid, p_secret text)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
  select exists (
    select 1
    from public.pet_devices d
    where d.id = p_device_id
      and d.secret_hash = public.pet_secret_hash(p_secret)
  );
$$;

create or replace function public.register_pet_device(
  p_device_id uuid,
  p_name text,
  p_secret text
)
returns uuid
language plpgsql
security definer
set search_path = ''
as $$
begin
  if p_device_id is null
     or char_length(trim(coalesce(p_name, ''))) not between 1 and 40
     or char_length(coalesce(p_secret, '')) < 32 then
    raise exception 'Invalid device registration';
  end if;

  insert into public.pet_devices (id, name, secret_hash)
  values (
    p_device_id,
    trim(p_name),
    public.pet_secret_hash(p_secret)
  )
  on conflict (id) do update
    set name = excluded.name
    where public.pet_devices.secret_hash = excluded.secret_hash;

  if not found then
    raise exception 'Device already exists with a different secret';
  end if;
  return p_device_id;
end;
$$;

create or replace function public.send_pet_message(
  p_device_id uuid,
  p_secret text,
  p_content text,
  p_client_message_id uuid
)
returns bigint
language plpgsql
security definer
set search_path = ''
as $$
declare
  message_id bigint;
begin
  if not public.pet_device_authorized(p_device_id, p_secret) then
    raise exception 'Device authentication failed';
  end if;
  if char_length(trim(coalesce(p_content, ''))) not between 1 and 300 then
    raise exception 'Message must contain 1 to 300 characters';
  end if;

  delete from public.pet_messages
  where device_id = p_device_id and expires_at < now();

  insert into public.pet_messages (
    client_message_id,
    device_id,
    content
  )
  values (
    p_client_message_id,
    p_device_id,
    trim(p_content)
  )
  on conflict (client_message_id) do update
    set client_message_id = excluded.client_message_id
    where public.pet_messages.device_id = excluded.device_id
  returning id into message_id;

  return message_id;
end;
$$;

create or replace function public.pet_heartbeat(
  p_device_id uuid,
  p_secret text
)
returns timestamptz
language plpgsql
security definer
set search_path = ''
as $$
declare
  seen_at timestamptz := now();
begin
  update public.pet_devices
  set last_seen_at = seen_at
  where id = p_device_id
    and secret_hash = public.pet_secret_hash(p_secret);
  if not found then
    raise exception 'Device authentication failed';
  end if;
  return seen_at;
end;
$$;

create or replace function public.get_pet_status(
  p_device_id uuid,
  p_secret text
)
returns table (
  device_name text,
  is_online boolean,
  last_seen_at timestamptz
)
language plpgsql
security definer
set search_path = ''
as $$
begin
  if not public.pet_device_authorized(p_device_id, p_secret) then
    raise exception 'Device authentication failed';
  end if;
  return query
    select
      d.name,
      coalesce(d.last_seen_at > now() - interval '25 seconds', false),
      d.last_seen_at
    from public.pet_devices d
    where d.id = p_device_id;
end;
$$;

create or replace function public.pull_pet_messages(
  p_device_id uuid,
  p_secret text,
  p_limit integer default 5
)
returns table (
  id bigint,
  content text,
  created_at timestamptz
)
language plpgsql
security definer
set search_path = ''
as $$
begin
  if not public.pet_device_authorized(p_device_id, p_secret) then
    raise exception 'Device authentication failed';
  end if;

  update public.pet_devices
  set last_seen_at = now()
  where public.pet_devices.id = p_device_id;

  return query
    with candidates as (
      select m.id
      from public.pet_messages m
      where m.device_id = p_device_id
        and m.expires_at > now()
        and (
          m.status = 'queued'
          or (m.status = 'leased' and m.lease_until < now())
        )
      order by m.created_at
      for update skip locked
      limit least(greatest(coalesce(p_limit, 5), 1), 10)
    )
    update public.pet_messages m
    set status = 'leased',
        lease_until = now() + interval '30 seconds'
    from candidates c
    where m.id = c.id
    returning m.id, m.content, m.created_at;
end;
$$;

create or replace function public.ack_pet_message(
  p_device_id uuid,
  p_secret text,
  p_message_id bigint
)
returns boolean
language plpgsql
security definer
set search_path = ''
as $$
begin
  if not public.pet_device_authorized(p_device_id, p_secret) then
    raise exception 'Device authentication failed';
  end if;

  update public.pet_messages
  set status = 'delivered',
      delivered_at = now(),
      lease_until = null
  where id = p_message_id
    and device_id = p_device_id
    and status in ('leased', 'delivered');
  return found;
end;
$$;

create or replace function public.get_pet_message_status(
  p_device_id uuid,
  p_secret text,
  p_message_id bigint
)
returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
  result text;
begin
  if not public.pet_device_authorized(p_device_id, p_secret) then
    raise exception 'Device authentication failed';
  end if;
  select m.status into result
  from public.pet_messages m
  where m.id = p_message_id and m.device_id = p_device_id;
  return result;
end;
$$;

revoke all on function public.pet_secret_hash(text) from public;
revoke all on function public.pet_device_authorized(uuid, text) from public;

grant execute on function public.register_pet_device(uuid, text, text) to anon, authenticated;
grant execute on function public.send_pet_message(uuid, text, text, uuid) to anon, authenticated;
grant execute on function public.pet_heartbeat(uuid, text) to anon, authenticated;
grant execute on function public.get_pet_status(uuid, text) to anon, authenticated;
grant execute on function public.pull_pet_messages(uuid, text, integer) to anon, authenticated;
grant execute on function public.ack_pet_message(uuid, text, bigint) to anon, authenticated;
grant execute on function public.get_pet_message_status(uuid, text, bigint) to anon, authenticated;
