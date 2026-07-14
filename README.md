# EffectAPK

Минималистичный сервис для Windows 11: **двойной клик по `.apk` → нативное resizable-окно с работающим Android-приложением.** Без эмуляторного UI, без лаунчера, без магазина приложений.

```
double-click app.apk
        │
        ▼
EffectApk.exe (трей, single-instance)
        │  aapt2 dump badging  → package, versionCode, label, иконка
        │  adb install -r      → только если versionCode изменился
        ▼
headless Android Emulator (AOSP x86_64, -no-window, живёт в фоне)
        │
        ▼
scrcpy --new-display --start-app=<pkg>   → приложение на СВОЁМ виртуальном дисплее
        │
        ▼
WPF-окно ⟵ SetParent(окно scrcpy)  — заголовок = имя приложения, иконка = иконка APK
```

## Почему ADB + эмулятор + scrcpy (обоснование архитектуры)

**WSA мёртв.** Windows Subsystem for Android прекратил поддержку 5 марта 2025 и удалён из Microsoft Store. Встроенного способа запускать APK в Windows 11 больше нет — рантайм нужно приносить свой.

Рассмотренные альтернативы:

| Подход | Почему нет |
|---|---|
| Собственный порт ART/Android | Годы работы, несовместимость с нативными `.so`-библиотеками |
| Waydroid-подобный контейнер | Требует Linux-ядра с binder; под Windows — только через WSL2 c кастомным ядром, хрупко |
| Сторонние эмуляторы (BlueStacks и т.п.) | Закрытый код, свой UI/реклама, нет headless-режима, нельзя распространять |
| **Android Emulator (QEMU) из SDK + ADB + scrcpy** | ✅ Официальный, скриптуемый (`sdkmanager`/`avdmanager`), headless (`-no-window`), AOSP-образ без Google Play, лицензии позволяют интеграцию |

**Почему scrcpy, а не окно эмулятора:** scrcpy ≥ 3.0 умеет `--new-display=WxH` — создаёт на устройстве *виртуальный дисплей* и запускает на нём одно приложение (`--start-app`). Это даёт: (1) картинку без статус-бара/навигации/домашнего экрана Android; (2) независимое окно на каждый APK; (3) возможность менять разрешение дисплея на лету (`adb shell wm size WxH -d <displayId>`) — на этом построен Shift+resize. Ввод (мышь/клавиатура/скролл/буфер обмена) scrcpy прокидывает сам.

**Почему WPF, а не WinUI 3:** сердце проекта — встраивание Win32-окна scrcpy (`HwndHost` + `SetParent`) и перехват `WM_SIZING` для фиксации пропорций. В WPF и то и другое — первоклассные механизмы; в WinUI 3 встраивание чужих HWND и трей-иконки — известные болевые точки.

## Структура решения

```
EffectApk.sln
└── src/EffectApk.App/            WinExe, net8.0-windows, WPF (+WinForms только ради NotifyIcon)
    ├── App.xaml(.cs)             Оркестратор: single-instance, трей, мастер, жизненный цикл сессий
    ├── app.manifest              Per-Monitor V2 DPI
    ├── Core/
    │   ├── AdbService.cs         Обёртка adb: install, versionCode, force-stop, wm size, поиск дисплея
    │   ├── ApkInspector.cs       aapt2 dump badging + извлечение PNG-иконки из zip-структуры APK
    │   ├── AppSettings.cs        %LOCALAPPDATA%\EffectApk\settings.json + вычисляемые пути SDK
    │   ├── EmulatorService.cs    Единственный headless-эмулятор: старт, ожидание boot, останов
    │   ├── FileAssociationService.cs  HKCU\Software\Classes (.apk → EffectApk.ApkFile), без админа
    │   ├── Logger.cs             Файловый лог в %LOCALAPPDATA%\EffectApk\logs
    │   ├── ProcessRunner.cs      Запуск процессов: с ожиданием/таймаутом и detached
    │   ├── ResizePolicy.cs       Политика Shift+resize: пиксели окна → разрешение Android-дисплея
    │   ├── RuntimeBootstrapper.cs Одноразовая установка SDK/AVD/scrcpy (единственные сетевые вызовы)
    │   └── SingleInstance.cs     Mutex + именованный канал для передачи путей второму клику
    ├── Sessions/
    │   ├── ScrcpySession.cs      Процесс scrcpy: --new-display, поиск HWND по заголовку, displayId
    │   └── AppSession.cs         Связка APK ↔ scrcpy ↔ окно
    ├── UI/
    │   ├── AppHostWindow.xaml(.cs)  Окно приложения: WM_SIZING (аспект) / Shift+resize (wm size)
    │   ├── ScrcpyHost.cs         HwndHost: SetParent + WS_CHILD для окна scrcpy
    │   ├── SetupWizardWindow.xaml(.cs)  Мастер первичной настройки с прогрессом
    │   └── TrayIconService.cs    NotifyIcon: ассоциация, лог, стоп рантайма, выход
    └── Interop/NativeMethods.cs  user32/shell32 P/Invoke
```

## Сборка и локальное тестирование

