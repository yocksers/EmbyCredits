# EmbyCredits - Automatic Credits Detection Plugin for Emby

![Version](https://img.shields.io/badge/version-1.0.7-blue)
![.NET](https://img.shields.io/badge/.NET-6.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)

Automatically detect and mark end credits in TV show episodes using OCR (Optical Character Recognition). Never miss the start of the next episode again!

## 🎯 Features

- **🔍 OCR-Based Detection** - Uses Tesseract OCR to read on-screen text and identify credit sequences
- **📊 Cross-Episode Comparison** - Compares detection results across episodes in the same season for improved accuracy
- **🔄 Failed Episode Fallback** - Automatically applies timestamps to failed episodes based on successful detections (configurable)
- **⚡ Batch Processing** - Efficiently process entire series with pre-computation and caching
- **🎛️ Highly Configurable** - Fine-tune detection parameters, frame rates, analysis windows, and more
- **🐳 Docker Integration** - Works seamlessly with Tesseract Docker containers
- **🔧 Test Connection** - Built-in OCR server connectivity testing with visual feedback
- **📈 Real-Time Progress** - Live progress tracking with detailed success/failure logs
- **⏸️ Cancellation Support** - Stop processing at any time with queue clearing

## 📋 Prerequisites

- **Emby Server** 4.8+ (tested on 4.9.1.90)
- **.NET 6.0** or later runtime
- **Tesseract OCR Server** (Docker recommended)
- **FFmpeg** (typically included with Emby)

## 🚀 Quick Start

### 1. Install Tesseract OCR Server (Docker)

```bash
docker run -d \
  --name tesseract-ocr \
  -p 8884:8884 \
  --restart unless-stopped \
  hertzg/tesseract-server:branch-renovate_all-minor-patch
```

**For Unraid users:**
- Container Name: `tesseract-ocr`
- Repository: `hertzg/tesseract-server:branch-renovate_all-minor-patch`
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
| **Minutes from End** | `0.0` | Start detection this many minutes from the end (0 = use percentage) |
| **Search Start** | `0.65` | Start detection at this percentage of video duration (65% = last 35%) |
| **Frame Rate** | `0.5` fps | Frames per second to extract (0.5 = 1 frame every 2 seconds) |
| **Minimum Matches** | `2` | Minimum keyword matches required for detection |
| **Max Analysis Duration** | `600` seconds | Maximum time to analyze (prevents excessive processing) |
| **Stop Seconds from End** | `20` | Stop analysis this many seconds before video end |
| **Image Format** | `png` | Frame format: `png` (accurate) or `jpg` (faster) |
| **JPEG Quality** | `92` | Quality for JPEG frames (1-100, only if format is `jpg`) |

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

## 📖 Usage

### Process a Single Episode

1. Go to **Dashboard** → **Plugins** → **Credits Detector**
2. Select a series from the dropdown
3. Click on an episode
4. Click **Process Episode**

### Process an Entire Series

1. Go to **Dashboard** → **Plugins** → **Credits Detector**
2. Select a series from the dropdown
3. Click **Process All Episodes**
4. Monitor progress in real-time

### Auto-Detection

Enable **Enable Auto Detection** in settings to automatically process new episodes as they're added to your library.

### View Results

After processing, credits markers appear as chapter points in Emby's video player. You can also:
- View detected timestamps in the plugin interface
- Check the detailed log for success/failure reasons
- See which episodes used fallback timestamps (marked as "OCR Detection (Fallback)")

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
- ✅ Check **Minutes from End** or **Search Start** covers the credits
- ✅ Enable **Detailed Logging** and check Emby logs for OCR responses

### Temp Folder Fills Up (Docker)

- ✅ **CRITICAL**: Set **Custom Temp Folder Path** to `/tmp` or a mapped volume
- ✅ The plugin auto-cleans temp files, but Docker needs proper path configuration
- ✅ Restart Emby after changing temp path

### Processing is Slow

- ✅ Reduce **Frame Rate** to `0.33` (1 frame every 3 seconds)
- ✅ Use **JPEG** format instead of PNG
- ✅ Reduce **JPEG Quality** to `85`
- ✅ Set **Max Analysis Duration** to `300` seconds
- ✅ Enable **Lower Thread Priority** to reduce system impact

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- [Emby Server](https://emby.media/) - Media server platform
- [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) - OCR engine
- [hertzg/tesseract-server](https://github.com/hertzg/tesseract-server) - Docker container

## 📧 Support

- **Issues**: [GitHub Issues](../../issues)
- **Discussions**: [GitHub Discussions](../../discussions)

---

If you enjoy this plugin and wish to show your appreciation, you can...

<a href="https://buymeacoffee.com/yockser" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me A Coffee" style="height: 60px !important;width: 217px !important;" ></a>


