# План разработки: BluSee — Bluetooth Battery Monitor в трее Windows

> Ревизия плана Gemini (`preplan.md`) под принятые решения. `preplan.md` оставлен как есть.

## Принятые решения (отличия от preplan)
- **Архитектура:** один проект (без Clean Architecture из CLAUDE.md — overkill для трей-утилиты).
- **Приоритет:** минимум ресурсов (размер .exe + RAM). Всё визуальное вторично.
- **Распространение:** запуск **без прав администратора**, **portable**. Допустим
  framework-dependent (нужен установленный .NET 10 Desktop Runtime), остальное — portable.
- **Источники заряда (multi-source):** не только Bluetooth, но и устройства через USB-приёмник
  (Logi Bolt / Unifying / LIGHTSPEED). PnP-свойство ОС проверено на железе пользователя —
  **Windows заряд НЕ публикует** (виден только в Logi Options+). Поэтому для Logitech-устройств
  **HID++ — основной путь** (не опциональный). PnP-провайдер остаётся для устройств, где ОС всё же
  кэширует заряд; BLE GATT — для BLE.

## Факты железа (диагностика Этапа 1)
- Нет ни одного devnode с battery DEVPKEY (`{104EA319-...}`) — исчерпывающий проход дал 0.
- Устройства ввода — через Logitech-приёмники: `046D:C548` (Bolt), `046D:C539` (LIGHTSPEED).
- HID++ канал = vendor-defined коллекция, usage page `0xFF00` (интерфейс `MI_02`, short report
  `0x10`/usage `0x0001`, long report `0x11`/usage `0x0002`).
- Доступ к vendor-коллекции: WinRT `HidDevice` даёт `UnauthorizedAccessException` (узкий sharing,
  Logitech-софт держит хэндл). Решение — **Win32 `CreateFile` с `FILE_SHARE_READ|WRITE`** (P/Invoke,
  `NativeHid.cs`) + async `FileStream` для read/write report'ов. Перечисление интерфейсов остаётся
  через WinRT `HidDevice.GetDeviceSelector` (usage 0xFF00, usageId 0x0001 short + 0x0002 long).

---

