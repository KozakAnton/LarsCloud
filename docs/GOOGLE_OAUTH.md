# Налаштування Google OAuth і Drive API

## 1. Створіть Google Cloud проєкт

1. Відкрийте [Google Cloud Console](https://console.cloud.google.com/).
2. Створіть новий проєкт, наприклад `Lars Cloud`.
3. У бібліотеці API знайдіть **Google Drive API** і натисніть **Enable**.

## 2. Налаштуйте Google Auth Platform

У розділі Google Auth Platform заповніть:

- App name: `Lar's Cloud`;
- User support email;
- Audience: External (якщо програмою користуватимуться різні Google-акаунти);
- контактний email розробника.

На етапі розробки додайте потрібні адреси до **Test users**. У тестовому режимі refresh token тестового застосунку може мати обмежений строк дії; для постійного розповсюдження опублікуйте застосунок і виконайте вимоги Google щодо перевірки.

У **Data Access** додайте область доступу
`https://www.googleapis.com/auth/drive.file`. Під час входу Google може дозволити
користувачеві підтвердити лише частину запитаних прав, тому на екрані згоди не
вимикайте пункт доступу Lar’s Cloud до файлів Google Drive.

## 3. Створіть Desktop OAuth client

1. Відкрийте **Clients** → **Create client**.
2. Application type: **Desktop app**.
3. Name: `Lar's Cloud Windows`.
4. Скопіюйте Client ID виду:

```text
1234567890-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx.apps.googleusercontent.com
```

Lar’s Cloud використовує випадковий `http://127.0.0.1:PORT/` loopback redirect і PKCE. Для OAuth client типу Desktop app порт вибирається програмою під час входу.

## 4. Додайте Client ID

Варіант A — змініть `src/LarsCloud.App/appsettings.json`:

```json
{
  "GoogleClientId": "1234567890-xxx.apps.googleusercontent.com",
  "GoogleClientSecret": "",
  "GitHubOwner": "your-login",
  "GitHubRepository": "LarsCloud",
  "InstallerAssetName": "LarsCloud_Setup.exe",
  "PrivacyPolicyUrl": "https://github.com/your-login/LarsCloud/blob/main/PRIVACY.md",
  "DriveScopes": [
    "openid",
    "email",
    "profile",
    "https://www.googleapis.com/auth/drive.file"
  ]
}
```

Варіант B — не змінюйте репозиторій і передайте параметр під час збирання:

```powershell
.\scripts\build-installer.ps1 -GoogleClientId "1234567890-xxx.apps.googleusercontent.com"
```

Для Desktop client Google може показати Client secret. Встановлені застосунки не здатні зберегти його як справжній секрет, тому код підтримує його лише як необов’язковий сумісний параметр.

## 5. Перевірте

1. Запустіть Lar’s Cloud.
2. Натисніть «Увійти через Google».
3. У браузері виберіть тестовий акаунт і підтвердьте доступ.
4. Після повернення до програми має з’явитися email.
5. Виберіть маленьку тестову папку й натисніть «Синхронізувати зараз».
6. На Drive перевірте `Lar's Cloud / НАЗВА_КОМП'ЮТЕРА`.

## Типові помилки

| Помилка | Що перевірити |
|---|---|
| `redirect_uri_mismatch` | Client має бути типу Desktop app, не Web application |
| `access_denied` | Користувач скасував вхід або Workspace admin заборонив застосунок |
| `Google Drive API has not been used` | Drive API не ввімкнений у правильному Cloud проєкті |
| `ACCESS_TOKEN_SCOPE_INSUFFICIENT` або `insufficientPermissions` | Додайте `drive.file` у Data Access, вийдіть з акаунта в Lar’s Cloud і увійдіть знову, підтвердивши доступ до файлів |
| `App is not verified` | Додайте акаунт у Test users або виконайте публікацію/перевірку |
| Немає refresh token | Відкличте доступ застосунку в Google Account і увійдіть повторно |

Офіційні матеріали: [OAuth 2.0 for Desktop Apps](https://developers.google.com/identity/protocols/oauth2/native-app), [Google Drive API](https://developers.google.com/workspace/drive/api/guides/about-sdk).
