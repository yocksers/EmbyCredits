# EmbyCredits - Automatic Credits Detection for Emby

Automatically detect and mark end credits in TV show episodes. Never miss the start of the next episode again!

## Features

- **Multiple Detection Methods** - OCR text detection, audio fingerprinting, black frame detection for anime
- **Cross-Episode Analysis** - Compares episodes in a season for improved accuracy
- **Smart Detection Rules** - Configure different detection methods per series, studio, or tag
- **Backup & Restore** - Export/import chapter markers for server migrations
- **Manual Editing** - Override automated timestamps when needed
- **Auto-Detection** - Automatically process new episodes as they're added
- **Batch Processing** - Process entire series or seasons at once

## Prerequisites

- Emby Server 4.8+
- OCR Server (for OCR detection mode - Docker recommended)
- FFmpeg (included with Emby)

## Quick Start

### 1. Install OCR Server (Optional - for OCR detection)

Using Docker:
```bash
docker run -d --name tesseract-ocr -p 8884:8884 --restart unless-stopped yock1/embycreditocr
```

### 2. Install the Plugin

1. Download `EmbyCredits.dll` from [Releases](../../releases)
2. Copy to your Emby plugins folder:
   - **Windows**: `C:\Users\[YourUser]\AppData\Roaming\Emby-Server\plugins`
   - **Linux**: `/var/lib/emby/plugins`
   - **Docker**: `/config/plugins`
3. Restart Emby Server

### 3. Configure

1. Go to **Dashboard** → **Plugins** → **Credits Detector**
2. Select detection mode (default is OCR Only)
3. For OCR: Set **OCR Endpoint** to `http://localhost:8884` and test connection
4. **Docker users**: Set **Custom Temp Folder Path** to `/tmp` to prevent container bloat
5. Save and you're ready to go

## Basic Usage

### Process Episodes

**Single Episode:**
1. Go to **Dashboard** → **Plugins** → **Credits Detector**
2. Select a series and episode
3. Click **Process Episode**

**Entire Series/Season:**
1. Select a series from the dropdown
2. Click **Process Selection** for all episodes, or select a specific season first
3. Monitor real-time progress

### Auto-Detection

Enable **Enable Auto Detection** in settings to automatically process new episodes when added to your library.

### View & Edit Markers

1. Select a series from **View Chapter Markers**
2. View all detected timestamps
3. Click **Edit** to manually adjust or **Add Marker** to create new ones
4. Use **Detect Missing** to process episodes without markers

### Backup & Restore

- **Export**: Click **Export Credits Backup** to download all markers
- **Import**: Click **Import Credits Backup** and select JSON file(s)
- **Bulk Export**: Export individual files per series for granular backup management

## Detection Modes

The plugin supports multiple detection methods, configurable per series using the Rules system:

- **OCR Only**: Analyzes on-screen text for credit keywords (recommended for most content)
- **Hash Only**: Uses audio fingerprinting to compare episodes (beta)
- **OCR with Hash Fallback**: Tries OCR first, then audio fingerprinting
- **Hash with OCR Fallback**: Tries audio fingerprinting first, then OCR

**Anime Detection**: Automatically detects black frames or uses specialized OCR patterns common in anime credits.

## Configuration

Most settings can be adjusted in the plugin configuration page. Key settings include:

- **Detection Mode**: Choose detection method (OCR/Hash/Combined)
- **OCR Endpoint**: URL for OCR server (default: `http://localhost:8884`)
- **Search Start Position**: Where to begin looking for credits (minutes from end or percentage)
- **Frame Rate**: Frames per second to analyze (lower = faster, higher = more accurate)
- **Keywords**: Text to search for in credits
- **Episode Comparison**: Compare timestamps across episodes for validation
- **CPU Throttling**: Limit CPU usage to prevent system slowdown
- **Auto-Detection**: Automatically process new episodes
- **Detection Rules**: Configure different detection methods for specific series, studios, or tags

For detailed configuration options, explore the plugin settings page in Emby.

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Support

If you find this plugin helpful:

<a href="https://buymeacoffee.com/yockser" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" ></a>



