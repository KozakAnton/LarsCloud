# GitHub Releases та автоматичні оновлення

## Підготовка репозиторію

1. Створіть GitHub-репозиторій, наприклад `LarsCloud`.
2. Завантажте в нього весь проєкт.
3. Settings → Secrets and variables → Actions → New repository secret.
4. Створіть `GOOGLE_CLIENT_ID` із Desktop OAuth Client ID.
5. `GOOGLE_CLIENT_SECRET` створюйте лише якщо його вимагає конкретний Google client; типово залиште порожнім.

Workflow уже має `permissions: contents: write`, тому `GITHUB_TOKEN` може створити Release.

## Перший реліз

```bash
git add .
git commit -m "Lar's Cloud 1.0.0"
git push origin main
git tag v1.0.0
git push origin v1.0.0
```

GitHub Actions на `windows-latest`:

- встановить .NET 8 і Inno Setup;
- виконає тести;
- підставить OAuth Client ID та поточний owner/repository;
- збере `LarsCloud_Setup.exe`;
- створить `.sha256`;
- створить Release із нотатками та двома assets.

## Наступна версія

1. Внесіть зміни.
2. Оновіть `CHANGELOG.md`.
3. Створіть більший SemVer-тег, наприклад `v1.1.0`.
4. Після публікації встановлена версія знайде Release через `GET /repos/{owner}/{repo}/releases/latest`.

## Умови автооновлення

- Release не має бути draft або prerelease, якщо він повинен вважатися latest.
- Asset має називатися точно `LarsCloud_Setup.exe`.
- GitHub API має повернути `digest: sha256:...` або поряд має бути `LarsCloud_Setup.exe.sha256`.
- Версія тегу має розбиратися як SemVer без суфікса, наприклад `v1.2.3`.

Lar’s Cloud завантажує Installer у `%LOCALAPPDATA%\LarsCloud\Updates`, повторно обчислює SHA-256, запускає тихе оновлення і завершує поточний процес. Налаштування, токен і SQLite-база розміщені поза Program Files і зберігаються.

## Приватний репозиторій

Поточна реалізація перевіряє публічний Release без GitHub token, щоб не зберігати ще один секрет на комп’ютері користувача. Для приватного репозиторію використайте окремий захищений update backend або додайте інший механізм автентифікації; не вшивайте personal access token у застосунок.
