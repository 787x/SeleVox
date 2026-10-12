# Offline MP3 fixture

`tone.mp3` is a generated 440 Hz sine, 24 kHz mono, 48 kbit/s MP3, 0.5 s input,
with no ID3 or Xing header. It contains no user text or service audio.

Created once using FFmpeg/libmp3lame:

```text
ffmpeg -f lavfi -i sine=frequency=440:sample_rate=24000:duration=0.5 -ac 1 -c:a libmp3lame -b:a 48k -write_xing 0 -id3v2_version 0 tone.mp3
```

FFmpeg is not a build, test or application dependency. Tests use the committed
fixture and the actual NAudio/Windows ACM decoder, including split network bytes,
30 independent MP3 turns and a truncated final frame. Service tests are separate.