## Стек технологий
- **Платформа:** .NET 10 (C# 14)
- **TFM:** `net10.0-windows10.0.19041.0` (прямой доступ к WinRT API без сторонних NuGet).
- **Тип:** WinForms `NotifyIcon` + `ApplicationContext`, без главного окна (`WinExe`).
  Самый лёгкий нативный путь к трею. WPF / Generic Host отклонены — лишняя RAM.
- **API Windows:** WinRT — `Windows.Devices.Enumeration` (`DeviceWatcher`, `DeviceInformation`),
  `Windows.Devices.Bluetooth` (BLE GATT, `BluetoothAdapter`, `Radio`).

---

## Этап 1: Исследование и MVP-консоль (диагностика)
**Цель:** найти реальный property key заряда и понять, какие устройства (BT + Bolt) его отдают.

1. **Проект:** консольный режим `Program.cs` (`--diag`) с TFM `net10.0-windows10.0.19041.0`.
2. **Широкое перечисление (НЕ только Bluetooth):** `DeviceInformation.FindAllAsync` без узкого
   AQS-фильтра — взять все HID/AEP-устройства, запросив пакет `additionalProperties` с кандидатами
   ключей батареи. Так в выборку попадают и BT-устройства, и USB-приёмник Logi Bolt/Unifying.
   Различать транспорт по `System.Devices.Aep.ProtocolId` / контейнеру.
3. **⚠️ Найти ключ батареи опытным путём:** **залогировать ВСЕ свойства** каждого устройства.
   Кандидаты: `"{104EA319-6EE2-4701-BD47-8D0F1493C853} 2"` (`DEVPKEY ... BatteryLevel`) и
   `... 9` (из preplan) — проверить оба на своём железе; ключ/ID зависит от драйвера.
4. **Чтение заряда — приоритет дешёвого пути:**
   - **PnP-свойство ОС (унифицировано):** значение из `DeviceInformation.Properties` по найденному
     ключу. Работает для Bluetooth Classic **и** для Bolt/Unifying, если Windows кэширует заряд
     (он виден в Settings → Bluetooth & devices).
   - **BLE GATT (fallback):** Battery Service `0x180F` (`GattServiceUuids.Battery`), характеристика
     `0x2A19` — для BLE-устройств, чей заряд не закэширован. Только для подключённых.
5. **MVP:** вывести список устройств с транспортом и `имя — XX%` (или `n/a`); пометить, какой
   провайдер дал значение.
6. **Дисциплина async (CLAUDE.md):** WinRT `IAsyncOperation` → `.AsTask()` + `await`. Запрещены
   `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`.

---

## Этап 2: Трей-инфраструктура
**Цель:** фоновое приложение с иконкой в трее.

1. **Жизненный цикл:** `Program.cs` с `[STAThread]`, `Application.Run(new TrayAppContext())`.
2. **Single instance:** именованный `Mutex`; при повторном запуске — выход (иначе несколько иконок).
3. **`NotifyIcon`:** иконка + `ContextMenuStrip`: список устройств `имя — XX%`, «Обновить»,
   «Автозапуск» (тоггл), «Выход».
4. **Динамическая иконка (`TrayIconRenderer`):** генерация `Icon` из процента через `Graphics`
   (16/32px под DPI). При нескольких устройствах — показывать минимальный заряд.
   **⚠️ Утечка GDI:** после `Icon.FromHandle(hicon)` обязательно `DestroyIcon` (P/Invoke) для
   предыдущего HICON + dispose `Bitmap`/`Graphics`. Иначе течёт GDI-хэндлами за часы работы.

---

## Этап 3: Фоновый мониторинг
**Цель:** стабильность, энергоэффективность, реактивность.

1. **`DeviceWatcher`:** подписка `Added` / `Removed` / `Updated` вместо busy-polling.
2. **`PeriodicTimer`:** опрос заряда раз в 10–15 мин (заряд BT-устройств меняется медленно; частый
   опрос разряжает само устройство).
3. **Маршалинг в UI-поток:** события `DeviceWatcher` приходят на пуле потоков → обновление
   `NotifyIcon` через `SynchronizationContext` / скрытый `Control.Invoke`.
4. **Агрегация:** иконка отражает устройство с минимальным зарядом.

---

## Этап 4: Интеграция с ОС и portable-сборка
**Цель:** соответствие Win10/11 + portable без admin.

1. **Тема меню:** чтение `AppsUseLightTheme` из реестра (`ThemeReader`), тёмное/светлое меню.
2. **Уведомления:** при заряде < 15% — **`NotifyIcon.ShowBalloonTip`** (нативный, без регистрации
   AUMID и без admin). ⚠️ Toast через `CommunityToolkit.WinUI.Notifications` НЕ используем: unpackaged
   toast требует регистрации ярлыка/AUMID → ломает portable.
3. **Автозапуск (`AutostartManager`):** тоггл записи в
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — per-user, **admin не нужен**.
4. **Portable publish:** framework-dependent single-file:
   `dotnet publish -c Release -p:PublishSingleFile=true --self-contained false`.
5. **Настройки:** лёгкий JSON рядом с .exe (`AppSettings`: интервал, порог, автозапуск).
   SQLite/EF из CLAUDE.md не нужны — история заряда вне scope.

---

## Этап 5: Пограничные случаи
1. **Устройство спит:** таймаут при чтении GATT-характеристик (CancellationToken).
2. **Bluetooth выключен:** `BluetoothAdapter.GetDefaultAsync` + `Radio.State`; подписка на изменение;
   иконка «BT off».
3. **Смена DPI/масштаба:** пересчёт размера шрифта/иконки при отрисовке.
4. **Нет данных заряда:** не все устройства публикуют заряд (Classic без BLE-GATT зависит от кэша
   драйвера) → показывать `n/a`, не падать.

---

## Этап 6 (опционально, stretch): прямой HID++ для Logi Bolt/Unifying
**Цель:** заряд для Logitech-устройств, когда Windows НЕ кэширует его в PnP-свойстве.

- Условие: включать только если Этап 1 показал, что для Bolt-устройств OS-свойство пустое.
- Открыть USB-HID приёмник (VID `046D`) через `HidD_*` / `CreateFile` (P/Invoke; admin не нужен),
  слать запросы протокола **HID++ 2.0**: feature `0x1000` (BatteryStatus) или `0x1004`
  (UnifiedBattery) на нужный device-index приёмника.
- Реализовать как отдельный `HidppBatteryProvider : IBatteryProvider` — изолированно, чтобы
  основной путь (BT + PnP) не зависел от проприетарного кода. Ориентир — протокол Solaar/libratbag.

---

## Структура проекта (один .csproj)
```
BluSee.csproj
Program.cs               # STAThread, Mutex, Application.Run(TrayAppContext)
Battery/
  IBatteryProvider.cs    # абстракция источника (тестируемость без WinRT)
  PnpBatteryProvider.cs  # DeviceWatcher + PnP-свойство ОС (BT + Bolt/Unifying)
  BleGattProvider.cs     # GATT 0x180F/0x2A19 fallback для BLE
  HidppBatteryProvider.cs# (Этап 6, опц.) прямой HID++ для Logitech
  DeviceBattery.cs       # Id, Name, Transport, BatteryPercent?, IsConnected, Source
Tray/
  TrayAppContext.cs      # NotifyIcon, меню, маршалинг
  TrayIconRenderer.cs    # генерация Icon, DestroyIcon, DPI/тема
  ThemeReader.cs         # AppsUseLightTheme
Monitoring/
  BatteryMonitor.cs      # PeriodicTimer + события Watcher, агрегация
Startup/
  AutostartManager.cs    # HKCU Run toggle
Settings/
  AppSettings.cs         # JSON рядом с exe
```

---

## .csproj (старт)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>

    <!-- минимизация ресурсов -->
    <InvariantGlobalization>true</InvariantGlobalization>
    <ServerGarbageCollection>false</ServerGarbageCollection>
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
    <DebugType Condition="'$(Configuration)'=='Release'">none</DebugType>
  </PropertyGroup>
</Project>
```

Publish (portable, framework-dependent):
```
dotnet publish -c Release -p:PublishSingleFile=true --self-contained false
```
