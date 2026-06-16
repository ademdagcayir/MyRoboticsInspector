-- ============================================================================
-- MyRoboticsInspector — Bulut Yedek Şeması (Supabase)
-- ============================================================================
-- KURULUM: Supabase → SQL Editor'da BİR KEZ çalıştır.
-- Proje: vmjqfqbfcwjiyzhzxnlr (ChequeApp ile aynı proje; inspector_ öneki ile
-- tamamen izole tablolar + ayrı 'inspector-files' bucket kullanılır).
--
-- GÜVENLİK MODELİ (add-only bulut):
--   • Her kayıt/dosya tenant_id = auth.uid() ile izole (RLS).
--   • SELECT / INSERT / UPDATE serbest (kendi tenant'ında).
--   • DELETE normalde İMKANSIZ — delete politikası "kilit açma penceresi" ister.
--   • Silme kilidi yalnız inspector_unlock_delete(pin) RPC'siyle 5 dakikalığına
--     açılır; PIN hash'i sunucuda bcrypt ile saklanır (pgcrypto).
-- ============================================================================

create extension if not exists pgcrypto;

-- ────────────────────────────────────────────────────────────────────────────
-- 1) Tablolar
-- ────────────────────────────────────────────────────────────────────────────

-- Tüm SQLite satırlarının jsonb anlık görüntüsü (tablo başına ayrı tablo yerine
-- tek generik tablo: tek upsert yolu, şema değişikliklerine dayanıklı).
create table if not exists inspector_records (
    id          bigint generated always as identity primary key,
    tenant_id   uuid        not null default auth.uid(),
    device_id   text        not null,
    table_name  text        not null,   -- customers | jobs | inspections | defects | profiles | app_settings
    local_id    bigint      not null,   -- cihazdaki SQLite satır Id'si
    payload     jsonb       not null,   -- satırın tamamı (System.Text.Json)
    updated_at  timestamptz not null default now(),
    unique (tenant_id, device_id, table_name, local_id)
);

-- Yüklenen dosyaların metadata'sı (storage.objects'e ek; listeleme/geri yükleme için).
create table if not exists inspector_files (
    id           bigint generated always as identity primary key,
    tenant_id    uuid        not null default auth.uid(),
    device_id    text        not null,
    rel_path     text        not null,   -- StorageRoot'a göre yol (projeler/...)
    storage_path text        not null,   -- bucket içi tam yol ({uid}/{device}/{rel})
    byte_size    bigint      not null default 0,
    content_type text,
    uploaded_at  timestamptz not null default now(),
    unique (tenant_id, storage_path)
);

-- Cihaz envanteri (hangi cihaz ne zaman senkronladı).
create table if not exists inspector_devices (
    tenant_id    uuid        not null default auth.uid(),
    device_id    text        not null,
    device_name  text,
    platform     text,
    app_version  text,
    last_seen_at timestamptz not null default now(),
    primary key (tenant_id, device_id)
);

-- Tenant ayarları: silme PIN hash'i.
create table if not exists inspector_settings (
    tenant_id       uuid primary key default auth.uid(),
    delete_pin_hash text,
    created_at      timestamptz not null default now()
);

-- Silme kilidi pencereleri (yalnız RPC yazar).
create table if not exists inspector_delete_grants (
    tenant_id  uuid primary key,
    expires_at timestamptz not null
);

create index if not exists idx_inspector_records_tenant on inspector_records (tenant_id, table_name);
create index if not exists idx_inspector_files_tenant   on inspector_files (tenant_id, uploaded_at desc);

-- ────────────────────────────────────────────────────────────────────────────
-- 2) RLS
-- ────────────────────────────────────────────────────────────────────────────

alter table inspector_records       enable row level security;
alter table inspector_files         enable row level security;
alter table inspector_devices       enable row level security;
alter table inspector_settings      enable row level security;
alter table inspector_delete_grants enable row level security;

-- Silme kilidi açık mı? (DELETE politikaları bunu çağırır.)
create or replace function inspector_delete_unlocked()
returns boolean
language sql stable security definer set search_path = public, extensions
as $$
    select exists (
        select 1 from inspector_delete_grants g
        where g.tenant_id = auth.uid() and g.expires_at > now()
    );
$$;

-- inspector_records: okuma/ekleme/güncelleme kendi tenant'ında; silme YALNIZ kilit açıkken.
drop policy if exists rec_select on inspector_records;
create policy rec_select on inspector_records for select using (tenant_id = auth.uid());
drop policy if exists rec_insert on inspector_records;
create policy rec_insert on inspector_records for insert with check (tenant_id = auth.uid());
drop policy if exists rec_update on inspector_records;
create policy rec_update on inspector_records for update using (tenant_id = auth.uid());
drop policy if exists rec_delete on inspector_records;
create policy rec_delete on inspector_records for delete
    using (tenant_id = auth.uid() and inspector_delete_unlocked());

-- inspector_files: aynı model.
drop policy if exists file_select on inspector_files;
create policy file_select on inspector_files for select using (tenant_id = auth.uid());
drop policy if exists file_insert on inspector_files;
create policy file_insert on inspector_files for insert with check (tenant_id = auth.uid());
drop policy if exists file_update on inspector_files;
create policy file_update on inspector_files for update using (tenant_id = auth.uid());
drop policy if exists file_delete on inspector_files;
create policy file_delete on inspector_files for delete
    using (tenant_id = auth.uid() and inspector_delete_unlocked());

