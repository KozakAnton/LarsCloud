# Збирання `.exe` та Windows Installer

## Вимоги

На Windows 10/11 x64 встановіть:

1. Git (якщо проєкт отримано з GitHub).
2. .NET 8 SDK x64.
3. Inno Setup 6.

Перевірка:

```powershell
dotnet --info
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /?
```

## Повне збирання

```powershell
.\scripts\build-installer.ps1 `
  -Version "1.0.0" `
  -GoogleClientId "ВАШ_ID.apps.googleusercontent.com" `
  -GitHubOwner "ВАШ_LOGIN" `
  -GitHubRepository "LarsCloud"
```

Скрипт послідовно:

1. відновлює NuGet-пакети;
2. запускає тести;
3. робить self-contained single-file publish для `win-x64`;
4. створює згенерований `appsettings.json` у папці publish;
5. запускає Inno Setup;
6. обчислює SHA-256 Installer.

Фінальні файли:

```text
artifacts\installer\LarsCloud_Setup.exe
artifacts\installer\LarsCloud_Setup.exe.sha256
```

Користувачу достатньо передати `LarsCloud_Setup.exe`.

## Лише портативний `.exe`

```powershell
.\scripts\build-installer.ps1 -ExeOnly -GoogleClientId "ВАШ_ID.apps.googleusercontent.com"
```

Результат у `artifacts\publish`. Поруч із `LarsCloud.exe` залишаються `appsettings.json`, `VERSION`, `PRIVACY.md` та папка `Assets`; їх також потрібно переносити. Для звичайного користувача рекомендований Installer.

## Що робить Installer

- встановлює в `%ProgramFiles%\LarsCloud`;
- створює ярлик у меню Пуск;
- за вибором створює ярлик на робочому столі;
- за вибором додає HKCU Run із параметром `--background`;
- реєструє деінсталяцію у Windows;
- при оновленні не перезаписує вже встановлений `appsettings.json`;
- не видаляє `%LOCALAPPDATA%\LarsCloud` та файли користувача.

## Цифровий підпис

Перед широким розповсюдженням придбайте Authenticode code-signing certificate і підпишіть `LarsCloud.exe` та `LarsCloud_Setup.exe` через `signtool.exe`. Не додавайте `.pfx` або пароль до Git. У CI використовуйте захищений секрет/сертифікат вашого провайдера.

SHA-256 у проєкті захищає автооновлення від пошкодженого або підміненого release asset, але не замінює довіру Windows SmartScreen до цифрового підпису видавця.

## Зміна версії

Версію передає параметр `-Version`. Для GitHub Release тег `v1.2.0` автоматично збере версію `1.2.0`. Не перевикористовуйте старий тег для інших байтів.
