# EmbyCredits - Credits Detection Plugin for Emby

Automatically detects and marks end credits in TV show episodes using OCR, audio fingerprinting, or black frame analysis.

## Features

- **Multiple Detection Methods** - OCR text detection, audio fingerprinting (Chromaprint), and black frame detection for anime
- **Cross-Episode Analysis** - Compares timestamps across episodes in a season for improved accuracy
- **Detection Rules** - Configure different detection methods per series, studio, or tag
- **Backup & Restore** - Export and import chapter markers for server migrations
- **Manual Editing** - Override automated timestamps when needed
- **Auto-Detection** - Automatically process new episodes as they are added to your library
- **Batch Processing** - Process entire series or individual seasons at once
- **Notifications** - Receive alerts when detection is complete

## Installation

### 1. Install the OCR Server (Optional)

Only required if you plan to use OCR-based detection:

```bash
docker run -d --name tesseract-ocr -p 8884:8884 --restart unless-stopped yock1/embycreditocr
```

## Usage

### Process Episodes

Open the plugin page from **Dashboard** > **Plugins** > **Credits Detector**.

- **Single episode**: Select a series, then an episode, and click **Process Episode**
- **Full series or season**: Select a series and season, then click **Process Selection**
- Progress is shown in real time

### Auto-Detection

Enable **Enable Auto Detection** in settings to automatically process new episodes when they are added to your library.

### View and Edit Markers

1. Use the **View Chapter Markers** section to browse detected timestamps by series
2. Click **Edit** to adjust a timestamp manually, or **Add Marker** to create one
3. Click **Detect Missing** to run detection only on episodes without existing markers

### Backup and Restore

- **Export**: Click **Export Credits Backup** to download all markers as a JSON file
- **Import**: Click **Import Credits Backup** and select one or more JSON files
- Individual per-series export is also available for more granular backup management

## Detection Modes

- **OCR Only**: Analyzes on-screen text for credit keywords. Recommended for most content.
- **Hash Only**: Uses audio fingerprinting (Chromaprint) to compare episode audio. Beta.
- **OCR with Hash Fallback**: Tries OCR first; falls back to audio fingerprinting if no result.
- **Hash with OCR Fallback**: Tries audio fingerprinting first; falls back to OCR if no result.

**Anime**: Black frame detection or specialized OCR patterns can be enabled for anime content.

## Key Settings

| Setting | Description |
|---|---|
| Detection Mode | OCR, audio hash, or combined |
| OCR Endpoint | URL of the OCR server (default: `http://localhost:8884`) |
| OCR Engine | Tesseract or PaddleOCR |
| Search Start Position | Where to start looking for credits (minutes from end or percentage) |
| Frame Rate | Frames per second to analyze |
| Keywords | Text strings to match in OCR output |
| Episode Comparison | Validate timestamps against other episodes in the same season |
| CPU Throttling | Limit CPU usage during processing |
| Auto-Detection | Process new episodes automatically |
| Detection Rules | Override settings for specific series, studios, or tags |

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Support

If you find this plugin helpful:

<a href="https://buymeacoffee.com/yockser" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" ></a>



