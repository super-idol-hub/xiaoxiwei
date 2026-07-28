-- One-account console login.
-- The desktop pet still authenticates with its local device secret.
-- The web console never reads or sends that secret.

create table if not exists public.pet_console_users (
  user_id uuid primary key references auth.users(id) on delete cascade,
  device_id uuid not null unique references public.pet_devices(id) on delete cascade,
  created_at timestamptz not null default now()
);

alter table public.pet_console_users enable row level security;
revoke all on public.pet_console_users from public, anon, authenticated;

create or replace function public.pet_console_device_id()
returns uuid
language sql
stable
security definer
set search_path = ''
as $$
  select c.device_id
  from public.pet_console_users c
  where c.user_id = (select auth.uid());
$$;

create or replace function public.get_my_pet_status()
returns table (
  device_name text,
  is_online boolean,
  last_seen_at timestamptz
)
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  current_device_id uuid := public.pet_console_device_id();
begin
  if current_device_id is null then
    raise exception 'Console access denied';
  end if;

  return query
    select
      d.name,
      coalesce(d.last_seen_at > now() - interval '25 seconds', false),
      d.last_seen_at
    from public.pet_devices d
    where d.id = current_device_id;
end;
$$;

create or replace function public.send_my_pet_message(
  p_content text,
  p_client_message_id uuid
)
returns bigint
language plpgsql
security definer
set search_path = ''
as $$
declare
  current_device_id uuid := public.pet_console_device_id();
  message_id bigint;
begin
  if current_device_id is null then
    raise exception 'Console access denied';
  end if;
  if char_length(trim(coalesce(p_content, ''))) not between 1 and 300 then
    raise exception 'Message must contain 1 to 300 characters';
  end if;

  delete from public.pet_messages
  where device_id = current_device_id and expires_at < now();

  insert into public.pet_messages (
    client_message_id,
    device_id,
    content
  )
  values (
    p_client_message_id,
    current_device_id,
    trim(p_content)
  )
  on conflict (client_message_id) do update
    set client_message_id = excluded.client_message_id
    where public.pet_messages.device_id = excluded.device_id
  returning id into message_id;

  return message_id;
end;
$$;

create or replace function public.get_my_pet_message_status(
  p_message_id bigint
)
returns text
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  current_device_id uuid := public.pet_console_device_id();
  result text;
begin
  if current_device_id is null then
    raise exception 'Console access denied';
  end if;

  select m.status into result
  from public.pet_messages m
  where m.id = p_message_id and m.device_id = current_device_id;
  return result;
end;
$$;

-- Remove browser access to the old secret-based console functions.
revoke all on function public.register_pet_device(uuid, text, text) from public, anon, authenticated;
revoke all on function public.send_pet_message(uuid, text, text, uuid) from public, anon, authenticated;
revoke all on function public.get_pet_status(uuid, text) from public, anon, authenticated;
revoke all on function public.get_pet_message_status(uuid, text, bigint) from public, anon, authenticated;

-- Keep only the three functions required by the desktop pet available to anon.
revoke all on function public.pet_heartbeat(uuid, text) from public, anon, authenticated;
revoke all on function public.pull_pet_messages(uuid, text, integer) from public, anon, authenticated;
revoke all on function public.ack_pet_message(uuid, text, bigint) from public, anon, authenticated;
grant execute on function public.pet_heartbeat(uuid, text) to anon, authenticated;
grant execute on function public.pull_pet_messages(uuid, text, integer) to anon, authenticated;
grant execute on function public.ack_pet_message(uuid, text, bigint) to anon, authenticated;

-- The web console functions require a valid Supabase Auth session.
revoke all on function public.pet_console_device_id() from public, anon, authenticated;
revoke all on function public.get_my_pet_status() from public, anon, authenticated;
revoke all on function public.send_my_pet_message(text, uuid) from public, anon, authenticated;
revoke all on function public.get_my_pet_message_status(bigint) from public, anon, authenticated;
grant execute on function public.get_my_pet_status() to authenticated;
grant execute on function public.send_my_pet_message(text, uuid) to authenticated;
grant execute on function public.get_my_pet_message_status(bigint) to authenticated;

-- Bind the one console account after it has been created in Supabase Auth.
insert into public.pet_console_users (user_id, device_id)
select u.id, 'bc0d1ff9-5d80-48bb-bd55-0b12620f82a6'::uuid
from auth.users u
where lower(u.email) = 'xiaoxiwei-console@local.invalid'
on conflict (user_id) do update
set device_id = excluded.device_id;
