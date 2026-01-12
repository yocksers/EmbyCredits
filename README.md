# EmbyCredits - Automatic Credits Detection Plugin for Emby

![License](https://img.shields.io/badge/license-MIT-green)

Automatically detect and mark end credits in TV show episodes using OCR (Optical Character Recognition). Never miss the start of the next episode again!

## 🎯 Features

- **🔍 OCR-Based Detection** - Uses Tesseract OCR to read on-screen text and identify credit sequences
- **⚡ Performance Optimizations** - Parallel processing, smart frame skipping, and early termination for faster detection
- **📊 Cross-Episode Comparison** - Compares detection results across episodes in the same season for improved accuracy
- **🔄 Failed Episode Fallback** - Automatically applies timestamps to failed episodes based on successful detections (configurable)
- **🎛️ Confidence Filtering** - Filter OCR results by confidence score to reduce false positives
- **💾 Backup & Restore** - Export and import credits markers with TheTVDB IDs for portability between servers
- **📦 Bulk Export by Series** - Export individual backup files for multiple selected TV shows at once
- **📥 Multi-File Import** - Import multiple backup JSON files simultaneously for faster restoration
- **✏️ Manual Marker Editing** - Edit or add credits timestamps directly in the interface for any episode
- **⚡ Batch Processing** - Efficiently process entire series with pre-computation and caching
- **🎯 Highly Configurable** - Fine-tune detection parameters, frame rates, analysis windows, and performance options
- **🐳 Docker Integration** - Works seamlessly with Tesseract Docker containers
- **🔧 Test Connection** - Built-in OCR server connectivity testing with visual feedback
- **📈 Real-Time Progress** - Live progress tracking with detailed success/failure logs
- **⏸️ Cancellation Support** - Stop processing at any time with queue clearing
- **🔍 Dry Run & Debug** - Test detection without saving markers, capture detailed logs

## 📋 Prerequisites

- **Emby Server** 4.8+ (tested on 4.9.1.90)
- **Tesseract OCR Server** (Docker recommended)
- **FFmpeg** (typically included with Emby)

## 🚀 Quick Start

### 1. Install Tesseract OCR Server (Docker)

```bash
docker run -d \
  --name tesseract-ocr \
  -p 8884:8884 \
  --restart unless-stopped \
  yock1/embycreditocr
```

**For Unraid users:**
- Container Name: `tesseract-ocr`
- Repository: `yock1/embycreditocr`
- Port: `8884:8884`
- Network: `bridge`

### 2. Install the Plugin

1. Download the latest `EmbyCredits.dll` from the [Releases](../../releases) page
2. Copy it to your Emby plugins folder:
   - **Windows**: `C:\Users\[YourUser]\AppData\Roaming\Emby-Server\plugins`
   - **Linux**: `/var/lib/emby/plugins`
   - **Docker**: `/config/plugins` (or your mapped config path)
3. Restart Emby Server

### 3. Configure the Plugin

1. Navigate to **Emby Dashboard** → **Plugins** → **Credits Detector**
2. Essential settings:
   - **Enable OCR Detection**: ✅ Check
   - **OCR Endpoint**: `http://localhost:8884` (or your Docker host IP)
   - **Custom Temp Folder Path**: 
     - **Docker users**: Set to `/tmp` or a mapped volume (e.g., `/config/temp`)
     - **Native users**: Leave empty
3. Click **Test Connection** to verify OCR server connectivity
4. Click **Save**

## ⚙️ Configuration Options

### OCR Detection Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **OCR Endpoint** | `http://localhost:8884` | URL of your Tesseract OCR server |
| **Detection Keywords** | `directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine` | Keywords to search for (case-insensitive, comma-separated) |
| **Search Start Position** | `3.0 minutes from end` | Where to begin detection - choose between **Minutes from End** (e.g., 3 minutes) or **Percentage** (e.g., 65%) |
| **Frame Rate** | `0.5` fps | Frames per second to extract (0.5 = 1 frame every 2 seconds) |
| **Minimum Matches** | `1` | Minimum keyword matches required for detection |
| **Max Analysis Duration** | `600` seconds | Maximum time to analyze (prevents excessive processing) |
| **Stop Seconds from End** | `20` | Stop analysis this many seconds before video end |
| **Image Format** | `jpg` | Frame format: `jpg` (faster) or `png` (accurate) |
| **JPEG Quality** | `92` | Quality for JPEG frames (1-100, only if format is `jpg`) |
| **Delay Between Frames** | `0` ms | Delay between processing frames (use for slower systems) |

### Performance Optimization Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **Enable Parallel Processing** | ❌ Disabled | Process multiple frames simultaneously (2-3x faster, requires powerful system) |
| **Parallel Batch Size** | `4` | Number of frames to process in parallel |
| **Enable Smart Frame Skipping** | ✅ Enabled | Skip ahead in larger chunks once keywords are detected |
| **Consecutive Matches for Early Stop** | `3` | Stop after finding this many consecutive frames with keywords (0 = disabled) |
| **Minimum OCR Confidence** | `0.0` | Minimum confidence score to accept OCR results (0 = accept all, 0.6-0.8 recommended for filtering) |

### Episode Comparison Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **Use Episode Comparison** | ✅ Enabled | Compare episodes in same season for accuracy |
| **Minimum Episodes to Compare** | `3` | Minimum episodes needed for comparison |
| **Correlation Window** | `5` seconds | Timestamps within this range are considered matches |
| **Enable Failed Episode Fallback** | ❌ Disabled | Use median timestamp for failed episodes |
| **Minimum Success Rate for Fallback** | `0.5` | Minimum success rate (50%) required for fallback |

### Performance Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **CPU Usage Limit** | `100%` | Throttle processing (100 = no limit, 50 = half speed) |
| **Delay Between Episodes** | `0` ms | Delay between processing episodes |
| **Lower Thread Priority** | ❌ Disabled | Run detection with lower priority |
| **Custom Temp Folder Path** | Empty | **CRITICAL for Docker**: Set to prevent image bloat |

### Backup & Restore Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **Overwrite Existing Credits on Import** | ❌ Disabled | When enabled, importing a backup will overwrite existing markers. When disabled, episodes with existing markers will be skipped |

## 📖 Usage

### Process a Single Episode

1. Go to **Dashboard** → **Plugins** → **Credits Detector**
2. Select a series from the dropdown
3. Click on an episode
4. Click **Process Episode**

### Process an Entire Series

1. Go to **Dashboard** → **Plugins** → **Credits Detector**
2. Select a library filter (optional) to narrow down your TV shows
3. Select a series from the dropdown
4. Click **Process Selection**
5. Monitor progress in real-time

### Process a Specific Season

1. Select a series from the dropdown
2. Select a season from the episode dropdown (e.g., "Season 1")
3. Click **Process Selection**
4. Only episodes from that season will be processed

### Auto-Detection

Enable **Enable Auto Detection** in settings to automatically process new episodes as they're added to your library.

### View Results

After processing, credits markers appear as chapter points in Emby's video player. You can:
- **View all markers**: Select a series from the **View Chapter Markers** section to see detected timestamps for all episodes
- **Edit markers**: Click the **Edit** button next to any marker to manually adjust the timestamp (format: `HH:MM:SS` or `MM:SS`)
- **Add markers**: For episodes without detected credits, click **Add Marker** to manually set the timestamp
- **Batch operations**: Use **Detect Missing** to process all episodes in a season without markers, or **Set Time for Missing** to apply a specific timestamp to multiple episodes
- **Export/Import per series**: Each series can be individually exported or imported for granular backup management
- **Check logs**: View detailed success/failure reasons in the processing log
- **Identify fallbacks**: Episodes using fallback timestamps are marked as "OCR Detection (Fallback)"

### Manual Marker Editing

You can manually edit or add credit markers for any episode:
1. Go to **Dashboard** → **Plugins** → **Credits Detector**
2. Select a series from the **View Chapter Markers** dropdown
3. Find the episode you want to edit
4. Click **Edit** (for existing markers) or **Add Marker** (for new ones)
5. Enter the credits start time in `HH:MM:SS` or `MM:SS` format (e.g., `45:30` or `00:45:30`)
6. The marker is saved and the display refreshes automatically

### Dry Run & Debug

Test detection without saving markers:
1. Select a series or episode
2. Click **Dry Run** (no markers saved) or **Dry Run with Debug** (captures detailed logs)
3. Monitor progress and results
4. Debug logs are automatically downloaded for troubleshooting

### Backup & Restore

Export and import credits markers for backups or server migrations:

#### Single Backup (All Shows)
1. **Export**: Click **Export Credits Backup** to download a JSON file with all credits markers and TheTVDB IDs
2. **Import**: Click **Import Credits Backup**, select one or more JSON files, and choose whether to overwrite existing markers
3. Markers are automatically matched using TheTVDB IDs, Emby IDs, file paths, or series + season/episode numbers
4. **Overwrite Setting**: Enable **Overwrite Existing Credits on Import** to replace existing markers, or disable to skip episodes that already have markers

#### Bulk Export by Series
1. Click **Bulk Export by Series** button in the Backup & Restore section
2. Filter TV shows by library (optional)
3. Check the TV shows you want to export
4. Use **Select All** / **Deselect All** buttons for quick selection
5. Click **Export Selected** to download individual JSON files for each show
6. Files are named `credits-[ShowName]-[timestamp].json` for easy identification

#### Multi-File Import
- The import function supports selecting multiple JSON files at once
- Perfect for restoring multiple shows exported via bulk export

## 🔍 How It Works

1. **Frame Extraction**: Extracts frames from the last portion of each episode using FFmpeg
2. **OCR Analysis**: Sends frames to Tesseract OCR server to read on-screen text
3. **Keyword Matching**: Searches for credit-related keywords in the OCR results
4. **Timestamp Detection**: Identifies the earliest sustained keyword match as credits start
5. **Cross-Episode Validation**: Compares timestamps across episodes to boost confidence
6. **Fallback Application**: Applies median timestamp to failed episodes (if enabled and success rate is sufficient)
7. **Marker Creation**: Creates chapter markers in Emby with type "CreditsStart"

## 🐛 Troubleshooting

### OCR Test Connection Fails

- ✅ Verify Docker container is running: `docker ps | grep tesseract`
- ✅ Check if port 8884 is accessible: `curl http://localhost:8884`
- ✅ For Docker Emby, use host IP instead of `localhost`: `http://192.168.1.x:8884`
- ✅ Ensure no firewall is blocking the connection

### No Credits Detected

- ✅ Verify keywords match your content's language/text
- ✅ Lower the **Minimum Matches** to `1` for testing
- ✅ Increase **Frame Rate** to `1.0` for more samples
- ✅ Check **Search Start Position** covers the credits (try switching between minutes and percentage)
- ✅ Enable **Detailed Logging** and check Emby logs for OCR responses
- ✅ Use **Manual Marker Editing** to set timestamps directly if auto-detection fails

### Temp Folder Fills Up (Docker)

- ✅ **CRITICAL**: Set **Custom Temp Folder Path** to `/tmp` or a mapped volume
- ✅ The plugin auto-cleans temp files, but Docker needs proper path configuration
- ✅ Restart Emby after changing temp path

### Processing is Slow

- ✅ **Enable Parallel Processing** if you have a powerful system and OCR server
- ✅ Reduce **Frame Rate** to `0.33` (1 frame every 3 seconds)
- ✅ Use **JPEG** format instead of PNG
- ✅ Reduce **JPEG Quality** to `85`
- ✅ Set **Max Analysis Duration** to `300` seconds
- ✅ Enable **Lower Thread Priority** to reduce system impact
- ✅ Enable **Smart Frame Skipping** (default on)
- ✅ Set **Consecutive Matches for Early Stop** to `2-3` for faster termination

### False Positives

- ✅ Increase **Minimum Matches** to `2-3`
- ✅ Set **Minimum OCR Confidence** to `0.6-0.8` to filter low-confidence results
- ✅ Refine your **Detection Keywords** to be more specific
- ✅ Enable **Use Episode Comparison** for better accuracy

### Backup Import Not Working

- ✅ Ensure episodes have proper metadata (TheTVDB, TMDB, or IMDB IDs)
- ✅ Check that the backup file is valid JSON
- ✅ Configure **Overwrite Existing Credits on Import** in settings for your preferred default behavior

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- [Emby Server](https://emby.media/) - Media server platform
- [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) - OCR engine
- [yock1/embycreditocr](https://hub.docker.com/r/yock1/embycreditocr) - Docker container

---

If you enjoy this plugin and wish to show your appreciation, you can...

<a href="https://buymeacoffee.com/yockser" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" ></a>



