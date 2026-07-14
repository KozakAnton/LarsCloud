# Lar’s Cloud

Lar’s Cloud — Windows 10/11 програма для автоматичного резервного копіювання вибраної локальної папки безпосередньо на Google Drive. Застосунок написаний на C# / .NET 8 / WPF, працює у системному треї, зберігає стан у SQLite та завантажує лише нові або змінені файли.

## Що вже реалізовано

- вхід і повторний вхід через офіційний Google OAuth 2.0 для Desktop app із PKCE та loopback redirect;
- шифрування OAuth-токена через Windows DPAPI для поточного користувача;
- пряме завантаження на Google Drive без стороннього сервера;
- папки `Lar's Cloud / НАЗВА_КОМП'ЮТЕРА`;
- збереження структури всіх вкладених папок;
- порівняння шляху, розміру, часу зміни й SHA-256 через локальну SQLite-базу;
- resumable upload частинами по 8 МБ для великих файлів;
- ручна синхронізація та графік від 1 до 7 днів (типово 2 дні);
- безпечна поведінка за замовчуванням: видалення локального файлу не видаляє копію з Drive;
- автозапуск Windows, фоновий планувальник, одна копія програми й системний трей;
- стан інтернету, Google Drive, останньої/наступної синхронізації, розміру папки й квоти;
- прогрес, поточний файл, швидкість, орієнтовний час, скасування;
- останні три синхронізації, SQLite-історія до 100 записів і ротація журналу;
- Windows-сповіщення через системний трей;
- перевірка GitHub Releases, SHA-256, завантаження й запуск Installer;
- self-contained `LarsCloud.exe` — користувачеві не потрібні Python або .NET;
- Inno Setup Installer із ярликами, автозапуском і деінсталяцією;
- GitHub Actions для автоматичного створення `LarsCloud_Setup.exe`.

## Перед першим збиранням

Програма навмисно не містить чужих ключів. Потрібно один раз:

1. Створити Google Cloud проєкт, увімкнути Google Drive API й отримати OAuth Client ID типу **Desktop app** — див. [docs/GOOGLE_OAUTH.md](docs/GOOGLE_OAUTH.md).
2. Указати Client ID у `src/LarsCloud.App/appsettings.json` або передати його скрипту збирання.
3. Для автооновлення вказати власника й назву GitHub-репозиторію.

Обидві кнопки «Зареєструватися» та «Увійти» запускають той самий безпечний Google OAuth-процес. Lar’s Cloud не створює окремих паролів і не бачить пароль Google.

## Найпростіше локальне збирання на Windows

Потрібні:

- Windows 10/11 x64;
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0);
- [Inno Setup 6](https://jrsoftware.org/isdl.php).

Відкрийте PowerShell у корені проєкту:

```powershell
.\scripts\build-installer.ps1 `
  -GoogleClientId "ВАШ_CLIENT_ID.apps.googleusercontent.com" `
  -GitHubOwner "ВАШ_GITHUB_LOGIN" `
  -GitHubRepository "LarsCloud"
```

Або відредагуйте `appsettings.json` і двічі натисніть `scripts\build_installer.bat`.

Результат:

```text
artifacts\installer\LarsCloud_Setup.exe
artifacts\installer\LarsCloud_Setup.exe.sha256
```

Лише `.exe` без Installer: `scripts\build_exe_only.bat`.

Деталі: [docs/BUILD_AND_INSTALLER.md](docs/BUILD_AND_INSTALLER.md).

## Автоматичне збирання через GitHub

У репозиторії вже є `.github/workflows/release.yml`. Додайте секрет `GOOGLE_CLIENT_ID`, створіть тег `v1.0.0` і відправте його на GitHub. Windows runner протестує програму, збере Installer, порахує SHA-256 і створить GitHub Release. Покроково: [docs/GITHUB_RELEASES.md](docs/GITHUB_RELEASES.md).

## Дані на комп’ютері користувача

```text
%LOCALAPPDATA%\LarsCloud\
├── auth.dat          # DPAPI-зашифрований OAuth-токен
├── settings.json     # налаштування без пароля/токена
├── state.db          # SQLite-стан файлів та історія
├── Logs\             # ротаційний журнал без токенів
└── Updates\          # перевірений Installer оновлення
```

Видалення програми не змінює локальні файли користувача і не видаляє файли з Google Drive.

## Структура

```text
src/LarsCloud.App/
├── Models/           # моделі налаштувань, Drive, синхронізації
├── Services/         # OAuth, Drive API, SQLite, scheduler, update, tray
├── ViewModels/       # логіка інтерфейсу
├── Infrastructure/   # команди, шляхи, форматування, single instance
├── Assets/           # наданий логотип і Windows .ico
├── App.xaml          # палітра і спільні стилі
└── MainWindow.xaml   # головна панель та налаштування
installer/            # Inno Setup
scripts/              # автоматичне збирання
tests/                # автоматичні тести
docs/                 # налаштування, архітектура, тест-план
```

## Важливе перед публічним розповсюдженням

- Налаштуйте OAuth consent screen і, якщо Google цього вимагатиме для вашої аудиторії/області доступу, пройдіть перевірку застосунку.
- Підпишіть Installer сертифікатом Authenticode. SHA-256 захищає механізм автооновлення, але цифровий підпис також зменшує попередження Windows SmartScreen.
- Заповніть актуальну політику конфіденційності й контакт підтримки.
- Пройдіть сценарії з [docs/TESTING.md](docs/TESTING.md) на чистих Windows 10 та Windows 11.

## Документація

- [Архітектура](docs/ARCHITECTURE.md)
- [Google OAuth і Drive API](docs/GOOGLE_OAUTH.md)
- [Збирання `.exe` та Installer](docs/BUILD_AND_INSTALLER.md)
- [GitHub Releases та оновлення](docs/GITHUB_RELEASES.md)
- [Повний тест-план](docs/TESTING.md)
- [Політика конфіденційності](PRIVACY.md)