Требования: Windows 11 x64, .NET 8 SDK, Java 17+ (для `sdkmanager`; например [Temurin](https://adoptium.net)), ~4 ГБ диска, включённая аппаратная виртуализация (WHPX/Hyper-V) для скорости эмулятора.

```powershell
# 1. Сборка
dotnet build EffectApk.sln -c Release

# 2. Публикация single-dir (по желанию)
dotnet publish src/EffectApk.App -c Release -r win-x64 --self-contained false -o out

# 3. Первый запуск — мастер настройки скачает SDK/AVD/scrcpy (один раз, 5–15 мин)
.\out\EffectApk.exe

# 4. Тест открытия APK из консоли (эквивалент двойного клика)
.\out\EffectApk.exe "C:\path\to\app.apk"
# Для теста удобен любой open-source APK, например F-Droid client или Simple Gallery

# 5. Файловая ассоциация: пункт трея «Назначить обработчиком .apk», затем двойной клик по .apk
#    Если у .apk уже был обработчик, Windows один раз спросит «Каким образом открыть?» — выберите EffectAPK
```

Проверка сценариев: обычный resize за угол/край (пропорции держатся) → resize с зажатым **Shift** (свободный; после отпускания мыши Android-приложение переверстается под новое разрешение, ~0.5–1 с) → закрытие окна (`am force-stop` + остановка трансляции) → повторный двойной клик по тому же APK (без переустановки, мгновенный запуск) → два разных APK (два независимых окна).

Диагностика: лог `%LOCALAPPDATA%\EffectApk\logs\effectapk.log` (пункт трея «Открыть лог») — туда пишется вывод adb, эмулятора и scrcpy.

## Установщик

Готовый MSI собирается одним скриптом:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
# → out\EffectApkSetup.msi (~57 МБ, self-contained: .NET у пользователя не требуется)
```

Инсталлятор (WiX v5, per-user, **без прав администратора**): ставит приложение в `%LOCALAPPDATA%\Programs\EffectAPK`, регистрирует ассоциацию `.apk`, добавляет ярлык в меню «Пуск» и иконку в «Установку и удаление программ». Android SDK и scrcpy в инсталлятор **не** включаются — их тянет мастер первой настройки (иначе инсталлятор весил бы гигабайты и нарушал лицензию на редистрибуцию SDK).

## Автозагрузка

После первой успешной настройки EffectAPK один раз включает автозапуск при входе в Windows (`HKCU\...\Run` с флагом `--background`: тихий старт в трей, без мастера и всплывающих подсказок). Управляется галочкой «Запускать при входе в Windows» в меню трея.

## Логи

Всё логируется в `%LOCALAPPDATA%\EffectApk\logs\effectapk.log` (пункт трея «Открыть лог»): команды adb/эмулятора/scrcpy с кодами выхода и таймингами, жизненный цикл окон и сессий, смены разрешения, необработанные исключения. При превышении 5 МБ лог ротируется в `effectapk.prev.log`.

## Сторонние компоненты и лицензии

| Компонент | Роль | Лицензия | Как попадает к пользователю |
|---|---|---|---|
| [scrcpy](https://github.com/Genymobile/scrcpy) | Трансляция экрана + ввод + виртуальный дисплей | Apache-2.0 | Скачивается мастером с GitHub Releases |
| Android SDK Platform-Tools (adb) | Управление устройством | Android SDK License¹ | Скачивается `sdkmanager` |
| Android Emulator + AOSP system image (`default;x86_64`, без GMS) | Рантайм | Android SDK License¹ | Скачивается `sdkmanager` |
| Android build-tools (aapt2) | Чтение метаданных APK | Android SDK License¹ | Скачивается `sdkmanager` |
| .NET 8 / WPF | Приложение | MIT | Зависимость сборки |

¹ Условия Android SDK запрещают редистрибуцию бинарников в составе своего продукта — поэтому загрузка выполняется на машине пользователя с принятием лицензий (`sdkmanager --licenses`), а не вшивается в инсталлятор.

Телеметрии нет; сетевые вызовы — только одноразовые загрузки в мастере (dl.google.com, github.com).

## Известные ограничения

- **Нет Google Play Services.** Приложения, жёстко требующие GMS (многие банковские, игры с Play-логином), не запустятся или будут ругаться. Это цена юридически чистого AOSP-образа.
- **Детект эмулятора/root.** Часть приложений (DRM, античиты, банки) отказывается работать на эмуляторе.
- **Только ARM-независимые APK.** Образ x86_64: APK только с `armeabi-v7a`/`arm64-v8a` нативными библиотеками не установятся (libhoudini в AOSP-образе нет). APK без нативного кода и с x86_64-библиотеками работают.
- **Мультитач и сложные жесты** ограничены возможностями scrcpy (pinch-zoom эмулируется Ctrl+мышь).
- **Первый холодный старт эмулятора** — до минуты; далее quick-boot снапшоты дают секунды.
- **Windows on ARM64** не поддерживается в этой версии (официального Android Emulator под Windows-ARM64 нет).
- `wm size -d <displayId>` требует Android 13+ — наш образ API 34, но при замене образа на более старый Shift+resize перестанет менять вёрстку.
- Определение id виртуального дисплея scrcpy — парсинг лога с fallback на `dumpsys display`; при смене формата вывода в будущих версиях scrcpy может потребоваться правка регулярного выражения (версия scrcpy закреплена в `RuntimeBootstrapper`).

## Возможные доработки

- Общий буфер обмена Windows ↔ Android (scrcpy уже умеет — прокинуть настройку).
- Drag-and-drop файлов в окно (scrcpy умеет push в `/sdcard/Download`).
- Автоопределение ориентации приложения (landscape-игры → альбомное окно по умолчанию).
- Декодирование адаптивных XML-иконок (сейчас — только PNG, иначе иконка по умолчанию).
- Автозагрузка JRE, если Java не найдена.
- «Спящий» режим: гасить эмулятор по таймауту после закрытия последнего окна.
- MSIX-пакет с декларативной файловой ассоциацией + WinGet-манифест.
- ARM64: интеграция со сторонним рантаймом, когда появится жизнеспособный вариант.
