# Turbify app password

The account's regular password may not work with IMAP. From the security settings for the Turbify/Yahoo Small Business account:

1. Enable two-step verification if the portal requires it.
2. Find **Generate app password**.
3. Create an app-specific password for ChronoMail Bridge.
4. Use the full email address as the username.
5. Initial settings: `imap.mail.yahoo.com`, port `993`, SSL/TLS.

Test the connection with simulation mode disabled. The test lists capabilities without modifying messages.

If **Remember** is not selected, the password remains only in memory until the application closes. If it is selected, the MVP protects the password for the current Windows user with DPAPI. Revoke the password in Turbify when the migration is complete.

The portal's exact labels may change. If the option is not available, refer to the current Turbify documentation or support service.
