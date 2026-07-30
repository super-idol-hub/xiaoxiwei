-- XiaoXiWei AI chat and human handoff.
-- Run once in the XiaoXiWei Supabase project, never in Cell Fate Lab.

create table if not exists public.pet_ai_usage (
  device_id uuid not null references public.pet_devices(id) on delete cascade,
  usage_day date not null default current_date,
  request_count integer not null default 0,
  updated_at timestamptz not null default now(),
  primary key (device_id, usage_day)
);

create table if not exists public.pet_support_sessions (
  id uuid primary key default gen_random_uuid(),
  device_id uuid not null references public.pet_devices(id) on delete cascade,
  status text not null default 'open'
    check (status in ('open', 'closed')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create unique index if not exists pet_support_one_open_session_idx
  on public.pet_support_sessions (device_id)
  where status = 'open';

create table if not exists public.pet_support_messages (
  id bigint generated always as identity primary key,
  session_id uuid not null
    references public.pet_support_sessions(id) on delete cascade,
  sender text not null
    check (sender in ('user', 'assistant', 'operator', 'system')),
  content text not null check (char_length(content) between 1 and 1200),
  created_at timestamptz not null default now(),
  delivered_at timestamptz
);

create index if not exists pet_support_messages_session_idx
  on public.pet_support_messages (session_id, created_at);

alter table public.pet_ai_usage enable row level security;
alter table public.pet_support_sessions enable row level security;
alter table public.pet_support_messages enable row level security;

revoke all on public.pet_ai_usage from public, anon, authenticated;
revoke all on public.pet_support_sessions from public, anon, authenticated;
revoke all on public.pet_support_messages from public, anon, authenticated;

create or replace function public.consume_pet_ai_quota(
  p_device_id uuid,
  p_secret text,
  p_daily_limit integer default 200
)
returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
  next_count integer;
begin
  if not public.pet_device_authorized(p_device_id, p_secret) then
    return false;
  end if;

  insert into public.pet_ai_usage (
    device_id,
    usage_day,
    request_count,
    updated_at
  )
  values (
    p_device_id,
    current_date,
    1,
    now()
  )
  on conflict (device_id, usage_day) do update
  set request_count = public.pet_ai_usage.request_count + 1,
      updated_at = now()
  where public.pet_ai_usage.request_count < greatest(p_daily_limit, 1)
  returning request_count into next_count;

  return next_count is not null;
end;
$$;

create or replace function public.get_my_support_sessions()
returns table (
  session_id uuid,
  status text,
  created_at timestamptz,
  updated_at timestamptz,
  last_message text,
  message_count bigint
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
      s.id,
      s.status,
      s.created_at,
      s.updated_at,
      coalesce((
        select m.content
        from public.pet_support_messages m
        where m.session_id = s.id
        order by m.created_at desc, m.id desc
        limit 1
      ), ''),
      (
        select count(*)
        from public.pet_support_messages m
        where m.session_id = s.id
      )
    from public.pet_support_sessions s
    where s.device_id = current_device_id
    order by
      case when s.status = 'open' then 0 else 1 end,
      s.updated_at desc
    limit 20;
end;
$$;

create or replace function public.get_my_support_messages(
  p_session_id uuid
)
returns table (
  message_id bigint,
  sender text,
  content text,
  created_at timestamptz
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
    select m.id, m.sender, m.content, m.created_at
    from public.pet_support_messages m
    join public.pet_support_sessions s on s.id = m.session_id
    where s.id = p_session_id
      and s.device_id = current_device_id
    order by m.created_at, m.id
    limit 200;
end;
$$;

create or replace function public.reply_my_support_session(
  p_session_id uuid,
  p_content text
)
returns bigint
language plpgsql
security definer
set search_path = ''
as $$
declare
  current_device_id uuid := public.pet_console_device_id();
  result_id bigint;
begin
  if current_device_id is null then
    raise exception 'Console access denied';
  end if;
  if char_length(trim(coalesce(p_content, ''))) not between 1 and 1200 then
    raise exception 'Message must contain 1 to 1200 characters';
  end if;
  if not exists (
    select 1
    from public.pet_support_sessions s
    where s.id = p_session_id
      and s.device_id = current_device_id
      and s.status = 'open'
  ) then
    raise exception 'Support session not found';
  end if;

  insert into public.pet_support_messages (session_id, sender, content)
  values (p_session_id, 'operator', trim(p_content))
  returning id into result_id;

  update public.pet_support_sessions
  set updated_at = now()
  where id = p_session_id;

  return result_id;
end;
$$;

create or replace function public.close_my_support_session(
  p_session_id uuid
)
returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
  current_device_id uuid := public.pet_console_device_id();
begin
  if current_device_id is null then
    raise exception 'Console access denied';
  end if;

  update public.pet_support_sessions
  set status = 'closed',
      updated_at = now()
  where id = p_session_id
    and device_id = current_device_id
    and status = 'open';

  return found;
end;
$$;

revoke all on function public.consume_pet_ai_quota(uuid, text, integer)
  from public, anon, authenticated;
grant execute on function public.consume_pet_ai_quota(uuid, text, integer)
  to service_role;
grant execute on function public.pet_device_authorized(uuid, text)
  to service_role;

revoke all on function public.get_my_support_sessions()
  from public, anon, authenticated;
revoke all on function public.get_my_support_messages(uuid)
  from public, anon, authenticated;
revoke all on function public.reply_my_support_session(uuid, text)
  from public, anon, authenticated;
revoke all on function public.close_my_support_session(uuid)
  from public, anon, authenticated;

grant execute on function public.get_my_support_sessions()
  to authenticated;
grant execute on function public.get_my_support_messages(uuid)
  to authenticated;
grant execute on function public.reply_my_support_session(uuid, text)
  to authenticated;
grant execute on function public.close_my_support_session(uuid)
  to authenticated;

notify pgrst, 'reload schema';
