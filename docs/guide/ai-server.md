# AI server

[OpenScanner.AIServer](https://github.com/briannippert/OpenScanner.AIServer) is a companion server that runs OpenScanner's heavy AI work on a more powerful machine, such as a desktop with a GPU, instead of on the Raspberry Pi. It provides:

- **Transcription**: Whisper speech-to-text, with models kept loaded in memory so clips are transcribed quickly and several at once.
- **AI call analysis**: a local LLM (through [Ollama](https://ollama.com)) reads each transcript for the call type, service, status and a one-line summary. This is what fills the [call map](map.md).
- **Location lookup**: addresses, intersections and businesses mentioned in a call are placed on the map using the US Census and OpenStreetMap geocoders.

It is optional. Without it, OpenScanner transcribes on the device and the call map is unavailable.

!!! note "Formerly OpenScanner.WhisperServer"
    The AI server used to be called OpenScanner.WhisperServer. Rerunning its installer migrates an existing install.

## What you need

- A Linux machine (Ubuntu or Debian recommended) on the same network as OpenScanner.
- An NVIDIA GPU is recommended. A fast CPU works, but analysis and the large Whisper models are much slower without a GPU.

## Install

On the AI server machine:

```bash
git clone https://github.com/briannippert/OpenScanner.AIServer.git
cd OpenScanner.AIServer
chmod +x scripts/*.sh
./scripts/install_service.sh
```

The installer:

1. Installs .NET, cmake and ffmpeg.
2. Asks for a default Whisper model and a port (default **8090**).
3. Builds whisper.cpp, with CUDA if it finds an NVIDIA GPU.
4. Installs Ollama and asks for the analysis model (default `qwen3:4b`).
5. Downloads the models, checks that they load, and starts the `openscanner-ai` service.

Useful options include `--port=NNNN`, `--llm-model=NAME` and `--rebuild-whisper` (after installing CUDA). Run `./scripts/install_service.sh --help` for the full list.

Check that it is running:

```bash
systemctl status openscanner-ai
journalctl -u openscanner-ai -f
```

`http://<ai-server>:8090/health` reports the loaded model and whether a GPU is in use.

## Connect OpenScanner to it

1. In OpenScanner, go to **Settings → Transcription** and set **Backend** to **Remote server**.
2. Enter the **Server URL**, for example `http://192.168.1.100:8090`, and click **Test**.
3. Pick a **Model**. The list comes from the models installed on the AI server.
4. Turn on **AI call analysis**, which appears once OpenScanner sees that the server supports it. See [Call map](map.md#turning-on-ai-call-analysis) for setting each channel's town and analyzing older calls.

Audio is sent to the AI server and transcripts come back. Nothing leaves your network except the geocoding lookups for call locations.

## Updating

Rerun the installer on the AI server. It pulls the latest code, rebuilds and restarts the service:

```bash
./scripts/install_service.sh
```

## Troubleshooting

- **Test fails**: check the URL and port, that `systemctl status openscanner-ai` shows it running, and that the AI server's firewall allows the port.
- **No AI call analysis switch**: the server is an older Whisper-only version. Update it as above.
- **Transcripts or analysis are slow**: check `/health` for `"acceleration": "GPU (CUDA)"`. If you installed the CUDA toolkit after the AI server, rerun the installer with `--rebuild-whisper`.

For configuration details and the server's API, see the [OpenScanner.AIServer README](https://github.com/briannippert/OpenScanner.AIServer#readme).
