ForgePanel V2 — WPF prototype for Windows 10/11
================================================

Что есть:
- Тёмная собственная верхняя панель окна (без стандартного белого title bar).
- Адаптивный WPF-интерфейс, боковая навигация.
- Главная, библиотека, каталог, загрузки, файлы/бэкапы, метрики, настройки.
- Создание/редактирование локальных конфигураций серверов.
- Импорт/экспорт JSON, дублирование и удаление конфигураций.
- Каталог Paper, Purpur, Fabric, Forge, NeoForge, Mohist, Magma, Vanilla, Quilt и Bedrock Dedicated Server.
- Ручной ZIP-бэкап выбранной папки.
- Локальные данные: %LOCALAPPDATA%\\ForgePanelV2

Сборка:
1. Установите .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
2. Распакуйте архив.
3. Откройте PowerShell в папке ForgePanelV2.
4. Выполните:
   Set-ExecutionPolicy -Scope Process Bypass
   .\\scripts\\publish-win-x64.ps1

Выходной EXE:
ForgePanel\\bin\\Release\\net8.0-windows\\win-x64\\publish\\ForgePanel.exe

Важно:
Это UI-прототип. Реальная установка ядер, запуск серверов, загрузка модов/плагинов,
веб-сервер, Docker, Argon2, роли и расписание бэкапов пока не реализованы.
