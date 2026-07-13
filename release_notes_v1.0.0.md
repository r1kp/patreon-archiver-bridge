# Patreon Archiver Bridge v1.0.0

We are excited to announce the first official release of the **Patreon Archiver Bridge** (v1.0.0)! 

This native Windows companion application works hand-in-hand with the **Patreon Archiver** Chrome extension to allow direct saving of files to your disk, bypassing browser download limitations and resolving sandbox restrictions.

---

## 🚀 Key Features

*   **Native WPF Dashboard:** A clean, modern Windows dashboard displaying active connection status, download progress, and media engine statistics.
*   **Native Messaging Bridge:** Secure communication with the Chrome extension over standard standard input/output pipes (stdio)—no open ports or local servers required.
*   **Self-Contained Installer:** The setup wizard includes all necessary WPF and .NET dependencies. It runs out of the box on any Windows 10 or 11 computer without requiring pre-installed runtimes.
*   **Automatic Updates:** Silent background updates powered by Velopack, ensuring you are always running the latest version without manual intervention.
*   **Robust Uninstaller:** Thoroughly cleans up registry entries, shortcuts, and temporary workspace directories to leave no trace upon removal.

---

## 🛡️ Windows SmartScreen Warning (Unsigned App)

Because this release is not signed with a paid commercial certificate, Windows SmartScreen will flag the setup file as untrusted when first downloaded. To run the installer, please follow these steps:

1.  **Download** the installer (`PatreonArchiverBridge_setup.exe`) to your computer.
2.  **Right-click** the downloaded file and select **Properties** from the context menu.
3.  On the **General** tab, look at the very bottom for the **Security** section.
4.  Check the **Unblock** checkbox (or "Zulassen" in German).
5.  Click **Apply**, and then **OK**.
6.  Double-click `PatreonArchiverBridge_setup.exe` to run the installation.

*Note: If you run the file directly without unblocking, click "More Info" in the SmartScreen popup, and then click "Run anyway".*

---

## 📦 Release Assets

Please find the following assets below:

*   **`PatreonArchiverBridge_setup.exe`**: The recommended Custom Setup Wizard. Run this to install the application to your preferred directory.
*   **`PatreonArchiverBridge-win-Setup.exe`**: The silent Velopack installer executed by the custom wizard in the background.
*   **`PatreonArchiverBridge-win-Portable.zip`**: A portable version of the app that does not require installation.
*   **`PatreonArchiverBridge-1.0.0-full.nupkg`**: Velopack package containing the application binaries (required for auto-updates).
*   **`releases.win.json`**: Velopack releases metadata index file (required for auto-updates).
