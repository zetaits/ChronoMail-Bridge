# Configure Google Workspace OAuth

1. Sign in to Google Cloud Console with an authorized account.
2. Create or select a dedicated project and enable the **Gmail API**.
3. Configure the consent screen. If the organization allows an internal application, select that type; limit test users during the pilot.
4. Create **Credentials → OAuth client ID → Desktop app**.
5. Download the JSON file and keep it outside the repository and the reports folder.
6. In ChronoMail Bridge, select the JSON file and choose **Authorize Google**.
7. On the Google screen, verify that only `https://www.googleapis.com/auth/gmail.modify` is requested.

The installed application opens the system browser and receives the response through a loopback redirect. The official library handles `state` and PKCE. Client values and tokens are stored as DPAPI blobs for the current user; the original JSON file selected by the user is not copied as plain text.

To revoke access, open the Google account security page, remove the application, and delete `state/secrets` while ChronoMail Bridge is closed. This requires a new authorization the next time the application runs.

References:

- [OAuth 2.0 for installed applications](https://developers.google.com/identity/protocols/oauth2/native-app)
- [OAuth best practices](https://developers.google.com/identity/protocols/oauth2/resources/best-practices)
