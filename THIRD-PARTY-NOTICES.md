# Third-party notices

The packages in this repository contain no third-party code.

`RecursiveAI.OneMedia.Audio` depends on the NuGet package `RecursiveAI.OneAudio`, published by the same authors under `MIT OR Apache-2.0`.

## Test media

Every media file committed under `testdata/` is our own work and is licensed like the rest of the repository (`MIT OR Apache-2.0`). None is taken from a third-party recording, film, sample collection or device. The files are one of:

| Folder | What they are | How they were made |
|---|---|---|
| `testdata/audio`, `testdata/isobmff`, `testdata/metadata`, `testdata/heaac` | Short MP4, MOV, 3GP, Matroska, WebM, Ogg and FLAC files | Written by ffmpeg, MP4Box, mkvmerge or ExifTool (versions recorded in the `VERSIONS.txt` beside them) from synthetic test patterns and tones (ffmpeg's `testsrc2`, `sine` and `aevalsrc` sources) by the scripts in `tools/gen-*.sh`. Names, places and devices in their tags are invented. |
| `testdata/isobmff/family` | AMR in 3GP, an iTunes gapless tag, a fragmented file, multi-track files | Generated the same way (synthetic tones, and AMR frames made of pseudo-random bytes) by the sibling OneAudio project, which has the same authors and licence, and copied unchanged; see the README beside them. |
| `testdata/avi` | Short AVI, OpenDML and DV files | Written by ffmpeg (version in `testdata/avi/VERSIONS.txt`) from synthetic test patterns and tones by `tools/gen-avi-testdata.sh`. |
| `testdata/fuzz` | A minimized fuzzer reproduction | A byte-edited copy of one of the generated files above. |

The encoders' own identification strings (for example `Lavc` and `Lavf`) inside those files are not third-party content.

The larger corpora the comparison tools in `tools/` can fetch (Blender open movies, Xiph test media, the Matroska test files, public sample collections) are downloaded into a cache outside the repository when someone runs those scripts. They are not distributed here or in the packages; their licences and credits are listed in `tools/corpus/manifest.tsv` where one applies.

## Documentation used to write the AVI reader

No text or code from these is included; field layouts were read from them.

- Microsoft, "AVI RIFF File Reference" (Microsoft Learn, win32 documentation), licensed under Creative Commons Attribution 4.0 International (CC BY 4.0), © Microsoft.
- Matrox Electronic Systems Ltd., "OpenDML AVI File Format Extensions" version 1.02, provided as is; it grants no patent licence.