-- inspector_devices: tam erişim kendi tenant'ında (silinebilir — veri değil envanter).
drop policy if exists dev_all on inspector_devices;
create policy dev_all on inspector_devices for all
    using (tenant_id = auth.uid()) with check (tenant_id = auth.uid());

-- inspector_settings: yalnız okuma; yazma RPC üzerinden (PIN hash'ini API ile ezmek yasak).
drop policy if exists set_select on inspector_settings;
create policy set_select on inspector_settings for select using (tenant_id = auth.uid());

-- inspector_delete_grants: yalnız okuma (kalan süre göstermek için); yazma RPC üzerinden.
drop policy if exists grant_select on inspector_delete_grants;
create policy grant_select on inspector_delete_grants for select using (tenant_id = auth.uid());

-- ────────────────────────────────────────────────────────────────────────────
-- 3) RPC'ler (SECURITY DEFINER — RLS'i bilinçli aşar, auth.uid() ile sınırlar)
-- ────────────────────────────────────────────────────────────────────────────

-- Silme PIN'i belirle/değiştir. İlk kurulumda p_current boş geçilir.
create or replace function inspector_set_delete_pin(p_current text, p_new text)
returns void
language plpgsql security definer set search_path = public, extensions
as $$
declare
    v_uid  uuid := auth.uid();
    v_hash text;
begin
    if v_uid is null then raise exception 'Oturum yok'; end if;
    if p_new is null or length(p_new) < 4 then
        raise exception 'PIN en az 4 karakter olmalı';
    end if;

    select delete_pin_hash into v_hash from inspector_settings where tenant_id = v_uid;

    if v_hash is not null and crypt(coalesce(p_current, ''), v_hash) <> v_hash then
        raise exception 'Mevcut PIN hatalı';
    end if;

    insert into inspector_settings (tenant_id, delete_pin_hash)
    values (v_uid, crypt(p_new, gen_salt('bf')))
    on conflict (tenant_id) do update set delete_pin_hash = excluded.delete_pin_hash;
end;
$$;

-- PIN doğrula → 5 dakikalık silme penceresi aç. Bitiş zamanını döndürür.
create or replace function inspector_unlock_delete(p_pin text)
returns timestamptz
language plpgsql security definer set search_path = public, extensions
as $$
declare
    v_uid     uuid := auth.uid();
    v_hash    text;
    v_expires timestamptz;
begin
    if v_uid is null then raise exception 'Oturum yok'; end if;

    select delete_pin_hash into v_hash from inspector_settings where tenant_id = v_uid;
    if v_hash is null then
        raise exception 'Önce silme PIN''i belirlemelisiniz (Ayarlar → Bulut Yedek)';
    end if;
    if crypt(coalesce(p_pin, ''), v_hash) <> v_hash then
        raise exception 'PIN hatalı';
    end if;

    v_expires := now() + interval '5 minutes';
    insert into inspector_delete_grants (tenant_id, expires_at)
    values (v_uid, v_expires)
    on conflict (tenant_id) do update set expires_at = excluded.expires_at;
    return v_expires;
end;
$$;

-- Silme penceresini hemen kapat.
create or replace function inspector_lock_delete()
returns void
language plpgsql security definer set search_path = public, extensions
as $$
begin
    delete from inspector_delete_grants where tenant_id = auth.uid();
end;
$$;

-- ────────────────────────────────────────────────────────────────────────────
-- 4) Storage: 'inspector-files' bucket + add-only politikalar
-- ────────────────────────────────────────────────────────────────────────────

insert into storage.buckets (id, name, public)
values ('inspector-files', 'inspector-files', false)
on conflict (id) do nothing;

-- Yükleme: yalnız kendi {uid}/ klasörüne. Üzerine yazma (UPDATE) politikası YOK
-- → dosyalar değiştirilemez (immutable yedek).
drop policy if exists inspector_files_insert on storage.objects;
create policy inspector_files_insert on storage.objects for insert
    with check (
        bucket_id = 'inspector-files'
        and (storage.foldername(name))[1] = auth.uid()::text
    );

drop policy if exists inspector_files_select on storage.objects;
create policy inspector_files_select on storage.objects for select
    using (
        bucket_id = 'inspector-files'
        and (storage.foldername(name))[1] = auth.uid()::text
    );

-- Silme: yalnız PIN ile açılmış pencere içinde.
drop policy if exists inspector_files_delete on storage.objects;
create policy inspector_files_delete on storage.objects for delete
    using (
        bucket_id = 'inspector-files'
        and (storage.foldername(name))[1] = auth.uid()::text
        and inspector_delete_unlocked()
    );

-- ────────────────────────────────────────────────────────────────────────────
-- Doğrulama sorguları (manuel kontrol için)
-- ────────────────────────────────────────────────────────────────────────────
-- select count(*) from inspector_records;
-- select rel_path, byte_size, uploaded_at from inspector_files order by uploaded_at desc limit 20;
-- select * from inspector_devices;
